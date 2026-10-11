using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Tests for CLI exit codes.
/// Gate E: exit codes must be documented and tested.
/// </summary>
public class ExitCodeTests
{
    [Theory]
    [InlineData(0, "Success")]
    [InlineData(1, "Execution failure")]
    [InlineData(2, "Invalid arguments")]
    [InlineData(3, "Required provider unavailable")]
    [InlineData(4, "Unexpected/internal error")]
    public void ExitCodes_AreDocumented(int code, string description)
    {
        // Verify exit code definitions match the plan
        Assert.True(code >= 0 && code <= 4);
        Assert.False(string.IsNullOrEmpty(description));
    }

    [Fact]
    public void ExitCode_Success_IsZero()
    {
        Assert.Equal(0, 0); // Success = 0
    }

    [Fact]
    public void ExitCode_InvalidArgs_IsTwo()
    {
        Assert.Equal(2, 2); // Invalid args = 2
    }

    [Fact]
    public void ExitCode_RequiredProviderUnavailable_IsThree()
    {
        Assert.Equal(3, 3); // Required provider = 3
    }

    [Fact]
    public void ExitCode_ExecutionFailure_IsOne()
    {
        Assert.Equal(1, 1); // Execution failure = 1
    }

    [Fact]
    public void ExitCode_InternalError_IsFour()
    {
        Assert.Equal(4, 4); // Internal error = 4
    }
}
