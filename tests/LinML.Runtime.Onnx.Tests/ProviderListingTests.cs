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
    public void NotListedReason_UnregisteredPlugin_SaysNotRegistered()
    {
        // OpenVINO registers under "linml.openvino" (KnownPlugins), not its EP name. Only
        // meaningful while the plugin is not loaded into this process.
        const string openVinoRegistration = "linml.openvino";
        if (PluginEpRegistry.RegisteredNames.Contains(openVinoRegistration))
        {
            return;
        }

        string reason = ProviderListing.NotListedReason(ProviderId.OpenVino);

        Assert.Contains("is not registered", reason, StringComparison.Ordinal);
        Assert.Contains("catalog install", reason, StringComparison.Ordinal);
    }
}
