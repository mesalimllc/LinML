using LinML.Catalog;
using LinML.Contracts;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinML.Cli.Commands;

/// <summary>
/// `linml bench` — benchmark inference latency, throughput, and percentiles across execution providers.
/// </summary>
public static class BenchCommand
{
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

    public static Command Create(ServiceProvider services, Option<bool> jsonOption)
    {
        var benchCommand = new Command("bench", "Benchmark inference latency and throughput");

        var modelOption = new Option<string>("--model") { Description = "Path to the ONNX model file", Required = true };
        var inputsOption = new Option<string>("--inputs") { Description = "Path to the inputs JSON file", Required = true };
        var providerOption = new Option<string?>("--provider") { Description = "Preferred execution provider to benchmark" };
        var allReadyOption = new Option<bool>("--all-ready") { Description = "Benchmark all ready providers for side-by-side comparison" };
        var warmupOption = new Option<int>("--warmup") { Description = "Number of warmup iterations (default: 5)", DefaultValueFactory = _ => 5 };
        var iterationsOption = new Option<int>("--iterations") { Description = "Number of timed measurement iterations (default: 50)", DefaultValueFactory = _ => 50 };
        var epOptionOption = new Option<string[]>("--ep-option") { Description = "Provider option: key=value or provider:key=value (repeatable)" };
        var epLibraryOption = new Option<string[]>("--ep-library") { Description = "Plugin EP library as name=path (repeatable)" };
        var rootOption = new Option<string?>("--root") { Description = "Custom catalog install root directory" };

        benchCommand.Options.Add(modelOption);
        benchCommand.Options.Add(inputsOption);
        benchCommand.Options.Add(providerOption);
        benchCommand.Options.Add(allReadyOption);
        benchCommand.Options.Add(warmupOption);
        benchCommand.Options.Add(iterationsOption);
        benchCommand.Options.Add(epOptionOption);
        benchCommand.Options.Add(epLibraryOption);
        benchCommand.Options.Add(rootOption);

        benchCommand.SetAction(async parseResult =>
        {
            var model = parseResult.GetValue(modelOption)!;
            var inputs = parseResult.GetValue(inputsOption)!;
            var providerName = parseResult.GetValue(providerOption);
            var allReady = parseResult.GetValue(allReadyOption);
            var warmup = parseResult.GetValue(warmupOption);
            var iterations = parseResult.GetValue(iterationsOption);
            var epOptionArgs = parseResult.GetValue(epOptionOption) ?? [];
            var epLibraryArgs = parseResult.GetValue(epLibraryOption) ?? [];
            var root = parseResult.GetValue(rootOption);
            var json = parseResult.GetValue(jsonOption);

            if (!File.Exists(model))
            {
                Console.Error.WriteLine($"Error: Model file not found: {model}");
                return 2;
            }
            if (!File.Exists(inputs))
            {
                Console.Error.WriteLine($"Error: Inputs file not found: {inputs}");
                return 2;
            }
            if (warmup < 0)
            {
                Console.Error.WriteLine("Error: --warmup must be greater than or equal to 0.");
                return 2;
            }
            if (iterations < 1)
            {
                Console.Error.WriteLine("Error: --iterations must be at least 1.");
                return 2;
            }

            try
            {
                var modelIdentity = await InputFiles.IdentifyModelAsync(model);
                var tensorInputs = await InputFiles.ReadInputsAsync(inputs);
                var epOptions = ProviderArgs.ParseEpOptions(epOptionArgs);

                var targetProviders = DetermineTargetProviders(
                    services, providerName, allReady, epLibraryArgs, epOptions, modelIdentity, root);

                if (targetProviders.Count == 0)
                {
                    Console.Error.WriteLine("Error: No available execution providers to benchmark.");
                    return 3;
                }

                var factory = services.GetRequiredService<IInferenceSessionFactory>();
                var benchmarkResults = new List<BenchmarkResultDto>();

                foreach (var target in targetProviders)
                {
                    var result = RunSingleBenchmark(
                        factory, target, modelIdentity, tensorInputs, epLibraryArgs, epOptions, root, warmup, iterations);
                    benchmarkResults.Add(result);
                }

                // Sort by throughput descending (fastest provider first)
                benchmarkResults = benchmarkResults
                    .OrderByDescending(r => r.Success)
                    .ThenByDescending(r => r.ThroughputPerSecond ?? 0)
                    .ToList();

                if (json)
                {
                    var output = new
                    {
                        model = modelIdentity,
                        warmupIterations = warmup,
                        measurementIterations = iterations,
                        results = benchmarkResults
                    };
                    Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
                }
                else
                {
                    PrintHumanReadableReport(model, inputs, warmup, iterations, benchmarkResults);
                }

                return benchmarkResults.All(r => r.Success) ? 0 : (benchmarkResults.Any(r => r.Success) ? 1 : 3);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        });

        return benchCommand;
    }

    private static List<ProviderId> DetermineTargetProviders(
        ServiceProvider services,
        string? providerName,
        bool allReady,
        string[] epLibraryArgs,
        IReadOnlyDictionary<string, string> epOptions,
        ModelIdentity model,
        string? root)
    {
        var preferred = ProviderArgs.ParseProvider(providerName);

        if (allReady)
        {
            var discovery = services.GetRequiredService<IProviderDiscovery>();
            var probe = services.GetRequiredService<IProviderReadinessProbe>();

            // Register explicit libraries passed via --ep-library
            var explicitLibs = ProviderArgs.ParseEpLibraries(epLibraryArgs);
            try
            {
                ProviderArgs.EnsureEpLibrariesRegistered(explicitLibs);
            }
            catch
            {
                // Isolate explicit library registration failure
            }

            // Register candidate catalog plugins
            var catalog = string.IsNullOrWhiteSpace(root)
                ? ExecutionProviderCatalog.GetDefault()
                : ExecutionProviderCatalog.Create(root);

            foreach (var p in catalog.FindAllProviders())
            {
                if (p.Source == ProviderSource.Plugin && p.LibraryPath is not null)
                {
                    try
                    {
                        var libs = ProviderArgs.ResolveEpLibraries(epLibraryArgs, p.Id, root);
                        ProviderArgs.EnsureEpLibrariesRegistered(libs);
                    }
                    catch
                    {
                        // Isolate individual plugin registration failure so other providers can run
                    }
                }
            }

            var report = discovery.Discover();
            var readyList = new List<ProviderId>();

            foreach (var candidate in report.AvailableProviders)
            {
                var readiness = probe.Probe(candidate);
                if (readiness.IsReady)
                {
                    readyList.Add(candidate);
                }
            }

            // Ensure CPU is in list if not already present, provided its probe succeeds
            if (!readyList.Contains(ProviderId.Cpu))
            {
                var cpuReadiness = probe.Probe(ProviderId.Cpu);
                if (cpuReadiness.IsReady)
                {
                    readyList.Add(ProviderId.Cpu);
                }
            }

            return readyList;
        }

        if (preferred is not null)
        {
            return [preferred];
        }

        // Default to policy-selected provider
        var selection = InferencePipeline.Select(services, model, null, requireProvider: false, epOptions);
        if (selection.Decision.Success && selection.Decision.SelectedProvider is not null)
        {
            return [selection.Decision.SelectedProvider];
        }

        var defaultProbe = services.GetRequiredService<IProviderReadinessProbe>();
        return defaultProbe.Probe(ProviderId.Cpu).IsReady ? [ProviderId.Cpu] : [];
    }

    private static BenchmarkResultDto RunSingleBenchmark(
        IInferenceSessionFactory factory,
        ProviderId providerId,
        ModelIdentity model,
        List<TensorInput> inputs,
        string[] epLibraryArgs,
        IReadOnlyDictionary<string, string> epOptions,
        string? root,
        int warmupCount,
        int iterationCount)
    {
        try
        {
            var epLibraries = ProviderArgs.ResolveEpLibraries(epLibraryArgs, providerId, root);
            ProviderArgs.EnsureEpLibrariesRegistered(epLibraries);
            var scopedOptions = ProviderArgs.OptionsForProvider(epOptions, providerId);

            // Cold start measurement (session creation + first execution)
            var coldSw = Stopwatch.StartNew();
            using var session = factory.CreateSession(
                model, providerId, scopedOptions, enableProfiling: false, epLibraries: epLibraries);
            session.Run(inputs);
            coldSw.Stop();
            double coldStartMs = coldSw.Elapsed.TotalMilliseconds;

            // Warmup iterations: exactly warmupCount additional runs
            for (int w = 0; w < warmupCount; w++)
            {
                session.Run(inputs);
            }

            // Measurement iterations
            var latencies = new List<double>(iterationCount);
            for (int i = 0; i < iterationCount; i++)
            {
                var iterSw = Stopwatch.StartNew();
                session.Run(inputs);
                iterSw.Stop();
                latencies.Add(iterSw.Elapsed.TotalMilliseconds);
            }

            latencies.Sort();

            double meanMs = latencies.Average();
            double minMs = latencies[0];
            double maxMs = latencies[^1];
            double p50Ms = CalculatePercentile(latencies, 50);
            double p90Ms = CalculatePercentile(latencies, 90);
            double p95Ms = CalculatePercentile(latencies, 95);
            double p99Ms = CalculatePercentile(latencies, 99);
            double throughput = meanMs > 0 ? (1000.0 / meanMs) : 0;

            return new BenchmarkResultDto(
                Provider: providerId.Value,
                Success: true,
                ThroughputPerSecond: Math.Round(throughput, 2),
                ColdStartMs: Math.Round(coldStartMs, 2),
                MeanMs: Math.Round(meanMs, 3),
                MinMs: Math.Round(minMs, 3),
                MaxMs: Math.Round(maxMs, 3),
                P50Ms: Math.Round(p50Ms, 3),
                P90Ms: Math.Round(p90Ms, 3),
                P95Ms: Math.Round(p95Ms, 3),
                P99Ms: Math.Round(p99Ms, 3),
                Error: null);
        }
        catch (Exception ex)
        {
            return new BenchmarkResultDto(
                Provider: providerId.Value,
                Success: false,
                ThroughputPerSecond: null,
                ColdStartMs: null,
                MeanMs: null,
                MinMs: null,
                MaxMs: null,
                P50Ms: null,
                P90Ms: null,
                P95Ms: null,
                P99Ms: null,
                Error: ex.Message);
        }
    }

    private static double CalculatePercentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        int idx = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        idx = Math.Clamp(idx, 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static void PrintHumanReadableReport(
        string modelPath,
        string inputsPath,
        int warmup,
        int iterations,
        List<BenchmarkResultDto> results)
    {
        Console.WriteLine("LinML Inference Benchmark");
        Console.WriteLine($"Model: {modelPath}");
        Console.WriteLine($"Inputs: {inputsPath}");
        Console.WriteLine($"Iterations: {iterations} | Warmup: {warmup}");
        Console.WriteLine();

        Console.WriteLine(
            $"{"PROVIDER",-32} {"THROUGHPUT",-15} {"MEAN",-11} {"p50",-11} {"p90",-11} {"p99",-11} {"COLD START",-11}");
        Console.WriteLine(new string('-', 106));

        foreach (var r in results)
        {
            if (r.Success)
            {
                var tp = $"{r.ThroughputPerSecond:F1} inf/s";
                var mean = $"{r.MeanMs:F2} ms";
                var p50 = $"{r.P50Ms:F2} ms";
                var p90 = $"{r.P90Ms:F2} ms";
                var p99 = $"{r.P99Ms:F2} ms";
                var cold = $"{r.ColdStartMs:F2} ms";

                Console.WriteLine(
                    $"{r.Provider,-32} {tp,-15} {mean,-11} {p50,-11} {p90,-11} {p99,-11} {cold,-11}");
            }
            else
            {
                Console.WriteLine($"{r.Provider,-32} {"FAILED",-15} {r.Error}");
            }
        }
    }

    public sealed record BenchmarkResultDto(
        string Provider,
        bool Success,
        double? ThroughputPerSecond,
        double? ColdStartMs,
        double? MeanMs,
        double? MinMs,
        double? MaxMs,
        double? P50Ms,
        double? P90Ms,
        double? P95Ms,
        double? P99Ms,
        string? Error);
}
