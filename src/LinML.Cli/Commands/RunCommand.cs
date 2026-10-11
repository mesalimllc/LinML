using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        var providerOption = new Option<string?>("--provider") { Description = "Preferred provider (cpu, cuda)" };
        var requireProviderOption = new Option<bool>("--require-provider") { Description = "Fail if the preferred provider is unavailable" };

        runCommand.Options.Add(modelOption);
        runCommand.Options.Add(inputsOption);
        runCommand.Options.Add(providerOption);
        runCommand.Options.Add(requireProviderOption);
        runCommand.Options.Add(jsonOption);

        runCommand.SetAction(async parseResult =>
        {
            var model = parseResult.GetValue(modelOption)!;
            var inputs = parseResult.GetValue(inputsOption)!;
            var providerName = parseResult.GetValue(providerOption);
            var requireProvider = parseResult.GetValue(requireProviderOption);
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

                // Compute model identity
                var modelBytes = await File.ReadAllBytesAsync(model);
                var sha256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
                var modelIdentity = new ModelIdentity
                {
                    Path = Path.GetFullPath(model),
                    Sha256 = sha256,
                    SizeBytes = modelBytes.Length
                };

                // Parse inputs
                var inputsJson = await File.ReadAllTextAsync(inputs);
                var inputDoc = JsonSerializer.Deserialize<InputDocument>(inputsJson, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to parse inputs JSON");

                var tensorInputs = inputDoc.Tensors.Select(t => new TensorInput
                {
                    Name = t.Name,
                    DataType = ParseDataType(t.Type),
                    Shape = t.Shape,
                    Data = t.Data
                }).ToList();

                // Build request
                var preferredProvider = providerName?.ToLowerInvariant() switch
                {
                    "cpu" => ProviderId.Cpu,
                    "cuda" => ProviderId.Cuda,
                    null => null,
                    _ => throw new ArgumentException($"Unknown provider: {providerName}")
                };

                var request = new InferenceRequest
                {
                    Model = modelIdentity,
                    Inputs = tensorInputs,
                    PreferredProvider = preferredProvider,
                    RequireProvider = requireProvider
                };

                // Select provider
                var selector = provider.GetRequiredService<ProviderSelector>();
                var fallbackPolicy = provider.GetRequiredService<FallbackPolicy>();

                var candidates = fallbackPolicy.BuildCandidateList(request);
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

                // Run inference
                var factory = provider.GetRequiredService<IInferenceSessionFactory>();
                using var session = factory.CreateSession(modelIdentity, decision.SelectedProvider!);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var outputs = session.Run(tensorInputs);
                stopwatch.Stop();

                var result = new InferenceResult
                {
                    RequestedProvider = preferredProvider ?? ProviderId.Cpu,
                    SelectedProvider = decision.SelectedProvider!,
                    Outputs = outputs,
                    Duration = stopwatch.Elapsed,
                    Model = modelIdentity,
                    RuntimeVersion = OnnxRuntimeDiagnostics.GetRuntimeVersion()
                };

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                }
                else
                {
                    Console.WriteLine($"Provider: {result.SelectedProvider} (requested: {result.RequestedProvider})");
                    foreach (var output in result.Outputs)
                    {
                        Console.WriteLine($"Output '{output.Name}': [{string.Join(", ", output.Data)}]");
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

    private static TensorDataType ParseDataType(string type) => type.ToLowerInvariant() switch
    {
        "float32" or "float" => TensorDataType.Float32,
        "float16" => TensorDataType.Float16,
        "int64" or "long" => TensorDataType.Int64,
        "int32" or "int" => TensorDataType.Int32,
        _ => throw new ArgumentException($"Unknown tensor type: {type}")
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new ProviderIdJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private sealed record InputDocument
    {
        public List<TensorSpec> Tensors { get; init; } = [];
    }

    private sealed record TensorSpec
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "float32";
        public List<int> Shape { get; init; } = [];
        public List<float> Data { get; init; } = [];
    }
}
