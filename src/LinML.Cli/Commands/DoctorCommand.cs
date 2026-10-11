using LinML.Cli;
using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml doctor` — diagnose runtime readiness. No downloads.
/// With --model (and --inputs), runs a real per-model smoke test per provider
/// instead of the Load/Append-only probe.
/// </summary>
public static class DoctorCommand
{
    public static Command Create(ServiceProvider provider, Option<bool> jsonOption)
    {
        var doctorCommand = new Command("doctor", "Diagnose runtime and provider readiness");

        var modelOption = new Option<string?>("--model") { Description = "Path to a model file for smoke testing" };
        var inputsOption = new Option<string?>("--inputs") { Description = "Path to inputs JSON (required with --model)" };
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable)" };
        var epOptionOption = new Option<string[]>("--ep-option") { Description = "Provider option: key=value or provider:key=value (repeatable)" };
        doctorCommand.Options.Add(modelOption);
        doctorCommand.Options.Add(inputsOption);
        doctorCommand.Options.Add(epLibraryOption);
        doctorCommand.Options.Add(epOptionOption);
        doctorCommand.Options.Add(jsonOption);

        doctorCommand.SetAction(parseResult =>
        {
            var model = parseResult.GetValue(modelOption);
            var inputs = parseResult.GetValue(inputsOption);
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var epOptionArgs = parseResult.GetValue(epOptionOption) ?? [];
            var json = parseResult.GetValue(jsonOption);

            try
            {
                if (model is not null && inputs is null)
                {
                    Console.Error.WriteLine("Error: --inputs is required with --model for smoke testing.");
                    return 2;
                }
                if (model is not null && !File.Exists(model))
                {
                    Console.Error.WriteLine($"Error: Model file not found: {model}");
                    return 2;
                }
                if (inputs is not null && !File.Exists(inputs))
                {
                    Console.Error.WriteLine($"Error: Inputs file not found: {inputs}");
                    return 2;
                }

                // Register plugin EP libraries before discovery so they are listed.
                var epLibraries = ProviderArgs.ParseEpLibraries(epLibraryArgs);
                ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);
                var epOptions = ProviderArgs.ParseEpOptions(epOptionArgs);

                var discovery = provider.GetRequiredService<IProviderDiscovery>();
                var report = discovery.Discover();

                var readinessReports = new List<ProviderReadinessReport>();

                if (model is not null)
                {
                    // Real per-model smoke per provider (Opp 4).
                    var smokeRunner = provider.GetRequiredService<ModelSmokeRunner>();
                    var modelIdentity = InputFiles.IdentifyModelAsync(model).GetAwaiter().GetResult();
                    var tensorInputs = InputFiles.ReadInputsAsync(inputs!).GetAwaiter().GetResult();

                    foreach (var p in report.AvailableProviders)
                    {
                        var scopedOptions = ProviderArgs.OptionsForProvider(epOptions, p);
                        readinessReports.Add(smokeRunner.RunSmoke(
                            p, modelIdentity, tensorInputs,
                            options: scopedOptions, epLibraries: epLibraries));
                    }
                }
                else
                {
                    // Probe readiness for all discovered providers (no model: Load/Append only).
                    var probe = provider.GetRequiredService<IProviderReadinessProbe>();
                    foreach (var p in report.AvailableProviders)
                    {
                        var readiness = probe.Probe(p);
                        readinessReports.Add(readiness);
                    }
                }

                var result = new
                {
                    discovery = report,
                    readiness = readinessReports
                };

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                }
                else
                {
                    Console.WriteLine($"Runtime: {report.RuntimeVersion}");
                    if (model is not null)
                    {
                        Console.WriteLine($"Smoke model: {model}");
                    }
                    Console.WriteLine("Provider readiness:");
                    foreach (var r in readinessReports)
                    {
                        var status = r.IsReady ? "READY" : $"NOT READY ({r.FailedPhase}: {r.FailureReason})";
                        Console.WriteLine($"  {r.Provider}: {status}");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return doctorCommand;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new ProviderIdJsonConverter(),
            new JsonStringEnumConverter()
        }
    };
}
