using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
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
    public void Catalog_ListsAllProviders_AndFindsThemById()
    {
        using var root = new TempDir();
        ExecutionProviderCatalog catalog = ExecutionProviderCatalog.Create(root.Path);

        // Built-ins
        IExecutionProvider? cpu = catalog.Find(Contracts.ProviderId.Cpu);
        Assert.NotNull(cpu);
        Assert.Equal(ProviderSource.BuiltIn, cpu.Source);
        Assert.Equal(ProviderReadyState.Ready, cpu.ReadyState);

        IExecutionProvider? cuda = catalog.Find(Contracts.ProviderId.Cuda);
        Assert.NotNull(cuda);
        Assert.Equal(ProviderSource.BuiltIn, cuda.Source);

        IExecutionProvider? trt = catalog.Find(Contracts.ProviderId.Tensorrt);
        Assert.NotNull(trt);
        Assert.Equal(ProviderSource.BuiltIn, trt.Source);

        // Plugins
        IExecutionProvider? openVino = catalog.Find(Contracts.ProviderId.OpenVino);
        Assert.NotNull(openVino);
        Assert.Equal(ProviderSource.Plugin, openVino.Source);
        Assert.Equal("1.6.1", openVino.PinnedVersion);

        IExecutionProvider? trtRtx = catalog.Find(Contracts.ProviderId.TensorrtRtx);
        Assert.NotNull(trtRtx);
        Assert.Equal(ProviderSource.Plugin, trtRtx.Source);
        Assert.Equal("0.4.0", trtRtx.PinnedVersion);

        IExecutionProvider? migraphx = catalog.Find(Contracts.ProviderId.MigraphX);
        Assert.NotNull(migraphx);
        Assert.Equal(ProviderSource.Plugin, migraphx.Source);
        Assert.Equal("2611.1", migraphx.PinnedVersion);

        IExecutionProvider? qnn = catalog.Find(Contracts.ProviderId.Qnn);
        Assert.NotNull(qnn);
        Assert.Equal(ProviderSource.Plugin, qnn.Source);
        Assert.Equal("2.6.0", qnn.PinnedVersion);

        Assert.Equal(7, catalog.FindAllProviders().Count);
    }

    [Fact]
    public async Task BuiltIn_Cpu_EnsureReady_AlwaysSucceeds()
    {
        using var root = new TempDir();
        ExecutionProviderCatalog catalog = ExecutionProviderCatalog.Create(root.Path);
        IExecutionProvider cpu = catalog.Find(Contracts.ProviderId.Cpu)!;

        ProviderEnsureResult result = await cpu.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));

        Assert.Equal(ProviderEnsureStatus.Ready, result.Status);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void AppendFromCatalog_BuiltIn_RejectsMismatchedDeviceTypes()
    {
        using var root = new TempDir();
        ExecutionProviderCatalog catalog = ExecutionProviderCatalog.Create(root.Path);
        IExecutionProvider cpu = catalog.Find(Contracts.ProviderId.Cpu)!;
        IExecutionProvider cuda = catalog.Find(Contracts.ProviderId.Cuda)!;

        using var sessionOptions = new SessionOptions();

        // CPU requested with GPU preference -> fails
        var cpuResult = sessionOptions.AppendFromCatalog(cpu, preferredDeviceTypes: [OrtHardwareDeviceType.GPU]);
        Assert.False(cpuResult.Appended);
        Assert.Contains("exposes no device of the preferred types", cpuResult.FailureReason);

        // CUDA requested with CPU preference -> fails
        var cudaResult = sessionOptions.AppendFromCatalog(cuda, preferredDeviceTypes: [OrtHardwareDeviceType.CPU]);
        Assert.False(cudaResult.Appended);
        Assert.Contains("exposes no device of the preferred types", cudaResult.FailureReason);
    }

    [Fact]
    public void BuiltIn_Cuda_AppendFromCatalog_BranchesToDedicatedCudaOptions()
    {
        var fakeRegistrar = new FakeRegistrar { AvailableProviders = [Contracts.ProviderId.Cuda.Value] };
        var cuda = new BuiltInExecutionProvider(Contracts.ProviderId.Cuda, fakeRegistrar, () => true);

        using var sessionOptions = new SessionOptions();
        var result = sessionOptions.AppendFromCatalog(cuda);
        // If CUDA native dependencies aren't present in this test environment, Appended is false with failure reason mentioning CUDA
        if (!result.Appended)
        {
            Assert.Contains("CUDA", result.FailureReason);
        }
    }

    [Theory]
    [InlineData("x64", true, Architecture.X64, true)]
    [InlineData("x64", true, Architecture.Arm64, false)]
    [InlineData("arm64", true, Architecture.Arm64, true)]
    [InlineData("arm64", true, Architecture.X64, false)]
    [InlineData("x64", false, Architecture.X64, false)]
    [InlineData("arm64", false, Architecture.Arm64, false)]
    [InlineData("riscv64", true, Architecture.X64, false)]
    public void SupportsHost_OnlyTheMatchingLinuxArchitecture(string architecture, bool linux, Architecture host, bool expected)
    {
        Assert.Equal(expected, PluginExecutionProvider.SupportsHost(architecture, linux, host));
    }

    [Fact]
    public void TensorrtRtx_IsTheWheelBuiltForThisHost()
    {
        PluginPackageSpec expected = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? KnownPlugins.TensorrtRtxLinuxArm64
            : KnownPlugins.TensorrtRtxLinuxX64;

        Assert.Same(expected, KnownPlugins.TensorrtRtx);
        Assert.Equal(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64", KnownPlugins.TensorrtRtx.Architecture);
    }

    [Theory]
    [InlineData("2.35", "2.35", true)]
    [InlineData("2.35", "2.39", true)]
    [InlineData("2.35", "2.34", false)]
    [InlineData(null, null, true)]
    [InlineData(null, "2.10", true)]
    public void GlibcSatisfies_ComparesHostAgainstTheMinimum(string? required, string? host, bool expected)
    {
        Assert.Equal(expected, PluginExecutionProvider.GlibcSatisfies(
            required is null ? null : new Version(required),
            host is null ? null : new Version(host)));
    }

    [Fact]
    public void GlibcSatisfies_NoGlibcAtAll_FailsAPlugin_WithAMinimum()
    {
        // musl or a non-glibc system: a wheel built against glibc cannot load there.
        Assert.False(PluginExecutionProvider.GlibcSatisfies(new Version(2, 35), null));
    }

    [Fact]
    public void Arm64Pin_InstallsBesideTheX64Pin_AndDeclaresItsGlibcFloor()
    {
        using var root = new TempDir();

        Assert.NotEqual(
            KnownPlugins.TensorrtRtxLinuxX64.InstallDirectory(root.Path),
            KnownPlugins.TensorrtRtxLinuxArm64.InstallDirectory(root.Path));
        Assert.Equal(new Version(2, 35), KnownPlugins.TensorrtRtxLinuxArm64.MinimumGlibc);
        Assert.Null(KnownPlugins.TensorrtRtxLinuxX64.MinimumGlibc);
    }

    [Fact]
    public void HostGlibc_IsDetectedOnAGlibcLinuxHost()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The aarch64 pin's glibc floor is judged against this probe; a null here would refuse every glibc host.
        Assert.NotNull(PluginExecutionProvider.HostGlibc);
        Assert.True(PluginExecutionProvider.GlibcSatisfies(new Version(2, 0), PluginExecutionProvider.HostGlibc));
    }
}
