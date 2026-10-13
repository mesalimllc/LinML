using System.Diagnostics;
using Xunit;

namespace LinML.Catalog.Tests;

[CollectionDefinition("WslProbe", DisableParallelization = true)]
public sealed class WslProbeCollection;

/// <summary>
/// The opt-in WSL2 TensorRT-RTX build links the stock wheel's runtime libraries, so it is
/// only selected / registered when those are vendored too.
/// </summary>
[Collection("WslProbe")]
public class WslVariantTests : IDisposable
{
    private readonly Func<bool> _probeBefore = PluginExecutionProvider.WslProbe;
    private readonly Func<bool> _dxgBefore = PluginExecutionProvider.DxgProbe;
    private readonly Func<string, string?> _envBefore = PluginExecutionProvider.EnvReader;

    public WslVariantTests()
    {
        PluginExecutionProvider.WslProbe = () => true;
        PluginExecutionProvider.DxgProbe = () => true;
        PluginExecutionProvider.EnvReader = _ => null;
    }

    public void Dispose()
    {
        PluginExecutionProvider.WslProbe = _probeBefore;
        PluginExecutionProvider.DxgProbe = _dxgBefore;
        PluginExecutionProvider.EnvReader = _envBefore;
    }

    private static void Vendor(string root, PluginPackageSpec spec)
    {
        Directory.CreateDirectory(spec.InstallDirectory(root));
        File.WriteAllText(spec.LibraryPath(root), "not-a-real-elf");
    }

    [Fact]
    public void TryRegister_PatchedBuildWithoutStockRuntime_FailsWithAClearReason()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtx, root.Path, new PluginInstaller(), registrar, () => true);

        bool registered = provider.TryRegister(out string? reason);

        Assert.False(registered);
        Assert.Contains("linml setup --trt-rtx-wsl2", reason);
        Assert.Empty(registrar.Registrations);
    }

    [Fact]
    public void TryRegister_PatchedBuildWithStockRuntime_RegistersThePatchedBuild_NotTheStockWheel()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtx);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtx, root.Path, new PluginInstaller(), registrar, () => true);

        Assert.Equal(KnownPlugins.TensorrtRtxWsl2.LibraryPath(root.Path), provider.LibraryPath);
        bool registered = provider.TryRegister(out _);

        Assert.True(registered);
        var registration = Assert.Single(registrar.Registrations);
        Assert.Equal(KnownPlugins.TensorrtRtxWsl2.LibraryPath(root.Path), registration.Path);
    }

    [Fact]
    public void WslVariantLibrary_OptOut_ReturnsNull()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtx);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        Assert.NotNull(KnownPlugins.WslVariantLibrary(LinML.Contracts.ProviderId.TensorrtRtx, root.Path));

        PluginExecutionProvider.EnvReader = name => name == "LINML_TRT_RTX_WSL2" ? "0" : null;

        Assert.Null(KnownPlugins.WslVariantLibrary(LinML.Contracts.ProviderId.TensorrtRtx, root.Path));
    }

    [Fact]
    public void LibraryPath_OffWsl_IgnoresThePatchedBuild()
    {
        PluginExecutionProvider.WslProbe = () => false;
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtx);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtx, root.Path, new PluginInstaller(), new FakeRegistrar(), () => true);

        Assert.Equal(KnownPlugins.TensorrtRtx.LibraryPath(root.Path), provider.LibraryPath);
    }
}

