using LinML.Catalog;
using LinML.Cli;
using LinML.Contracts;
using LinML.Runtime.Onnx;
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
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable)" };
        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };
        listCommand.Options.Add(epLibraryOption);
        listCommand.Options.Add(rootOption);

        listCommand.SetAction(parseResult =>
        {
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var root = parseResult.GetValue(rootOption);
            var json = parseResult.GetValue(jsonOption);

            // Register plugin EP libraries before discovery so they are listed, the same
            // way `doctor` does, so the two commands report the same providers.
            var epLibraries = ProviderArgs.ParseEpLibraries(epLibraryArgs);
            ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);
            ProviderArgs.RegisterCatalogPlugins(epLibraryArgs, string.IsNullOrWhiteSpace(root) ? null : root);

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
                    // A plugin can be registered yet expose no devices on this host; say so, since
                    // selecting it will not run on this host.
                    bool plugin = KnownPlugins.All.Any(s => s.Id == p);
                    string note = plugin && !ProviderListing.HasEpDevices(p) ? " (registered; no EP devices on this host)" : string.Empty;
                    Console.WriteLine($"  - {p}{note}");
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
