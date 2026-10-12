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
}
