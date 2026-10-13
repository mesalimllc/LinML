using Xunit;

namespace LinML.Catalog.Tests;

public class CudartSymlinkTests
{
    [Fact]
    public void CreateRequiredSymlinks_LinksWhicheverCudartPatchVersionTheWheelShips()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The aarch64 wheel ships libcudart.so.13.4.46; the x86_64 wheel ships .36. The SONAME link
        // must follow whichever is present, or registration paths that rely on it fail to load.
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, "libcudart.so.13.4.46"), "not-a-real-elf");

        PluginInstaller.CreateRequiredSymlinks(root.Path);

        Assert.Equal("libcudart.so.13.4.46", new FileInfo(Path.Combine(root.Path, "libcudart.so.13")).LinkTarget);
    }
}