/// <summary>Serves a different body per URL so the stock wheel and the WSL2 zip can both be faked.</summary>
internal sealed class RoutingHandler(Dictionary<Uri, byte[]> bodies) : HttpMessageHandler
{
    public List<Uri> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested.Add(request.RequestUri!);
        return Task.FromResult(bodies.TryGetValue(request.RequestUri!, out byte[]? body)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

[Collection("WslProbe")]
public class WslEnsureReadyTests : IDisposable
{
    private readonly Func<bool> _probeBefore = PluginExecutionProvider.WslProbe;
    private readonly Func<bool> _dxgBefore = PluginExecutionProvider.DxgProbe;
    private readonly Func<string, string?> _envBefore = PluginExecutionProvider.EnvReader;
    private readonly Dictionary<string, string?> _env = [];

    private readonly byte[] _stockWheel = FakeWheel.Build();
    private readonly PluginPackageSpec _stock;
    private readonly PluginPackageSpec _wsl;

    public WslEnsureReadyTests()
    {
        PluginExecutionProvider.WslProbe = () => true;
        PluginExecutionProvider.DxgProbe = () => true;
        PluginExecutionProvider.EnvReader = name => _env.GetValueOrDefault(name);

        _stock = FakeWheel.Spec(_stockWheel) with
        {
            Id = LinML.Contracts.ProviderId.TensorrtRtx,
            WheelUrl = new Uri("https://example.invalid/stock.whl"),
            InstallSubdirectory = Path.Combine("ep-plugins", "trt-rtx", "fake_ep"),
        };
        _wsl = FakeWheel.Spec(_stockWheel) with
        {
            Id = LinML.Contracts.ProviderId.TensorrtRtx,
            WheelUrl = new Uri("https://example.invalid/wsl2.zip"),
            WheelSha256 = FakeWheel.Sha256(_stockWheel),
            InstallSubdirectory = Path.Combine("ep-plugins", "trt-rtx-wsl2", "fake_ep"),
        };
    }

    public void Dispose()
    {
        PluginExecutionProvider.WslProbe = _probeBefore;
        PluginExecutionProvider.DxgProbe = _dxgBefore;
        PluginExecutionProvider.EnvReader = _envBefore;
    }

    private RoutingHandler Handler() => new(new()
    {
        [_stock.WheelUrl] = _stockWheel,
        [_wsl.WheelUrl] = _stockWheel,
    });

    private PluginExecutionProvider Provider(string root, RoutingHandler handler, FakeRegistrar registrar) =>
        new(_stock, root, new PluginInstaller(handler), registrar, () => true, _wsl);

    private static void Vendor(string root, PluginPackageSpec spec)
    {
        Directory.CreateDirectory(spec.InstallDirectory(root));
        File.WriteAllText(spec.LibraryPath(root), "x");
    }

    [Fact]
    public async Task NothingVendored_DownloadsStockThenWsl2_AndRegistersOnlyTheWsl2Build()
    {
        using var root = new TempDir();
        var handler = Handler();
        var registrar = new FakeRegistrar();

        var result = await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_stock.WheelUrl, _wsl.WheelUrl], handler.Requested);
        var reg = Assert.Single(registrar.Registrations);
        Assert.Equal(_wsl.LibraryPath(root.Path), reg.Path);
        // FakeRegistrar can never report devices, so the WSL2-specific zero-device result is the outcome.
        Assert.Equal(ProviderEnsureStatus.NoDevices, result.Status);
        Assert.Contains("/dev/dxg", result.Detail);
    }

