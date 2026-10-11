using LinML.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli;

/// <summary>
/// Shared input-file handling for CLI commands: parsing --inputs JSON and
/// identifying --model files. Schemas are documented in docs/input-schema.md.
/// </summary>
internal static class InputFiles
{
    public static readonly JsonSerializerOptions JsonOptions = new()
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

    /// <summary>Read and parse an --inputs JSON file into tensor inputs.</summary>
    public static async Task<List<TensorInput>> ReadInputsAsync(string path)
    {
        var inputsJson = await File.ReadAllTextAsync(path);
        var inputDoc = JsonSerializer.Deserialize<InputDocument>(inputsJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse inputs JSON");

        return inputDoc.Tensors.Select(t => new TensorInput
        {
            Name = t.Name,
            DataType = ParseDataType(t.Type),
            Shape = t.Shape,
            Data = t.Data
        }).ToList();
    }

    /// <summary>Compute the identity (path, sha256, size) of a model file.</summary>
    public static async Task<ModelIdentity> IdentifyModelAsync(string path)
    {
        var modelBytes = await File.ReadAllBytesAsync(path);
        return new ModelIdentity
        {
            Path = Path.GetFullPath(path),
            Sha256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length
        };
    }

    public static TensorDataType ParseDataType(string type) => type.ToLowerInvariant() switch
    {
        "float32" or "float" => TensorDataType.Float32,
        "float16" => TensorDataType.Float16,
        "int64" or "long" => TensorDataType.Int64,
        "int32" or "int" => TensorDataType.Int32,
        _ => throw new ArgumentException($"Unknown tensor type: {type}")
    };

    public sealed record InputDocument
    {
        public List<TensorSpec> Tensors { get; init; } = [];
    }

    public sealed record TensorSpec
    {
        public string Name { get; init; } = "";
        public string Type { get; init; } = "float32";
        public List<int> Shape { get; init; } = [];
        public List<float> Data { get; init; } = [];
    }
}
