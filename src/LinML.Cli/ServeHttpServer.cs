using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LinML.Cli;

/// <summary>
/// Lightweight HTTP/REST inference daemon for <c>linml serve --port &lt;port&gt;</c>.
/// Hosts JSON REST endpoints for health checks, batch/streaming inference,
/// and graceful shutdown.
/// </summary>
internal sealed class ServeHttpServer : IAsyncDisposable, IDisposable
{
    private const long MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB

    private readonly ServiceProvider _services;
    private readonly ModelIdentity _modelIdentity;
    private readonly SelectionOutcome _selection;
    private readonly ProviderId? _preferredProvider;
    private readonly bool _requireProvider;
    private readonly IReadOnlyDictionary<string, string> _epOptions;
    private readonly IReadOnlyDictionary<string, string> _epLibraries;

    private HttpListener _listener;
    private readonly string _primaryPrefix;
    private readonly string? _localhostPrefix;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly TaskCompletionSource _shutdownTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allRequestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _serverCts = new();

    private IInferenceSession? _session;
    private ProviderId _selectedProvider;
    private List<FallbackStep> _fallbackChain;
    private Task? _listenerLoopTask;
    private int _activeRequests;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }
    public string Url { get; }
    public bool IsListening => _listener.IsListening;

    /// <summary>
    /// True when the extra http://localhost prefix could not be bound at start and was dropped, so only the
    /// primary address serves. Requests by the 127.0.0.1 address still work.
    /// </summary>
    public bool LocalhostDropped { get; private set; }

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

        string prefixHost;
        if (Host is "0.0.0.0" or "*" or "+")
        {
            prefixHost = "*";
        }
        else if (Host.Contains(':') && !Host.StartsWith('['))
        {
            prefixHost = $"[{Host}]";
        }
        else
        {
            prefixHost = Host;
        }

        string urlHost = prefixHost == "*" ? "127.0.0.1" : prefixHost;
        Url = $"http://{urlHost}:{Port}/";
        _primaryPrefix = $"http://{prefixHost}:{Port}/";

        // HttpListener routes by the Host header, so a request to http://localhost:<port> is rejected
        // unless localhost is a registered prefix too. Only the 127.0.0.1 host needs this extra prefix.
        _localhostPrefix = Host == "127.0.0.1" ? $"http://localhost:{Port}/" : null;

        _listener = CreateListener(includeLocalhost: _localhostPrefix is not null);
    }

    private HttpListener CreateListener(bool includeLocalhost)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(_primaryPrefix);
        if (includeLocalhost && _localhostPrefix is not null)
        {
            listener.Prefixes.Add(_localhostPrefix);
        }

        return listener;
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException) when (_localhostPrefix is not null)
        {
            // localhost can resolve to a second address (often ::1) that this host cannot bind, or that another
            // process already holds. Serve the primary address alone; fail only if that address fails too.
            _listener.Close();
            _listener = CreateListener(includeLocalhost: false);
            LocalhostDropped = true;
            _listener.Start();
        }

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
            await _shutdownTcs.Task.ConfigureAwait(false);
            return;
        }

        _serverCts.Cancel();

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

        // Wait up to 5 seconds for in-flight requests to complete
        if (Volatile.Read(ref _activeRequests) > 0)
        {
            using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _allRequestsDrained.Task.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Proceed with shutdown if drain timeout expires
            }
        }

        _shutdownTcs.TrySetResult();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
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

                Interlocked.Increment(ref _activeRequests);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleContextAsync(context, cancellationToken);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref _activeRequests) == 0 && _serverCts.IsCancellationRequested)
                        {
                            _allRequestsDrained.TrySetResult();
                        }
                    }
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _shutdownTcs.TrySetException(ex);
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
                    processId = Environment.ProcessId,
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

                bool isHealthy = _session is not null;
                var statusCode = isHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
                await WriteJsonAsync(context, statusCode, new
                {
                    status = isHealthy ? "healthy" : "unhealthy",
                    processId = Environment.ProcessId,
                    model = _modelIdentity,
                    requestedProvider = _preferredProvider?.ToString(),
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

                if (!context.Request.IsLocal)
                {
                    await WriteJsonAsync(context, HttpStatusCode.Forbidden, new { error = "Shutdown is only permitted from loopback" }, cancellationToken);
                    return;
                }

                await WriteJsonAsync(context, HttpStatusCode.OK, new { status = "shutting down" }, cancellationToken);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(50, CancellationToken.None);
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
        if (context.Request.ContentLength64 > MaxRequestBodySize)
        {
            await WriteJsonAsync(context, HttpStatusCode.RequestEntityTooLarge, new { error = "Request payload exceeds maximum size limit (100MB)" }, cancellationToken);
            return;
        }

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

        InferenceResult? result = null;
        string? clientError = null;
        string? serverError = null;
        HttpStatusCode statusCode = HttpStatusCode.OK;

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
                    statusCode = HttpStatusCode.ServiceUnavailable;
                    serverError = reopened.ErrorMessage;
                }
                else
                {
                    _session = reopened.Session;
                    _selectedProvider = reopened.SelectedProvider;
                    _fallbackChain = reopened.FallbackChain;
                }
            }

            if (serverError is null && _session is not null)
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var outputs = _session.Run(tensorInputs);
                    stopwatch.Stop();

                    result = new InferenceResult
                    {
                        RequestedProvider = _preferredProvider,
                        SelectedProvider = _selectedProvider,
                        FallbackChain = _fallbackChain,
                        Outputs = outputs,
                        Duration = stopwatch.Elapsed,
                        Model = _modelIdentity,
                        RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                        Diagnostics = []
                    };
                }
                catch (ArgumentException ex)
                {
                    // Invalid input shapes/dimensions: client error, do not drop session
                    statusCode = HttpStatusCode.BadRequest;
                    clientError = $"Invalid tensor input: {ex.Message}";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Unexpected inference failure: drop poisoned session so next request reopens
                    _session?.Dispose();
                    _session = null;

                    statusCode = HttpStatusCode.InternalServerError;
                    serverError = $"Inference failed on {_selectedProvider}: {ex.Message}";
                }
            }
        }
        finally
        {
            _sessionLock.Release();
        }

        // Response write occurs OUTSIDE _sessionLock so slow clients don't block inference
        if (result is not null)
        {
            await WriteJsonAsync(context, HttpStatusCode.OK, result, cancellationToken);
        }
        else if (clientError is not null)
        {
            await WriteJsonAsync(context, statusCode, new { error = clientError }, cancellationToken);
        }
        else
        {
            await WriteJsonAsync(context, statusCode, new { error = serverError ?? "Internal error" }, cancellationToken);
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
        _allRequestsDrained.TrySetResult();
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
