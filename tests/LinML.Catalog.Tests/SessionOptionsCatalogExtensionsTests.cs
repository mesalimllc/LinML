using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace LinML.Catalog.Tests;

/// <summary>
/// <see cref="SessionOptionsCatalogExtensions.AppendFromCatalog"/> without
/// devices. The successful append needs a real plugin and is covered by
/// <see cref="OpenVinoCatalogEndToEndTests"/>.
/// </summary>
public class SessionOptionsCatalogExtensionsTests
{
    private sealed class DevicelessProvider(ProviderReadyState state) : IExecutionProvider
    {
        public ProviderId Id { get; } = new("FakeExecutionProvider");
        public ProviderSource Source => ProviderSource.Plugin;
        public ProviderReadyState ReadyState { get; } = state;
        public string? LibraryPath => null;
        public string? PinnedVersion => "1.0.0";

        public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryRegister(out string? failureReason) => throw new NotSupportedException();

        public IReadOnlyList<OrtEpDevice> GetDevices() => [];
    }

    [Theory]
    [InlineData(ProviderReadyState.NotPresent)]
    [InlineData(ProviderReadyState.NotReady)]
    [InlineData(ProviderReadyState.Unsupported)]
    public void NotReadyProvider_IsNotAppended_AndSaysToEnsureFirst(ProviderReadyState state)
    {
        using var options = new SessionOptions();

        ProviderAppendResult result = options.AppendFromCatalog(new DevicelessProvider(state));

        Assert.False(result.Appended);
        Assert.Equal("FakeExecutionProvider", result.Provider.Value);
        Assert.Contains(state.ToString(), result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("EnsureReadyAsync", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyProvider_WithoutMatchingDevice_ReportsPreferredTypeMismatch()
    {
        using var options = new SessionOptions();

        ProviderAppendResult result = options.AppendFromCatalog(
            new DevicelessProvider(ProviderReadyState.Ready),
            [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU]);

        Assert.False(result.Appended);
        Assert.Contains("preferred types", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        using var options = new SessionOptions();
        var provider = new DevicelessProvider(ProviderReadyState.Ready);

        Assert.Throws<ArgumentNullException>(() => SessionOptionsCatalogExtensions.AppendFromCatalog(null!, provider));
        Assert.Throws<ArgumentNullException>(() => options.AppendFromCatalog(null!));
    }
}
