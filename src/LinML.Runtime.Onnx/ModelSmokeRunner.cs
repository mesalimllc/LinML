using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Runs a real per-model smoke test for a provider: Load (EP listed) →
/// Append (session creation) → Smoke (actual inference run).
/// Unlike <see cref="Providers.CudaReadinessProbe"/>, the Smoke phase executes
/// a model instead of returning a placeholder success.
/// </summary>
public sealed class ModelSmokeRunner
{
    private readonly IInferenceSessionFactory _factory;
    private readonly ILogger<ModelSmokeRunner> _logger;

    public ModelSmokeRunner(IInferenceSessionFactory factory, ILogger<ModelSmokeRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderReadinessReport RunSmoke(
        ProviderId provider,
        ModelIdentity model,
        IReadOnlyList<TensorInput> inputs,
        IReadOnlyDictionary<string, string>? options = null,
        IReadOnlyDictionary<string, string>? epLibraries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(inputs);

        var phaseResults = new List<ReadinessPhaseResult>();
        var durations = new Dictionary<ReadinessPhase, TimeSpan>();

        // Phase 1: Load — provider listed by ORT.
        var loadStart = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Same listing rule as the readiness probe: plugin EPs appear as EP devices,
            // not in GetAvailableProviders, so checking only the latter would reject them.
            var listed = ProviderListing.IsListed(provider);
            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;

            if (!listed)
            {
                return Fail(provider, phaseResults, durations, ReadinessPhase.Load,
                    ProviderListing.NotListedReason(provider));
            }
            phaseResults.Add(new ReadinessPhaseResult { Phase = ReadinessPhase.Load, Success = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;
            return Fail(provider, phaseResults, durations, ReadinessPhase.Load,
                $"Load failed: {ex.Message}");
        }

        // Phase 2+3: Append (session creation) + Smoke (real inference run).
        var appendStart = DateTime.UtcNow;
        var smokeStart = appendStart;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var session = _factory.CreateSession(
                model, provider, options, epLibraries: epLibraries, cancellationToken: cancellationToken);
            durations[ReadinessPhase.Append] = DateTime.UtcNow - appendStart;
            phaseResults.Add(new ReadinessPhaseResult { Phase = ReadinessPhase.Append, Success = true });

            smokeStart = DateTime.UtcNow;
            var outputs = session.Run(inputs, cancellationToken);
            durations[ReadinessPhase.Smoke] = DateTime.UtcNow - smokeStart;

            if (outputs.Count == 0)
            {
                return Fail(provider, phaseResults, durations, ReadinessPhase.Smoke,
                    "Smoke run produced no outputs.");
            }
            phaseResults.Add(new ReadinessPhaseResult { Phase = ReadinessPhase.Smoke, Success = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Append completed iff its duration was recorded: otherwise creation
            // failed; if recorded, the failure came from the Smoke run.
            var failedPhase = durations.ContainsKey(ReadinessPhase.Append)
                ? ReadinessPhase.Smoke : ReadinessPhase.Append;
            durations[failedPhase] = DateTime.UtcNow -
                (failedPhase == ReadinessPhase.Append ? appendStart : smokeStart);
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = failedPhase,
                Success = false,
                Error = ex.Message
            });
            return Fail(provider, phaseResults, durations, failedPhase,
                $"{failedPhase} failed: {ex.Message}");
        }

        _logger.LogInformation("Model smoke passed for provider {Provider}", provider);
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
