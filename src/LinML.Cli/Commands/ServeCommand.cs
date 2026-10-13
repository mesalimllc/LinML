using LinML.Cli;
using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml serve` — hold a long-lived session and serve inference requests
/// over stdin/stdout (one JSON document per line). Shares selection, fallback,
/// and exit-code semantics with `run` via <see cref="InferencePipeline"/>.
///
/// Protocol:
///   stdin:  {"tensors":[...]} per line (same schema as --inputs files)
///   stdout: one <see cref="InferenceResult"/> JSON object per line (compact)
///   stdout: {"error":"..."} per failed request (server keeps serving)
///   stderr: startup/shutdown diagnostics only
/// EOF on stdin shuts the server down cleanly (exit 0).
///
/// No --profile: profiling ends a session's profiling state and is
/// incompatible with session reuse. Use one-shot `run --profile` instead.
/// </summary>
public static class ServeCommand
{
    public static Command Create(ServiceProvider provider)
    {
        var serveCommand = new Command("serve", "Serve inference requests over stdin/stdout with a long-lived session");

        var modelOption = new Option<string>("--model") { Description = "Path to the ONNX model file", Required = true };
        var providerOption = new Option<string?>("--provider") { Description = "Preferred provider (cpu, cuda, trt, trt-rtx, openvino, qnn, migraphx)" };
        var requireProviderOption = new Option<bool>("--require-provider") { Description = "Fail if the preferred provider is unavailable" };
        var warmupInputsOption = new Option<string?>("--warmup-inputs") { Description = "Path to inputs JSON for a warmup run at startup (fail fast on error)" };
        var epOptionOption = new Option<string[]>("--ep-option") { Description = "Provider option: key=value (all providers) or provider:key=value (scoped, repeatable)" };
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable)" };
        var portOption = new Option<int?>("--port") { Description = "Port to listen on for HTTP REST daemon (e.g. 8080). If omitted, serves over stdin/stdout." };
        var hostOption = new Option<string?>("--host") { Description = "Host address to bind HTTP daemon to (default: 127.0.0.1)" };

        serveCommand.Options.Add(modelOption);
        serveCommand.Options.Add(providerOption);
        serveCommand.Options.Add(requireProviderOption);
        serveCommand.Options.Add(warmupInputsOption);
        serveCommand.Options.Add(epOptionOption);
        serveCommand.Options.Add(epLibraryOption);
        serveCommand.Options.Add(portOption);
        serveCommand.Options.Add(hostOption);

        serveCommand.SetAction(async parseResult =>
        {
            var model = parseResult.GetValue(modelOption)!;
            var providerName = parseResult.GetValue(providerOption);
            var requireProvider = parseResult.GetValue(requireProviderOption);
            var warmupInputs = parseResult.GetValue(warmupInputsOption);
            var epOptionArgs = parseResult.GetValue(epOptionOption) ?? [];
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var port = parseResult.GetValue(portOption);
            var host = parseResult.GetValue(hostOption) ?? "127.0.0.1";

            try
            {
                if (port.HasValue && (port.Value <= 0 || port.Value > 65535))
                {
                    Console.Error.WriteLine($"Error: Invalid port number: {port.Value}. Port must be between 1 and 65535.");
                    return 2;
                }

                if (!File.Exists(model))
                {
                    Console.Error.WriteLine($"Error: Model file not found: {model}");
                    return 2;
                }
                if (warmupInputs is not null && !File.Exists(warmupInputs))
                {
                    Console.Error.WriteLine($"Error: Warmup inputs file not found: {warmupInputs}");
                    return 2;
                }

                var preferredProvider = ProviderArgs.ParseProvider(providerName);
                var explicitLibraries = ProviderArgs.ParseEpLibraries(epLibraryArgs);
                ProviderArgs.EnsureEpLibrariesRegistered(explicitLibraries);

                var epLibraries = ProviderArgs.ResolveEpLibraries(epLibraryArgs, preferredProvider);
                try
                {
                    ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);
                }
                catch (Exception ex)
                {
                    if (requireProvider)
                    {
                        Console.Error.WriteLine($"Error: {ex.Message}");
                        return 3;
                    }
                    epLibraries = explicitLibraries;
                }
                var epOptions = ProviderArgs.ParseEpOptions(epOptionArgs);

                var modelIdentity = await InputFiles.IdentifyModelAsync(model);

                var selection = InferencePipeline.Select(
                    provider, modelIdentity, preferredProvider, requireProvider, epOptions);
                if (!selection.Decision.Success)
                {
                    var errorMsg = InferencePipeline.FormatSelectionFailure(selection.Decision.FailureReason, preferredProvider);
                    Console.Error.WriteLine($"Error: {errorMsg}");
                    return 3;
                }

                var opened = InferencePipeline.OpenSession(
                    provider, modelIdentity, selection, requireProvider, epOptions, epLibraries);
                if (!opened.Success)
                {
                    Console.Error.WriteLine($"Error: {opened.ErrorMessage}");
                    return opened.ExitCode;
                }

                IInferenceSession? session = opened.Session;
                var selectedProvider = opened.SelectedProvider;
                var fallbackChain = opened.FallbackChain;

                try
                {
                    // Warmup: pay JIT/context/engine-build cost once, fail fast.
                    if (warmupInputs is not null)
                    {
                        var warmupTensors = await InputFiles.ReadInputsAsync(warmupInputs);
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            session!.Run(warmupTensors);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.Error.WriteLine($"Error: Warmup run failed on {selectedProvider}: {ex.Message}");
                            return 1;
                        }
                        stopwatch.Stop();
                        Console.Error.WriteLine($"Warmup: {selectedProvider} ready in {stopwatch.Elapsed}");
                    }

                    if (port.HasValue)
                    {
                        await using var server = new ServeHttpServer(
                            provider,
                            modelIdentity,
                            selection,
                            preferredProvider,
                            requireProvider,
                            epOptions,
                            epLibraries,
                            session,
                            selectedProvider,
                            fallbackChain,
                            host,
                            port.Value);

                        // Ownership of initial session transferred to server
                        session = null;

                        using var cts = new CancellationTokenSource();
                        ConsoleCancelEventHandler onCancel = (_, e) =>
                        {
                            e.Cancel = true;
                            cts.Cancel();
                            _ = server.StopAsync();
                        };
                        Console.CancelKeyPress += onCancel;

                        try
                        {
                            server.Start();
                            Console.Error.WriteLine($"Serving HTTP: {selectedProvider} listening on {server.Url}");
                            Console.Error.WriteLine("Endpoints: GET /health, POST /v1/models/predict, POST /shutdown");

                            await server.WaitForShutdownAsync();
                            return 0;
                        }
                        finally
                        {
                            Console.CancelKeyPress -= onCancel;
                            await server.StopAsync();
                        }
                    }

                    Console.Error.WriteLine($"Serving: {selectedProvider} (requested: {preferredProvider?.ToString() ?? "auto"}). Awaiting requests on stdin.");

                    string? line;
                    while ((line = await Console.In.ReadLineAsync()) is not null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        List<TensorInput> tensorInputs;
                        try
                        {
                            tensorInputs = InputFiles.ParseInputs(line);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(
                                new { error = $"Invalid request: {ex.Message}" },
                                InputFiles.JsonLineOptions));
                            continue;
                        }

                        // Reopen the session if a previous request poisoned it.
                        if (session is null)
                        {
                            var reopened = InferencePipeline.OpenSession(
                                provider, modelIdentity, selection, requireProvider, epOptions, epLibraries);
                            if (!reopened.Success)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(
                                    new { error = reopened.ErrorMessage },
                                    InputFiles.JsonLineOptions));
                                continue;
                            }
                            session = reopened.Session;
                            selectedProvider = reopened.SelectedProvider;
                            fallbackChain = reopened.FallbackChain;
                        }

                        try
                        {
                            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                            var outputs = session!.Run(tensorInputs);
                            stopwatch.Stop();

                            var result = new InferenceResult
                            {
                                RequestedProvider = preferredProvider,
                                SelectedProvider = selectedProvider,
                                FallbackChain = fallbackChain,
                                Outputs = outputs,
                                Duration = stopwatch.Elapsed,
                                Model = modelIdentity,
                                RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                                Diagnostics = []
                            };
                            Console.WriteLine(JsonSerializer.Serialize(result, InputFiles.JsonLineOptions));
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(
                                new { error = $"Inference failed on {selectedProvider}: {ex.Message}" },
                                InputFiles.JsonLineOptions));
                            // The session may be poisoned (e.g. CUDA errors stick);
                            // drop it so the next request reopens cleanly.
                            session?.Dispose();
                            session = null;
                        }
                    }

                    return 0;
                }
                finally
                {
                    session?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Error: Operation cancelled.");
                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return serveCommand;
    }
}
