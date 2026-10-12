using LinML.Cli.Commands;
using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using LinML.Runtime.Onnx.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;

namespace LinML.Cli;

/// <summary>
/// LinML CLI entry point.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Vendored cuDNN / TensorRT must be loaded before ORT first opens a provider
        // library, so the CLI works without LD_LIBRARY_PATH from env-gpu-deps.sh.
        if (!IsInformationalInvocation(args))
        {
            // An explicit --root outranks LINML_DEPS and the default root.
            LinML.Catalog.GpuDependencyLoader.PreloadVendored(FindRootArgument(args));
        }

        await using var provider = BuildServices();

        var rootCommand = new RootCommand("LinML: Linux ONNX Runtime inference integration layer");

        // Global options (recursive = available on all subcommands)
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output machine-readable JSON to stdout",
            Recursive = true
        };
        rootCommand.Options.Add(jsonOption);

        // Subcommands
        rootCommand.Subcommands.Add(CatalogCommand.Create(jsonOption));
        rootCommand.Subcommands.Add(SetupCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(ProvidersCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(DoctorCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(RunCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(InspectCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(BenchCommand.Create(provider, jsonOption));
        rootCommand.Subcommands.Add(ServeCommand.Create(provider));

        // Version is built-in via RootCommand (--version)

        return rootCommand.Parse(args).Invoke();
    }

    /// <summary>The value of <c>--root</c> / <c>--root=</c> if present (the catalog install root).</summary>
    internal static string? FindRootArgument(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root")
            {
                return i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[i + 1] : null;
            }

            if (args[i].StartsWith("--root=", StringComparison.Ordinal))
            {
                string value = args[i]["--root=".Length..];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static bool IsInformationalInvocation(string[] args) =>
        args.Length == 0
        || args.Any(a => a is "-h" or "--help" or "-?" or "--version");

    internal static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));

        // Contracts implementations
        services.AddSingleton<IProviderDiscovery, OnnxProviderDiscovery>();
        services.AddSingleton<OnnxProviderDiscovery>();

        // Policy
        services.AddSingleton<ProviderSelector>();
        services.AddSingleton<FallbackPolicy>();

        // Runtime
        services.AddSingleton<IInferenceSessionFactory, OnnxSessionFactory>();
        services.AddSingleton<OnnxSessionFactory>();
        services.AddSingleton<ModelSmokeRunner>();
        services.AddSingleton<IModelInspector, OnnxModelInspector>();
        services.AddSingleton<OnnxModelInspector>();

        // Providers
        services.AddSingleton<CudaReadinessProbe>();

        // Readiness probe adapter (wraps CudaReadinessProbe into IProviderReadinessProbe)
        services.AddSingleton<IProviderReadinessProbe, ReadinessProbeAdapter>();
    }
}

/// <summary>
/// Adapter that routes readiness probes to the appropriate provider-specific probe.
/// </summary>
internal sealed class ReadinessProbeAdapter : IProviderReadinessProbe
{
    private readonly CudaReadinessProbe _cudaProbe;
    private readonly ILogger<ReadinessProbeAdapter> _logger;

    public ReadinessProbeAdapter(CudaReadinessProbe cudaProbe, ILogger<ReadinessProbeAdapter> logger)
    {
        _cudaProbe = cudaProbe;
        _logger = logger;
    }

    public ProviderReadinessReport Probe(ProviderId provider, CancellationToken cancellationToken = default)
    {
        return provider.Value switch
        {
            "CPUExecutionProvider" => ProbeCpu(),
            "CUDAExecutionProvider" => _cudaProbe.Probe(cancellationToken),
            "OpenVINOExecutionProvider" => new LinML.Runtime.Onnx.Providers.GenericReadinessProbe(_logger).Probe(provider, cancellationToken: cancellationToken),
            // All other providers (TensorRT, TRT-RTX plugin, QNN,
            // MIGraphX, ...) get the generic Load+Append probe.
            _ => new LinML.Runtime.Onnx.Providers.GenericReadinessProbe(_logger).Probe(provider, cancellationToken: cancellationToken)
        };
    }

    private static ProviderReadinessReport ProbeCpu()
    {
        // CPU is always available in standard ORT builds. Load+Append only;
        // the Smoke phase (real inference) requires a model — see doctor --model.
        return new ProviderReadinessReport
        {
            Provider = ProviderId.Cpu,
            IsReady = true,
            PhaseResults =
            [
                new ReadinessPhaseResult { Phase = ReadinessPhase.Load, Success = true },
                new ReadinessPhaseResult { Phase = ReadinessPhase.Append, Success = true }
            ]
        };
    }
}
