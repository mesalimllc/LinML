using System.Text.Json;
using LinML.Catalog;
using LinML.Cli.Commands;
using Xunit;

namespace LinML.Cli.Tests;

public class CatalogCommandTests
{
    [Fact]
    public void CatalogList_PrintsTableWithAllProviders()
    {
        var (exitCode, stdout, stderr) = CliTestHost.Run("catalog", "list");

        Assert.Equal(0, exitCode);
        Assert.Contains("LinML Execution Provider Catalog", stdout);
        Assert.Contains("cpu", stdout);
        Assert.Contains("cuda", stdout);
        Assert.Contains("tensorrt", stdout);
        Assert.Contains("openvino", stdout);
        Assert.Contains("trt-rtx", stdout);
        Assert.Contains("migraphx", stdout);
    }

    [Fact]
    public void CatalogList_Json_ReturnsValidJsonArray()
    {
        var (exitCode, stdout, stderr) = CliTestHost.Run("catalog", "list", "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(7, doc.RootElement.GetArrayLength());

        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("cpu", ids);
        Assert.Contains("cuda", ids);
        Assert.Contains("tensorrt", ids);
        Assert.Contains("openvino", ids);
        Assert.Contains("trt-rtx", ids);
        Assert.Contains("migraphx", ids);
    }

    [Fact]
    public void CatalogInstall_BuiltIn_ExitsZeroWithExplanation()
    {
        var (exitCode, stdout, _) = CliTestHost.Run("catalog", "install", "cuda");

        Assert.Equal(0, exitCode);
        Assert.Contains("built into ONNX Runtime", stdout);

        var (jsonExit, jsonStdout, _) = CliTestHost.Run("catalog", "install", "cpu", "--json");
        Assert.Equal(0, jsonExit);
        using var doc = JsonDocument.Parse(jsonStdout);
        Assert.Equal("BuiltIn", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void CatalogInstall_UnknownProvider_ReturnsExitCode1()
    {
        var (exitCode, _, stderr) = CliTestHost.Run("catalog", "install", "unknown_foo");

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown plugin provider", stderr);
    }

    [Fact]
    public void CatalogInstall_AlreadyInstalled_ExitsZero()
    {
        // OpenVINO is linux-x64 only. Elsewhere the install is refused before the already-installed check.
        if (!CliTestHost.IsLinuxX64Host)
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "linml-test-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spec = KnownPlugins.OpenVino;
            string targetDir = spec.InstallDirectory(tempDir);
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(spec.LibraryPath(tempDir), "fake-so");

            var (exitCode, stdout, _) = CliTestHost.Run("catalog", "install", "openvino", "--root", tempDir);

            Assert.Equal(0, exitCode);
            Assert.Contains("Already installed", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // `catalog install` only knows KnownPlugins.All, so on its own it would report success for trt-rtx and leave an
    // earlier patched WSL2 build (0.4.0-wsl2p1: needs a newer glibc than most hosts have) selected.
    private static void VendorWsl2Build(string root, string epVersion)
    {
        PluginPackageSpec wsl2 = KnownPlugins.TensorrtRtxWsl2;
        Directory.CreateDirectory(wsl2.InstallDirectory(root));
        File.WriteAllText(wsl2.LibraryPath(root), "fake-so");
        File.WriteAllText(Path.Combine(wsl2.InstallDirectory(root), "build-info.json"), $"{{ \"epVersion\": \"{epVersion}\" }}");
    }

    // The patched build is x86_64 only, and ResolveInstallTargets picks the wheel of the host's architecture, so
    // these name the x86_64 wheel to mean the same thing on every runner.
    private static IReadOnlyList<PluginPackageSpec> TrtRtxTargets() => [KnownPlugins.TensorrtRtxLinuxX64];

    [Fact]
    public void WithInstalledWsl2Build_EarlierBuildVendored_IsUpgradedAlongsideTheWheel()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, "0.4.0-wsl2p1");

        var targets = CatalogCommand.WithInstalledWsl2Build(TrtRtxTargets(), root.Path, force: false, wsl2Selected: true);

        Assert.Equal([KnownPlugins.TensorrtRtx.Version, KnownPlugins.TensorrtRtxWsl2.Version], targets.Select(t => t.Version));
        Assert.Same(KnownPlugins.TensorrtRtxWsl2, targets[^1]);
    }

    [Fact]
    public void WithInstalledWsl2Build_AlsoAppliesToTheAllPluginsInstall()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, "0.4.0-wsl2p1");
        PluginPackageSpec[] all = [KnownPlugins.OpenVino, KnownPlugins.TensorrtRtxLinuxX64, KnownPlugins.MigraphX];

        var targets = CatalogCommand.WithInstalledWsl2Build(all, root.Path, force: false, wsl2Selected: true);

        Assert.Equal(all.Length + 1, targets.Count);
        Assert.Contains(KnownPlugins.TensorrtRtxWsl2, targets);
    }

    [Fact]
    public void WithInstalledWsl2Build_CurrentBuild_IsLeftAlone_UnlessForced()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, KnownPlugins.TensorrtRtxWsl2.Version);

