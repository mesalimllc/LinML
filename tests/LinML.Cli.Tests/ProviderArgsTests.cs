using LinML.Cli;
using LinML.Contracts;
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
    public void OptionsForProvider_GlobalKeys_ApplyToAllProviders()
    {
        var options = ProviderArgs.ParseEpOptions(["device_id=0"]);

        Assert.Equal("0", ProviderArgs.OptionsForProvider(options, ProviderId.Cuda)["device_id"]);
        Assert.Equal("0", ProviderArgs.OptionsForProvider(options, ProviderId.Tensorrt)["device_id"]);
        Assert.Equal("0", ProviderArgs.OptionsForProvider(options, ProviderId.Cpu)["device_id"]);
    }

    [Fact]
    public void OptionsForProvider_PrefixedKeys_OnlyMatchingProvider()
    {
        var options = ProviderArgs.ParseEpOptions(
            ["trt:trt_fp16_enable=1", "cuda:device_id=0", "global_key=x"]);

        var trt = ProviderArgs.OptionsForProvider(options, ProviderId.Tensorrt);
        Assert.Equal("1", trt["trt_fp16_enable"]);   // prefix stripped
        Assert.Equal("x", trt["global_key"]);        // global included
        Assert.False(trt.ContainsKey("device_id"));  // cuda-scoped excluded

        var cuda = ProviderArgs.OptionsForProvider(options, ProviderId.Cuda);
        Assert.Equal("0", cuda["device_id"]);
        Assert.False(cuda.ContainsKey("trt_fp16_enable"));

        var cpu = ProviderArgs.OptionsForProvider(options, ProviderId.Cpu);
        Assert.False(cpu.ContainsKey("trt_fp16_enable"));
        Assert.False(cpu.ContainsKey("device_id"));
        Assert.Equal("x", cpu["global_key"]);
    }

    [Fact]
    public void OptionsForProvider_FullEpNamePrefix_Matches()
    {
        var options = ProviderArgs.ParseEpOptions(
            ["TensorrtExecutionProvider:trt_fp16_enable=1",
             "NvTensorRTRTXExecutionProvider:enable_cuda_graph=1"]);

        var trt = ProviderArgs.OptionsForProvider(options, ProviderId.Tensorrt);
        Assert.Equal("1", trt["trt_fp16_enable"]);
        Assert.False(trt.ContainsKey("enable_cuda_graph"));

        var rtx = ProviderArgs.OptionsForProvider(options, ProviderId.TensorrtRtx);
        Assert.Equal("1", rtx["enable_cuda_graph"]);
        Assert.False(rtx.ContainsKey("trt_fp16_enable"));
    }

    [Fact]
    public void OptionsForProvider_PrefixMatching_IsCaseInsensitive()
    {
        var options = ProviderArgs.ParseEpOptions(["TRT:trt_fp16_enable=1"]);
        var trt = ProviderArgs.OptionsForProvider(options, ProviderId.Tensorrt);
        Assert.Equal("1", trt["trt_fp16_enable"]);
    }

    [Fact]
    public void OptionsForProvider_AliasAndEpName_AreEquivalent()
    {
        var byAlias = ProviderArgs.OptionsForProvider(
            ProviderArgs.ParseEpOptions(["tensorrt:trt_fp16_enable=1"]), ProviderId.Tensorrt);
        var byEpName = ProviderArgs.OptionsForProvider(
            ProviderArgs.ParseEpOptions(["TensorrtExecutionProvider:trt_fp16_enable=1"]), ProviderId.Tensorrt);

        Assert.Equal(byAlias, byEpName);
    }

    [Fact]
    public void EnsureEpLibrariesRegistered_MissingFile_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ProviderArgs.EnsureEpLibrariesRegistered(
                new Dictionary<string, string> { ["bogus"] = "/nonexistent/libbogus.so" }));
    }

    [Fact]
    public void GetShortName_ReturnsExpectedAliases()
    {
        Assert.Equal("cpu", ProviderArgs.GetShortName(ProviderId.Cpu));
        Assert.Equal("cuda", ProviderArgs.GetShortName(ProviderId.Cuda));
        Assert.Equal("tensorrt", ProviderArgs.GetShortName(ProviderId.Tensorrt));
        Assert.Equal("trt-rtx", ProviderArgs.GetShortName(ProviderId.TensorrtRtx));
        Assert.Equal("openvino", ProviderArgs.GetShortName(ProviderId.OpenVino));
        Assert.Equal("migraphx", ProviderArgs.GetShortName(ProviderId.MigraphX));
        Assert.Equal("qnn", ProviderArgs.GetShortName(ProviderId.Qnn));
        Assert.Equal("CustomEP", ProviderArgs.GetShortName(new ProviderId("CustomEP")));
    }

    [Fact]
    public void ResolveEpLibraries_BuiltInProvider_DoesNotAutoResolve()
    {
        var result = ProviderArgs.ResolveEpLibraries([], ProviderId.Cpu);
        Assert.Empty(result);

        result = ProviderArgs.ResolveEpLibraries([], ProviderId.Cuda);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveEpLibraries_ExplicitArg_OverridesAutoResolution()
    {
        var result = ProviderArgs.ResolveEpLibraries(
            ["NvTensorRTRTXExecutionProvider=/custom/override.so"],
            ProviderId.TensorrtRtx);

        Assert.Equal("/custom/override.so", result["NvTensorRTRTXExecutionProvider"]);
    }

    [Fact]
    public void ResolveEpLibraries_AutoResolvesFromCustomCatalogCache()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "linml-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fakeTrtDir = Path.Combine(tempRoot, "ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx");
            Directory.CreateDirectory(fakeTrtDir);
            var fakeSo = Path.Combine(fakeTrtDir, "libonnxruntime_providers_nv_tensorrt_rtx.so");
            File.WriteAllText(fakeSo, "fake-so-content");

            var result = ProviderArgs.ResolveEpLibraries([], ProviderId.TensorrtRtx, customRoot: tempRoot);

            Assert.True(result.ContainsKey("NvTensorRTRTXExecutionProvider"));
            Assert.Equal(fakeSo, result["NvTensorRTRTXExecutionProvider"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveEpLibraries_TrtRtxCustomEp_OverridesVendoredWheel()
    {
        // TRT_RTX_CUSTOM_EP wins over the catalog cache so a locally built WSL2
        // fallback EP can be selected without uninstalling the wheel.
        var tempDir = Path.Combine(Path.GetTempPath(), "linml-test-" + Guid.NewGuid().ToString("N"));
        var envVar = ProviderArgs.TrtRtxCustomEpEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            var vendoredDir = Path.Combine(tempDir, "ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx");
            Directory.CreateDirectory(vendoredDir);
            File.WriteAllText(Path.Combine(vendoredDir, "libonnxruntime_providers_nv_tensorrt_rtx.so"), "wheel");

            var customSo = Path.Combine(tempDir, "custom", "libonnxruntime_providers_nv_tensorrt_rtx.so");
            Directory.CreateDirectory(Path.GetDirectoryName(customSo)!);
            File.WriteAllText(customSo, "custom");
            Environment.SetEnvironmentVariable(envVar, customSo);

            var result = ProviderArgs.ResolveEpLibraries([], ProviderId.TensorrtRtx, customRoot: tempDir);

            Assert.Equal(customSo, result["NvTensorRTRTXExecutionProvider"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveEpLibraries_TrtRtxCustomEp_NonexistentPath_FallsBackToWheel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "linml-test-" + Guid.NewGuid().ToString("N"));
        var envVar = ProviderArgs.TrtRtxCustomEpEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            var vendoredDir = Path.Combine(tempDir, "ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx");
            Directory.CreateDirectory(vendoredDir);
            var vendoredSo = Path.Combine(vendoredDir, "libonnxruntime_providers_nv_tensorrt_rtx.so");
            File.WriteAllText(vendoredSo, "wheel");

            Environment.SetEnvironmentVariable(envVar, Path.Combine(tempDir, "missing.so"));

            var result = ProviderArgs.ResolveEpLibraries([], ProviderId.TensorrtRtx, customRoot: tempDir);

            Assert.Equal(vendoredSo, result["NvTensorRTRTXExecutionProvider"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveEpLibraries_TrtRtxCustomEp_DoesNotAffectOtherProviders()
    {
        var envVar = ProviderArgs.TrtRtxCustomEpEnvVar;
        var original = Environment.GetEnvironmentVariable(envVar);
        try
        {
            Environment.SetEnvironmentVariable(envVar, "/does/not/matter.so");

            Assert.Empty(ProviderArgs.ResolveEpLibraries([], ProviderId.Cuda));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, original);
        }
    }
}
