using LinML.Catalog;
using LinML.Contracts;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml catalog` — manage and inspect the LinML execution provider catalog.
/// </summary>
public static class CatalogCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Command Create(Option<bool> jsonOption)
    {
        var catalogCommand = new Command("catalog", "Inspect and install execution provider plugins");

        catalogCommand.Subcommands.Add(CreateListCommand(jsonOption));
        catalogCommand.Subcommands.Add(CreateInstallCommand(jsonOption));

        return catalogCommand;
    }

    private static Command CreateListCommand(Option<bool> jsonOption)
    {
        var listCommand = new Command("list", "List all execution providers in the catalog");
        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };
        listCommand.Options.Add(rootOption);
        listCommand.Options.Add(jsonOption);

        listCommand.SetAction(parseResult =>
        {
            var root = parseResult.GetValue(rootOption);
            var json = parseResult.GetValue(jsonOption);

            var catalog = string.IsNullOrWhiteSpace(root)
                ? ExecutionProviderCatalog.GetDefault()
                : ExecutionProviderCatalog.Create(root);

            var providers = catalog.FindAllProviders();

            if (json)
            {
                var items = providers.Select(p => new CatalogProviderDto(
                    Id: GetShortName(p.Id),
                    Name: p.Id.Value,
                    Source: p.Source == ProviderSource.BuiltIn ? "built-in" : "plugin",
                    Version: p.PinnedVersion,
                    Status: p.ReadyState.ToString(),
                    LibraryPath: p.LibraryPath
                )).ToList();

                Console.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
                return 0;
            }

            string displayRoot = string.IsNullOrWhiteSpace(root)
                ? ExecutionProviderCatalog.DefaultInstallRoot
                : root;

            Console.WriteLine("LinML Execution Provider Catalog");
            Console.WriteLine($"Install root: {displayRoot}");
            Console.WriteLine();

            Console.WriteLine($"{"PROVIDER",-16} {"TYPE",-10} {"VERSION",-10} {"STATUS",-18} {"LOCATION / DETAILS"}");
            Console.WriteLine(new string('-', 85));

            foreach (var p in providers)
            {
                string shortName = GetShortName(p.Id);
                string source = p.Source == ProviderSource.BuiltIn ? "built-in" : "plugin";
                string version = p.PinnedVersion ?? "-";
                string status = p.ReadyState.ToString();
                string details = p.Source == ProviderSource.BuiltIn
                    ? "(built-in with ORT)"
                    : (p.LibraryPath is not null && File.Exists(p.LibraryPath) ? p.LibraryPath : $"(not installed; run 'linml catalog install {shortName}')");

                Console.WriteLine($"{shortName,-16} {source,-10} {version,-10} {status,-18} {details}");
            }

            return 0;
        });

        return listCommand;
    }

    private static Command CreateInstallCommand(Option<bool> jsonOption)
    {
        var installCommand = new Command("install", "Install plugin execution providers from the catalog");
        var providerArg = new Argument<string?>("provider")
        {
            Description = "Plugin provider to install: openvino, trt-rtx, migraphx, or all (default: all)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };
        var forceOption = new Option<bool>("--force") { Description = "Force re-download and re-installation even if already present" };

        installCommand.Arguments.Add(providerArg);
        installCommand.Options.Add(rootOption);
        installCommand.Options.Add(forceOption);
        installCommand.Options.Add(jsonOption);

        installCommand.SetAction(async parseResult =>
        {
            var providerName = parseResult.GetValue(providerArg);
            var root = parseResult.GetValue(rootOption);
            var force = parseResult.GetValue(forceOption);
            var json = parseResult.GetValue(jsonOption);

            string installRoot = string.IsNullOrWhiteSpace(root)
                ? ExecutionProviderCatalog.DefaultInstallRoot
                : root;

            var targets = ResolveInstallTargets(providerName, out string? errorMessage, out bool isBuiltIn);
            if (errorMessage is not null)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { error = errorMessage }, JsonOptions));
                }
                else
                {
                    Console.Error.WriteLine($"Error: {errorMessage}");
                }
                return 1;
            }

            if (isBuiltIn)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        provider = providerName,
                        status = "BuiltIn",
                        message = $"Provider '{providerName}' is built into ONNX Runtime and does not require downloading."
                    }, JsonOptions));
                }
                else
                {
                    Console.WriteLine($"Provider '{providerName}' is built into ONNX Runtime and does not require downloading.");
                }
                return 0;
            }

            var installer = new PluginInstaller();
            var results = new List<InstallResultDto>();
            bool allSuccess = true;

            foreach (var spec in targets)
            {
                string shortName = GetShortName(spec.Id);
                string libraryPath = spec.LibraryPath(installRoot);
                string installDir = spec.InstallDirectory(installRoot);

                if (!json)
                {
                    Console.WriteLine($"=== Installing {shortName} ({spec.Version}) ===");
                }

                try
                {
                    bool wasDownloaded = await installer.InstallAsync(spec, installRoot, CancellationToken.None, force: force);
                    string status = wasDownloaded ? "Installed" : "AlreadyInstalled";

                    if (!json)
                    {
                        if (wasDownloaded)
                        {
                            Console.WriteLine($"  Installed {shortName} {spec.Version} to {installDir}");
                            Console.WriteLine($"  Library: {libraryPath}");
                        }
                        else
                        {
                            Console.WriteLine($"  Already installed: {libraryPath}");
                        }
                        Console.WriteLine();
                    }

                    results.Add(new InstallResultDto(shortName, spec.Version, status, libraryPath, installDir, null));
                }
                catch (Exception ex)
                {
                    allSuccess = false;
                    if (!json)
                    {
                        Console.Error.WriteLine($"  Error installing {shortName}: {ex.Message}");
                        Console.WriteLine();
                    }
                    results.Add(new InstallResultDto(shortName, spec.Version, "Failed", libraryPath, installDir, ex.Message));
                }
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            }

            return allSuccess ? 0 : 1;
        });

        return installCommand;
    }

    internal static IReadOnlyList<PluginPackageSpec> ResolveInstallTargets(
        string? providerName,
        out string? errorMessage,
        out bool isBuiltIn)
    {
        errorMessage = null;
        isBuiltIn = false;

        if (string.IsNullOrWhiteSpace(providerName) || string.Equals(providerName, "all", StringComparison.OrdinalIgnoreCase))
        {
            return KnownPlugins.All;
        }

        string trimmed = providerName.Trim();

        if (trimmed.Equals("cpu", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("trt", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("tensorrt", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cpuexecutionprovider", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cudaexecutionprovider", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("tensorrtexecutionprovider", StringComparison.OrdinalIgnoreCase))
        {
            isBuiltIn = true;
            return [];
        }

        ProviderId? parsedId;
        try
        {
            parsedId = ProviderArgs.ParseProvider(trimmed);
        }
        catch
        {
            parsedId = null;
        }

        if (parsedId is not null)
        {
            var match = KnownPlugins.All.FirstOrDefault(p => p.Id == parsedId);
            if (match is not null)
            {
                return [match];
            }
        }

        errorMessage = $"Unknown plugin provider '{providerName}'. Available plugins: openvino, trt-rtx, migraphx, all.";
        return [];
    }

    internal static string GetShortName(ProviderId id) => ProviderArgs.GetShortName(id);

    private sealed record CatalogProviderDto(
        string Id,
        string Name,
        string Source,
        string? Version,
        string Status,
        string? LibraryPath);

    private sealed record InstallResultDto(
        string Provider,
        string Version,
        string Status,
        string LibraryPath,
        string InstallDirectory,
        string? Error);
}
