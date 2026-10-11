using LinML.Cli;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Tests for ProviderArgs: provider aliases, EP library and EP option parsing.
/// </summary>
public class ProviderArgsTests
{
    [Theory]
    [InlineData("cpu", "CPUExecutionProvider")]
    [InlineData("cuda", "CUDAExecutionProvider")]
    [InlineData("trt", "TensorrtExecutionProvider")]
    [InlineData("tensorrt", "TensorrtExecutionProvider")]
    [InlineData("trt-rtx", "NvTensorRTRTXExecutionProvider")]
    [InlineData("tensorrt-rtx", "NvTensorRTRTXExecutionProvider")]
    [InlineData("nvtensort-rtx", "NvTensorRTRTXExecutionProvider")]
    [InlineData("openvino", "OpenVINOExecutionProvider")]
    [InlineData("ov", "OpenVINOExecutionProvider")]
    [InlineData("qnn", "QNNExecutionProvider")]
    [InlineData("migraphx", "MIGraphXExecutionProvider")]
    [InlineData("amd", "MIGraphXExecutionProvider")]
    [InlineData("CUDAExecutionProvider", "CUDAExecutionProvider")] // raw EP name
    [InlineData(" NvTensorRTRTXExecutionProvider ", "NvTensorRTRTXExecutionProvider")] // raw, trimmed
    public void ParseProvider_KnownAliases_MapToEpNames(string input, string expected)
    {
        Assert.Equal(expected, ProviderArgs.ParseProvider(input)?.Value);
    }

    [Fact]
    public void ParseProvider_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(ProviderArgs.ParseProvider(null));
        Assert.Null(ProviderArgs.ParseProvider(""));
        Assert.Null(ProviderArgs.ParseProvider("   "));
    }

    [Fact]
    public void ParseProvider_UnknownAlias_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProviderArgs.ParseProvider("tpu"));
    }

    [Fact]
    public void ParseEpLibraries_ArgsAndEnvVar_Merged()
    {
        const string envVar = ProviderArgs.EpLibrariesEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "fromenv=/libs/env.so");
            var result = ProviderArgs.ParseEpLibraries(["fromcli=/libs/cli.so"]);

            Assert.Equal("/libs/env.so", result["fromenv"]);
            Assert.Equal("/libs/cli.so", result["fromcli"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void ParseEpLibraries_MultipleEnvEntries_SemicolonSeparated()
    {
        const string envVar = ProviderArgs.EpLibrariesEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "a=/x/a.so;b=/y/b.so");
            var result = ProviderArgs.ParseEpLibraries([]);

            Assert.Equal(2, result.Count);
            Assert.Equal("/x/a.so", result["a"]);
            Assert.Equal("/y/b.so", result["b"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }

    [Fact]
    public void ParseEpLibraries_BadFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProviderArgs.ParseEpLibraries(["no-equals-sign"]));
    }

    [Fact]
    public void ParseEpOptions_KeyValuePairs_Parsed()
    {
        var result = ProviderArgs.ParseEpOptions(["trt_fp16_enable=1", "device_type=CPU"]);

        Assert.Equal("1", result["trt_fp16_enable"]);
        Assert.Equal("CPU", result["device_type"]);
    }

    [Fact]
    public void ParseEpOptions_ValueMayContainEquals()
    {
        var result = ProviderArgs.ParseEpOptions(["load_config={\"CPU\":{}}\"="]);
        Assert.True(result.ContainsKey("load_config"));
    }

    [Fact]
    public void ParseEpOptions_BadFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProviderArgs.ParseEpOptions(["no-equals"]));
        Assert.Throws<ArgumentException>(() => ProviderArgs.ParseEpOptions(["=novalue"]));
    }

    [Fact]
    public void EnsureEpLibrariesRegistered_MissingFile_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ProviderArgs.EnsureEpLibrariesRegistered(
                new Dictionary<string, string> { ["bogus"] = "/nonexistent/libbogus.so" }));
    }
}
