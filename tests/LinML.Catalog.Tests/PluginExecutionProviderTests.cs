using Xunit;

namespace LinML.Catalog.Tests;

public class PluginExecutionProviderTests
{
    private static PluginExecutionProvider Provider(
        string root,
        PluginPackageSpec spec,
        FakeRegistrar registrar,
        FakeWheelHandler? handler = null,
        bool supported = true) =>
        new(spec, root, new PluginInstaller(handler), registrar, () => supported);

    [Fact]
    public async Task NotVendored_DownloadsDisallowed_ReportsDownloadDisallowed()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        var handler = new FakeWheelHandler(wheel);
        PluginExecutionProvider ep = Provider(root.Path, FakeWheel.Spec(wheel), new FakeRegistrar(), handler);

        Assert.Equal(ProviderReadyState.NotPresent, ep.ReadyState);
        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Equal(ProviderEnsureStatus.DownloadDisallowed, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task UnsupportedPlatform_ReportsNotAvailable()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginExecutionProvider ep = Provider(root.Path, FakeWheel.Spec(wheel), new FakeRegistrar(), supported: false);

        Assert.Equal(ProviderReadyState.Unsupported, ep.ReadyState);
        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.NotAvailable, result.Status);
    }

    [Fact]
    public async Task OrtOlderThanMinimum_ReportsAbiIncompatible_WithoutDownloading()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        var handler = new FakeWheelHandler(wheel);
        var registrar = new FakeRegistrar { OrtVersion = new Version(1, 22, 0) };
        PluginExecutionProvider ep = Provider(root.Path, FakeWheel.Spec(wheel), registrar, handler);

        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.AbiIncompatible, result.Status);
        Assert.Contains("1.22", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Downloaded_Registered_ButNoDevices_ReportsNoDevices()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        var registrar = new FakeRegistrar();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        PluginExecutionProvider ep = Provider(root.Path, spec, registrar, new FakeWheelHandler(wheel));

        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.NoDevices, result.Status);
        Assert.True(registrar.IsRegistered(spec.RegistrationName));
        Assert.Equal(spec.LibraryPath(root.Path), ep.LibraryPath);
        Assert.Equal(ProviderReadyState.NotReady, ep.ReadyState);
    }

    [Fact]
    public async Task RegistrationFailure_ReportsFailed()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        var registrar = new FakeRegistrar { RegisterThrows = new InvalidOperationException("boom") };
        PluginExecutionProvider ep = Provider(root.Path, FakeWheel.Spec(wheel), registrar, new FakeWheelHandler(wheel));

        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("boom", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShaMismatch_ReportsFailed()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginExecutionProvider ep = Provider(
            root.Path, FakeWheel.Spec(wheel, sha256: new string('0', 64)), new FakeRegistrar(), new FakeWheelHandler(wheel));

        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("sha256 mismatch", result.Detail, StringComparison.Ordinal);
        Assert.Equal(ProviderReadyState.NotPresent, ep.ReadyState);
    }

    [Fact]
    public async Task DownloadTimeout_ReportsFailed()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        var ep = new PluginExecutionProvider(
            FakeWheel.Spec(wheel), root.Path, new PluginInstaller(new TimingOutHandler()), new FakeRegistrar(), () => true);

        ProviderEnsureResult result = await ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

        Assert.Equal(ProviderEnsureStatus.Failed, result.Status);
        Assert.Contains("timed out", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginExecutionProvider ep = Provider(root.Path, FakeWheel.Spec(wheel), new FakeRegistrar(), new FakeWheelHandler(wheel));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ep.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true), cts.Token));
    }

    [Fact]
    public void TryRegister_RealOrt_RejectsNonLibraryFile()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        Directory.CreateDirectory(spec.InstallDirectory(root.Path));
        File.WriteAllText(spec.LibraryPath(root.Path), "not an ELF file");
        var ep = new PluginExecutionProvider(spec, root.Path, new PluginInstaller(), OrtRegistrar.Instance, () => true);

        bool registered = ep.TryRegister(out string? failure);

        Assert.False(registered);
        Assert.Contains("Registering", failure, StringComparison.Ordinal);
        Assert.Empty(ep.GetDevices());
    }

    [Fact]
    public void Catalog_ListsOpenVino_AndFindsItById()
    {
        using var root = new TempDir();
        ExecutionProviderCatalog catalog = ExecutionProviderCatalog.Create(root.Path);

        IExecutionProvider? openVino = catalog.Find(Contracts.ProviderId.OpenVino);

        Assert.NotNull(openVino);
        Assert.Single(catalog.FindAllProviders());
        Assert.Equal(ProviderSource.Plugin, openVino.Source);
        Assert.Equal("1.6.1", openVino.PinnedVersion);
        Assert.Null(openVino.LibraryPath);
        Assert.Null(catalog.Find(Contracts.ProviderId.Cuda));
    }
}
