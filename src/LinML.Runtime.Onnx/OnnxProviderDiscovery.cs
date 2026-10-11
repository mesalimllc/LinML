using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Discovers available ONNX Runtime execution providers and hardware evidence.
/// </summary>
public sealed class OnnxProviderDiscovery : IProviderDiscovery
{
    private readonly ILogger<OnnxProviderDiscovery> _logger;

    public OnnxProviderDiscovery(ILogger<OnnxProviderDiscovery> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderDiscoveryReport Discover()
    {
        var diagnostics = new List<DiagnosticEntry>();
        var availableProviders = new List<ProviderId>();
        var devices = new List<DeviceIdentity>();
        var hardwareEvidence = new List<HardwareEvidence>();

        try
        {
            // Get available providers from ORT
            var ortProviders = OrtEnv.Instance().GetAvailableProviders();

            foreach (var provider in ortProviders)
            {
                availableProviders.Add(new ProviderId(provider));
                diagnostics.Add(new DiagnosticEntry
                {
                    Phase = "discovery",
                    Message = $"Found ORT provider: {provider}",
                    Severity = DiagnosticSeverity.Info
                });
            }

            // Check for NVIDIA hardware (evidence only, not proof of CUDA availability)
            var nvidiaEvidence = ProbeNvidiaHardware();
            hardwareEvidence.AddRange(nvidiaEvidence);
            devices.AddRange(ExtractDevices(nvidiaEvidence));

            return new ProviderDiscoveryReport
            {
                AvailableProviders = availableProviders,
                Devices = devices,
                HardwareEvidence = hardwareEvidence,
                RuntimeVersion = GetOrtVersion(),
                Diagnostics = diagnostics
            };
        }
        catch (Exception ex)
        {
            diagnostics.Add(new DiagnosticEntry
            {
                Phase = "discovery",
                Message = $"Discovery failed: {ex.Message}",
                Severity = DiagnosticSeverity.Error
            });

            return new ProviderDiscoveryReport
            {
                AvailableProviders = availableProviders,
                Devices = devices,
                HardwareEvidence = hardwareEvidence,
                RuntimeVersion = GetOrtVersion(),
                Diagnostics = diagnostics
            };
        }
    }

    private static string GetOrtVersion()
    {
        // ORT doesn't expose version directly in C# API; use assembly version as approximation
        var assembly = typeof(OrtEnv).Assembly;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private List<HardwareEvidence> ProbeNvidiaHardware()
    {
        var evidence = new List<HardwareEvidence>();

        // Try nvidia-smi for hardware evidence
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    evidence.Add(new HardwareEvidence
                    {
                        Kind = "nvidia-smi",
                        Description = output.Trim(),
                        Source = "nvidia-smi"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nvidia-smi probe failed (may not be installed or no NVIDIA hardware)");
        }

        return evidence;
    }

    private static List<DeviceIdentity> ExtractDevices(List<HardwareEvidence> evidence)
    {
        // Parse nvidia-smi output into device identities
        var devices = new List<DeviceIdentity>();

        foreach (var e in evidence.Where(e => e.Kind == "nvidia-smi"))
        {
            // Simple parsing: "NVIDIA GeForce RTX 5070, 617.42, 12228"
            var parts = e.Description.Split(',');
            if (parts.Length >= 3)
            {
                devices.Add(new DeviceIdentity
                {
                    Id = $"gpu{devices.Count}",
                    Name = parts[0].Trim(),
                    Vendor = "NVIDIA",
                    MemoryBytes = long.TryParse(parts[2].Trim(), out var mem) ? mem * 1024 * 1024 : null
                });
            }
        }

        return devices;
    }
}
