namespace LinML.Cli.Tests;

/// <summary>
/// Invokes the real CLI entry point in-process with captured stdout/stderr.
/// Console redirection is process-global, so all invocations serialize on a lock.
/// </summary>
internal static class CliTestHost
{
    private static readonly object Gate = new();

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
