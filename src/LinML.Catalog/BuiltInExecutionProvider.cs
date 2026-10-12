using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>
/// An execution provider compiled directly into the loaded ONNX Runtime build
/// (e.g. CPU, CUDA, classic TensorRT). Never downloaded.
/// </summary>
internal sealed class BuiltInExecutionProvider : IExecutionProvider
{
    private readonly ProviderId _id;
    private readonly IOrtRegistrar _registrar;
    private readonly Func<bool>? _isSupportedPlatform;

    internal BuiltInExecutionProvider(
        ProviderId id,
        IOrtRegistrar? registrar = null,
        Func<bool>? isSupportedPlatform = null)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _registrar = registrar ?? OrtRegistrar.Instance;
        _isSupportedPlatform = isSupportedPlatform;
    }

    public ProviderId Id => _id;

    public ProviderSource Source => ProviderSource.BuiltIn;

    public string? LibraryPath => null;

    public string? PinnedVersion => null;

    public ProviderReadyState ReadyState
    {
        get
        {
            if (_isSupportedPlatform is not null && !_isSupportedPlatform())
            {
                return ProviderReadyState.Unsupported;
            }

            if (_id == ProviderId.Cpu)
            {
                return ProviderReadyState.Ready;
            }

            if (!_registrar.AvailableProviders.Contains(_id.Value))
            {
                return ProviderReadyState.Unsupported;
            }

            if (GetDevices().Count > 0)
            {
                return ProviderReadyState.Ready;
            }

            return ProviderReadyState.NotReady;
        }
    }

    public bool TryRegister(out string? failureReason)
    {
        if (_isSupportedPlatform is not null && !_isSupportedPlatform())
        {
            failureReason = $"{_id} is unsupported on this platform.";
            return false;
        }

        if (_id != ProviderId.Cpu && !_registrar.AvailableProviders.Contains(_id.Value))
        {
            failureReason = $"{_id} is not compiled into the loaded ONNX Runtime build.";
            return false;
        }

        failureReason = null;
        return true;
    }

    public IReadOnlyList<OrtEpDevice> GetDevices() => _registrar.GetDevices(_id.Value);

    public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isSupportedPlatform is not null && !_isSupportedPlatform())
        {
            return Task.FromResult(new ProviderEnsureResult(
                _id, ProviderEnsureStatus.NotAvailable, $"{_id} is unsupported on this platform.", []));
        }

        if (_id == ProviderId.Cpu)
        {
            return Task.FromResult(new ProviderEnsureResult(
                _id, ProviderEnsureStatus.Ready, "CPU execution provider is built in and ready.", GetDevices()));
        }

        if (!_registrar.AvailableProviders.Contains(_id.Value))
        {
            return Task.FromResult(new ProviderEnsureResult(
                _id, ProviderEnsureStatus.NotAvailable,
                $"{_id} is not compiled into the loaded ONNX Runtime build.", []));
        }

        try
        {
            using var sessionOptions = new SessionOptions();
            var appendResult = sessionOptions.AppendFromCatalog(this);
            if (!appendResult.Appended)
            {
                return Task.FromResult(new ProviderEnsureResult(
                    _id, ProviderEnsureStatus.Failed, appendResult.FailureReason ?? $"Failed to append {_id}.", []));
            }

            return Task.FromResult(new ProviderEnsureResult(
                _id, ProviderEnsureStatus.Ready, $"{_id} is built in and ready.", GetDevices()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ProviderEnsureResult(
                _id, ProviderEnsureStatus.Failed, $"{_id} initialization failed: {ex.Message}", []));
        }
    }
}
