using Xunit;

namespace LinML.Runtime.Onnx.Tests;

public class CudaStickyErrorTests
{
    [Fact]
    public void Clear_NeverThrows_AndLeavesNoErrorBehind()
    {
        // Hosts without a CUDA runtime report 0; hosts with one must report 0 on the
        // second call, since the first call clears whatever was latched.
        var first = Record.Exception(() => CudaStickyError.Clear());

        Assert.Null(first);
        Assert.Equal(0, CudaStickyError.Clear());
    }

    [Fact]
    public void Clearer_RetriesLookup_AfterAnEarlyFailure_ThenCachesTheSuccess()
    {
        // A CPU session created before any CUDA provider has loaded the runtime must not
        // disable clearing for the TensorRT / CUDA sessions that follow.
        int lookups = 0;
        int cleared = 0;
        var clearer = new StickyErrorClearer(() =>
        {
            lookups++;
            return lookups < 3 ? null : () => { cleared++; return 101; };
        });

        Assert.Equal(0, clearer.Clear());   // runtime not loaded yet
        Assert.Equal(0, clearer.Clear());   // still not loaded
        Assert.Equal(101, clearer.Clear()); // loaded: the latched error is cleared
        Assert.Equal(101, clearer.Clear());

        Assert.Equal(3, lookups);           // success cached, no further lookups
        Assert.Equal(2, cleared);
    }
}
