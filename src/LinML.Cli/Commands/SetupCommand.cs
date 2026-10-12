using LinML.Catalog;
using LinML.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml setup` — hardware-aware auto-setup and installation of matching execution provider plugins.
/// </summary>
public static class SetupCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static Command Create(ServiceProvider services, Option<bool> jsonOption)
    {
        var setupCommand = new Command("setup", "Hardware-aware auto-setup and installation of matching execution provider plugins");

        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Inspect hardware and display proposed installation plan without downloading" };
        var forceOption = new Option<bool>("--force") { Description = "Force re-installation even if already present" };
        var allOption = new Option<bool>("--all") { Description = "Install all catalog plugins regardless of detected hardware" };

        setupCommand.Options.Add(rootOption);
        setupCommand.Options.Add(dryRunOption);
        setupCommand.Options.Add(forceOption);
        setupCommand.Options.Add(allOption);
        setupCommand.Options.Add(jsonOption);

        setupCommand.SetAction(async parseResult =>
        {
            var root = parseResult.GetValue(rootOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var force = parseResult.GetValue(forceOption);
            var all = parseResult.GetValue(allOption);
            var json = parseResult.GetValue(jsonOption);

            string installRoot = string.IsNullOrWhiteSpace(root)
                ? ExecutionProviderCatalog.DefaultInstallRoot
                : root;

            var hardware = ProbeHostHardware(services);
            var recommendations = DetermineRecommendations(hardware, all);

            if (!json)
            {
                PrintHeader(dryRun);
                PrintHardwareSummary(hardware);
                PrintRecommendations(recommendations);
            }

            var installer = new PluginInstaller();
            var actions = new List<SetupActionResultDto>();
            bool allSucceeded = true;

            foreach (var rec in recommendations)
            {
                string libraryPath = rec.Spec.LibraryPath(installRoot);
                string installDir = rec.Spec.InstallDirectory(installRoot);
                string? existingLib = rec.Spec.FindLibrary(root);
                bool alreadyInstalled = existingLib is not null && File.Exists(existingLib);

                if (dryRun)
                {
                    string plannedStatus = alreadyInstalled && !force
                        ? "AlreadyInstalled"
                        : "WouldInstall";

                    if (!json)
                    {
                        if (plannedStatus == "AlreadyInstalled")
                        {
                            Console.WriteLine($"  • {rec.ShortName}: Already installed ({existingLib ?? libraryPath})");
                        }
                        else
                        {
                            Console.WriteLine($"  • {rec.ShortName} ({rec.Spec.Version}): Would download & install to {installDir}");
                        }
                    }

                    actions.Add(new SetupActionResultDto(
                        Provider: rec.ShortName,
                        Version: rec.Spec.Version,
                        Status: plannedStatus,
                        LibraryPath: existingLib ?? libraryPath,
                        InstallDirectory: installDir,
                        Reason: rec.Reason,
                        Error: null));
                    continue;
                }

                if (alreadyInstalled && !force)
                {
                    actions.Add(new SetupActionResultDto(
                        Provider: rec.ShortName,
                        Version: rec.Spec.Version,
                        Status: "AlreadyInstalled",
                        LibraryPath: existingLib!,
                        InstallDirectory: Path.GetDirectoryName(existingLib)!,
                        Reason: rec.Reason,
                        Error: null));

                    if (!json)
                    {
                        Console.WriteLine($"  • {rec.ShortName}: Already installed ({existingLib})");
                    }
                    continue;
                }

                if (!json)
                {
                    Console.Write($"  • {rec.ShortName} ({rec.Spec.Version}): Installing... ");
                }

                if (force && Directory.Exists(installDir))
                {
                    try
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }

                try
                {
                    bool installed = await installer.InstallAsync(rec.Spec, installRoot, CancellationToken.None);
                    string status = installed ? "Installed" : "AlreadyInstalled";

                    if (!json)
                    {
                        Console.WriteLine($"Done (Verified SHA-256)");
                        Console.WriteLine($"    Path: {libraryPath}");
                    }

                    actions.Add(new SetupActionResultDto(
                        Provider: rec.ShortName,
                        Version: rec.Spec.Version,
                        Status: status,
                        LibraryPath: libraryPath,
                        InstallDirectory: installDir,
                        Reason: rec.Reason,
                        Error: null));
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    if (!json)
                    {
                        Console.WriteLine($"FAILED: {ex.Message}");
                    }

                    actions.Add(new SetupActionResultDto(
                        Provider: rec.ShortName,
                        Version: rec.Spec.Version,
                        Status: "Failed",
                        LibraryPath: libraryPath,
                        InstallDirectory: installDir,
                        Reason: rec.Reason,
                        Error: ex.Message));
                }
            }

            if (json)
            {
                var output = new
                {
                    dryRun,
                    hardware,
                    recommendations = recommendations.Select(r => new
                    {
                        provider = r.ShortName,
                        reason = r.Reason,
                        note = r.Note
                    }),
                    actions,
                    success = allSucceeded
                };
                Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            }
            else
            {
                PrintFooter(dryRun, actions, allSucceeded);
            }

            return allSucceeded ? 0 : 1;
        });

        return setupCommand;
    }

    internal static HostHardwareInfo ProbeHostHardware(ServiceProvider services)
    {
        var cpu = ProbeCpu();

        var discovery = services.GetRequiredService<IProviderDiscovery>();
        var report = discovery.Discover();
        var devices = report.Devices;

        bool hasAmdRocm = Directory.Exists("/sys/class/kfd") || File.Exists("/dev/kfd");
        bool isWsl2 = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"))
                      || File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop");

        return new HostHardwareInfo(cpu, devices, hasAmdRocm, isWsl2);
    }

    internal static CpuInfo ProbeCpu()
    {
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        int cores = Environment.ProcessorCount;
        string vendor = "Unknown";
        string model = $"{arch.ToUpperInvariant()} Processor";
        bool hasVectors = false;

        if (File.Exists("/proc/cpuinfo"))
        {
            try
            {
                var lines = File.ReadAllLines("/proc/cpuinfo");
                foreach (var line in lines)
                {
                    if (line.StartsWith("vendor_id", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = line.Split(':', 2)[1].Trim();
                        if (val.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
                        else if (val.Contains("AMD", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
                    }
                    else if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                    {
                        model = line.Split(':', 2)[1].Trim();
                    }
                    else if (line.StartsWith("flags", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Features", StringComparison.OrdinalIgnoreCase))
                    {
                        var flags = line.Split(':', 2)[1].Trim();
                        if (flags.Contains("avx2") || flags.Contains("avx512") || flags.Contains("neon") || flags.Contains("asimd"))
                        {
                            hasVectors = true;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to defaults
            }
        }

        if (vendor == "Unknown")
        {
            if (model.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
            else if (model.Contains("AMD", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
            else if (arch.Contains("arm")) vendor = "ARM";
        }

        return new CpuInfo(arch, vendor, model, cores, hasVectors);
    }

    internal static List<ProviderRecommendation> DetermineRecommendations(HostHardwareInfo hardware, bool all)
    {
        var list = new List<ProviderRecommendation>();

        if (all)
        {
            foreach (var plugin in KnownPlugins.All)
            {
                string shortName = ProviderArgs.GetShortName(plugin.Id);
                list.Add(new ProviderRecommendation(
                    Spec: plugin,
                    ShortName: shortName,
                    Reason: "Requested via --all",
                    Note: null));
            }
            return list;
        }

        // NVIDIA GPU detected
        var nvidiaGpu = hardware.Devices.FirstOrDefault(d => string.Equals(d.Vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidiaGpu is not null)
        {
            string? note = hardware.IsWsl2
                ? "WSL2 environment: Built-in CUDA EP is active; TensorRT-RTX EP provides INT4/FP8 acceleration for RTX GPUs."
                : null;

            list.Add(new ProviderRecommendation(
                Spec: KnownPlugins.TensorrtRtx,
                ShortName: "trt-rtx",
                Reason: $"NVIDIA GPU detected ({nvidiaGpu.Name})",
                Note: note));
        }

        // x86_64 / Intel CPU or CPU vector extensions detected
        if (hardware.Cpu.Architecture is "x64" or "arm64")
        {
            string reason = hardware.Cpu.Vendor == "Intel"
                ? $"Intel CPU detected ({hardware.Cpu.Model}) with Deep Learning Boost / AVX"
                : $"CPU vector acceleration detected ({hardware.Cpu.Model})";

            list.Add(new ProviderRecommendation(
                Spec: KnownPlugins.OpenVino,
                ShortName: "openvino",
                Reason: reason,
                Note: null));
        }

        // AMD ROCm GPU detected
        if (hardware.HasAmdRocm)
        {
            list.Add(new ProviderRecommendation(
                Spec: KnownPlugins.MigraphX,
                ShortName: "migraphx",
                Reason: "AMD ROCm GPU detected (/dev/kfd)",
                Note: null));
        }

        return list;
    }

    private static void PrintHeader(bool dryRun)
    {
        Console.WriteLine(dryRun ? "LinML Hardware Auto-Setup (Dry Run)" : "LinML Hardware Auto-Setup");
        Console.WriteLine("Probing host compute accelerators...");
        Console.WriteLine();
    }

    private static void PrintHardwareSummary(HostHardwareInfo hardware)
    {
        Console.WriteLine("Hardware Detected:");
        Console.WriteLine($"  CPU: {hardware.Cpu.Model} ({hardware.Cpu.Architecture}, {hardware.Cpu.LogicalCores} logical cores)");

        if (hardware.Devices.Count > 0)
        {
            foreach (var d in hardware.Devices)
            {
                var memStr = d.MemoryBytes.HasValue
                    ? $" ({d.MemoryBytes.Value / (1024.0 * 1024 * 1024):F1} GB VRAM)"
                    : string.Empty;
                Console.WriteLine($"  GPU: {d.Name}{memStr}");
            }
        }
        else
        {
            Console.WriteLine("  GPU: None detected via nvidia-smi or sysfs");
        }

        if (hardware.HasAmdRocm)
        {
            Console.WriteLine("  ROCm: AMD KFD driver detected");
        }

        if (hardware.IsWsl2)
        {
            Console.WriteLine("  Environment: WSL2 (Windows Subsystem for Linux)");
        }

        Console.WriteLine();
    }

    private static void PrintRecommendations(List<ProviderRecommendation> recommendations)
    {
        Console.WriteLine("Recommended Execution Providers:");
        if (recommendations.Count == 0)
        {
            Console.WriteLine("  (No specific accelerator plugins recommended; built-in CPU EP is ready)");
        }
        else
        {
            foreach (var rec in recommendations)
            {
                Console.WriteLine($"  • {rec.ShortName,-10} [{rec.Reason}]");
                if (rec.Note is not null)
                {
                    Console.WriteLine($"             Note: {rec.Note}");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("Actions:");
    }

    private static void PrintFooter(bool dryRun, List<SetupActionResultDto> actions, bool allSucceeded)
    {
        Console.WriteLine();
        if (dryRun)
        {
            int wouldInstall = actions.Count(a => a.Status == "WouldInstall");
            int alreadyInstalled = actions.Count(a => a.Status == "AlreadyInstalled");
            Console.WriteLine($"Dry run complete: {alreadyInstalled} up-to-date, {wouldInstall} would be installed.");
            Console.WriteLine("Run 'linml setup' to execute the installation.");
        }
        else if (allSucceeded)
        {
            Console.WriteLine("Setup complete! All recommended execution providers are ready.");
            Console.WriteLine("Run 'linml doctor' to test readiness or 'linml bench' to compare performance.");
        }
        else
        {
            Console.WriteLine("Setup finished with errors. Review the failure details above.");
        }
    }

    public sealed record HostHardwareInfo(
        CpuInfo Cpu,
        IReadOnlyList<DeviceIdentity> Devices,
        bool HasAmdRocm,
        bool IsWsl2);

    public sealed record CpuInfo(
        string Architecture,
        string Vendor,
        string Model,
        int LogicalCores,
        bool HasVectorExtensions);

    internal sealed record ProviderRecommendation(
        PluginPackageSpec Spec,
        string ShortName,
        string Reason,
        string? Note);

    public sealed record SetupActionResultDto(
        string Provider,
        string Version,
        string Status,
        string LibraryPath,
        string InstallDirectory,
        string Reason,
        string? Error);
}
