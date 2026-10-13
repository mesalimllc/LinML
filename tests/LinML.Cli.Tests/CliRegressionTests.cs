using System.Text.Json;
using LinML.Cli.Commands;
using LinML.Contracts;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Regression tests for CLI output and exit-code contracts: provider labels, fallback
/// order, provider/doctor agreement, usage errors and option parsing.
/// </summary>
public class CliRegressionTests
{
    private static bool HasTinyFixture() =>
        File.Exists(CliTestHost.Fixture("model.onnx")) && File.Exists(CliTestHost.Fixture("inputs.json"));

    [Fact]
    public async Task Run_MissingInputsOption_Returns2_WithMessageNotHelpDump()
    {
        var (exitCode, stdout, stderr) = await Task.Run(() => CliTestHost.Run("run", "--model", "model.onnx"));

        // Usage errors use the documented invalid-arguments code, with a message rather than the full help.
        Assert.Equal(2, exitCode);
        Assert.Contains("--inputs", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage:", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Providers_UnknownSubcommand_Returns2()
    {
        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run("providers", "bogus"));

        Assert.Equal(2, exitCode);
        Assert.Contains("bogus", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsJsonOptionOnce()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run("run", "--help"));

        Assert.Equal(0, exitCode);
        Assert.Equal(1, CountOccurrences(stdout, "--json"));
    }

    [Fact]
    public async Task Run_JsonAfterSubcommand_ProducesJson()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run", "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"), "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("selectedProvider").GetString()));
    }

    [Fact]
    public async Task Run_WithoutProvider_ReportsNoRequestAndNoFallback()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run", "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"), "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        // Auto selection has no requested provider, so it cannot be a fallback.
        Assert.True(!doc.RootElement.TryGetProperty("requestedProvider", out var requested)
            || requested.ValueKind == JsonValueKind.Null);
        Assert.False(doc.RootElement.GetProperty("fallbackOccurred").GetBoolean());
    }

    [Fact]
    public async Task Run_WithoutProvider_TextOutputSaysAuto()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run", "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json")));

        Assert.Equal(0, exitCode);
        Assert.Contains("(requested: auto)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ExplicitUnavailableProvider_FallsBackToCpuNotAnotherAccelerator()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run", "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "migraphx", "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        string selected = doc.RootElement.GetProperty("selectedProvider").GetString()!;
        // MIGraphX is not installed here. The failed request must land on CPU, never on TensorRT.
        Assert.Contains(selected, new[] { "CPUExecutionProvider", "MIGraphXExecutionProvider" });
    }

    [Fact]
    public async Task Providers_List_MatchesDoctorDiscovery()
    {
        var (providersExit, providersOut, _) = await Task.Run(() => CliTestHost.Run("providers", "list", "--json"));
        var (doctorExit, doctorOut, _) = await Task.Run(() => CliTestHost.Run("doctor", "--json"));

        Assert.Equal(0, providersExit);
        Assert.Equal(0, doctorExit);

        using var providers = JsonDocument.Parse(providersOut);
        using var doctor = JsonDocument.Parse(doctorOut);
        var fromProviders = Names(providers.RootElement.GetProperty("availableProviders"));
        var fromDoctor = Names(doctor.RootElement.GetProperty("discovery").GetProperty("availableProviders"));

        // Both commands register the same catalog plugins before discovery, so they agree.
        Assert.Equal(fromDoctor, fromProviders);
    }

    [Fact]
    public async Task CatalogList_PluginStatus_AgreesWithDoctor()
    {
        var (catalogExit, catalogOut, _) = await Task.Run(() => CliTestHost.Run("catalog", "list", "--json"));
        var (doctorExit, doctorOut, _) = await Task.Run(() => CliTestHost.Run("doctor", "--json"));

        Assert.Equal(0, catalogExit);
        Assert.Equal(0, doctorExit);

        using var catalog = JsonDocument.Parse(catalogOut);
        using var doctor = JsonDocument.Parse(doctorOut);
        var doctorStatus = doctor.RootElement.GetProperty("catalog").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("status").GetString()!);

        // Compare only where doctor has a verdict on the plugin's state (not NotPresent/NotApplicable).
        foreach (var entry in catalog.RootElement.EnumerateArray()
                     .Where(e => string.Equals(e.GetProperty("source").GetString(), "plugin", StringComparison.Ordinal)))
        {
            string id = entry.GetProperty("id").GetString()!;
            if (!doctorStatus.TryGetValue(id, out string? status) || status is not ("Ready" or "NotReady"))
            {
                continue;
            }

            Assert.Equal(status, entry.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Doctor_ModelSmoke_AgreesWithPlainDoctorForReadyPlugins()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (plainExit, plainOut, _) = await Task.Run(() => CliTestHost.Run("doctor", "--json"));
        var (smokeExit, smokeOut, _) = await Task.Run(() => CliTestHost.Run(
            "doctor", "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"), "--json"));

        Assert.Equal(0, plainExit);
        Assert.Equal(0, smokeExit);

        using var plain = JsonDocument.Parse(plainOut);
        using var smoke = JsonDocument.Parse(smokeOut);
        var smokeByProvider = smoke.RootElement.GetProperty("readiness").EnumerateArray()
            .ToDictionary(r => r.GetProperty("provider").GetString()!, r => r);

        // Anything plain doctor lists as ready must also pass the smoke Load phase. Plugin EPs
        // are listed by EP device, so a Load check that ignores devices rejects them here.
        foreach (var readiness in plain.RootElement.GetProperty("readiness").EnumerateArray())
        {
            string provider = readiness.GetProperty("provider").GetString()!;
            if (!readiness.GetProperty("isReady").GetBoolean() || !smokeByProvider.TryGetValue(provider, out var smokeReport))
            {
                continue;
            }

            bool failedAtLoad = smokeReport.TryGetProperty("failedPhase", out var failedPhase)
                && failedPhase.ValueKind == JsonValueKind.String
                && failedPhase.GetString() == "Load";
            string reason = smokeReport.TryGetProperty("failureReason", out var message) ? message.GetString() ?? "" : "";
            Assert.False(failedAtLoad,
                $"{provider} is ready in plain doctor but the smoke Load phase rejected it: {reason}");
        }
    }

    [Fact]
    public void InferenceResult_AutoSelection_IsNotAFallback()
    {
        var result = new InferenceResult
        {
            RequestedProvider = null,
            SelectedProvider = ProviderId.Tensorrt,
            Outputs = [],
            Duration = TimeSpan.Zero,
            Model = new ModelIdentity { Path = "model.onnx", Sha256 = "abc" },
            RuntimeVersion = "test"
        };

        Assert.False(result.FallbackOccurred);
    }

    [Fact]
    public void InferenceResult_ExplicitRequestNotHonored_IsAFallback()
    {
        var result = new InferenceResult
        {
            RequestedProvider = ProviderId.Cuda,
            SelectedProvider = ProviderId.Cpu,
            Outputs = [],
            Duration = TimeSpan.Zero,
            Model = new ModelIdentity { Path = "model.onnx", Sha256 = "abc" },
            RuntimeVersion = "test"
        };

        Assert.True(result.FallbackOccurred);
    }

    [Fact]
    public async Task CatalogList_CustomRootWithoutLibrary_ReportsPluginNotPresent()
    {
        // Nothing is vendored under this root, so trt-rtx is not installed here, whatever any other registration exposes.
        string root = Path.Combine(Path.GetTempPath(), "linml-empty-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run("catalog", "list", "--root", root, "--json"));

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(stdout);
            var trt = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "trt-rtx");
            Assert.Equal("NotPresent", trt.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_InstalledMigraphxWithoutRocm_IsNotApplicable()
    {
        // On an AMD host with ROCm the plugin does apply, so the check is for hosts without it.
        if (SetupCommand.ProbeAmdRocm())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "linml-migraphx-root-" + Guid.NewGuid().ToString("N"));
        // Installed, at the path PluginPackageSpec.LibraryPath gives for MIGraphX.
        string library = Path.Combine(root, "ep-plugins", "migraphx", "linml_migraphx_ep", "libmigraphx-ep.so");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.WriteAllText(library, "installed, but this host cannot run it");
        try
        {
            var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run("doctor", "--root", root, "--json"));

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(stdout);
            var migraphx = doc.RootElement.GetProperty("catalog").EnumerateArray()
                .Single(c => c.GetProperty("id").GetString() == "migraphx");
            Assert.Equal("NotApplicable", migraphx.GetProperty("status").GetString());
            Assert.Contains("ROCm", migraphx.GetProperty("details").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static HashSet<string> Names(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        for (int index = text.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
