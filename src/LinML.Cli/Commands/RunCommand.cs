using LinML.Cli;
using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml run` — run inference with a model (one-shot).
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
        var epOptionOption = new Option<string[]>("--ep-option") { Description = "Provider option: key=value (all providers) or provider:key=value (scoped, repeatable), e.g. --ep-option trt:trt_fp16_enable=1" };
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable), e.g. --ep-library NvTensorRTRTXExecutionProvider=/path/libonnxruntime_providers_nv_tensorrt_rtx.so" };

        runCommand.Options.Add(modelOption);
        runCommand.Options.Add(inputsOption);
        runCommand.Options.Add(providerOption);
        runCommand.Options.Add(requireProviderOption);
        runCommand.Options.Add(profileOption);
        runCommand.Options.Add(epOptionOption);
        runCommand.Options.Add(epLibraryOption);

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

                var preferredProvider = ProviderArgs.ParseProvider(providerName);
                // An inherited mapping (LINML_EP_LIBRARIES) for another provider that fails to
                // load here is skipped with a warning; the requested provider's own is not.
                var explicitLibraries = ProviderArgs.RegisterEpLibraries(
                    ProviderArgs.ParseEpLibraries(epLibraryArgs), epLibraryArgs, warnings: Console.Error);

                IReadOnlyDictionary<string, string> epLibraries;
                try
                {
                    epLibraries = ProviderArgs.RegisterEpLibraries(
                        ProviderArgs.ResolveEpLibraries(epLibraryArgs, preferredProvider),
                        epLibraryArgs, requiredProvider: preferredProvider);
                }
                catch (Exception ex)
                {
                    if (requireProvider)
                    {
                        var hint = ProviderArgs.ProviderUnavailableHint(preferredProvider);
                        Console.Error.WriteLine($"Error: {ex.Message}{(hint is null ? string.Empty : " " + hint)}");
                        return 3;
                    }
                    epLibraries = explicitLibraries;
                }
                var epOptions = ProviderArgs.ParseEpOptions(epOptionArgs);

                var modelIdentity = await InputFiles.IdentifyModelAsync(model);
                var tensorInputs = await InputFiles.ReadInputsAsync(inputs);

                var outcome = InferencePipeline.Execute(
                    provider, modelIdentity, tensorInputs, preferredProvider,
                    requireProvider, epOptions, epLibraries, profile);

                if (!outcome.Success)
                {
                    Console.Error.WriteLine($"Error: {outcome.ErrorMessage}");
                    return outcome.ExitCode;
                }

                var result = new InferenceResult
                {
                    RequestedProvider = preferredProvider,
                    SelectedProvider = outcome.SelectedProvider,
                    FallbackChain = outcome.FallbackChain,
                    Outputs = outcome.Outputs!,
                    Duration = outcome.Duration,
                    Model = modelIdentity,
                    RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion(),
                    Diagnostics = outcome.Diagnostics,
                    NodePlacements = outcome.NodePlacements ?? []
                };

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, InputFiles.JsonOptions));
                }
                else
                {
                    Console.WriteLine($"Provider: {result.SelectedProvider} (requested: {result.RequestedProvider?.ToString() ?? "auto"})");
                    foreach (var fallback in result.FallbackChain)
                    {
                        Console.WriteLine($"Fallback: {fallback.Provider} failed: {fallback.Reason}");
                    }
                    foreach (var output in result.Outputs)
                    {
                        Console.WriteLine($"Output '{output.Name}': [{string.Join(", ", output.Data)}]");
                    }
                    foreach (var diagnostic in outcome.Diagnostics)
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
