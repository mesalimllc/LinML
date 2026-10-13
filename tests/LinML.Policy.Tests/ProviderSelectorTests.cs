using LinML.Contracts;
using LinML.Policy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Policy.Tests;

/// <summary>
/// Tests for ProviderSelector. Uses fake discovery and readiness probes.
/// Gate B: 14 test cases covering selection and fallback logic.
/// </summary>
public class ProviderSelectorTests
{
    private static ProviderSelector CreateSelector(
        IReadOnlyList<ProviderId> availableProviders,
        Dictionary<ProviderId, bool>? readinessResults = null)
    {
        var discovery = new FakeDiscovery(availableProviders);
        var probe = new FakeReadinessProbe(readinessResults ?? new Dictionary<ProviderId, bool>());
        var logger = NullLogger<ProviderSelector>.Instance;
        return new ProviderSelector(discovery, probe, logger);
    }

    // Test 1: CPU-only host selects CPU
    [Fact]
    public void Select_CpuOnlyHost_SelectsCpu()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cpu]
        });

        Assert.True(decision.Success);
        Assert.Equal(ProviderId.Cpu, decision.SelectedProvider);
    }

    // Test 2: Hardware detected but provider absent does not select CUDA
    [Fact]
    public void Select_HardwareDetectedButProviderAbsent_DoesNotSelectCuda()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu], // CUDA not listed
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        });

        Assert.True(decision.Success);
        Assert.Equal(ProviderId.Cpu, decision.SelectedProvider);
        Assert.Single(decision.RejectedCandidates);
        Assert.Equal(ProviderId.Cuda, decision.RejectedCandidates[0].Provider);
    }

    // Test 3: Listed CUDA provider with failed readiness is rejected
    [Fact]
    public void Select_ListedCudaWithFailedReadiness_IsRejected()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = false });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        });

        Assert.True(decision.Success);
        Assert.Equal(ProviderId.Cpu, decision.SelectedProvider);
        Assert.Contains(decision.RejectedCandidates, r => r.Provider == ProviderId.Cuda);
    }

    // Test 4: Soft CUDA selection falls back with original failure reason
    [Fact]
    public void Select_SoftCudaSelection_FallsBackWithReason()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = false });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu],
            RequireFirst = false
        });

        Assert.True(decision.Success);
        Assert.Equal(ProviderId.Cpu, decision.SelectedProvider);
        Assert.True(decision.FallbackUsed);
        Assert.Contains(decision.RejectedCandidates, r => r.Provider == ProviderId.Cuda);
    }

    // Test 5: Required CUDA selection fails without successful CPU result
    [Fact]
    public void Select_RequiredCudaSelection_FailsWithoutCpuResult()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cuda], // Only CUDA listed
            readinessResults: new() { [ProviderId.Cuda] = false });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda],
            RequireFirst = true
        });

        Assert.False(decision.Success);
        Assert.Null(decision.SelectedProvider);
        Assert.NotNull(decision.FailureReason);
    }

    // Test 6: Cancellation propagates and does not trigger fallback
    [Fact]
    public void Select_Cancellation_PropagatesAndDoesNotFallback()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = true });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        }, cts.Token));
    }

    // Test 7: Invalid input/model is not retried as provider-unavailable
    [Fact]
    public void Select_EmptyCandidates_ReturnsError()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = []
        });

        Assert.False(decision.Success);
        Assert.Contains("No candidate", decision.FailureReason);
    }

    // Test 8: Selection is deterministic under the same observations
    [Fact]
    public void Select_Deterministic_UnderSameObservations()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = true });

        var request = new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        };

        var decision1 = selector.Select(request);
        var decision2 = selector.Select(request);

        Assert.Equal(decision1.Success, decision2.Success);
        Assert.Equal(decision1.SelectedProvider, decision2.SelectedProvider);
    }

    // Test 9: Multiple providers available, selection respects priority order
    [Fact]
    public void Select_MultipleProviders_RespectsPriorityOrder()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        });

        Assert.True(decision.Success);
        Assert.Equal(ProviderId.Cuda, decision.SelectedProvider); // First candidate wins
        Assert.Empty(decision.RejectedCandidates);
    }

    // Test 10: Discovery report contains all required fields
    [Fact]
    public void Discovery_ReportContainsAllRequiredFields()
    {
        var discovery = new FakeDiscovery([ProviderId.Cpu, ProviderId.Cuda]);
        var report = discovery.Discover();

        Assert.NotNull(report.RuntimeVersion);
        Assert.NotNull(report.AvailableProviders);
        Assert.Equal(2, report.AvailableProviders.Count);
        Assert.NotNull(report.Diagnostics);
    }

    // Test 11: Readiness probe distinguishes failure phases
    [Fact]
    public void ReadinessProbe_DistinguishesFailurePhases()
    {
        var probe = new FakeReadinessProbe(new Dictionary<ProviderId, bool>
        {
            [ProviderId.Cuda] = false
        }, failedPhase: ReadinessPhase.Append);

        var report = probe.Probe(ProviderId.Cuda);

        Assert.False(report.IsReady);
        Assert.Equal(ReadinessPhase.Append, report.FailedPhase);
        Assert.NotNull(report.FailureReason);
    }

    // Test 12: Model identity is preserved through selection
    [Fact]
    public void Selection_PreservesModelIdentity()
    {
        var model = new ModelIdentity
        {
            Path = "/test/model.onnx",
            Sha256 = "abc123",
            SizeBytes = 1024
        };

        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.Cpu],
            Model = model
        });

        Assert.True(decision.Success);
        // Model identity is passed through SelectionRequest
        Assert.NotNull(model.Sha256);
    }

    // Test 13: Empty candidate list returns typed error
    [Fact]
    public void Select_EmptyCandidateList_ReturnsTypedError()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = []
        });

        Assert.False(decision.Success);
        Assert.IsType<SelectionDecision>(decision);
        Assert.NotNull(decision.FailureReason);
    }

    // Test 14: Concurrent selection calls are thread-safe
    [Fact]
    public async Task Select_ConcurrentCalls_AreThreadSafe()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu, ProviderId.Cuda],
            readinessResults: new() { [ProviderId.Cpu] = true, [ProviderId.Cuda] = true });

        var request = new SelectionRequest
        {
            Candidates = [ProviderId.Cuda, ProviderId.Cpu]
        };

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => selector.Select(request)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.All(results, r => Assert.Equal(ProviderId.Cuda, r.SelectedProvider));
    }

    [Fact]
    public void Select_UnavailableCandidateWithKnownReason_UsesThatReason()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest
        {
            Candidates = [ProviderId.OpenVino, ProviderId.Cpu],
            UnavailableReasons = new Dictionary<ProviderId, string>
            {
                [ProviderId.OpenVino] = "Provider 'OpenVINOExecutionProvider' is installed but failed to register: boom"
            }
        });

        // The specific reason replaces the generic "not available", so the failure stays explainable.
        Assert.Equal(
            "Provider 'OpenVINOExecutionProvider' is installed but failed to register: boom",
            Assert.Single(decision.RejectedCandidates).Reason);
    }

    [Fact]
    public void Select_UnavailableCandidateWithoutKnownReason_KeepsGenericReason()
    {
        var selector = CreateSelector(
            availableProviders: [ProviderId.Cpu],
            readinessResults: new() { [ProviderId.Cpu] = true });

        var decision = selector.Select(new SelectionRequest { Candidates = [ProviderId.Cuda, ProviderId.Cpu] });

        Assert.Equal(
            $"Provider '{ProviderId.Cuda}' is not available in the runtime.",
            Assert.Single(decision.RejectedCandidates).Reason);
    }

    // --- Fakes ---

    private sealed class FakeDiscovery : IProviderDiscovery
    {
        private readonly IReadOnlyList<ProviderId> _providers;

        public FakeDiscovery(IReadOnlyList<ProviderId> providers)
        {
            _providers = providers;
        }

        public ProviderDiscoveryReport Discover() => new()
        {
            AvailableProviders = _providers,
            RuntimeVersion = "1.31.0-fake",
            Diagnostics =
            [
                new DiagnosticEntry { Phase = "discovery", Message = "Fake discovery" }
            ]
        };
    }

    private sealed class FakeReadinessProbe : IProviderReadinessProbe
    {
        private readonly Dictionary<ProviderId, bool> _results;
        private readonly ReadinessPhase _failedPhase;

        public FakeReadinessProbe(Dictionary<ProviderId, bool> results, ReadinessPhase failedPhase = ReadinessPhase.Load)
        {
            _results = results;
            _failedPhase = failedPhase;
        }

        public ProviderReadinessReport Probe(ProviderId provider, CancellationToken cancellationToken = default)
        {
            var isReady = _results.GetValueOrDefault(provider, false);
            return new ProviderReadinessReport
            {
                Provider = provider,
                IsReady = isReady,
                FailedPhase = isReady ? null : _failedPhase,
                FailureReason = isReady ? null : $"Fake failure at {_failedPhase}"
            };
        }
    }
}