    [Fact]
    public async Task StockVendoredOnly_DownloadsOnlyTheWsl2Build_NeverRegistersStock()
    {
        using var root = new TempDir();
        Vendor(root.Path, _stock);
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_wsl.WheelUrl], handler.Requested);
        Assert.Equal(_wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task BothVendored_NoHttp_RegistersTheWsl2Build()
    {
        using var root = new TempDir();
        Vendor(root.Path, _stock);
        Vendor(root.Path, _wsl);
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Empty(handler.Requested);
        Assert.Equal(_wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsDisallowed_Wsl2BuildMissing_ReportsDownloadDisallowed_WithoutRegistering(bool stockVendored)
    {
        using var root = new TempDir();
        if (stockVendored)
        {
            Vendor(root.Path, _stock);
        }

        var handler = Handler();
        var registrar = new FakeRegistrar();

        var result = await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Equal(ProviderEnsureStatus.DownloadDisallowed, result.Status);
        Assert.Empty(handler.Requested);
        Assert.Empty(registrar.Registrations);
    }

    [Fact]
    public async Task Wsl1_NoDxg_UsesStockAndNeverDownloadsTheWsl2Build()
    {
        PluginExecutionProvider.DxgProbe = () => false;
        using var root = new TempDir();
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_stock.WheelUrl], handler.Requested);
        Assert.Equal(_stock.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task NotWsl_BehavesAsBefore()
    {
        PluginExecutionProvider.WslProbe = () => false;
        using var root = new TempDir();
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_stock.WheelUrl], handler.Requested);
        Assert.Equal(_stock.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task OptOut_SelectsStock()
    {
        _env["LINML_TRT_RTX_WSL2"] = "0";
        using var root = new TempDir();
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_stock.WheelUrl], handler.Requested);
        Assert.Equal(_stock.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task CustomEp_BeatsTheWsl2Build_AndNeedsNoDownload()
    {
        using var root = new TempDir();
        string custom = Path.Combine(root.Path, "custom.so");
        File.WriteAllText(custom, "x");
        _env["TRT_RTX_CUSTOM_EP"] = custom;
        var handler = Handler();
        var registrar = new FakeRegistrar();

        await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Empty(handler.Requested);
        Assert.Equal(custom, Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task Wsl2Sha256Mismatch_IsReportedAsFailed()
    {
        using var root = new TempDir();
        Vendor(root.Path, _stock);
        var handler = new RoutingHandler(new() { [_wsl.WheelUrl] = [1, 2, 3] });
        var registrar = new FakeRegistrar();

        var result = await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Empty(registrar.Registrations);
    }
}

public class EnvScriptTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LinML.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("LinML.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Sources env-gpu-deps.sh in bash against a fake DEPS tree and returns LINML_EP_LIBRARIES.</summary>
    private static string Source(string deps, string? previous)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"source '{Path.Combine(RepoRoot(), "scripts", "env-gpu-deps.sh")}' >/dev/null 2>&1; printf %s \"$LINML_EP_LIBRARIES\"");
        psi.Environment["DEPS"] = deps;
        psi.Environment.Remove("TRT_RTX_CUSTOM_EP");
        if (previous is null)
        {
            psi.Environment.Remove("LINML_EP_LIBRARIES");
        }
        else
        {
            psi.Environment["LINML_EP_LIBRARIES"] = previous;
        }

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string FakeDeps(TempDir root)
    {
        string ov = Path.Combine(root.Path, "ep-plugins", "openvino", "onnxruntime_ep_openvino");
        Directory.CreateDirectory(ov);
        File.WriteAllText(Path.Combine(ov, "libonnxruntime_providers_openvino_plugin.so"), "x");
        return root.Path;
    }

    [Fact]
    public void Resourcing_DropsTheStaleOpenVinoAlias_AndKeepsOtherMappings()
    {
        using var root = new TempDir();
        string deps = FakeDeps(root);

        string result = Source(deps, "openvino=/old/path.so;QNNExecutionProvider=/q.so");

        string ov = Path.Combine(deps, "ep-plugins", "openvino", "onnxruntime_ep_openvino", "libonnxruntime_providers_openvino_plugin.so");
        var pairs = result.Split(';');
        Assert.DoesNotContain(pairs, p => p.StartsWith("openvino=", StringComparison.Ordinal));
        Assert.Contains($"linml.openvino={ov}", pairs);
        Assert.Contains("QNNExecutionProvider=/q.so", pairs);
    }

    [Theory]
    [InlineData("linml.openvino=/my/custom.so")]
    [InlineData("OpenVINOExecutionProvider=/my/custom.so")]
    public void Resourcing_KeepsAnIntentionalCanonicalOpenVinoOverride(string mapping)
    {
        using var root = new TempDir();
        string deps = FakeDeps(root);

        string result = Source(deps, mapping + ";openvino=/old/path.so");

        var pairs = result.Split(';');
        Assert.Contains(mapping, pairs);
        Assert.DoesNotContain(pairs, p => p.StartsWith("openvino=", StringComparison.Ordinal));
        Assert.Single(pairs, p => p.Contains("openvino=", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("OpenVINOExecutionProvider=", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstSource_RegistersOpenVinoUnderTheCatalogName()
    {
        using var root = new TempDir();
        string deps = FakeDeps(root);

        string result = Source(deps, previous: null);

        Assert.StartsWith("linml.openvino=", result, StringComparison.Ordinal);
    }
}
