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
/// `linml doctor` — diagnose runtime readiness. No downloads.
/// With --model (and --inputs), runs a real per-model smoke test per provider
/// instead of the Load/Append-only probe. Inspects catalog providers and suggests
/// install commands for missing plugins.
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
        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };
        doctorCommand.Options.Add(modelOption);
        doctorCommand.Options.Add(inputsOption);
        doctorCommand.Options.Add(epLibraryOption);
        doctorCommand.Options.Add(epOptionOption);
        doctorCommand.Options.Add(rootOption);
        doctorCommand.Options.Add(jsonOption);

        doctorCommand.SetAction(parseResult =>
        {
            var model = parseResult.GetValue(modelOption);
            var inputs = parseResult.GetValue(inputsOption);
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var epOptionArgs = parseResult.GetValue(epOptionOption) ?? [];
            var root = parseResult.GetValue(rootOption);
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

                var catalog = string.IsNullOrWhiteSpace(root)
                    ? ExecutionProviderCatalog.GetDefault()
                    : ExecutionProviderCatalog.Create(root);

                // Register plugin EP libraries before discovery so they are listed.
                var epLibraries = ProviderArgs.ParseEpLibraries(epLibraryArgs);
                ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);

                // Pre-register available catalog plugins before discovery so smoke tests and discovery cover them
                foreach (var cp in catalog.FindAllProviders().OfType<PluginExecutionProvider>())
                {
                    try
                    {
                        var libs = ProviderArgs.ResolveEpLibraries(epLibraryArgs, cp.Id, root);
                        ProviderArgs.EnsureEpLibrariesRegistered(libs);
                    }
                    catch
                    {
                        // Ignore pre-registration errors; reported below
                    }
                }

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

                // Inspect catalog providers and report status & install hints
                var catalogReports = new List<CatalogDoctorReport>();
                foreach (var p in catalog.FindAllProviders())
                {
                    if (p.Source == ProviderSource.BuiltIn)
                    {
                        continue; // Built-ins are covered in readinessReports
                    }

                    var shortId = ProviderArgs.GetShortName(p.Id);
                    var spec = KnownPlugins.All.FirstOrDefault(k => k.Id == p.Id);
                    var libPath = p.LibraryPath ?? (spec is not null ? spec.FindLibrary(root) : null);

                    if (libPath is null || !File.Exists(libPath))
                    {
                        catalogReports.Add(new CatalogDoctorReport(
                            Id: shortId,
                            Name: p.Id.Value,
                            Source: "plugin",
                            Version: p.PinnedVersion,
                            Status: "NotPresent",
                            LibraryPath: null,
                            Details: "(not installed)",
                            Hint: $"Run 'linml catalog install {shortId}' to vendor missing libraries."));
                    }
                    else
                    {
                        bool isRegistered;
                        string? failureReason = null;
                        if (spec is not null && PluginEpRegistry.RegisteredNames.Contains(spec.RegistrationName))
                        {
                            isRegistered = true;
                        }
                        else
                        {
                            isRegistered = p.TryRegister(out failureReason);
                        }

                        if (!isRegistered)
                        {
                            catalogReports.Add(new CatalogDoctorReport(
                                Id: shortId,
                                Name: p.Id.Value,
                                Source: "plugin",
                                Version: p.PinnedVersion,
                                Status: "NotReady",
                                LibraryPath: libPath,
                                Details: failureReason ?? "Registration failed",
                                Hint: null));
                        }
                        else
                        {
                            var devices = p.GetDevices();
                            if (devices.Count > 0)
                            {
                                catalogReports.Add(new CatalogDoctorReport(
                                    Id: shortId,
                                    Name: p.Id.Value,
                                    Source: "plugin",
                                    Version: p.PinnedVersion,
                                    Status: "Ready",
                                    LibraryPath: libPath,
                                    Details: $"{devices.Count} device(s) detected",
                                    Hint: null));
                            }
                            else
                            {
                                string detail = p.Id == ProviderId.TensorrtRtx && PluginExecutionProvider.IsWsl()
                                    ? (libPath.Contains("trt-rtx-wsl2", StringComparison.Ordinal)
                                        ? "patched WSL2 build registered but exposes no devices (see docs/compatibility.md)"
                                        : "WSL2 virtual PCI mapping blocks device enumeration with the stock wheel; opt in with 'linml setup --trt-rtx-wsl2' (see docs/compatibility.md)")
                                    : "0 devices detected";
                                catalogReports.Add(new CatalogDoctorReport(
                                    Id: shortId,
                                    Name: p.Id.Value,
                                    Source: "plugin",
                                    Version: p.PinnedVersion,
                                    Status: "NotReady",
                                    LibraryPath: libPath,
                                    Details: detail,
                                    Hint: null));
                            }
                        }
                    }
                }

                // Native GPU runtime libraries (cuDNN, TensorRT) matter only with an NVIDIA GPU.
                var gpuDependencyReports = new List<GpuDependencyDoctorReport>();
                if (report.Devices.Any(d => string.Equals(d.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase)))
                {
                    string depsRoot = string.IsNullOrWhiteSpace(root) ? ExecutionProviderCatalog.DefaultInstallRoot : root;
                    foreach (var status in GpuDependencyLoader.GetStatus(depsRoot))
                    {
                        gpuDependencyReports.Add(new GpuDependencyDoctorReport(
                            Id: status.Id,
                            Description: status.Description,
                            Status: status.Installed ? "Present" : "NotPresent",
                            LibraryDirectory: status.LibraryDirectory,
                            Hint: status.Installed ? null : "Run 'linml setup' to vendor missing GPU libraries."));
                    }
                }

                bool vendoredPresent = gpuDependencyReports.Any(g => g.Status == "Present")
                    || catalogReports.Any(c => c.LibraryPath is not null);
                var hints = DoctorHints.Build(readinessReports, vendoredPresent, DoctorHints.LocateEnvScript());

                var result = new
                {
                    discovery = report,
                    readiness = readinessReports,
                    catalog = catalogReports,
                    gpuDependencies = gpuDependencyReports,
                    hints
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

                    if (gpuDependencyReports.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("GPU runtime libraries:");
                        foreach (var g in gpuDependencyReports)
                        {
                            Console.WriteLine(g.Status == "Present"
                                ? $"  {g.Id}: PRESENT ({g.LibraryDirectory})"
                                : $"  {g.Id}: NOT PRESENT — {g.Hint}");
                        }
                    }

                    if (catalogReports.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Catalog plugins:");
                        foreach (var c in catalogReports)
                        {
                            if (c.Status == "NotPresent")
                            {
                                Console.WriteLine($"  {c.Id}: NOT PRESENT — {c.Hint}");
                            }
                            else if (c.Status == "Ready")
                            {
                                Console.WriteLine($"  {c.Id}: READY ({c.Details})");
                            }
                            else
                            {
                                Console.WriteLine($"  {c.Id}: NOT READY ({c.Details})");
                            }
                        }
                    }

                    if (hints.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Hint:");
                        foreach (var line in hints)
                        {
                            Console.WriteLine($"  {line}");
                        }
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

public sealed record GpuDependencyDoctorReport(
    string Id,
    string Description,
    string Status,
    string? LibraryDirectory,
    string? Hint);

public sealed record CatalogDoctorReport(
    string Id,
    string Name,
    string Source,
    string? Version,
    string Status,
    string? LibraryPath,
    string? Details,
    string? Hint);
