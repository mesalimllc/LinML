using Xunit;

namespace LinML.Catalog.Tests;

public class PluginInstallerTests
{
    [Fact]
    public async Task Install_ExtractsTopLevelNativeFilesFlat()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);

        bool installed = await new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(spec, root.Path, CancellationToken.None);

        Assert.True(installed);
        string dir = spec.InstallDirectory(root.Path);
        Assert.Equal(
            ["cache.json", "libfake_plugin.so", "libruntime.so.2621"],
            Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(Directory.GetDirectories(dir));
    }

    [Fact]
    public async Task Install_WhenAlreadyPresent_DoesNotDownload()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        var handler = new FakeWheelHandler(wheel);
        var installer = new PluginInstaller(handler);
        await installer.InstallAsync(spec, root.Path, CancellationToken.None);

        bool second = await installer.InstallAsync(spec, root.Path, CancellationToken.None);

        Assert.False(second);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Install_ShaMismatch_Throws_AndLeavesNothingBehind()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel, sha256: new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(spec, root.Path, CancellationToken.None));

        string parent = Path.GetDirectoryName(spec.InstallDirectory(root.Path))!;
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    [Fact]
    public async Task Install_WheelWithoutLibrary_Throws_AndInstallsNothing()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build(includeLibrary: false);
        PluginPackageSpec spec = FakeWheel.Spec(wheel);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(spec, root.Path, CancellationToken.None));

        Assert.False(Directory.Exists(spec.InstallDirectory(root.Path)));
    }

    [Fact]
    public void Promote_WhenAnotherInstallerWon_KeepsTheExistingInstall()
    {
        using var root = new TempDir();
        string finalDir = Path.Combine(root.Path, "plugin");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, FakeWheel.Library), "winner");
        string stage = finalDir + ".partial-test";
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, FakeWheel.Library), "loser");

        bool promoted = PluginInstaller.Promote(stage, finalDir, FakeWheel.Library);

        Assert.False(promoted);
        Assert.Equal("winner", File.ReadAllText(Path.Combine(finalDir, FakeWheel.Library)));
    }

    [Fact]
    public async Task Install_ReplacesLeftoverDirectoryWithoutLibrary()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        string dir = spec.InstallDirectory(root.Path);
        Directory.CreateDirectory(Path.Combine(dir, "fake_ep"));
        File.WriteAllText(Path.Combine(dir, "stale.txt"), "old nested install");

        bool installed = await new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(spec, root.Path, CancellationToken.None);

        Assert.True(installed);
        Assert.True(File.Exists(spec.LibraryPath(root.Path)));
        Assert.False(File.Exists(Path.Combine(dir, "stale.txt")));
        Assert.Empty(Directory.GetDirectories(dir));
    }

    [Fact]
    public void CreateRequiredSymlinks_CreatesRelativeSonameLinksOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var root = new TempDir();
        string targetFile = Path.Combine(root.Path, "libtensorrt_rtx.so.1.6.1");
        File.WriteAllText(targetFile, "dummy");

        PluginInstaller.CreateRequiredSymlinks(root.Path);

        string linkFile = Path.Combine(root.Path, "libtensorrt_rtx.so.1");
        Assert.True(File.Exists(linkFile));
        var linkTarget = File.ResolveLinkTarget(linkFile, returnFinalTarget: false);
        Assert.NotNull(linkTarget);
        Assert.Equal("libtensorrt_rtx.so.1.6.1", linkTarget.Name);
    }
}
