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
        Assert.Equal(6, doc.RootElement.GetArrayLength());

        var ids = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Id").GetString())
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

    [Fact]
    public void ResolveInstallTargets_MapsAliasesCorrectly()
    {
        var all = CatalogCommand.ResolveInstallTargets("all", out string? errAll, out bool isBuiltInAll);
        Assert.Null(errAll);
        Assert.False(isBuiltInAll);
        Assert.Equal(3, all.Count);

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

        var cuda = CatalogCommand.ResolveInstallTargets("cuda", out string? errCuda, out bool isBuiltInCuda);
        Assert.Null(errCuda);
        Assert.True(isBuiltInCuda);
        Assert.Empty(cuda);

        var invalid = CatalogCommand.ResolveInstallTargets("nonexistent", out string? errInvalid, out bool isBuiltInInv);
        Assert.NotNull(errInvalid);
        Assert.False(isBuiltInInv);
        Assert.Empty(invalid);
    }
}
