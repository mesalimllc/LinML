using System.Runtime.InteropServices;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Clears the calling thread's sticky CUDA error (<c>cudaGetLastError</c>).
/// </summary>
/// <remarks>
/// The CUDA runtime keeps a per-thread "last error". Creating a
/// <c>TensorrtExecutionProvider</c> session on WSL2 leaves error 101
/// (<c>invalid device ordinal</c>) latched: the provider looks the GPU up by the WSL2
/// virtual PCI bus id, which CUDA rejects, and handles the failure itself. The CUDA EP
/// later checks <c>cudaGetLastError()</c> after its first kernel and reports that stale
/// 101 as its own failure, so a <c>bench --all-ready</c> that ran TensorRT before CUDA
/// failed the CUDA entry. Clearing the error once a session exists keeps one
/// provider's handled error from failing the next provider in the same process.
/// </remarks>
internal static class CudaStickyError
{
    private static readonly string[] RuntimeNames = ["libcudart.so.13", "libcudart.so.12"];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLastErrorFn();

    private static readonly Lazy<GetLastErrorFn?> Entry = new(Resolve);

    /// <summary>Clears the sticky error; returns the cleared <c>cudaError_t</c> (0 = none, or CUDA unavailable).</summary>
    public static int Clear() => Entry.Value?.Invoke() ?? 0;

    private static GetLastErrorFn? Resolve()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (string name in RuntimeNames)
        {
            // The CUDA runtime is already loaded by ORT's CUDA provider when it matters;
            // this only binds to it and never loads it for a CPU-only host.
            if (NativeLibrary.TryLoad(name, out nint handle)
                && NativeLibrary.TryGetExport(handle, "cudaGetLastError", out nint export))
            {
                return Marshal.GetDelegateForFunctionPointer<GetLastErrorFn>(export);
            }
        }

        return null;
    }
}
