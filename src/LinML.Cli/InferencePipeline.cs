using LinML.Catalog;
using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;

namespace LinML.Cli;

/// <summary>
/// Shared select-and-execute pipeline used by `run` (one-shot) and `serve`
/// (long-lived session). Selection, provider-scoped options, execution-time
/// fallback, and exit-code mapping live here so both commands behave identically.
/// </summary>
internal sealed record SelectionOutcome(
    SelectionDecision Decision,
    IReadOnlyList<ProviderId> Candidates);

/// <summary>Result of a full select+execute pass. ExitCode follows CLI conventions.</summary>
internal sealed record ExecutionOutcome(
    bool Success,
    int ExitCode,
    string? ErrorMessage,
    IReadOnlyList<TensorOutput>? Outputs,
    ProviderId SelectedProvider,
    TimeSpan Duration,
    List<FallbackStep> FallbackChain,
    List<DiagnosticEntry> Diagnostics);

/// <summary>Result of opening (but not running) a session, for `serve` startup.</summary>
internal sealed record OpenSessionOutcome(
    bool Success,
    int ExitCode,
    string? ErrorMessage,
    IInferenceSession? Session,
    ProviderId SelectedProvider,
    List<FallbackStep> FallbackChain);

internal static class InferencePipeline
{
    /// <summary>
    /// Rank the providers the runtime actually lists. Does not print or exit;
    /// callers map !Decision.Success to exit 3.
    /// </summary>
    public static SelectionOutcome Select(
        ServiceProvider services,
        ModelIdentity model,
        ProviderId? preferredProvider,
        bool requireProvider,
        IReadOnlyDictionary<string, string> epOptions)
    {
        var selector = services.GetRequiredService<ProviderSelector>();
        var fallbackPolicy = services.GetRequiredService<FallbackPolicy>();
        var discovery = services.GetRequiredService<IProviderDiscovery>();

        var request = new InferenceRequest
        {
            Model = model,
            Inputs = [],
            PreferredProvider = preferredProvider,
            RequireProvider = requireProvider,
            Options = epOptions
        };

        var report = discovery.Discover();
        var candidates = fallbackPolicy.BuildCandidateList(request, report.AvailableProviders);
        var decision = selector.Select(new SelectionRequest
        {
            Candidates = candidates,
            RequireFirst = requireProvider,
            Model = model
        });

        return new SelectionOutcome(decision, candidates);
    }

