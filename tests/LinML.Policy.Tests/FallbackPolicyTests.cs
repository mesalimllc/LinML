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
}
