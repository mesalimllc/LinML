using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace LinML.Catalog;

/// <summary>Result of one pip invocation.</summary>
internal sealed record PipResult(int ExitCode, string Output);

/// <summary>
/// Vendors a pinned GPU runtime dependency with <c>pip install --target</c> into a
/// staging directory that is renamed into place only when the sentinel library is
/// present. Port of the per-dependency steps in <c>scripts/setup-gpu-deps.sh</c>.
/// </summary>
/// <remarks>
/// Unlike plugin wheels these are large NVIDIA wheels with transitive dependencies,
/// so pip does the resolution; the pin is on the package version, not a file hash.
/// </remarks>
internal sealed class GpuDependencyInstaller(
    Func<IReadOnlyList<string>, CancellationToken, Task<PipResult>>? pipRunner = null)
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<PipResult>> _pip =
        pipRunner ?? RunPipAsync;

    /// <returns>True if this call vendored it; false if it was already present.</returns>
    /// <exception cref="InvalidOperationException">pip failed or did not produce the expected library.</exception>
    public async Task<bool> InstallAsync(
        GpuDependencySpec spec,
        string installRoot,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!force && spec.IsInstalled(installRoot))
        {
            return false;
        }

        string finalDir = spec.InstallDirectory(installRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);

        string stage = $"{finalDir}.partial-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(stage);

            List<string> args = ["install", "--target", stage, "--no-input", "--disable-pip-version-check"];
            if (spec.IndexUrl is not null)
            {
                args.AddRange(["--index-url", spec.IndexUrl]);
            }

            args.Add(spec.PipPackage);

            PipResult result = await _pip(args, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pip install {spec.PipPackage} failed (exit {result.ExitCode}): {Tail(result.Output)}");
            }

            if (!File.Exists(Path.Combine(stage, spec.SentinelLibrary)))
            {
                throw new InvalidOperationException(
                    $"pip install {spec.PipPackage} did not produce {spec.SentinelLibrary}.");
            }

            return PluginInstaller.Promote(stage, finalDir, spec.SentinelLibrary, force);
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Tail(string output)
    {
        string trimmed = output.Trim();
        const int max = 600;
        return trimmed.Length <= max ? trimmed : "..." + trimmed[^max..];
    }

    /// <summary>Runs <c>pip3</c>, falling back to <c>python3 -m pip</c>, with no shell.</summary>
    private static async Task<PipResult> RunPipAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync("pip3", args, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            // pip3 not on PATH; try the module form below.
        }

        try
        {
            return await RunAsync("python3", ["-m", "pip", .. args], cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Neither 'pip3' nor 'python3 -m pip' is available; install python3-pip to vendor GPU dependencies.", ex);
        }
    }

    private static async Task<PipResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new Win32Exception($"Could not start {fileName}.");
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        // Flush the async readers.
        process.WaitForExit();
        lock (output)
        {
            return new PipResult(process.ExitCode, output.ToString());
        }
    }
}