    /// <summary>
    /// Full one-shot execution: walk candidates from the selected provider,
    /// creating a session and running per attempt, with execution-time fallback
    /// for soft selection. Sessions are disposed per attempt.
    /// </summary>
    public static ExecutionOutcome Execute(
        ServiceProvider services,
        ModelIdentity model,
        List<TensorInput> inputs,
        ProviderId? preferredProvider,
        bool requireProvider,
        IReadOnlyDictionary<string, string> epOptions,
        IReadOnlyDictionary<string, string> epLibraries,
        bool profile)
    {
        var selection = Select(services, model, preferredProvider, requireProvider, epOptions);
        var (decision, candidates) = (selection.Decision, selection.Candidates);

        if (!decision.Success)
        {
            var errorMsg = FormatSelectionFailure(decision.FailureReason, preferredProvider);
            return new ExecutionOutcome(
                false, 3, errorMsg,
                null, preferredProvider ?? ProviderId.Cpu, TimeSpan.Zero, [], []);
        }

        var factory = services.GetRequiredService<IInferenceSessionFactory>();
        var diagnostics = new List<DiagnosticEntry>();
        var fallbackChain = decision.RejectedCandidates
            .Select(r => new FallbackStep { Provider = r.Provider, Reason = r.Reason })
            .ToList();

        var attemptOrder = candidates
            .SkipWhile(c => c != decision.SelectedProvider!)
            .ToList();

        IReadOnlyList<TensorOutput>? outputs = null;
        var selectedProvider = decision.SelectedProvider!;
        var duration = TimeSpan.Zero;
        foreach (var attempt in attemptOrder)
        {
            IInferenceSession? attemptSession;
            try
            {
                var attemptOptions = ProviderArgs.OptionsForProvider(epOptions, attempt);
                attemptSession = factory.CreateSession(
                    model, attempt, attemptOptions,
                    enableProfiling: profile, epLibraries: epLibraries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (requireProvider)
                {
                    return new ExecutionOutcome(
                        false, 3, $"Required provider '{attempt}' unavailable: {ex.Message}",
                        null, attempt, TimeSpan.Zero, fallbackChain, diagnostics);
                }
                fallbackChain.Add(new FallbackStep { Provider = attempt, Reason = ex.Message });
                continue;
            }

            using (attemptSession)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    outputs = attemptSession.Run(inputs);
                    stopwatch.Stop();
                    duration = stopwatch.Elapsed;
                    selectedProvider = attempt;
                }
                catch (Exception ex) when (!requireProvider && ex is not OperationCanceledException)
                {
                    fallbackChain.Add(new FallbackStep { Provider = attempt, Reason = ex.Message });
                    continue;
                }

                if (profile)
                {
                    var placements = attemptSession.EndProfiling();
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Phase = "profile",
                        Message = $"ORT profile written to {attemptSession.ProfilePath}",
                        Severity = DiagnosticSeverity.Info
                    });
                    foreach (var placement in placements)
                    {
                        diagnostics.Add(new DiagnosticEntry
                        {
                            Phase = "profile",
                            Message = $"Node '{placement.NodeName}' ran on {placement.Provider}",
                            Severity = DiagnosticSeverity.Info
                        });
                    }
                }
                break;
            }
        }

        if (outputs is null)
        {
            var attempted = string.Join(", ", fallbackChain.Select(f => f.Provider.ToString()));
            return new ExecutionOutcome(
                false, 1, $"All providers failed at execution: {attempted}",
                null, selectedProvider, TimeSpan.Zero, fallbackChain, diagnostics);
        }

        return new ExecutionOutcome(
            true, 0, null, outputs, selectedProvider, duration, fallbackChain, diagnostics);
    }

    /// <summary>
    /// Open (but do not run) a session for `serve` startup, walking candidates
    /// from the selected provider. The caller owns the returned session and
    /// must dispose it. No profiling: profiling ends a session's profiling
    /// state and is incompatible with session reuse.
    /// </summary>
    public static OpenSessionOutcome OpenSession(
        ServiceProvider services,
        ModelIdentity model,
        SelectionOutcome selection,
        bool requireProvider,
        IReadOnlyDictionary<string, string> epOptions,
        IReadOnlyDictionary<string, string> epLibraries)
    {
        var (decision, candidates) = (selection.Decision, selection.Candidates);

        if (!decision.Success)
        {
            return new OpenSessionOutcome(
                false, 3, $"Provider selection failed: {decision.FailureReason}",
                null, ProviderId.Cpu, []);
        }

        var factory = services.GetRequiredService<IInferenceSessionFactory>();
        var fallbackChain = decision.RejectedCandidates
            .Select(r => new FallbackStep { Provider = r.Provider, Reason = r.Reason })
            .ToList();

        var attemptOrder = candidates
            .SkipWhile(c => c != decision.SelectedProvider!)
            .ToList();

        foreach (var attempt in attemptOrder)
        {
            try
            {
                var attemptOptions = ProviderArgs.OptionsForProvider(epOptions, attempt);
                var session = factory.CreateSession(
                    model, attempt, attemptOptions, epLibraries: epLibraries);
                return new OpenSessionOutcome(true, 0, null, session, attempt, fallbackChain);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (requireProvider)
                {
                    return new OpenSessionOutcome(
                        false, 3, $"Required provider '{attempt}' unavailable: {ex.Message}",
                        null, attempt, fallbackChain);
                }
                fallbackChain.Add(new FallbackStep { Provider = attempt, Reason = ex.Message });
            }
        }

        var attempted = string.Join(", ", fallbackChain.Select(f => f.Provider.ToString()));
        return new OpenSessionOutcome(
            false, 1, $"All providers failed at session creation: {attempted}",
            null, decision.SelectedProvider!, fallbackChain);
    }

    internal static string FormatSelectionFailure(string? failureReason, ProviderId? preferredProvider)
    {
        var errorMsg = $"Provider selection failed: {failureReason ?? "Unknown reason"}";
        if (preferredProvider is not null)
        {
            var spec = KnownPlugins.All.FirstOrDefault(p => p.Id == preferredProvider);
            if (spec is not null && spec.FindLibrary() is null)
            {
                var shortId = ProviderArgs.GetShortName(preferredProvider);
                errorMsg += $" Run 'linml catalog install {shortId}' to vendor missing libraries.";
            }
        }
        return errorMsg;
    }
}
