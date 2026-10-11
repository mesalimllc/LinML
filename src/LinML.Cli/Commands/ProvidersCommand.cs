using LinML.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml providers list` — list available execution providers.
/// </summary>
public static class ProvidersCommand
{
    public static Command Create(ServiceProvider provider, Option<bool> jsonOption)
    {
        var providersCommand = new Command("providers", "Provider discovery commands");

        var listCommand = new Command("list", "List available execution providers");
        listCommand.Options.Add(jsonOption);

        listCommand.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            var discovery = provider.GetRequiredService<IProviderDiscovery>();
            var report = discovery.Discover();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            }
            else
            {
                Console.WriteLine($"Runtime version: {report.RuntimeVersion}");
                Console.WriteLine("Available providers:");
                foreach (var p in report.AvailableProviders)
                {
                    Console.WriteLine($"  - {p}");
                }
                if (report.Devices.Count > 0)
                {
                    Console.WriteLine("Devices:");
                    foreach (var d in report.Devices)
                    {
                        Console.WriteLine($"  - {d.Name} ({d.Vendor})");
                    }
                }
            }
            return 0;
        });

        providersCommand.Subcommands.Add(listCommand);
        return providersCommand;
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
