using System.Text.RegularExpressions;
using Xunit;

namespace LinML.Catalog.Tests;

/// <summary>
/// The C# catalog (<see cref="KnownPlugins"/>) and the shell scripts
/// (<c>scripts/fetch-ep-plugins.sh</c>, <c>scripts/env-gpu-deps.sh</c>) vendor
/// the same plugins into the same <c>~/.local/linml-deps</c> layout. Nothing
/// else ties the two together, so a pin bumped on one side only would silently
/// install different binaries, or make one side miss the other's install.
/// </summary>
public partial class ScriptPinAgreementTests
{
    private sealed record ScriptPin(string Function, string Url, string Sha256, string InstallSubdirectory);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LinML.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("LinML.slnx not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"^(?<fn>fetch_[a-z0-9_]+)\(\)\s*\{(?<body>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FetchFunction();

    [GeneratedRegex(@"local\s+(?<key>OUT|WHEEL_URL|WHEEL_SHA256)=""(?<value>[^""]+)""")]
    private static partial Regex LocalAssignment();

    private static List<ScriptPin> ReadFetchScriptPins()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "fetch-ep-plugins.sh"));
        var pins = new List<ScriptPin>();
        foreach (Match fn in FetchFunction().Matches(script))
        {
            var values = LocalAssignment().Matches(fn.Groups["body"].Value)
                .ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
            if (values.TryGetValue("WHEEL_URL", out string? url))
            {
                const string depsPrefix = "$DEPS/";
                string outDir = values["OUT"];
                Assert.StartsWith(depsPrefix, outDir, StringComparison.Ordinal);
                pins.Add(new ScriptPin(fn.Groups["fn"].Value, url, values["WHEEL_SHA256"], outDir[depsPrefix.Length..]));
            }
        }

        return pins;
    }

    [Fact]
    public void FetchScript_PinsAreParsed()
    {
        // Guards the parser itself: if the script's shape changes and nothing
        // is parsed, the agreement test below would pass vacuously.
        List<ScriptPin> pins = ReadFetchScriptPins();

        Assert.Contains(pins, p => p.Function == "fetch_openvino");
        Assert.Contains(pins, p => p.Function == "fetch_trt_rtx");
        Assert.Contains(pins, p => p.Function == "fetch_migraphx");
        Assert.Contains(pins, p => p.Function == "fetch_trt_rtx_wsl2");
    }

    [Fact]
    public void EveryCatalogPlugin_MatchesTheFetchScriptPin()
    {
        List<ScriptPin> pins = ReadFetchScriptPins();

        foreach (PluginPackageSpec spec in KnownPlugins.All)
        {
            ScriptPin? pin = pins.SingleOrDefault(p => p.Url == spec.WheelUrl.ToString());
            Assert.True(pin is not null,
                $"{spec.Id} {spec.Version}: scripts/fetch-ep-plugins.sh has no fetch function for {spec.WheelUrl}.");
            Assert.Equal(spec.WheelSha256, pin.Sha256);
            Assert.Equal(spec.InstallSubdirectory.Replace('\\', '/'), pin.InstallSubdirectory);
        }
    }

    [Fact]
    public void EveryCatalogPlugin_IsWhereEnvScriptRegistersIt()
    {
        string env = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "env-gpu-deps.sh"));

        foreach (PluginPackageSpec spec in KnownPlugins.All)
        {
            string expected = "$DEPS/" + Path.Combine(spec.InstallSubdirectory, spec.LibraryFileName).Replace('\\', '/');
            Assert.True(env.Contains($"=\"{expected}\"", StringComparison.Ordinal),
                $"scripts/env-gpu-deps.sh does not register {spec.Id} at {expected}.");
        }
    }

    [Fact]
    public void EveryGpuDependency_MatchesTheSetupScriptPin()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "setup-gpu-deps.sh"));

        foreach (GpuDependencySpec spec in KnownGpuDependencies.All)
        {
            Assert.True(script.Contains($"\"{spec.PipPackage}\"", StringComparison.Ordinal),
                $"scripts/setup-gpu-deps.sh does not pin {spec.PipPackage} for {spec.Id}.");

            string sentinel = $"$DEPS/{spec.InstallSubdirectory}/{spec.SentinelLibrary.Replace('\\', '/')}";
            Assert.True(script.Contains(sentinel, StringComparison.Ordinal),
                $"scripts/setup-gpu-deps.sh does not check {sentinel} for {spec.Id}.");
        }
    }

    [Fact]
    public void EnvScript_RegistersOpenVinoUnderTheCatalogRegistrationName()
    {
        string env = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "env-gpu-deps.sh"));

        // LinML only recognises a registered plugin under its catalog name; the bare
        // "openvino" alias registers a library discovery cannot attribute.
        Assert.Contains($"LINML_EP_LIBRARIES=\"{KnownPlugins.OpenVino.RegistrationName}=$OPENVINO_EP", env, StringComparison.Ordinal);
    }

    [Fact]
    public void QnnPlugin_MatchesTheFetchScriptPin_AndTheEnvScript()
    {
        // QNN is aarch64-only and not in KnownPlugins.All, so the loops above skip it.
        PluginPackageSpec spec = KnownPlugins.Qnn;

        ScriptPin? pin = ReadFetchScriptPins().SingleOrDefault(p => p.Function == "fetch_qnn");

        Assert.True(pin is not null, "scripts/fetch-ep-plugins.sh has no fetch_qnn.");
        Assert.Equal(spec.WheelUrl.ToString(), pin.Url);
        Assert.Equal(spec.WheelSha256, pin.Sha256);
        Assert.Equal(spec.InstallSubdirectory.Replace('\\', '/'), pin.InstallSubdirectory);

        string env = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "env-gpu-deps.sh"));
        string expected = "$DEPS/" + Path.Combine(spec.InstallSubdirectory, spec.LibraryFileName).Replace('\\', '/');
        Assert.Contains($"QNN_EP=\"{expected}\"", env, StringComparison.Ordinal);
        Assert.Contains($"{spec.RegistrationName}=$QNN_EP", env, StringComparison.Ordinal);
    }

    [Fact]
    public void Wsl2TrtRtxVariant_MatchesTheFetchScriptPin()
    {
        // The opt-in WSL2 build is not in KnownPlugins.All (it is a drop-in for
        // trt-rtx, not a provider), so the loop above does not cover it.
        PluginPackageSpec spec = KnownPlugins.TensorrtRtxWsl2;

        ScriptPin? pin = ReadFetchScriptPins().SingleOrDefault(p => p.Function == "fetch_trt_rtx_wsl2");

        Assert.True(pin is not null, "scripts/fetch-ep-plugins.sh has no fetch_trt_rtx_wsl2.");
        Assert.Equal(spec.WheelUrl.ToString(), pin.Url);
        Assert.Equal(spec.WheelSha256, pin.Sha256);
        Assert.Equal(spec.InstallSubdirectory.Replace('\\', '/'), pin.InstallSubdirectory);
        Assert.DoesNotContain(KnownPlugins.All, p => p == spec);
        Assert.Equal(KnownPlugins.TensorrtRtx.RegistrationName, spec.RegistrationName);
    }
}
