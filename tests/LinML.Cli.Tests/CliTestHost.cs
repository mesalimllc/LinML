using System.Runtime.InteropServices;

namespace LinML.Cli.Tests;

/// <summary>
/// Invokes the real CLI entry point in-process with captured stdout/stderr.
/// Console redirection is process-global, so all invocations serialize on a lock.
/// </summary>
internal static class CliTestHost
{
    private static readonly object Gate = new();

    /// <summary>
    /// True on a linux-x64 host, where the x64-only plugins (OpenVINO, MIGraphX) apply.
    /// Tests whose expectations depend on the host architecture check this rather than assume x64.
    /// </summary>
    public static bool IsLinuxX64Host =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// TensorRT EP options (<c>trt:</c>-scoped) that keep tests that must exercise the
    /// TensorRT EP fast: the lowest builder optimization level, and an engine cache in a
    /// stable temp directory so only the first run on a machine pays the ~25 s build.
    /// Ignored on hosts where TensorRT is not loadable.
    /// </summary>
    public static string[] FastTensorRtOptions() =>
    [
        "--ep-option", "trt:trt_builder_optimization_level=0",
        "--ep-option", "trt:trt_engine_cache_enable=1",
        "--ep-option", "trt:trt_engine_cache_path=" + Path.Combine(Path.GetTempPath(), "linml-test-trt-cache"),
    ];

    public static (int ExitCode, string Stdout, string Stderr) Run(params string[] args) =>
        RunWithStdin(null, args);

    /// <summary>Invoke with stdin lines (for `serve`); lines are joined with newlines.</summary>
    public static (int ExitCode, string Stdout, string Stderr) RunWithStdin(
        IEnumerable<string>? stdinLines, params string[] args)
    {
        lock (Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var originalIn = Console.In;
            try
            {
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                if (stdinLines is not null)
                {
                    Console.SetIn(new StringReader(string.Join("\n", stdinLines)));
                }
                var exitCode = LinML.Cli.Program.Main(args).GetAwaiter().GetResult();
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Console.SetIn(originalIn);
            }
        }
    }

    public static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);
}
