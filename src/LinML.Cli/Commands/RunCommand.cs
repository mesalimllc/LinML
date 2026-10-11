using LinML.Cli;
using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml run` — run inference with a model.
/// </summary>
public static class RunCommand
{
    public static Command Create(ServiceProvider provider, Option<bool> jsonOption)
    {
        var runCommand = new Command("run", "Run inference with a model");

        var modelOption = new Option<string>("--model") { Description = "Path to the ONNX model file", Required = true };
        var inputsOption = new Option<string>("--inputs") { Description = "Path to the inputs JSON file", Required = true };
        var providerOption = new Option<string?>("--provider") { Description = "Preferred provider (cpu, cuda, trt, trt-rtx, openvino, qnn, migraphx)" };
        var requireProviderOption = new Option<bool>("--require-provider") { Description = "Fail if the preferred provider is unavailable" };
        var profileOption = new Option<bool>("--profile") { Description = "Enable ORT profiling and report per-node provider placement" };
        var epOptionOption = new Option<string[]>("--ep-option") { Description = "Provider option as key=value (repeatable), e.g. --ep-option trt_fp16_enable=1" };
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable), e.g. --ep-library nvtensorrtrtx=/path/libonnxruntime_providers_nv_tensorrt_rtx.so" };

        runCommand.Options.Add(modelOption);
        runCommand.Options.Add(inputsOption);
        runCommand.Options.Add(providerOption);
        runCommand.Options.Add(requireProviderOption);
        runCommand.Options.Add(profileOption);
        runCommand.Options.Add(epOptionOption);
        runCommand.Options.Add(epLibraryOption);
        runCommand.Options.Add(jsonOption);

        runCommand.SetAction(async parseResult =>
        {
            var model = parseResult.GetValue(modelOption)!;
            var inputs = parseResult.GetValue(inputsOption)!;
            var providerName = parseResult.GetValue(providerOption);
            var requireProvider = parseResult.GetValue(requireProviderOption);
            var profile = parseResult.GetValue(profileOption);
            var epOptionArgs = parseResult.GetValue(epOptionOption) ?? [];
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var json = parseResult.GetValue(jsonOption);

            try
            {
                // Validate files exist
                if (!File.Exists(model))
                {
                    Console.Error.WriteLine($"Error: Model file not found: {model}");
                    return 2;
                }
                if (!File.Exists(inputs))
                {
                    Console.Error.WriteLine($"Error: Inputs file not found: {inputs}");
                    return 2;
                }

                // Register plugin EP libraries before discovery so they are listed.
                var epLibraries = ProviderArgs.ParseEpLibraries(epLibraryArgs);
                ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);
                var epOptions = ProviderArgs.ParseEpOptions(epOptionArgs);

                // Compute model identity
                var modelIdentity = await InputFiles.IdentifyModelAsync(model);

                // Parse inputs
                var tensorInputs = await InputFiles.ReadInputsAsync(inputs);

                // Build request
                var preferredProvider = ProviderArgs.ParseProvider(providerName);

                var request = new InferenceRequest
                {
                    Model = modelIdentity,
                    Inputs = tensorInputs,
                    PreferredProvider = preferredProvider,
                    RequireProvider = requireProvider,
                    Options = epOptions
                };

                // Select provider: rank the providers the runtime actually lists.
                var selector = provider.GetRequiredService<ProviderSelector>();
                var fallbackPolicy = provider.GetRequiredService<FallbackPolicy>();
                var discovery = provider.GetRequiredService<IProviderDiscovery>();

                var report = discovery.Discover();
                var candidates = fallbackPolicy.BuildCandidateList(request, report.AvailableProviders);
                var selectionRequest = new SelectionRequest
                {
                    Candidates = candidates,
                    RequireFirst = requireProvider,
                    Model = modelIdentity
                };

                var decision = selector.Select(selectionRequest);

                if (!decision.Success)
                {
                    Console.Error.WriteLine($"Error: Provider selection failed: {decision.FailureReason}");
                    return 3;
                }

                // Run inference, with execution-time fallback for soft selection:
                // a provider that passed readiness can still fail at run time
                // (e.g. CUDA libs present but no GPU). Retry remaining
                // candidates and record why, per correctness rule 3.
                var factory = provider.GetRequiredService<IInferenceSessionFactory>();
                var diagnostics = new List<DiagnosticEntry>();
                var fallbackChain = decision.RejectedCandidates
                    .Select(r => new FallbackStep { Provider = r.Provider, Reason = r.Reason })
                    .ToList();

                var attemptOrder = candidates
                    .SkipWhile(c => c != decision.SelectedProvider!)
                    .ToList();

                IReadOnlyList<TensorOutput>? outputs = null;
                var selectedProvider = decision.SelectedProvider!;
                var duration = TimeSpan.Zero;
                foreach (var attempt in attemptOrder)
                {
                    IInferenceSession? attemptSession;
                    try
                    {
                        attemptSession = factory.CreateSession(
                            modelIdentity, attempt, epOptions,
                            enableProfiling: profile, epLibraries: epLibraries);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // The provider cannot run at all: hard requirement fails
                        // honestly (3); soft selection records why and retries.
                        if (requireProvider)
                        {
                            Console.Error.WriteLine(
                                $"Error: Required provider '{attempt}' unavailable: {ex.Message}");
                            return 3;
                        }
                        fallbackChain.Add(new FallbackStep { Provider = attempt, Reason = ex.Message });
                        continue;
                    }

                    using (attemptSession)
                    {
                        try
                        {
                            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                            outputs = attemptSession.Run(tensorInputs);
                            stopwatch.Stop();
                            duration = stopwatch.Elapsed;
                            selectedProvider = attempt;
                        }
                        catch (Exception ex) when (!requireProvider && ex is not OperationCanceledException)
                        {
                            fallbackChain.Add(new FallbackStep { Provider = attempt, Reason = ex.Message });
                            continue;
                        }

                        if (profile)
                        {
                            var placements = attemptSession.EndProfiling();
                            diagnostics.Add(new DiagnosticEntry
                            {
                                Phase = "profile",
                                Message = $"ORT profile written to {attemptSession.ProfilePath}",
                                Severity = DiagnosticSeverity.Info
                            });
                            foreach (var placement in placements)
                            {
                                diagnostics.Add(new DiagnosticEntry
                                {
                                    Phase = "profile",
                                    Message = $"Node '{placement.NodeName}' ran on {placement.Provider}",
                                    Severity = DiagnosticSeverity.Info
                                });
                            }
                        }
                        break;
                    }
                }

                if (outputs is null)
                {
                    var attempted = string.Join(", ", fallbackChain.Select(f => f.Provider.ToString()));
                    Console.Error.WriteLine($"Error: All providers failed at execution: {attempted}");
                    return 1;
                }

                var result = new InferenceResult
                {
                    RequestedProvider = preferredProvider ?? ProviderId.Cpu,
                    SelectedProvider = selectedProvider,
                    FallbackChain = fallbackChain,
                    Outputs = outputs,
                    Duration = duration,
                    Model = modelIdentity,
                    RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                    Diagnostics = diagnostics
                };

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, InputFiles.JsonOptions));
                }
                else
                {
                    Console.WriteLine($"Provider: {result.SelectedProvider} (requested: {result.RequestedProvider})");
                    foreach (var fallback in result.FallbackChain)
                    {
                        Console.WriteLine($"Fallback: {fallback.Provider} failed: {fallback.Reason}");
                    }
                    foreach (var output in result.Outputs)
                    {
                        Console.WriteLine($"Output '{output.Name}': [{string.Join(", ", output.Data)}]");
                    }
                    foreach (var diagnostic in diagnostics)
                    {
                        Console.WriteLine($"[{diagnostic.Phase}] {diagnostic.Message}");
                    }
                }
                return 0;
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

        return runCommand;
    }
}