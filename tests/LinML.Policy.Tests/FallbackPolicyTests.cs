using LinML.Contracts;
using LinML.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Policy.Tests;

/// <summary>
/// Tests for FallbackPolicy.
/// </summary>
public class FallbackPolicyTests
{
    private static FallbackPolicy CreatePolicy() => new(NullLogger<FallbackPolicy>.Instance);

    [Fact]
    public void BuildCandidateList_PreferredCuda_AddsCpuFallback()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            PreferredProvider = ProviderId.Cuda
        };

        var candidates = policy.BuildCandidateList(request);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(ProviderId.Cuda, candidates[0]);
        Assert.Equal(ProviderId.Cpu, candidates[1]);
    }

    [Fact]
    public void BuildCandidateList_PreferredCpu_NoDuplicate()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            PreferredProvider = ProviderId.Cpu
        };

        var candidates = policy.BuildCandidateList(request);

        Assert.Single(candidates);
        Assert.Equal(ProviderId.Cpu, candidates[0]);
    }

    [Fact]
    public void BuildCandidateList_NoPreferred_DefaultsToCpu()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = []
        };

        var candidates = policy.BuildCandidateList(request);

        Assert.Single(candidates);
        Assert.Equal(ProviderId.Cpu, candidates[0]);
    }

    [Fact]
    public void IsFallbackAllowed_RequireProviderFalse_AllowsFallback()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            RequireProvider = false
        };

        Assert.True(policy.IsFallbackAllowed(request));
    }

    [Fact]
    public void IsFallbackAllowed_RequireProviderTrue_DisallowsFallback()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            RequireProvider = true
        };

        Assert.False(policy.IsFallbackAllowed(request));
    }

    [Fact]
    public void BuildCandidateList_WithAvailable_RanksGpuProvidersBeforeCpu()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = []
        };
        var available = new[]
        {
            ProviderId.Cpu, ProviderId.Cuda, ProviderId.Tensorrt, ProviderId.TensorrtRtx
        };

        var candidates = policy.BuildCandidateList(request, available);

        // No preferred provider: ranked order, CPU last.
        Assert.Equal(
            [ProviderId.TensorrtRtx, ProviderId.Tensorrt, ProviderId.Cuda, ProviderId.Cpu],
            candidates);
    }

    [Fact]
    public void BuildCandidateList_WithAvailable_ExplicitPreferred_FallsBackToCpuNotOtherAccelerators()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            PreferredProvider = ProviderId.Cuda
        };
        var available = new[]
        {
            ProviderId.Cpu, ProviderId.Cuda, ProviderId.Tensorrt
        };

        var candidates = policy.BuildCandidateList(request, available);

        // A failed explicit request goes to CPU, not to another GPU EP.
        Assert.Equal(
            [ProviderId.Cuda, ProviderId.Cpu],
            candidates);
    }

    [Fact]
    public void BuildCandidateList_WithAvailable_PreferredNotListed_StillFirst()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = [],
            PreferredProvider = ProviderId.MigraphX // not in available list
        };
        var available = new[] { ProviderId.Cpu, ProviderId.Cuda };

        var candidates = policy.BuildCandidateList(request, available);

        // Preferred is first even when not listed — selection rejects it
        // honestly and records why — and the only fallback is CPU.
        Assert.Equal(
            [ProviderId.MigraphX, ProviderId.Cpu],
            candidates);
    }

    [Fact]
    public void BuildCandidateList_WithAvailable_UnavailableProvidersExcluded()
    {
        var policy = CreatePolicy();
        var request = new InferenceRequest
        {
            Model = new ModelIdentity { Path = "test.onnx", Sha256 = "abc" },
            Inputs = []
        };
        // Only CPU listed (e.g. CI runner without GPU EP libs registered).
        var available = new[] { ProviderId.Cpu };

        var candidates = policy.BuildCandidateList(request, available);

        Assert.Equal([ProviderId.Cpu], candidates);
    }
}
