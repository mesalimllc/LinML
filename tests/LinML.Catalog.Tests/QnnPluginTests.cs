using System.Formats.Tar;
using System.IO.Compression;
using Xunit;

namespace LinML.Catalog.Tests;

public class QnnPluginTests
{
    private const string Library = "libonnxruntime_providers_qnn.so";

    [Fact]
    public async Task Install_FromTarball_ExtractsNativeFilesFlatAndKeepsLicences()
    {
        using var root = new TempDir();
        byte[] tgz = BuildTarball();
        PluginPackageSpec spec = TarSpec(tgz);

        bool installed = await new PluginInstaller(new FakeWheelHandler(tgz)).InstallAsync(spec, root.Path, CancellationToken.None);

        Assert.True(installed);
        string dir = spec.InstallDirectory(root.Path);
        Assert.Equal(
            ["libQnnHtp.so", "libQnnHtpV73Skel.so", Library],
            Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["LICENSE", "Qualcomm_LICENSE.pdf", "ThirdPartyNotices.txt"],
            Directory.GetFiles(Path.Combine(dir, "licenses")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["licenses"], Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!).ToArray());
    }

    [Fact]
    public async Task Install_FromTarball_ShaMismatch_Throws_AndLeavesNothingBehind()
    {
        using var root = new TempDir();
        byte[] tgz = BuildTarball();
        PluginPackageSpec spec = TarSpec(tgz) with { WheelSha256 = new string('0', 64) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PluginInstaller(new FakeWheelHandler(tgz)).InstallAsync(spec, root.Path, CancellationToken.None));

        Assert.False(File.Exists(spec.LibraryPath(root.Path)));
    }

    [Fact]
    public async Task Install_FromTarball_WithoutThePluginLibrary_Throws()
    {
        using var root = new TempDir();
        byte[] tgz = BuildTarball(includeLibrary: false);
        PluginPackageSpec spec = TarSpec(tgz);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new PluginInstaller(new FakeWheelHandler(tgz)).InstallAsync(spec, root.Path, CancellationToken.None));

        Assert.False(Directory.Exists(spec.InstallDirectory(root.Path)));
    }

    [Fact]
    public void QnnPin_IsTheLinuxAarch64InferenceTarball()
    {
        PluginPackageSpec spec = KnownPlugins.Qnn;

        Assert.Equal("QNNExecutionProvider", spec.RegistrationName);
        Assert.EndsWith("onnxruntime-qnn-2.6.0-linux-aarch64.tgz", spec.WheelUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(Library, spec.LibraryFileName);
        // aarch64-only until PluginExecutionProvider can gate by architecture.
        Assert.DoesNotContain(spec, KnownPlugins.All);
    }

    private static PluginPackageSpec TarSpec(byte[] tgz) => KnownPlugins.Qnn with
    {
        WheelUrl = new Uri("https://example.invalid/onnxruntime-qnn-0.0.0-linux-aarch64.tgz"),
        WheelSha256 = FakeWheel.Sha256(tgz),
    };

    private static byte[] BuildTarball(bool includeLibrary = true)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            if (includeLibrary)
            {
                Add(tar, Library);
            }

            Add(tar, "libQnnHtp.so");
            Add(tar, "libQnnHtpV73Skel.so");
            Add(tar, "LICENSE");
            Add(tar, "Qualcomm_LICENSE.pdf");
            Add(tar, "ThirdPartyNotices.txt");
            Add(tar, "README.md");
            Add(tar, "docs/execution_providers/QNN-ExecutionProvider.md");
            Add(tar, "nested/libnested.so");
        }

        return buffer.ToArray();
    }

    private static void Add(TarWriter tar, string name) =>
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream("content"u8.ToArray()) });
}
