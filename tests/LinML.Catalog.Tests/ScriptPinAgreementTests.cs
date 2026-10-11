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

    [GeneratedRegex(@"^(?<fn>fetch_[a-z_]+)\(\)\s*\{(?<body>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline)]
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
}
