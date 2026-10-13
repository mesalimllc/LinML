using System.Diagnostics;
using Xunit;

namespace LinML.Catalog.Tests;

/// <summary>
/// <c>scripts/check-elf-floor.sh</c> fails a plugin library that needs a newer glibc or libstdc++ than the
/// hosts LinML supports. The 0.4.0-wsl2p1 TensorRT-RTX build was compiled on a host with glibc 2.43 and
/// needed <c>GLIBC_2.43</c>, so it loaded nowhere older (Ubuntu 24.04 has 2.39).
/// </summary>
public class ElfFloorTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LinML.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("LinML.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)));

    private static (int ExitCode, string Output) Gate(string library, params (string Name, string Value)[] env)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(RepoRoot(), "scripts", "check-elf-floor.sh"));
        psi.ArgumentList.Add(library);
        foreach ((string name, string value) in env)
        {
            psi.Environment[name] = value;
        }

        using var process = Process.Start(psi)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }

    /// <summary>
    /// A real ELF with real versioned imports: any dynamically linked C program needs
    /// <c>__libc_start_main</c> at some GLIBC_ version, which is what the gate is asked about. Null when the host
    /// has no C compiler or objdump (the gate cannot run there either).
    /// </summary>
    private static string? CompileFixture(TempDir dir)
    {
        if (!OperatingSystem.IsLinux() || !OnPath("gcc") || !OnPath("objdump") || !OnPath("bash"))
        {
            return null;
        }

        string source = Path.Combine(dir.Path, "fixture.c");
        string binary = Path.Combine(dir.Path, "fixture");
        File.WriteAllText(source, "int main(void) { return 0; }\n");

        var psi = new ProcessStartInfo("gcc") { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(binary);
        using var process = Process.Start(psi)!;
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? binary : null;
    }

    [Fact]
    public void Gate_PassesALibraryWithinTheFloor()
    {
        using var dir = new TempDir();
        if (CompileFixture(dir) is not { } fixture)
        {
            return;
        }

        (int exit, string output) = Gate(fixture, ("MAX_GLIBC", "99.0"));

        Assert.Equal(0, exit);
        Assert.Contains("ok   GLIBC max", output);
    }

    [Fact]
    public void Gate_FailsALibraryNeedingANewerGlibc_AndNamesTheSymbol()
    {
        using var dir = new TempDir();
        if (CompileFixture(dir) is not { } fixture)
        {
            return;
        }

        (int exit, string output) = Gate(fixture, ("MAX_GLIBC", "2.0"));

        Assert.Equal(1, exit);
        Assert.Contains("FAIL GLIBC_", output);
        Assert.Contains("__libc_start_main", output);
    }

    [Fact]
    public void Gate_ComparesVersionsNumerically_NotAsText()
    {
        // As text "2.34" sorts after "2.100", so a text comparison would reject a library needing 2.34 under a
        // floor of 2.100. Versions compare as numbers.
        using var dir = new TempDir();
        if (CompileFixture(dir) is not { } fixture)
        {
            return;
        }

        Assert.Equal(0, Gate(fixture, ("MAX_GLIBC", "2.100")).ExitCode);
    }

    [Fact]
    public void Gate_MissingLibrary_IsAUsageError_NotAPass()
    {
        if (!OperatingSystem.IsLinux() || !OnPath("bash"))
        {
            return;
        }

        (int exit, _) = Gate("/nonexistent/libplugin.so");

        Assert.Equal(2, exit);
    }

    /// <summary>
    /// The published archive must keep loading on the oldest supported host. Opt-in
    /// (LINML_CATALOG_NETWORK_TESTS=1, as the other tests that reach GitHub): downloads the pinned archive, which
    /// the installer verifies against its sha256, and gates the library inside it. A rebuild on a new host that
    /// is released without the container build fails here.
    /// </summary>
    [Fact]
    public async Task PublishedTrtRtxWsl2Pin_IsWithinTheGlibcFloor_AndCarriesItsVersion()
    {
        if (Environment.GetEnvironmentVariable("LINML_CATALOG_NETWORK_TESTS") != "1"
            || !OperatingSystem.IsLinux() || !OnPath("objdump") || !OnPath("bash"))
        {
            return;
        }

        using var root = new TempDir();
        PluginPackageSpec pin = KnownPlugins.TensorrtRtxWsl2;

        await new PluginInstaller().InstallAsync(pin, root.Path, CancellationToken.None);

        Assert.True(pin.IsVendored(root.Path), $"the published archive does not record epVersion {pin.Version} in build-info.json");
        (int exit, string output) = Gate(pin.LibraryPath(root.Path));
        Assert.True(exit == 0, output);
    }
}
