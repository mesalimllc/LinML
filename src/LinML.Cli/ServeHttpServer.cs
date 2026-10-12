using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LinML.Cli;

/// <summary>
/// Lightweight HTTP/REST daemon for <c>linml serve --port &lt;port&gt;</c>.
/// Serves model inference requests over HTTP with a long-lived ONNX Runtime session.
/// </summary>
internal sealed class ServeHttpServer : IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ModelIdentity _modelIdentity;
    private readonly SelectionOutcome _selection;
    private readonly ProviderId? _preferredProvider;
    private readonly bool _requireProvider;
    private readonly IReadOnlyDictionary<string, string> _epOptions;
    private readonly IReadOnlyDictionary<string, string> _epLibraries;

    private readonly HttpListener _listener;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _serverCts = new();

    private IInferenceSession? _session;
    private ProviderId _selectedProvider;
    private List<FallbackStep> _fallbackChain;
    private Task? _listenerLoopTask;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }
    public string Url { get; }
    public bool IsListening => _listener.IsListening;

    public ServeHttpServer(
        ServiceProvider services,
        ModelIdentity modelIdentity,
        SelectionOutcome selection,
        ProviderId? preferredProvider,
        bool requireProvider,
        IReadOnlyDictionary<string, string> epOptions,
        IReadOnlyDictionary<string, string> epLibraries,
        IInferenceSession? initialSession,
        ProviderId initialSelectedProvider,
        List<FallbackStep> initialFallbackChain,
        string host,
        int port)
    {
        _services = services;
        _modelIdentity = modelIdentity;
        _selection = selection;
        _preferredProvider = preferredProvider;
        _requireProvider = requireProvider;
        _epOptions = epOptions;
        _epLibraries = epLibraries;
        _session = initialSession;
        _selectedProvider = initialSelectedProvider;
        _fallbackChain = initialFallbackChain;

        Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
        Port = port;

        _listener = new HttpListener();
        string prefixHost = Host is "0.0.0.0" or "*" or "+" ? "*" : Host;
        Url = $"http://{Host}:{Port}/";
        _listener.Prefixes.Add($"http://{prefixHost}:{Port}/");
    }

    public void Start()
    {
        _listener.Start();
        _listenerLoopTask = Task.Run(() => AcceptLoopAsync(_serverCts.Token));
    }

    public async Task WaitForShutdownAsync()
    {
        await _shutdownTcs.Task.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_serverCts.IsCancellationRequested)
        {
            return;
        }

        _serverCts.Cancel();
        _shutdownTcs.TrySetResult();

        if (_listener.IsListening)
        {
            try
            {
                _listener.Stop();
            }
            catch
            {
                // listener may already be stopping
            }
        }

        if (_listenerLoopTask is not null)
        {
            try
            {
                await _listenerLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // loop cancellation is expected
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var req = context.Request;
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = req.HttpMethod.ToUpperInvariant();

            if (method == "GET" && (path is "" or "/"))
            {
                await WriteJsonAsync(context, HttpStatusCode.OK, new
                {
                    service = "LinML",
                    version = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                    status = "ready",
                    model = Path.GetFileName(_modelIdentity.Path),
                    selectedProvider = _selectedProvider.ToString(),
                    endpoints = new[]
                    {
                        "GET /health",
                        "POST /v1/models/predict",
                        "POST /shutdown"
                    }
                }, cancellationToken);
                return;
            }

            if (path is "/health" or "/v1/health")
            {
                if (method != "GET")
                {
                    await WriteJsonAsync(context, HttpStatusCode.MethodNotAllowed, new { error = "Method Not Allowed" }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(context, HttpStatusCode.OK, new
                {
                    status = "healthy",
                    model = _modelIdentity,
                    requestedProvider = _preferredProvider?.ToString() ?? "cpu",
                    selectedProvider = _selectedProvider.ToString(),
                    fallbackChain = _fallbackChain.Select(f => f.Provider.ToString()).ToList(),
                    runtimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion()
                }, cancellationToken);
                return;
            }

            if (path is "/v1/models/predict" or "/predict")
            {
                if (method != "POST")
                {
                    await WriteJsonAsync(context, HttpStatusCode.MethodNotAllowed, new { error = "Method Not Allowed" }, cancellationToken);
                    return;
                }

                await HandlePredictAsync(context, cancellationToken);
                return;
            }

            if (path is "/shutdown" or "/v1/shutdown")
            {
                if (method != "POST")
                {
                    await WriteJsonAsync(context, HttpStatusCode.MethodNotAllowed, new { error = "Method Not Allowed" }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(context, HttpStatusCode.OK, new { status = "shutting down" }, cancellationToken);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100, CancellationToken.None);
                    await StopAsync();
                });
                return;
            }

            await WriteJsonAsync(context, HttpStatusCode.NotFound, new { error = "Not Found" }, cancellationToken);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonAsync(context, HttpStatusCode.InternalServerError, new { error = ex.Message }, cancellationToken);
            }
            catch
            {
                // context might already be closed
            }
        }
    }

    private async Task HandlePredictAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            await WriteJsonAsync(context, HttpStatusCode.BadRequest, new { error = "Request body cannot be empty" }, cancellationToken);
            return;
        }

        List<TensorInput> tensorInputs;
        try
        {
            tensorInputs = InputFiles.ParseInputs(body);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, HttpStatusCode.BadRequest, new { error = $"Invalid request: {ex.Message}" }, cancellationToken);
            return;
        }

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            // Reopen session if dropped due to previous error
            if (_session is null)
            {
                var reopened = InferencePipeline.OpenSession(
                    _services, _modelIdentity, _selection, _requireProvider, _epOptions, _epLibraries);
                if (!reopened.Success)
                {
                    await WriteJsonAsync(context, HttpStatusCode.ServiceUnavailable, new { error = reopened.ErrorMessage }, cancellationToken);
                    return;
                }
                _session = reopened.Session;
                _selectedProvider = reopened.SelectedProvider;
                _fallbackChain = reopened.FallbackChain;
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var outputs = _session!.Run(tensorInputs);
                stopwatch.Stop();

                var result = new InferenceResult
                {
                    RequestedProvider = _preferredProvider ?? ProviderId.Cpu,
                    SelectedProvider = _selectedProvider,
                    FallbackChain = _fallbackChain,
                    Outputs = outputs,
                    Duration = stopwatch.Elapsed,
                    Model = _modelIdentity,
                    RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                    Diagnostics = []
                };

                await WriteJsonAsync(context, HttpStatusCode.OK, result, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Drop poisoned session so next request reopens
                _session?.Dispose();
                _session = null;

                await WriteJsonAsync(context, HttpStatusCode.InternalServerError, new
                {
                    error = $"Inference failed on {_selectedProvider}: {ex.Message}"
                }, cancellationToken);
            }
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext context,
        HttpStatusCode statusCode,
        object responseObj,
        CancellationToken cancellationToken)
    {
        try
        {
            context.Response.StatusCode = (int)statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(responseObj, InputFiles.JsonOptions);
            context.Response.ContentLength64 = jsonBytes.Length;
            await context.Response.OutputStream.WriteAsync(jsonBytes, cancellationToken);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serverCts.Cancel();
        _shutdownTcs.TrySetResult();
        try { _listener.Close(); } catch { }
        _session?.Dispose();
        _sessionLock.Dispose();
        _serverCts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }
}
