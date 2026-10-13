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
            KnownPlugins.TensorrtRtxLinuxX64, root.Path, new PluginInstaller(), registrar, () => true);

        bool registered = provider.TryRegister(out string? reason);

        Assert.False(registered);
        Assert.Contains("linml setup --trt-rtx-wsl2", reason);
        Assert.Empty(registrar.Registrations);
    }

    [Fact]
    public void TryRegister_PatchedBuildWithStockRuntime_RegistersThePatchedBuild_NotTheStockWheel()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtxLinuxX64);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtxLinuxX64, root.Path, new PluginInstaller(), registrar, () => true);

        Assert.Equal(KnownPlugins.TensorrtRtxWsl2.LibraryPath(root.Path), provider.LibraryPath);
        bool registered = provider.TryRegister(out _);

        Assert.True(registered);
        var registration = Assert.Single(registrar.Registrations);
        Assert.Equal(KnownPlugins.TensorrtRtxWsl2.LibraryPath(root.Path), registration.Path);
    }

    [Fact]
    public void Arm64Stock_UnderWsl2_UsesItsOwnWheel_NotThePatchedX64Build()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtxLinuxArm64);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtxLinuxArm64, root.Path, new PluginInstaller(), new FakeRegistrar(), () => true);

        Assert.Equal(KnownPlugins.TensorrtRtxLinuxArm64.LibraryPath(root.Path), provider.LibraryPath);
        Assert.Null(KnownPlugins.WslVariantLibrary(KnownPlugins.TensorrtRtxLinuxArm64, root.Path));
    }

    [Fact]
    public void WslVariantLibrary_OptOut_ReturnsNull()
    {
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtxLinuxX64);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        Assert.NotNull(KnownPlugins.WslVariantLibrary(KnownPlugins.TensorrtRtxLinuxX64, root.Path));

        PluginExecutionProvider.EnvReader = name => name == "LINML_TRT_RTX_WSL2" ? "0" : null;

        Assert.Null(KnownPlugins.WslVariantLibrary(KnownPlugins.TensorrtRtxLinuxX64, root.Path));
    }

    [Fact]
    public void LibraryPath_OffWsl_IgnoresThePatchedBuild()
    {
        PluginExecutionProvider.WslProbe = () => false;
        using var root = new TempDir();
        Vendor(root.Path, KnownPlugins.TensorrtRtxLinuxX64);
        Vendor(root.Path, KnownPlugins.TensorrtRtxWsl2);
        var provider = new PluginExecutionProvider(
            KnownPlugins.TensorrtRtxLinuxX64, root.Path, new PluginInstaller(), new FakeRegistrar(), () => true);

        Assert.Equal(KnownPlugins.TensorrtRtxLinuxX64.LibraryPath(root.Path), provider.LibraryPath);
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
    public async Task Wsl2BuildRegistersButExposesNoDevices_ReportsNoDevices_WithoutStockFallback()
    {
        using var root = new TempDir();
        Vendor(root.Path, _stock);
        Vendor(root.Path, _wsl);
        var registrar = new FakeRegistrar(); // GetDevices() stays empty
        var handler = Handler();

        var result = await Provider(root.Path, handler, registrar).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.NoDevices, result.Status);
        Assert.Contains("/dev/dxg", result.Detail);
        Assert.Contains("nvidia-smi", result.Detail);
        Assert.Empty(handler.Requested);
        Assert.Equal(_wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
        Assert.DoesNotContain(registrar.Registrations, r => r.Path == _stock.LibraryPath(root.Path));
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

    // 0.4.0-wsl2p1 needs a newer glibc than Ubuntu 24.04 has, and p2 installs into the same directory.
    // A host that already has p1 must get the pinned build, not keep loading the library that is there.
    private (PluginPackageSpec Wsl, byte[] Wheel) PinnedWsl2Build()
    {
        byte[] wheel = FakeWheel.Build(buildInfoVersion: "2.0.0");
        PluginPackageSpec wsl = _wsl with
        {
            Version = "2.0.0",
            VersionManifestFile = "build-info.json",
            WheelSha256 = FakeWheel.Sha256(wheel),
        };
        return (wsl, wheel);
    }

    private static void VendorStaleWsl2Build(string root, PluginPackageSpec wsl)
    {
        Directory.CreateDirectory(wsl.InstallDirectory(root));
        File.WriteAllText(wsl.LibraryPath(root), "previous-build");
        File.WriteAllText(Path.Combine(wsl.InstallDirectory(root), "build-info.json"), FakeWheel.BuildInfo("1.0.0"));
    }

    [Fact]
    public async Task StaleWsl2BuildVendored_DownloadsAllowed_ReplacesItBeforeRegistering()
    {
        using var root = new TempDir();
        (PluginPackageSpec wsl, byte[] wheel) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        VendorStaleWsl2Build(root.Path, wsl);
        var handler = new RoutingHandler(new() { [_stock.WheelUrl] = _stockWheel, [wsl.WheelUrl] = wheel });
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), registrar, () => true, wsl);

        await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([wsl.WheelUrl], handler.Requested);
        Assert.Equal("plugin", File.ReadAllText(wsl.LibraryPath(root.Path)));
        Assert.Equal("2.0.0", wsl.VendoredVersion(wsl.LibraryPath(root.Path)));
        Assert.Equal(wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
    }

    [Fact]
    public async Task CurrentWsl2BuildVendored_NoHttp()
    {
        using var root = new TempDir();
        (PluginPackageSpec wsl, byte[] wheel) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        Directory.CreateDirectory(wsl.InstallDirectory(root.Path));
        File.WriteAllText(wsl.LibraryPath(root.Path), "current-build");
        File.WriteAllText(Path.Combine(wsl.InstallDirectory(root.Path), "build-info.json"), FakeWheel.BuildInfo("2.0.0"));
        var handler = new RoutingHandler(new() { [wsl.WheelUrl] = wheel });
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), new FakeRegistrar(), () => true, wsl);

        await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Empty(handler.Requested);
        Assert.Equal("current-build", File.ReadAllText(wsl.LibraryPath(root.Path)));
    }

    [Fact]
    public async Task StaleWsl2BuildVendored_DownloadsDisallowed_KeepsUsingIt_AndReportsItsVersion()
    {
        // Offline hosts where the older build works keep working; nothing is fetched or replaced.
        using var root = new TempDir();
        (PluginPackageSpec wsl, byte[] wheel) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        VendorStaleWsl2Build(root.Path, wsl);
        var handler = new RoutingHandler(new() { [wsl.WheelUrl] = wheel });
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Empty(handler.Requested);
        Assert.Equal("previous-build", File.ReadAllText(wsl.LibraryPath(root.Path)));
        Assert.Equal(wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
        Assert.Equal(ProviderEnsureStatus.NoDevices, result.Status);
        Assert.Contains("1.0.0", result.Detail);
        Assert.DoesNotContain("2.0.0", result.Detail);
    }

    [Fact]
    public async Task StaleWsl2Build_WhenTheUpdateCannotBeDownloaded_KeepsUsingTheVendoredBuild()
    {
        // GitHub unreachable, a proxy, a pulled release: an earlier build that loads is better than none.
        using var root = new TempDir();
        (PluginPackageSpec wsl, _) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        VendorStaleWsl2Build(root.Path, wsl);
        var handler = new RoutingHandler(new()); // every URL is a 404
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([wsl.WheelUrl], handler.Requested);
        Assert.Equal("previous-build", File.ReadAllText(wsl.LibraryPath(root.Path)));
        Assert.Equal(wsl.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
        Assert.NotEqual(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("1.0.0", result.Detail);
        Assert.Contains("Could not replace", result.Detail);
        Assert.Contains("2.0.0", result.Detail);
    }

    [Fact]
    public async Task StaleWsl2Build_WhenTheUpdateFailsAndItDoesNotLoad_ReportsBothFailures()
    {
        using var root = new TempDir();
        (PluginPackageSpec wsl, _) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        VendorStaleWsl2Build(root.Path, wsl);
        var registrar = new FakeRegistrar { RegisterThrows = new InvalidOperationException("version `GLIBC_2.43' not found") };
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(new RoutingHandler(new())), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("GLIBC_2.43", result.Detail);
        Assert.Contains("Could not replace", result.Detail);
    }

    [Fact]
    public async Task Wsl2BuildNotVendored_WhenTheDownloadFails_StillFails()
    {
        // Nothing to fall back to: this must not read as success.
        using var root = new TempDir();
        (PluginPackageSpec wsl, _) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(new RoutingHandler(new())), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Empty(registrar.Registrations);
    }

    [Fact]
    public async Task StaleWsl2BuildThatFailsToLoad_PointsAtTheUpdate()
    {
        using var root = new TempDir();
        (PluginPackageSpec wsl, byte[] wheel) = PinnedWsl2Build();
        Vendor(root.Path, _stock);
        VendorStaleWsl2Build(root.Path, wsl);
        var registrar = new FakeRegistrar { RegisterThrows = new InvalidOperationException("version `GLIBC_2.43' not found") };
        var provider = new PluginExecutionProvider(
            _stock, root.Path, new PluginInstaller(new RoutingHandler(new() { [wsl.WheelUrl] = wheel })), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("GLIBC_2.43", result.Detail);
        Assert.Contains("1.0.0", result.Detail);
        Assert.Contains("2.0.0", result.Detail);
        Assert.Contains("linml setup --trt-rtx-wsl2", result.Detail);
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

    // The patched build is built against glibc 2.35, newer than the stock wheel's manylinux_2_28, so a WSL2 distro
    // between the two (Ubuntu 20.04) loads the wheel but not this. It must be reported before anything is downloaded.
    [Fact]
    public async Task Wsl2BuildNeedsANewerGlibcThanTheHostHas_IsNotAvailable_WithoutDownloading()
    {
        using var root = new TempDir();
        PluginPackageSpec wsl = _wsl with { MinimumGlibc = new Version(99, 0) };
        var handler = Handler();
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), registrar, () => true, wsl);

        var result = await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.NotAvailable, result.Status);
        Assert.Contains("glibc 99.0", result.Detail);
        Assert.Contains(wsl.Version, result.Detail);
        Assert.Empty(handler.Requested);
        Assert.Empty(registrar.Registrations);
        Assert.False(provider.TryRegister(out string? reason));
        Assert.Contains("glibc 99.0", reason);
        Assert.Equal(ProviderReadyState.Unsupported, provider.ReadyState);
    }

    [Fact]
    public async Task Wsl2BuildGlibcMinimum_DoesNotApply_WhenTheStockWheelIsSelected()
    {
        _env["LINML_TRT_RTX_WSL2"] = "0";
        using var root = new TempDir();
        PluginPackageSpec wsl = _wsl with { MinimumGlibc = new Version(99, 0) };
        var handler = Handler();
        var registrar = new FakeRegistrar();
        var provider = new PluginExecutionProvider(_stock, root.Path, new PluginInstaller(handler), registrar, () => true, wsl);

        await provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal([_stock.WheelUrl], handler.Requested);
        Assert.Equal(_stock.LibraryPath(root.Path), Assert.Single(registrar.Registrations).Path);
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

    [Theory]
    [InlineData(false, "/dev/dxg is missing")]
    [InlineData(true, "opted out")]
    public async Task StockOnWsl_NoDevicesHint_NamesTheActualCause(bool optedOut, string expected)
    {
        PluginExecutionProvider.DxgProbe = () => optedOut;
        if (optedOut)
        {
            _env["LINML_TRT_RTX_WSL2"] = "0";
        }

        using var root = new TempDir();
        var result = await Provider(root.Path, Handler(), new FakeRegistrar())
            .EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.NoDevices, result.Status);
        Assert.Contains(expected, result.Detail);
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
