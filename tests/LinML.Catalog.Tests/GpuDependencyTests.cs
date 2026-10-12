using Xunit;

namespace LinML.Catalog.Tests;

public class GpuDependencyInstallerTests
{
    private static readonly GpuDependencySpec Spec = new(
        Id: "fakedep",
        Description: "fake",
        PipPackage: "fake-pkg==1.2.3",
        IndexUrl: "https://example.invalid/simple/",
        InstallSubdirectory: "fakedep",
        SentinelLibrary: Path.Combine("pkg", "lib", "libfake.so.1"),
        PreloadLibraries: ["libfake.so.1"]);

    /// <summary>A pip stand-in that writes the sentinel into the --target directory.</summary>
    private sealed class FakePip(bool succeed = true, bool writeSentinel = true)
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<PipResult> Run(IReadOnlyList<string> args, CancellationToken ct)
        {
            Calls.Add(args);
            if (!succeed)
            {
                return Task.FromResult(new PipResult(1, "ERROR: no matching distribution"));
            }

            string target = args[args.ToList().IndexOf("--target") + 1];
            if (writeSentinel)
            {
                string sentinel = Path.Combine(target, Spec.SentinelLibrary);
                Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
                File.WriteAllText(sentinel, "lib");
            }

            return Task.FromResult(new PipResult(0, "ok"));
        }
    }

    [Fact]
    public async Task Install_RunsPinnedPipIntoStagingAndPromotes()
    {
        using var root = new TempDir();
        var pip = new FakePip();

        bool installed = await new GpuDependencyInstaller(pip.Run).InstallAsync(Spec, root.Path, CancellationToken.None);

        Assert.True(installed);
        Assert.True(Spec.IsInstalled(root.Path));
        var call = Assert.Single(pip.Calls);
        Assert.Equal("install", call[0]);
        Assert.Contains("fake-pkg==1.2.3", call);
        Assert.Equal("https://example.invalid/simple/", call[call.ToList().IndexOf("--index-url") + 1]);
        // Staged beside the final directory, never straight into it.
        Assert.NotEqual(Spec.InstallDirectory(root.Path), call[call.ToList().IndexOf("--target") + 1]);
        Assert.DoesNotContain(Directory.GetDirectories(root.Path), d => d.Contains(".partial-"));
    }

    [Fact]
    public async Task Install_WhenAlreadyPresent_DoesNotRunPip()
    {
        using var root = new TempDir();
        var pip = new FakePip();
        var installer = new GpuDependencyInstaller(pip.Run);
        await installer.InstallAsync(Spec, root.Path, CancellationToken.None);

        bool second = await installer.InstallAsync(Spec, root.Path, CancellationToken.None);

        Assert.False(second);
        Assert.Single(pip.Calls);
    }

    [Fact]
    public async Task Install_WithForce_ReinstallsOverExisting()
    {
        using var root = new TempDir();
        var pip = new FakePip();
        var installer = new GpuDependencyInstaller(pip.Run);
        await installer.InstallAsync(Spec, root.Path, CancellationToken.None);

        bool forced = await installer.InstallAsync(Spec, root.Path, CancellationToken.None, force: true);

        Assert.True(forced);
        Assert.Equal(2, pip.Calls.Count);
        Assert.True(Spec.IsInstalled(root.Path));
    }

    [Fact]
    public async Task Install_WhenPipFails_ThrowsAndLeavesNothingBehind()
    {
        using var root = new TempDir();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GpuDependencyInstaller(new FakePip(succeed: false).Run).InstallAsync(Spec, root.Path, CancellationToken.None));

        Assert.Contains("no matching distribution", ex.Message);
        Assert.False(Spec.IsInstalled(root.Path));
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }

    [Fact]
    public async Task Install_WhenSentinelMissingAfterPip_Throws()
    {
        using var root = new TempDir();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GpuDependencyInstaller(new FakePip(writeSentinel: false).Run).InstallAsync(Spec, root.Path, CancellationToken.None));

        Assert.False(Spec.IsInstalled(root.Path));
        Assert.Empty(Directory.GetFileSystemEntries(root.Path));
    }
}

public class GpuDependencyLoaderTests
{
    [Fact]
    public void GetStatus_ReportsPresenceFromSentinel()
    {
        using var root = new TempDir();
        string sentinel = KnownGpuDependencies.TensorRt.SentinelPath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "x");

        var status = GpuDependencyLoader.GetStatus(root.Path);

        var trt = Assert.Single(status, s => s.Id == "tensorrt");
        Assert.True(trt.Installed);
        Assert.Equal(Path.GetDirectoryName(sentinel), trt.LibraryDirectory);
        // Only assert the absent ones when the host has no real vendored copy to fall back to.
        var cudnn = Assert.Single(status, s => s.Id == "cudnn");
        Assert.Equal(KnownGpuDependencies.Cudnn.IsInstalled(ExecutionProviderCatalog.DefaultInstallRoot), cudnn.Installed);
    }

    [Fact]
    public void PreloadVendored_WithInvalidBinary_DoesNotThrow()
    {
        using var root = new TempDir();
        string sentinel = KnownGpuDependencies.Cudnn.SentinelPath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "not an ELF file");

        var ex = Record.Exception(() => GpuDependencyLoader.PreloadVendored(root.Path));

        Assert.Null(ex);
    }

    [Fact]
    public void Cudnn_PreloadsVendoredCublasAndNvrtcBeforeCudnn()
    {
        var order = KnownGpuDependencies.Cudnn.PreloadLibraries.Select(Path.GetFileName).ToList();

        // libcublas resolves libcublasLt by SONAME, and cuDNN's engines resolve both.
        Assert.True(order.IndexOf("libcublasLt.so.13") < order.IndexOf("libcublas.so.13"));
        Assert.True(order.IndexOf("libcublas.so.13") < order.IndexOf("libcudnn.so.9"));
        Assert.Contains("libnvrtc.so.13", order);
        Assert.All(KnownGpuDependencies.All, s =>
            Assert.All(s.PreloadLibraries, l => Assert.False(Path.IsPathRooted(l), $"{s.Id}: {l} must be relative")));
    }

    [Fact]
    public void LeanRuntime_IsScriptOnly_NotPreloadedOrInstalledBySetup()
    {
        Assert.False(KnownGpuDependencies.TensorRtLean.InstalledBySetup);
        Assert.Empty(KnownGpuDependencies.TensorRtLean.PreloadLibraries);
        Assert.True(KnownGpuDependencies.Cudnn.InstalledBySetup && KnownGpuDependencies.TensorRt.InstalledBySetup);
    }
}
