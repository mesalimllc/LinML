using System.Runtime.InteropServices;
using LinML.Catalog;
using LinML.Contracts;
using LinML.Runtime.Onnx;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

public class ProviderListingTests
{
    // OpenVINO is published for linux-x64 only. Mirrors PluginExecutionProvider.SupportsHost,
    // which is internal to LinML.Catalog and not visible to this test project.
    private static bool OpenVinoRunsOnThisHost =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [Fact]
    public void NotListedReason_BuiltInProvider_DoesNotSuggestCatalogInstall()
    {
        string reason = ProviderListing.NotListedReason(ProviderId.Cuda);

        Assert.Contains("not listed by the ORT runtime", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog install", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NotListedReason_UnloadedPlugin_SuggestsInstallWithShortId()
    {
        // Only meaningful on a host OpenVINO is built for, and while its library is not loaded into this process.
        const string openVinoLibrary = "libonnxruntime_providers_openvino_plugin.so";
        if (!OpenVinoRunsOnThisHost || PluginLibraryRegistry.RegisteredLibraryFileNames.Contains(openVinoLibrary))
        {
            return;
        }

        string reason = ProviderListing.NotListedReason(ProviderId.OpenVino);

        Assert.Contains("is not loaded into this process", reason, StringComparison.Ordinal);
        Assert.Contains("linml catalog install openvino", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NotListedReason_PluginForAnotherArchitecture_SaysItWillNotRunHere()
    {
        // OpenVINO is linux-x64 only and QNN is linux-arm64 only, so one of them is always for another host.
        (ProviderId provider, string architecture) = OpenVinoRunsOnThisHost
            ? (ProviderId.Qnn, "arm64")
            : (ProviderId.OpenVino, "x64");

        string reason = ProviderListing.NotListedReason(provider);

        Assert.Contains($"available for linux-{architecture} only", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog install", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NotListedReason_LibraryThatFailsToLoad_ReportsTheLoadFailure()
    {
        // A file under the plugin's library name that ORT cannot load: the failure is recorded for the
        // listing, so the reason names it rather than suggesting an install that cannot help.
        string dir = Path.Combine(Path.GetTempPath(), "linml-bad-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string library = Path.Combine(dir, "libonnxruntime_providers_nv_tensorrt_rtx.so");
            File.WriteAllText(library, "not an ELF file");
            Assert.ThrowsAny<Exception>(() => PluginLibraryRegistry.Register("linml.test.bad-trt-rtx", library));

            string reason = ProviderListing.NotListedReason(ProviderId.TensorrtRtx);

            Assert.Contains("installed but failed to register", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("catalog install", reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
