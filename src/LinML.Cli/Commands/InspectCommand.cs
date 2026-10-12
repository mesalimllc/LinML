using LinML.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;

namespace LinML.Cli.Commands;

/// <summary>
/// Implements `linml inspect --model &lt;path&gt; [--json]`:
/// Inspects ONNX model graph structure, input/output tensors, opset versions,
/// custom metadata, and execution provider compatibility warnings without running inference.
/// </summary>
internal static class InspectCommand
{
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var modelOption = new Option<string>("--model")
        {
            Description = "Path to the ONNX model file to inspect"
        };

        var command = new Command("inspect", "Inspect ONNX model graph, tensor shapes, metadata, and EP compatibility")
        {
            modelOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var model = parseResult.GetValue(modelOption);
            var isJson = parseResult.GetValue(jsonOption);

            if (string.IsNullOrWhiteSpace(model))
            {
                Console.Error.WriteLine("Error: --model option is required.");
                return 2;
            }

            if (!File.Exists(model))
            {
                Console.Error.WriteLine($"Error: Model file not found: '{model}'");
                return 2;
            }

            try
            {
                var inspector = services.GetRequiredService<IModelInspector>();
                var report = await inspector.InspectAsync(model, cancellationToken);

                if (isJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(report, InputFiles.JsonOptions));
                }
                else
                {
                    PrintHumanReadableReport(report);
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: Model inspection failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void PrintHumanReadableReport(ModelInspectionReport report)
    {
        Console.WriteLine("LinML Model Inspection");
        Console.WriteLine("----------------------");
        Console.WriteLine($"Model:       {report.Model.Path}");
        Console.WriteLine($"Size:        {FormatBytes(report.Model.SizeBytes)} ({report.Model.SizeBytes:N0} bytes)");
        Console.WriteLine($"SHA-256:     {report.Model.Sha256}");
        Console.WriteLine($"IR Version:  v{report.IrVersion}");

        if (report.OpsetImports.Count > 0)
        {
            var opsetStr = string.Join(", ", report.OpsetImports.Select(o =>
                string.IsNullOrEmpty(o.Domain) || o.Domain == "ai.onnx"
                    ? $"ai.onnx v{o.Version}"
                    : $"{o.Domain} v{o.Version}"));
            Console.WriteLine($"Opset:       {opsetStr}");
        }

        if (!string.IsNullOrWhiteSpace(report.ProducerName))
        {
            var prodStr = string.IsNullOrWhiteSpace(report.ProducerVersion)
                ? report.ProducerName
                : $"{report.ProducerName} {report.ProducerVersion}";
            Console.WriteLine($"Producer:    {prodStr}");
        }

        if (!string.IsNullOrWhiteSpace(report.Domain))
        {
            Console.WriteLine($"Domain:      {report.Domain}");
        }

        if (!string.IsNullOrWhiteSpace(report.Description))
        {
            Console.WriteLine($"Description: {report.Description}");
        }

        Console.WriteLine();
        Console.WriteLine($"Inputs ({report.Inputs.Count}):");
        if (report.Inputs.Count == 0)
        {
            Console.WriteLine("  (None found)");
        }
        else
        {
            foreach (var input in report.Inputs)
            {
                var shapeStr = input.Shape.Count == 0 ? "scalar" : $"[{string.Join(", ", input.Shape)}]";
                var dynamicNote = input.IsDynamic ? "dynamic" : "static";
                var symStr = input.SymbolicDimensions.Count > 0 && input.SymbolicDimensions.Any(s => !string.IsNullOrEmpty(s))
                    ? $" (symbolic: {string.Join(", ", input.SymbolicDimensions)})"
                    : "";
                Console.WriteLine($"  • {input.Name}: {input.DataType} {shapeStr} ({dynamicNote}){symStr}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Outputs ({report.Outputs.Count}):");
        if (report.Outputs.Count == 0)
        {
            Console.WriteLine("  (None found)");
        }
        else
        {
            foreach (var output in report.Outputs)
            {
                var shapeStr = output.Shape.Count == 0 ? "scalar" : $"[{string.Join(", ", output.Shape)}]";
                var dynamicNote = output.IsDynamic ? "dynamic" : "static";
                var symStr = output.SymbolicDimensions.Count > 0 && output.SymbolicDimensions.Any(s => !string.IsNullOrEmpty(s))
                    ? $" (symbolic: {string.Join(", ", output.SymbolicDimensions)})"
                    : "";
                Console.WriteLine($"  • {output.Name}: {output.DataType} {shapeStr} ({dynamicNote}){symStr}");
            }
        }

        if (report.MetadataProps.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Metadata Properties ({report.MetadataProps.Count}):");
            foreach (var (k, v) in report.MetadataProps)
            {
                Console.WriteLine($"  • {k}: {v}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Graph Structure:");
        Console.WriteLine($"  • Nodes:     {report.Graph.NodeCount}");
        Console.WriteLine($"  • Operators: {report.Graph.Operators.Count} distinct ({string.Join(", ", report.Graph.Operators)})");

        Console.WriteLine();
        Console.WriteLine("Execution Provider Compatibility:");
        if (report.CompatibilityWarnings.Count == 0)
        {
            Console.WriteLine("  ✓ All detected operators are compatible with standard execution providers.");
        }
        else
        {
            foreach (var warning in report.CompatibilityWarnings)
            {
                var icon = warning.Severity == "Warning" ? "⚠" : "ℹ";
                Console.WriteLine($"  {icon} [{warning.Provider}] {warning.Message}");
            }
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
        >= 1024 => $"{bytes / 1024.0:F2} KB",
        _ => $"{bytes} B"
    };
}
