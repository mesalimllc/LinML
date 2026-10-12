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

    private static readonly StickyErrorClearer Shared = new(ResolveCudaRuntime);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLastErrorFn();

    /// <summary>Clears the sticky error; returns the cleared <c>cudaError_t</c> (0 = none, or CUDA unavailable).</summary>
    public static int Clear() => Shared.Clear();

    private static Func<int>? ResolveCudaRuntime()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        foreach (string name in RuntimeNames)
        {
            // Binds to a CUDA runtime that is already loaded (or loadable by name).
            if (NativeLibrary.TryLoad(name, out nint handle)
                && NativeLibrary.TryGetExport(handle, "cudaGetLastError", out nint export))
            {
                return Marshal.GetDelegateForFunctionPointer<GetLastErrorFn>(export).Invoke;
            }
        }

        return null;
    }
}

/// <summary>
/// Calls a resolved <c>cudaGetLastError</c>, resolving it lazily and only caching a
/// success. A lookup that fails (for example a CPU session created before any CUDA
/// provider has loaded the runtime) is retried on the next call.
/// </summary>
internal sealed class StickyErrorClearer(Func<Func<int>?> resolve)
{
    private Func<int>? _clear;

    public int Clear()
    {
        Func<int>? clear = _clear;
        if (clear is null)
        {
            clear = resolve();
            if (clear is null)
            {
                return 0;
            }

            _clear = clear;
        }

        return clear();
    }
}
