using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx.Providers;

/// <summary>
/// Probes CUDA provider readiness: load, append, and smoke test.
/// </summary>
public sealed class CudaReadinessProbe
{
    private readonly ILogger<CudaReadinessProbe> _logger;

    public CudaReadinessProbe(ILogger<CudaReadinessProbe> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Probe CUDA readiness through all phases: load, append, smoke.
    /// </summary>
    public ProviderReadinessReport Probe(CancellationToken cancellationToken = default)
    {
        var phaseResults = new List<ReadinessPhaseResult>();
        var durations = new Dictionary<ReadinessPhase, TimeSpan>();

        // Phase 1: Load — check if CUDA provider is listed by ORT
        var loadStart = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var availableProviders = OrtEnv.Instance().GetAvailableProviders();
            var cudaListed = availableProviders.Contains("CUDAExecutionProvider");

            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;

            if (!cudaListed)
            {
                phaseResults.Add(new ReadinessPhaseResult
                {
                    Phase = ReadinessPhase.Load,
                    Success = false,
                    Error = "CUDAExecutionProvider not listed by ORT. GPU package may not be installed."
                });

                return new ProviderReadinessReport
                {
                    Provider = Contracts.ProviderId.Cuda,
                    IsReady = false,
                    FailedPhase = ReadinessPhase.Load,
                    FailureReason = "CUDAExecutionProvider not available in ORT runtime.",
                    PhaseResults = phaseResults,
                    PhaseDurations = durations
                };
            }

            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = ReadinessPhase.Load,
                Success = true
            });
        }
        catch (Exception ex)
        {
            durations[ReadinessPhase.Load] = DateTime.UtcNow - loadStart;
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = ReadinessPhase.Load,
                Success = false,
                Error = ex.Message
            });

            return new ProviderReadinessReport
            {
                Provider = Contracts.ProviderId.Cuda,
                IsReady = false,
                FailedPhase = ReadinessPhase.Load,
                FailureReason = $"CUDA provider load failed: {ex.Message}",
                PhaseResults = phaseResults,
                PhaseDurations = durations
            };
        }

        // Phase 2: Append — try to append CUDA EP to session options
        var appendStart = DateTime.UtcNow;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sessionOptions = new SessionOptions();
            using var cudaOptions = new OrtCUDAProviderOptions();
            sessionOptions.AppendExecutionProvider_CUDA(cudaOptions);

            durations[ReadinessPhase.Append] = DateTime.UtcNow - appendStart;
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = ReadinessPhase.Append,
                Success = true
            });
        }
        catch (Exception ex)
        {
            durations[ReadinessPhase.Append] = DateTime.UtcNow - appendStart;
            phaseResults.Add(new ReadinessPhaseResult
            {
                Phase = ReadinessPhase.Append,
                Success = false,
                Error = ex.Message
            });

            return new ProviderReadinessReport
            {
                Provider = Contracts.ProviderId.Cuda,
                IsReady = false,
                FailedPhase = ReadinessPhase.Append,
                FailureReason = $"CUDA provider append failed: {ex.Message}",
                PhaseResults = phaseResults,
                PhaseDurations = durations
            };
        }

        // Phase 3: Smoke — real inference requires a model and is covered by
        // ModelSmokeRunner via `doctor --model`. Without a model there is no
        // honest way to claim the Smoke phase, so it is not reported here.
        // A provider that passes Load+Append can still fail at run time;
        // the CLI's execution-time fallback covers that case.

        _logger.LogInformation("CUDA readiness probe passed Load+Append (smoke requires a model)");

        return new ProviderReadinessReport
        {
            Provider = Contracts.ProviderId.Cuda,
            IsReady = true,
            PhaseResults = phaseResults,
            PhaseDurations = durations
        };
    }
}
