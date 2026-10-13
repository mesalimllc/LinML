using LinML.Catalog;
using LinML.Contracts;
using LinML.Runtime.Onnx;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

public class ProviderListingTests
{
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
        // Only meaningful while OpenVINO's library is not loaded into this process.
        const string openVinoLibrary = "libonnxruntime_providers_openvino_plugin.so";
        if (PluginLibraryRegistry.RegisteredLibraryFileNames.Contains(openVinoLibrary))
        {
            return;
        }

        string reason = ProviderListing.NotListedReason(ProviderId.OpenVino);

        Assert.Contains("is not loaded into this process", reason, StringComparison.Ordinal);
        Assert.Contains("linml catalog install openvino", reason, StringComparison.Ordinal);
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
