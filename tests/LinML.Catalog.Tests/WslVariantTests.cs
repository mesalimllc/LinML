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

    public WslVariantTests() => PluginExecutionProvider.WslProbe = () => true;

    public void Dispose() => PluginExecutionProvider.WslProbe = _probeBefore;

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
