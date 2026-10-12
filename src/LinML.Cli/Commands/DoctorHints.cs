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

    /// <summary>True when a readiness failure looks like a native library that could not be found or loaded.</summary>
    internal static bool IsLoaderFailure(ProviderReadinessReport report) =>
        !report.IsReady
        && report.FailureReason is { } reason
        && LoaderFailureMarkers.Any(m => reason.Contains(m, StringComparison.Ordinal));

    /// <summary>True when an installed catalog plugin failed to load its native library.</summary>
    internal static bool IsLoaderFailure(CatalogDoctorReport report) =>
        report.Status == "NotReady"
        && report.LibraryPath is not null
        && report.Details is { } details
        && LoaderFailureMarkers.Any(m => details.Contains(m, StringComparison.Ordinal));

    /// <summary>
    /// Returns the hints to print: empty unless something is vendored on disk
    /// (<paramref name="vendoredPresent"/>) yet a provider failed to load a native library.
    /// </summary>
    /// <param name="readiness">Per-provider readiness reports.</param>
    /// <param name="vendoredPresent">Whether any vendored GPU library or plugin EP exists on disk.</param>
    /// <param name="envScriptPath">Path to <c>env-gpu-deps.sh</c> if it can be located; otherwise <c>null</c>.</param>
    /// <param name="catalog">Catalog plugin reports, including installed plugins that failed registration.</param>
    internal static IReadOnlyList<string> Build(
        IEnumerable<ProviderReadinessReport> readiness,
        bool vendoredPresent,
        string? envScriptPath,
        IEnumerable<CatalogDoctorReport>? catalog = null)
    {
        if (!vendoredPresent
            || (!readiness.Any(IsLoaderFailure) && !(catalog?.Any(IsLoaderFailure) ?? false)))
        {
            return [];
        }

        string source = envScriptPath is null
            ? "source scripts/env-gpu-deps.sh   (from a LinML checkout or release archive)"
            : $"source {envScriptPath}";
        return
        [
            "Vendored GPU libraries or plugin EPs are installed, but ONNX Runtime could not load them.",
            "Upgrade linml (older builds do not preload vendored libraries), or set up this shell with:",
            $"  {source}",
        ];
    }

    /// <summary><c>scripts/env-gpu-deps.sh</c> next to the installed binary, if present.</summary>
    internal static string? LocateEnvScript()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "scripts", "env-gpu-deps.sh");
        return File.Exists(path) ? path : null;
    }
}
