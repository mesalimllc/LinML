using LinML.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml doctor` — diagnose runtime readiness. No downloads.
/// </summary>
public static class DoctorCommand
{
    public static Command Create(ServiceProvider provider, Option<bool> jsonOption)
    {
        var doctorCommand = new Command("doctor", "Diagnose runtime and provider readiness");

        var modelOption = new Option<string?>("--model") { Description = "Path to a model file for smoke testing" };
        doctorCommand.Options.Add(modelOption);
        doctorCommand.Options.Add(jsonOption);

        doctorCommand.SetAction(parseResult =>
        {
            var model = parseResult.GetValue(modelOption);
            var json = parseResult.GetValue(jsonOption);

            var discovery = provider.GetRequiredService<IProviderDiscovery>();
            var report = discovery.Discover();

            var readinessReports = new List<ProviderReadinessReport>();

            // Probe readiness for all discovered providers
            var probe = provider.GetRequiredService<IProviderReadinessProbe>();
            foreach (var p in report.AvailableProviders)
            {
                var readiness = probe.Probe(p);
                readinessReports.Add(readiness);
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
                Console.WriteLine("Provider readiness:");
                foreach (var r in readinessReports)
                {
                    var status = r.IsReady ? "READY" : $"NOT READY ({r.FailedPhase}: {r.FailureReason})";
                    Console.WriteLine($"  {r.Provider}: {status}");
                }
            }
            return 0;
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
