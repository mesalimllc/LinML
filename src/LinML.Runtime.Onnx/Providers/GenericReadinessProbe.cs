using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx.Providers;

/// <summary>
/// Generic readiness probe for any execution provider: Load (listed by ORT,
/// including plugin EP devices) → Append (provider appends to SessionOptions).
/// The Smoke phase (real inference) is intentionally NOT performed here —
/// it requires a model and is covered by <see cref="ModelSmokeRunner"/>
/// via `doctor --model`. A provider that passes Load+Append can still fail
/// at run time; the CLI's execution-time fallback covers that case.
/// </summary>
public sealed class GenericReadinessProbe
{
    private readonly ILogger _logger;

    public GenericReadinessProbe(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderReadinessReport Probe(
        ProviderId provider,
        IReadOnlyDictionary<string, string>? epLibraries = null,
        CancellationToken cancellationToken = default)
    {
        LinML.Catalog.GpuDependencyLoader.PreloadVendored();
        var phaseResults = new List<ReadinessPhaseResult>();
        var durations = new Dictionary<ReadinessPhase, TimeSpan>();

        // Phase 1: Load — provider listed by ORT (built-in or plugin EP device).
        var loadStart = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            PluginEpRegistry.EnsureRegistered(epLibraries);

            var listed = OrtEnv.Instance().GetAvailableProviders().Contains(provider.Value)
                || OrtEnv.Instance().GetEpDevices().Any(d => d.EpName == provider.Value);
            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;

            if (!listed)
            {
                return Fail(provider, phaseResults, durations, ReadinessPhase.Load,
                    $"Provider '{provider}' is not listed by the ORT runtime. " +
                    "Its native EP library may be missing (check LD_LIBRARY_PATH / plugin registration).");
            }
            phaseResults.Add(new ReadinessPhaseResult { Phase = ReadinessPhase.Load, Success = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;
            return Fail(provider, phaseResults, durations, ReadinessPhase.Load,
                $"Load failed: {ex.Message}");
        }

        // Phase 2: Append — provider appends to SessionOptions.
        var appendStart = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sessionOptions = new SessionOptions();
            EpAppender.Append(sessionOptions, provider, options: null, epLibraries: null);

            durations[ReadinessPhase.Append] = DateTime.UtcNow - appendStart;
            phaseResults.Add(new ReadinessPhaseResult { Phase = ReadinessPhase.Append, Success = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            durations[ReadinessPhase.Append] = DateTime.UtcNow - appendStart;
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = ReadinessPhase.Append,
                Success = false,
                Error = ex.Message
            });
            return Fail(provider, phaseResults, durations, ReadinessPhase.Append,
                $"Append failed: {ex.Message}");
        }

        _logger.LogInformation("Provider {Provider} passed Load+Append (smoke requires a model)", provider);
        return new ProviderReadinessReport
        {
            Provider = provider,
            IsReady = true,
            PhaseResults = phaseResults,
            PhaseDurations = durations
        };
    }

    private static ProviderReadinessReport Fail(
        ProviderId provider,
        List<ReadinessPhaseResult> phaseResults,
        Dictionary<ReadinessPhase, TimeSpan> durations,
        ReadinessPhase failedPhase,
        string reason)
    {
        if (!phaseResults.Any(r => r.Phase == failedPhase))
        {
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = failedPhase,
                Success = false,
                Error = reason
            });
        }
        return new ProviderReadinessReport
        {
            Provider = provider,
            IsReady = false,
            FailedPhase = failedPhase,
            FailureReason = reason,
            PhaseResults = phaseResults,
            PhaseDurations = durations
        };
    }
}
