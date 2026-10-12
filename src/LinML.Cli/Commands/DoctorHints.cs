using System.Text.RegularExpressions;
using LinML.Contracts;

namespace LinML.Cli.Commands;

/// <summary>
/// Environment hints for `linml doctor`: the case where vendored GPU libraries or
/// plugin EPs are installed on disk but ONNX Runtime still could not load them.
/// </summary>
internal static class DoctorHints
{
    private static readonly string[] LoaderFailureMarkers =
    [
        "shared object file",
        "Failed to load shared library",
        "Failed to load library",
    ];

    // Loader errors that name a specific library only count when the vendored
    // dependency that ships it is actually present.
    private static readonly (string Library, string[] GpuDependencyIds)[] LibraryOwners =
    [
        ("libnvinfer", ["tensorrt", "tensorrt-lean"]),
        ("libcudnn", ["cudnn"]),
        ("libcublas", ["cudnn"]),
    ];

    /// <summary>True when a failure message looks like a native library that could not be found or loaded.</summary>
    internal static bool IsLoaderFailure(string? message) =>
        message is not null
        && LoaderFailureMarkers.Any(m => message.Contains(m, StringComparison.Ordinal));

    /// <summary>True when a readiness failure looks like a native library that could not be found or loaded.</summary>
    internal static bool IsLoaderFailure(ProviderReadinessReport report) =>
        !report.IsReady && IsLoaderFailure(report.FailureReason);

    /// <summary>
    /// Returns the hints to print: empty unless a provider failed to load a native
    /// library that is vendored on disk.
    /// </summary>
    /// <param name="readiness">Per-provider readiness reports.</param>
    /// <param name="catalogFailures">Registration failure messages of installed catalog plugins.</param>
    /// <param name="presentGpuDependencyIds">Ids of vendored GPU dependencies found on disk (e.g. <c>tensorrt</c>, <c>cudnn</c>).</param>
    /// <param name="pluginInstalled">Whether any catalog plugin EP library exists on disk.</param>
    /// <param name="envScriptPath">Path to <c>env-gpu-deps.sh</c> if it can be located; otherwise <c>null</c>.</param>
    /// <param name="customRoot">The <c>--root</c> dependency root, when one was given; the script reads it from <c>DEPS</c>.</param>
    internal static IReadOnlyList<string> Build(
        IEnumerable<ProviderReadinessReport> readiness,
        IEnumerable<string?> catalogFailures,
        IReadOnlyCollection<string> presentGpuDependencyIds,
        bool pluginInstalled,
        string? envScriptPath,
        string? customRoot = null)
    {
        IEnumerable<string> failures = readiness
            .Where(IsLoaderFailure)
            .Select(r => r.FailureReason!)
            .Concat(catalogFailures.Where(IsLoaderFailure).Select(m => m!));

        if (!failures.Any(f => ExplainedByVendoredFiles(f, presentGpuDependencyIds, pluginInstalled)))
        {
            return [];
        }

        string command = envScriptPath is null
            ? "source scripts/env-gpu-deps.sh"
            : $"source {ShellQuote(envScriptPath)}";
        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            command = $"DEPS={ShellQuote(customRoot)} {command}";
        }

        List<string> lines =
        [
            "Vendored GPU libraries or plugin EPs are installed, but ONNX Runtime could not load them.",
            "Upgrade linml (older builds do not preload vendored libraries), or set up this shell with:",
            $"  {command}",
        ];
        if (envScriptPath is null)
        {
            lines.Add("(run from a LinML checkout or an unpacked release archive)");
        }

        return lines;
    }

    private static bool ExplainedByVendoredFiles(
        string failure,
        IReadOnlyCollection<string> presentGpuDependencyIds,
        bool pluginInstalled)
    {
        foreach (var (library, ids) in LibraryOwners)
        {
            if (failure.Contains(library, StringComparison.Ordinal))
            {
                return ids.Any(presentGpuDependencyIds.Contains);
            }
        }

        // The failing library is not named: any vendored file could be responsible.
        return pluginInstalled || presentGpuDependencyIds.Count > 0;
    }

    /// <summary>Quotes <paramref name="value"/> for a POSIX shell, leaving plain paths untouched.</summary>
    internal static string ShellQuote(string value) =>
        Regex.IsMatch(value, "^[A-Za-z0-9_@%+=:,./~-]+$")
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";

    /// <summary><c>scripts/env-gpu-deps.sh</c> next to the installed binary, if present.</summary>
    internal static string? LocateEnvScript()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scripts", "env-gpu-deps.sh");
        return File.Exists(path) ? path : null;
    }
}