        Assert.DoesNotContain(KnownPlugins.TensorrtRtxWsl2,
            CatalogCommand.WithInstalledWsl2Build(TrtRtxTargets(), root.Path, force: false, wsl2Selected: true));
        Assert.Contains(KnownPlugins.TensorrtRtxWsl2,
            CatalogCommand.WithInstalledWsl2Build(TrtRtxTargets(), root.Path, force: true, wsl2Selected: true));
    }

    [Fact]
    public void WithInstalledWsl2Build_NothingVendored_DoesNotInstallItFresh()
    {
        using var root = new TempDirectory();

        var targets = CatalogCommand.WithInstalledWsl2Build(TrtRtxTargets(), root.Path, force: false, wsl2Selected: true);

        Assert.DoesNotContain(KnownPlugins.TensorrtRtxWsl2, targets);
        Assert.Equal(TrtRtxTargets().Count, targets.Count);
    }

    [Fact]
    public void WithInstalledWsl2Build_NotSelectedOnThisHost_ChangesNothing()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, "0.4.0-wsl2p1");

        var targets = CatalogCommand.WithInstalledWsl2Build(TrtRtxTargets(), root.Path, force: false, wsl2Selected: false);

        Assert.DoesNotContain(KnownPlugins.TensorrtRtxWsl2, targets);
    }

    [Fact]
    public void WithInstalledWsl2Build_OtherPluginsOnly_ChangesNothing()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, "0.4.0-wsl2p1");
        PluginPackageSpec[] openvino = [KnownPlugins.OpenVino];

        var targets = CatalogCommand.WithInstalledWsl2Build(openvino, root.Path, force: false, wsl2Selected: true);

        Assert.Equal(openvino.Length, targets.Count);
        Assert.DoesNotContain(KnownPlugins.TensorrtRtxWsl2, targets);
    }

    [Fact]
    public void WithInstalledWsl2Build_Arm64Wheel_NeverAddsTheX64OnlyBuild()
    {
        using var root = new TempDirectory();
        VendorWsl2Build(root.Path, "0.4.0-wsl2p1");

        var targets = CatalogCommand.WithInstalledWsl2Build(
            [KnownPlugins.TensorrtRtxLinuxArm64], root.Path, force: true, wsl2Selected: true);

        Assert.Equal([KnownPlugins.TensorrtRtxLinuxArm64], targets);
    }

    [Fact]
    public void ResolveInstallTargets_MapsAliasesCorrectly()
    {
        var all = CatalogCommand.ResolveInstallTargets("all", out string? errAll, out bool isBuiltInAll);
        Assert.Null(errAll);
        Assert.False(isBuiltInAll);
        Assert.Equal(4, all.Count);

        var ov = CatalogCommand.ResolveInstallTargets("openvino", out string? errOv, out bool isBuiltInOv);
        Assert.Null(errOv);
        Assert.False(isBuiltInOv);
        Assert.Single(ov);
        Assert.Equal(KnownPlugins.OpenVino.Id, ov[0].Id);

        var trtRtx = CatalogCommand.ResolveInstallTargets("trt-rtx", out string? errTrt, out bool isBuiltInTrt);
        Assert.Null(errTrt);
        Assert.False(isBuiltInTrt);
        Assert.Single(trtRtx);
        Assert.Equal(KnownPlugins.TensorrtRtx.Id, trtRtx[0].Id);

        var migraphx = CatalogCommand.ResolveInstallTargets("migraphx", out string? errMx, out bool isBuiltInMx);
        Assert.Null(errMx);
        Assert.False(isBuiltInMx);
        Assert.Single(migraphx);
        Assert.Equal(KnownPlugins.MigraphX.Id, migraphx[0].Id);

        // Canonical raw ExecutionProvider names
        var canonicalOv = CatalogCommand.ResolveInstallTargets("OpenVINOExecutionProvider", out string? errCOv, out bool isBuiltInCOv);
        Assert.Null(errCOv);
        Assert.False(isBuiltInCOv);
        Assert.Single(canonicalOv);
        Assert.Equal(KnownPlugins.OpenVino.Id, canonicalOv[0].Id);

        var canonicalTrt = CatalogCommand.ResolveInstallTargets("TensorRTRtxExecutionProvider", out string? errCTrt, out bool isBuiltInCTrt);
        Assert.Null(errCTrt);
        Assert.False(isBuiltInCTrt);
        Assert.Single(canonicalTrt);
        Assert.Equal(KnownPlugins.TensorrtRtx.Id, canonicalTrt[0].Id);

        var canonicalMx = CatalogCommand.ResolveInstallTargets("MIGraphXExecutionProvider", out string? errCMx, out bool isBuiltInCMx);
        Assert.Null(errCMx);
        Assert.False(isBuiltInCMx);
        Assert.Single(canonicalMx);
        Assert.Equal(KnownPlugins.MigraphX.Id, canonicalMx[0].Id);

        var cuda = CatalogCommand.ResolveInstallTargets("cuda", out string? errCuda, out bool isBuiltInCuda);
        Assert.Null(errCuda);
        Assert.True(isBuiltInCuda);
        Assert.Empty(cuda);

        var invalid = CatalogCommand.ResolveInstallTargets("nonexistent", out string? errInvalid, out bool isBuiltInInv);
        Assert.NotNull(errInvalid);
        Assert.False(isBuiltInInv);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ResolveInstallTargets_Qnn_ResolvesToTheArm64Plugin()
    {
        var qnn = CatalogCommand.ResolveInstallTargets("qnn", out string? err, out bool isBuiltIn);

        Assert.Null(err);
        Assert.False(isBuiltIn);
        Assert.Equal(KnownPlugins.Qnn.Id, Assert.Single(qnn).Id);
        Assert.Equal("arm64", Assert.Single(qnn).Architecture);
    }

    [Fact]
    public void CatalogInstall_Qnn_OnAnotherArchitecture_RefusesWithoutDownloading()
    {
        // On an aarch64 host the install is real, so this check only applies elsewhere.
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
        {
            return;
        }

        var (exitCode, _, stderr) = CliTestHost.Run("catalog", "install", "qnn");

        Assert.Equal(1, exitCode);
        Assert.Contains("available for linux-arm64 only", stderr, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "linml-cli-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
