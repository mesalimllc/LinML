using LinML.Catalog;
using LinML.Contracts;
using LinML.Runtime.Onnx;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>
/// The CLI's <see cref="PluginEpRegistry"/> and the catalog share one
/// process-wide <see cref="PluginLibraryRegistry"/>.
/// </summary>
public class SharedPluginRegistryTests
{
    [Fact]
    public void PluginEpRegistry_MissingFile_IsReportedBySharedRegistry()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => PluginEpRegistry.EnsureRegistered(
            new Dictionary<string, string> { ["shared-missing"] = "/nonexistent/libshared.so" }));

        Assert.Contains("shared-missing", ex.Message, StringComparison.Ordinal);
        Assert.False(PluginLibraryRegistry.IsRegistered("shared-missing"));
    }

    /// <summary>
    /// Opt-in: set LINML_OPENVINO_EP_LIB to a vendored
    /// libonnxruntime_providers_openvino_plugin.so (onnxruntime-ep-openvino 1.6.1).
    /// </summary>
    [Fact]
    public async Task CliRegistration_IsVisibleToCatalog_WhichReusesIt()
    {
        string? lib = Environment.GetEnvironmentVariable("LINML_OPENVINO_EP_LIB");
        if (string.IsNullOrEmpty(lib) || !File.Exists(lib))
        {
            return;
        }

        PluginEpRegistry.EnsureRegistered(new Dictionary<string, string> { ["cli.openvino"] = lib });
        Assert.True(PluginLibraryRegistry.IsRegistered("cli.openvino"));
        Assert.Contains("cli.openvino", PluginEpRegistry.RegisteredNames);

        // A catalog with an empty root has no vendored copy of its own: it can
        // only be Ready by reusing the CLI's registration.
        string root = Path.Combine(Path.GetTempPath(), "linml-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            IExecutionProvider openVino = ExecutionProviderCatalog.Create(root).Find(ProviderId.OpenVino)!;
            Assert.Equal(ProviderReadyState.Ready, openVino.ReadyState);

            ProviderEnsureResult ensured = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));
            Assert.Equal(ProviderEnsureStatus.Ready, ensured.Status);
            Assert.False(PluginLibraryRegistry.IsRegistered("linml.openvino"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
