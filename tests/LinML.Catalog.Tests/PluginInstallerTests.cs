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
    public async Task Install_WhenAlreadyPresent_WithForce_DownloadsAndReplaces()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        var handler = new FakeWheelHandler(wheel);
        var installer = new PluginInstaller(handler);
        await installer.InstallAsync(spec, root.Path, CancellationToken.None);

        bool forced = await installer.InstallAsync(spec, root.Path, CancellationToken.None, force: true);

        Assert.True(forced);
        Assert.Equal(2, handler.Requests);
        Assert.True(File.Exists(spec.LibraryPath(root.Path)));
    }

    [Fact]
    public async Task Install_WhenAlreadyPresent_WithForceFailingSha_PreservesExistingInstall()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build();
        PluginPackageSpec spec = FakeWheel.Spec(wheel);
        var handler = new FakeWheelHandler(wheel);
        var installer = new PluginInstaller(handler);
        await installer.InstallAsync(spec, root.Path, CancellationToken.None);
        Assert.True(File.Exists(spec.LibraryPath(root.Path)));

        // Create a spec with mismatched hash to simulate download failure during force reinstall
        PluginPackageSpec badSpec = FakeWheel.Spec(wheel, sha256: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.InstallAsync(badSpec, root.Path, CancellationToken.None, force: true));

        // The working installation must NOT be deleted or destroyed by the failed re-install attempt
        Assert.True(File.Exists(spec.LibraryPath(root.Path)));
    }

    // A package that ships a build-info.json (the patched WSL2 TensorRT-RTX build) records the version it
    // was built as. Presence of the library alone must not count as installed: 0.4.0-wsl2p1 and p2 share
    // one install directory, and p1 does not load on Ubuntu 24.04.
    private static PluginPackageSpec Versioned(byte[] wheel, string version) =>
        FakeWheel.Spec(wheel) with { Version = version, VersionManifestFile = "build-info.json" };

    private static void VendorByHand(PluginPackageSpec spec, string root, string? buildInfoVersion)
    {
        Directory.CreateDirectory(spec.InstallDirectory(root));
        File.WriteAllText(spec.LibraryPath(root), "previous-build");
        if (buildInfoVersion is not null)
        {
            File.WriteAllText(Path.Combine(spec.InstallDirectory(root), "build-info.json"), FakeWheel.BuildInfo(buildInfoVersion));
        }
    }

    [Fact]
    public async Task Install_VersionedPackage_ReplacesAnInstallOfAnotherVersion()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build(buildInfoVersion: "2.0.0");
        PluginPackageSpec pinned = Versioned(wheel, "2.0.0");
        VendorByHand(pinned, root.Path, buildInfoVersion: "1.0.0");
        var handler = new FakeWheelHandler(wheel);

        bool installed = await new PluginInstaller(handler).InstallAsync(pinned, root.Path, CancellationToken.None);

        Assert.True(installed);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("plugin", File.ReadAllText(pinned.LibraryPath(root.Path)));
        Assert.Equal("2.0.0", pinned.VendoredVersion(pinned.LibraryPath(root.Path)));
        // No staging or moved-aside directories are left beside it.
        Assert.Equal(
            [pinned.InstallDirectory(root.Path)],
            Directory.GetDirectories(Path.GetDirectoryName(pinned.InstallDirectory(root.Path))!));
    }

    [Fact]
    public async Task Install_VersionedPackage_ReplacesAnInstallWithNoManifest()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build(buildInfoVersion: "2.0.0");
        PluginPackageSpec pinned = Versioned(wheel, "2.0.0");
        VendorByHand(pinned, root.Path, buildInfoVersion: null);

        bool installed = await new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(pinned, root.Path, CancellationToken.None);

        Assert.True(installed);
        Assert.Equal("plugin", File.ReadAllText(pinned.LibraryPath(root.Path)));
    }

    [Fact]
    public async Task Install_VersionedPackage_KeepsAnInstallOfTheSameVersion()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build(buildInfoVersion: "2.0.0");
        PluginPackageSpec pinned = Versioned(wheel, "2.0.0");
        var handler = new FakeWheelHandler(wheel);
        var installer = new PluginInstaller(handler);
        await installer.InstallAsync(pinned, root.Path, CancellationToken.None);

        bool second = await installer.InstallAsync(pinned, root.Path, CancellationToken.None);

        Assert.False(second);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Install_VersionedPackage_FailedDownloadLeavesTheStaleInstallInPlace()
    {
        using var root = new TempDir();
        byte[] wheel = FakeWheel.Build(buildInfoVersion: "2.0.0");
        PluginPackageSpec pinned = Versioned(wheel, "2.0.0") with { WheelSha256 = new string('0', 64) };
        VendorByHand(pinned, root.Path, buildInfoVersion: "1.0.0");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PluginInstaller(new FakeWheelHandler(wheel)).InstallAsync(pinned, root.Path, CancellationToken.None));

        Assert.Equal("previous-build", File.ReadAllText(pinned.LibraryPath(root.Path)));
    }

    [Fact]
    public void IsVendored_UnversionedPackage_OnlyNeedsTheLibrary()
    {
        using var root = new TempDir();
        PluginPackageSpec spec = FakeWheel.Spec(FakeWheel.Build());
        Assert.False(spec.IsVendored(root.Path));

        VendorByHand(spec, root.Path, buildInfoVersion: "anything");

        Assert.True(spec.IsVendored(root.Path));
    }

    [Theory]
    [InlineData("2.0.0", true)]
    [InlineData("1.0.0", false)]
    [InlineData(null, false)]
    public void IsVendored_VersionedPackage_RequiresTheManifestToMatchThePin(string? vendoredVersion, bool expected)
    {
        using var root = new TempDir();
        PluginPackageSpec pinned = Versioned(FakeWheel.Build(), "2.0.0");
        VendorByHand(pinned, root.Path, vendoredVersion);

        Assert.Equal(expected, pinned.IsVendored(root.Path));
    }

    [Fact]
    public void IsVendored_VersionedPackage_UnreadableManifestIsStale()
    {
        using var root = new TempDir();
        PluginPackageSpec pinned = Versioned(FakeWheel.Build(), "2.0.0");
        VendorByHand(pinned, root.Path, buildInfoVersion: null);
        File.WriteAllText(Path.Combine(pinned.InstallDirectory(root.Path), "build-info.json"), "{ not json");

        Assert.False(pinned.IsVendored(root.Path));
        Assert.Null(pinned.VendoredVersion(pinned.LibraryPath(root.Path)));
    }

    [Fact]
    public void IsVendored_PatchedWsl2Build_IsStaleOnAnInstallOfWsl2p1()
    {
        // What a host that installed catalog 0.4.2-0.5.0 has on disk.
        using var root = new TempDir();
        PluginPackageSpec pinned = KnownPlugins.TensorrtRtxWsl2;
        VendorByHand(pinned, root.Path, buildInfoVersion: "0.4.0-wsl2p1");

        Assert.NotEqual("0.4.0-wsl2p1", pinned.Version);
        Assert.False(pinned.IsVendored(root.Path));

        File.WriteAllText(Path.Combine(pinned.InstallDirectory(root.Path), "build-info.json"), FakeWheel.BuildInfo(pinned.Version));

        Assert.True(pinned.IsVendored(root.Path));
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
    public void Promote_ForcedReplacement_WhenTheStagedMoveFails_PutsTheOldBuildBack()
    {
        // An upgrade must never leave a host with neither build: the old directory is only discarded once
        // the staged one is in place.
        using var root = new TempDir();
        string finalDir = Path.Combine(root.Path, "install");
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, FakeWheel.Library), "old-build");

        Assert.ThrowsAny<IOException>(() =>
            PluginInstaller.Promote(Path.Combine(root.Path, "no-such-stage"), finalDir, FakeWheel.Library, force: true));

        Assert.Equal("old-build", File.ReadAllText(Path.Combine(finalDir, FakeWheel.Library)));
        Assert.Equal([finalDir], Directory.GetDirectories(root.Path));
    }

    [Fact]
    public void Promote_ForcedReplacement_LeavesOnlyTheNewBuild()
    {
        using var root = new TempDir();
        string finalDir = Path.Combine(root.Path, "install");
        string stage = Path.Combine(root.Path, "stage");
        Directory.CreateDirectory(finalDir);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(finalDir, FakeWheel.Library), "old-build");
        File.WriteAllText(Path.Combine(stage, FakeWheel.Library), "new-build");

        bool promoted = PluginInstaller.Promote(stage, finalDir, FakeWheel.Library, force: true);

        Assert.True(promoted);
        Assert.Equal("new-build", File.ReadAllText(Path.Combine(finalDir, FakeWheel.Library)));
        Assert.Equal([finalDir], Directory.GetDirectories(root.Path));
    }

    [Fact]
    public void IsCurrent_ALibraryThatIsNotThere_IsNotCurrent_WhateverTheManifestSays()
    {
        using var root = new TempDir();
        PluginPackageSpec pinned = Versioned(FakeWheel.Build(), "2.0.0");
        Directory.CreateDirectory(pinned.InstallDirectory(root.Path));
        File.WriteAllText(Path.Combine(pinned.InstallDirectory(root.Path), "build-info.json"), FakeWheel.BuildInfo("2.0.0"));

        Assert.False(pinned.IsCurrent(pinned.LibraryPath(root.Path)));
        Assert.False(pinned.IsVendored(root.Path));
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

    [Fact]
    public async Task Install_WhenAlreadyPresent_RepairsMissingSymlinks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var root = new TempDir();
        PluginPackageSpec spec = KnownPlugins.TensorrtRtx;
        string installDir = spec.InstallDirectory(root.Path);
        Directory.CreateDirectory(installDir);
        File.WriteAllText(spec.LibraryPath(root.Path), "dummy lib");
        File.WriteAllText(Path.Combine(installDir, "libtensorrt_rtx.so.1.6.1"), "dummy soname");

        var installer = new PluginInstaller();
        bool installed = await installer.InstallAsync(spec, root.Path, CancellationToken.None);

        Assert.False(installed);
        string linkFile = Path.Combine(installDir, "libtensorrt_rtx.so.1");
        Assert.True(File.Exists(linkFile));
    }

    [Fact]
    public void LoadBundledLibraries_ReturnsEmptyOnNonExistentDir()
    {
        var handles = PluginInstaller.LoadBundledLibraries("/non/existent/path", "libtest.so");
        Assert.Empty(handles);
    }

    [Fact]
    public void ExtractFlat_KeepsLicensesDirectory_ButNoOtherNestedPath()
    {
        using var temp = new TempDir();
        string wheel = Path.Combine(temp.Path, "p.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(wheel, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var (name, body) in new[]
            {
                ("pkg/libplugin.so", "so"),
                ("pkg/PATCH-NOTICE.md", "notice"),
                ("pkg/licenses/LICENSE", "apache"),
                ("pkg/licenses/ThirdPartyNotices.txt", "third"),
                ("pkg/licenses/deeper/EVIL", "x"),
                ("pkg/other/libnested.so", "nested"),
            })
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(body);
            }
        }

        string stage = Path.Combine(temp.Path, "stage");
        Directory.CreateDirectory(stage);

        PluginInstaller.ExtractFlat(wheel, "pkg/", stage);

        Assert.Equal(
            ["PATCH-NOTICE.md", "libplugin.so", "licenses/LICENSE", "licenses/ThirdPartyNotices.txt"],
            Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(stage, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}
