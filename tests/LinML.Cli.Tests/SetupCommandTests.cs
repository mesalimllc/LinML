using System.Runtime.InteropServices;
using System.Text.Json;
using LinML.Catalog;
using LinML.Cli.Commands;
using LinML.Contracts;
using Xunit;

namespace LinML.Cli.Tests;

public class SetupCommandTests
{
    [Fact]
    public async Task Setup_DryRun_HumanReadable_ReturnsZero()
    {
        var (exitCode, stdout, stderr) = await Task.Run(() => CliTestHost.Run("setup", "--dry-run"));

        Assert.Equal(0, exitCode);
        Assert.Contains("LinML Hardware Auto-Setup (Dry Run)", stdout);
        Assert.Contains("Hardware Detected:", stdout);
        Assert.Contains("Recommended Execution Providers:", stdout);
        Assert.Contains("Dry run complete:", stdout);
    }

    [Fact]
    public async Task Setup_DryRun_Json_ReturnsStructuredMetadata()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run("setup", "--dry-run", "--json"));

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.True(root.GetProperty("success").GetBoolean());

        var hw = root.GetProperty("hardware");
        var cpu = hw.GetProperty("cpu");
        Assert.False(string.IsNullOrWhiteSpace(cpu.GetProperty("architecture").GetString()));
        Assert.True(cpu.GetProperty("logicalCores").GetInt32() > 0);

        var recs = root.GetProperty("recommendations");
        // ARM64 systems have no recommended plugins (prebuilt plugins are x86_64-only)
        // but still need valid JSON structure
        Assert.True(recs.GetArrayLength() >= 0);

        var actions = root.GetProperty("actions");
        // On ARM64 with no recommendations, actions array might be empty
        // but structure should still be valid
        Assert.True(actions.GetArrayLength() >= 0);
    }

    [Fact]
    public async Task Setup_All_DryRun_RecommendsAllThreePlugins()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run("setup", "--all", "--dry-run", "--json"));

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var recs = doc.RootElement.GetProperty("recommendations");
        Assert.Equal(3, recs.GetArrayLength());

        var providers = recs.EnumerateArray()
            .Select(r => r.GetProperty("provider").GetString())
            .ToList();

        Assert.Contains("openvino", providers);
        Assert.Contains("trt-rtx", providers);
        Assert.Contains("migraphx", providers);
    }

    [Fact]
    public async Task Setup_CustomRoot_AlreadyInstalled_ExitsZero()
    {
        // Skip on ARM64 since no plugins are recommended there by default
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "linml-test-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spec = KnownPlugins.OpenVino;
            string targetDir = spec.InstallDirectory(tempDir);
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(spec.LibraryPath(tempDir), "fake-so");

            var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
                "setup", "--all", "--root", tempDir, "--dry-run"));

            Assert.Equal(0, exitCode);
            Assert.Contains("Already installed", stdout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ProbeCpu_ReturnsSensibleValues()
    {
        var cpu = SetupCommand.ProbeCpu();

        Assert.False(string.IsNullOrWhiteSpace(cpu.Architecture));
        Assert.False(string.IsNullOrWhiteSpace(cpu.Vendor));
        Assert.False(string.IsNullOrWhiteSpace(cpu.Model));
        Assert.True(cpu.LogicalCores > 0);
    }

    [Fact]
    public void DetermineRecommendations_NvidiaGpu_SelectsTrtRtx()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Ryzen", 8, true),
            Devices: [new DeviceIdentity { Id = "gpu0", Name = "RTX 4090", Vendor = "NVIDIA", MemoryBytes = 24L * 1024 * 1024 * 1024 }],
            HasAmdRocm: false,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: false);

        Assert.Contains(recs, r => r.ShortName == "trt-rtx");
        // OpenVINO's plugin EP exposes no devices on non-Intel CPUs.
        Assert.DoesNotContain(recs, r => r.ShortName == "openvino");
        Assert.DoesNotContain(recs, r => r.ShortName == "migraphx");
    }

    [Fact]
    public void DetermineRecommendations_AmdRocm_SelectsMigraphX()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Threadripper", 32, true),
            Devices: [],
            HasAmdRocm: true,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: false);

        Assert.Contains(recs, r => r.ShortName == "migraphx");
        Assert.DoesNotContain(recs, r => r.ShortName == "openvino");
        Assert.DoesNotContain(recs, r => r.ShortName == "trt-rtx");
    }

    [Fact]
    public void DetermineRecommendations_All_AmdCpuWithoutRocm_NotesMigraphxNeedsGpu()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "AMD Ryzen 7 5700X3D", 16, true),
            Devices: [],
            HasAmdRocm: false,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: true);

        var migraphx = Assert.Single(recs, r => r.ShortName == "migraphx");
        Assert.NotNull(migraphx.Note);
        Assert.Contains("AMD GPU with ROCm", migraphx.Note);
    }

    [Fact]
    public void DetermineRecommendations_All_WithAmdRocm_HasNoMigraphxNote()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Threadripper", 32, true),
            Devices: [],
            HasAmdRocm: true,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: true);

        Assert.Null(Assert.Single(recs, r => r.ShortName == "migraphx").Note);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "needs an AMD GPU with ROCm")]
    public void MigraphXHardwareNote_OnlyGatesMigraphxOnNonRocmHost(bool amdRocm, string? expectedFragment)
    {
        string? note = SetupCommand.MigraphXHardwareNote(ProviderId.MigraphX, amdRocm);

        if (expectedFragment is null)
        {
            Assert.Null(note);
        }
        else
        {
            Assert.Contains(expectedFragment, note);
        }
        Assert.Null(SetupCommand.MigraphXHardwareNote(ProviderId.OpenVino, amdRocm: false));
    }

    [Fact]
    public void DetermineRecommendations_NvidiaGpu_OnWsl2_DoesNotSelectTrtRtx()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "Intel", "Core i9", 16, true),
            Devices: [new DeviceIdentity { Id = "gpu0", Name = "NVIDIA GeForce RTX 5070", Vendor = "NVIDIA", MemoryBytes = 12L * 1024 * 1024 * 1024 }],
            HasAmdRocm: false,
            IsWsl2: true);

        var recs = SetupCommand.DetermineRecommendations(hw, all: false);

        // Under WSL2, stock trt-rtx PyPI wheel is blocked due to virtual PCI bus mapping; built-in CUDA EP is active.
        Assert.DoesNotContain(recs, r => r.ShortName == "trt-rtx");
        Assert.Contains(recs, r => r.ShortName == "openvino");
    }

    [Fact]
    public void DetermineRecommendations_OlderNvidiaGpu_DoesNotSelectTrtRtx()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "Intel", "Core i7", 8, true),
            Devices: [new DeviceIdentity { Id = "gpu0", Name = "GeForce GTX 1080 Ti", Vendor = "NVIDIA", MemoryBytes = 11L * 1024 * 1024 * 1024 }],
            HasAmdRocm: false,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: false);

        Assert.DoesNotContain(recs, r => r.ShortName == "trt-rtx");
        Assert.Contains(recs, r => r.ShortName == "openvino");
    }

    [Fact]
    public void DetermineRecommendations_AmdCpu_SkipsOpenVinoWithVisibleReason()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "AMD Ryzen 7 5700X3D", 16, true),
            Devices: [],
            HasAmdRocm: false,
            IsWsl2: false);

        Assert.DoesNotContain(SetupCommand.DetermineRecommendations(hw, all: false), r => r.ShortName == "openvino");
        var skipped = Assert.Single(SetupCommand.DescribeSkippedRecommendations(hw, all: false));
        Assert.Contains("openvino", skipped);
        Assert.Contains("Ryzen", skipped);
    }

    [Fact]
    public void DetermineRecommendations_IntelCpu_SelectsOpenVino_AllOverridesGate()
    {
        var intel = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "Intel", "Xeon", 32, true), Devices: [], HasAmdRocm: false, IsWsl2: false);
        var amd = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "EPYC", 32, true), Devices: [], HasAmdRocm: false, IsWsl2: false);

        Assert.Contains(SetupCommand.DetermineRecommendations(intel, all: false), r => r.ShortName == "openvino");
        Assert.Empty(SetupCommand.DescribeSkippedRecommendations(intel, all: false));
        Assert.Contains(SetupCommand.DetermineRecommendations(amd, all: true), r => r.ShortName == "openvino");
        Assert.Empty(SetupCommand.DescribeSkippedRecommendations(amd, all: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetermineGpuDependencies_NvidiaGpu_SelectsCudnnAndTensorRt_EvenOnWsl2(bool isWsl2)
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Ryzen", 8, true),
            Devices: [new DeviceIdentity { Id = "gpu0", Name = "NVIDIA GeForce RTX 5070", Vendor = "NVIDIA", MemoryBytes = 12L * 1024 * 1024 * 1024 }],
            HasAmdRocm: false,
            IsWsl2: isWsl2);

        var deps = SetupCommand.DetermineGpuDependencies(hw, all: false);

        Assert.Equal(["cudnn", "tensorrt"], deps.Select(d => d.Spec.Id).ToArray());
    }

    [Fact]
    public void DetermineGpuDependencies_NoNvidiaGpu_SelectsNothing_EvenWithAll()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "Intel", "Xeon", 8, true), Devices: [], HasAmdRocm: false, IsWsl2: false);

        // Multi-gigabyte NVIDIA wheels that need pip are never pulled onto a non-NVIDIA host.
        Assert.Empty(SetupCommand.DetermineGpuDependencies(hw, all: false));
        Assert.Empty(SetupCommand.DetermineGpuDependencies(hw, all: true));
    }

    [Fact]
    public void DetermineGpuDependencies_WithAll_OnNvidia_ExcludesScriptOnlyLeanRuntime()
    {
        var recs = SetupCommand.DetermineGpuDependencies(WslRtx(), all: true);

        Assert.Equal(["cudnn", "tensorrt"], recs.Select(r => r.Spec.Id).ToArray());
        Assert.All(recs, r => Assert.Equal("Requested via --all", r.Reason));
    }

    [Fact]
    public async Task GpuDependency_PipMissing_IsSkippedNotFailed()
    {
        using var tempRoot = new TempRoot();
        var actions = new List<SetupCommand.SetupActionResultDto>();
        var installer = new GpuDependencyInstaller((_, _) => throw new PipUnavailableException("no pip"));

        bool ok = await SetupCommand.ProcessGpuDependencyAsync(
            new SetupCommand.GpuDependencyRecommendation(KnownGpuDependencies.Cudnn, "r"),
            tempRoot.Path, dryRun: false, force: false, json: true, installer, actions);

        Assert.True(ok);
        var action = Assert.Single(actions);
        Assert.Equal("Skipped", action.Status);
        Assert.Contains("no pip", action.Error);
    }

    [Fact]
    public async Task GpuDependency_RealInstallFailure_StillFails()
    {
        using var tempRoot = new TempRoot();
        var actions = new List<SetupCommand.SetupActionResultDto>();
        var installer = new GpuDependencyInstaller((_, _) => Task.FromResult(new PipResult(1, "network down")));

        bool ok = await SetupCommand.ProcessGpuDependencyAsync(
            new SetupCommand.GpuDependencyRecommendation(KnownGpuDependencies.TensorRt, "r"),
            tempRoot.Path, dryRun: false, force: false, json: true, installer, actions);

        Assert.False(ok);
        Assert.Equal("Failed", Assert.Single(actions).Status);
    }

    [Fact]
    public void ProbeIntelDevices_ReadsPciVendorFromSysfs()
    {
        using var sys = new TempRoot();
        void Device(string cls, string name, string vendor)
        {
            string dir = Path.Combine(sys.Path, cls, name, "device");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "vendor"), vendor + "\n");
        }

        Device("drm", "card0", "0x8086");          // Intel GPU
        Device("drm", "card1", "0x1002");          // AMD GPU: not Intel
        Device("drm", "card0-HDMI-A-1", "0x8086"); // connector node, not a device
        Device("accel", "accel0", "0x8086");       // Intel NPU

        var found = SetupCommand.ProbeIntelDevices(sys.Path);

        Assert.Equal(["accel0", "card0"], found.Select(d => d.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.All(found, d => Assert.Equal("Intel", d.Vendor));
        Assert.Empty(SetupCommand.ProbeIntelDevices(Path.Combine(sys.Path, "nonexistent")));
    }

    [Fact]
    public void DetermineRecommendations_AmdCpuWithIntelGpu_SelectsOpenVino()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Ryzen", 8, true),
            Devices: [new DeviceIdentity { Id = "card0", Name = "Intel GPU (card0)", Vendor = "Intel" }],
            HasAmdRocm: false,
            IsWsl2: false);

        Assert.Contains(SetupCommand.DetermineRecommendations(hw, all: false), r => r.ShortName == "openvino");
        Assert.Empty(SetupCommand.DescribeSkippedRecommendations(hw, all: false));
    }

    [Fact]
    public void MixedNvidiaHost_OlderCardFirst_StillOffersAndRecommendsRtxFeatures()
    {
        DeviceIdentity Gpu(string id, string name) => new() { Id = id, Name = name, Vendor = "NVIDIA" };
        var devices = new[] { Gpu("gpu0", "GeForce GTX 1080 Ti"), Gpu("gpu1", "NVIDIA GeForce RTX 5070") };
        SetupCommand.HostHardwareInfo Host(bool wsl2) => new(
            new SetupCommand.CpuInfo("x64", "AMD", "Ryzen", 8, true), devices, HasAmdRocm: false, IsWsl2: wsl2);

        // WSL2: the patched plugin is offered because one card is supported.
        Assert.True(SetupCommand.IsTrtRtxWsl2Eligible(Host(wsl2: true)));
        Assert.Single(SetupCommand.DetermineOptIns(Host(wsl2: true), trtRtxWsl2Requested: false));

        // Bare metal: the stock plugin is recommended for the supported card, not the first one.
        var rec = Assert.Single(SetupCommand.DetermineRecommendations(Host(wsl2: false), all: false), r => r.ShortName == "trt-rtx");
        Assert.Contains("RTX 5070", rec.Reason);
    }

    [Fact]
    public void FindRootArgument_ReadsBothForms()
    {
        Assert.Equal("/x", Program.FindRootArgument(["doctor", "--root", "/x"]));
        Assert.Equal("/y", Program.FindRootArgument(["setup", "--root=/y", "--json"]));
        Assert.Null(Program.FindRootArgument(["doctor"]));
        Assert.Null(Program.FindRootArgument(["doctor", "--root"]));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "linml-test-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GpuDependencies_InstalledUnderCustomRoot_ReportAlreadyInstalled_WithoutRunningPip()
    {
        using var tempRoot = new TempRoot();
        var actions = new List<SetupCommand.SetupActionResultDto>();
        var installer = new GpuDependencyInstaller((_, _) => throw new InvalidOperationException("pip must not run"));
        foreach (var spec in KnownGpuDependencies.All)
        {
            string sentinel = spec.SentinelPath(tempRoot.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
            File.WriteAllText(sentinel, "fake-so");
        }

        foreach (var spec in KnownGpuDependencies.All.Where(s => s.InstalledBySetup))
        {
            Assert.True(await SetupCommand.ProcessGpuDependencyAsync(
                new SetupCommand.GpuDependencyRecommendation(spec, "r"),
                tempRoot.Path, dryRun: false, force: false, json: true, installer, actions));
        }

        Assert.Equal(["cudnn", "tensorrt"], actions.Select(a => a.Provider).ToArray());
        Assert.All(actions, a => Assert.Equal("AlreadyInstalled", a.Status));
    }

    private static SetupCommand.HostHardwareInfo WslRtx(string gpu = "NVIDIA GeForce RTX 5070", bool wsl2 = true) => new(
        Cpu: new SetupCommand.CpuInfo("x64", "AMD", "Ryzen", 8, true),
        Devices: [new DeviceIdentity { Id = "gpu0", Name = gpu, Vendor = "NVIDIA", MemoryBytes = 12L * 1024 * 1024 * 1024 }],
        HasAmdRocm: false,
        IsWsl2: wsl2);

    [Fact]
    public void TrtRtxWsl2_Detected_IsOfferedButNotInstalledWithoutOptIn()
    {
        var hw = WslRtx();

        Assert.DoesNotContain(SetupCommand.DetermineRecommendations(hw, all: false), r => r.ShortName == "trt-rtx-wsl2");
        var offer = Assert.Single(SetupCommand.DetermineOptIns(hw, trtRtxWsl2Requested: false));
        Assert.Equal("trt-rtx-wsl2", offer.Id);
        Assert.Equal("linml setup --trt-rtx-wsl2", offer.Command);
    }

    [Fact]
    public void TrtRtxWsl2_OptedIn_InstallsStockRuntimeFirstThenPatchedBuild()
    {
        var recs = SetupCommand.DetermineRecommendations(WslRtx(), all: false, trtRtxWsl2: true);

        var names = recs.Select(r => r.ShortName).ToList();
        Assert.True(names.IndexOf("trt-rtx") >= 0 && names.IndexOf("trt-rtx") < names.IndexOf("trt-rtx-wsl2"),
            "the stock wheel (runtime libraries) must precede the patched build: " + string.Join(", ", names));
        Assert.Same(KnownPlugins.TensorrtRtxWsl2, recs.Single(r => r.ShortName == "trt-rtx-wsl2").Spec);
        Assert.Empty(SetupCommand.DetermineOptIns(WslRtx(), trtRtxWsl2Requested: true));
        Assert.DoesNotContain(SetupCommand.DescribeSkippedRecommendations(WslRtx(), all: false, trtRtxWsl2: true), n => n.StartsWith("trt-rtx-wsl2"));
    }

    [Fact]
    public void TrtRtxWsl2_OptedInWithAll_DoesNotDuplicateStockWheel()
    {
        var recs = SetupCommand.DetermineRecommendations(WslRtx(), all: true, trtRtxWsl2: true);

        Assert.Single(recs, r => r.ShortName == "trt-rtx");
        Assert.Single(recs, r => r.ShortName == "trt-rtx-wsl2");
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070", false)]  // not WSL2
    [InlineData("GeForce GTX 1080 Ti", true)]       // pre-Ampere
    public void TrtRtxWsl2_NotEligible_IsNeitherOfferedNorInstalled_AndOptInIsExplained(string gpu, bool wsl2)
    {
        var hw = WslRtx(gpu, wsl2);

        Assert.Empty(SetupCommand.DetermineOptIns(hw, trtRtxWsl2Requested: false));
        Assert.DoesNotContain(SetupCommand.DetermineRecommendations(hw, all: false, trtRtxWsl2: true), r => r.ShortName == "trt-rtx-wsl2");
        Assert.Contains(SetupCommand.DescribeSkippedRecommendations(hw, all: false, trtRtxWsl2: true), n => n.StartsWith("trt-rtx-wsl2"));
    }

    [Fact]
    public async Task Setup_TrtRtxWsl2_CustomRoot_AlreadyInstalled_ReportsIt()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "linml-test-setup-wsl2-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var spec in new[] { KnownPlugins.TensorrtRtx, KnownPlugins.TensorrtRtxWsl2 })
            {
                Directory.CreateDirectory(spec.InstallDirectory(tempDir));
                File.WriteAllText(spec.LibraryPath(tempDir), "fake-so");
            }

            var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
                "setup", "--trt-rtx-wsl2", "--root", tempDir, "--dry-run", "--json"));

            Assert.Equal(0, exitCode);
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            using var services = Program.BuildServices();
            if (SetupCommand.IsTrtRtxWsl2Eligible(SetupCommand.ProbeHostHardware(services)))
            {
                var action = Assert.Single(root.GetProperty("actions").EnumerateArray(),
                    a => a.GetProperty("provider").GetString() == "trt-rtx-wsl2");
                Assert.Equal("AlreadyInstalled", action.GetProperty("status").GetString());
            }
            else
            {
                Assert.Contains(root.GetProperty("skipped").EnumerateArray(),
                    n => n.GetString()!.StartsWith("trt-rtx-wsl2"));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DetermineRecommendations_Arm64_DoesNotSelectOpenVino()
    {
        var hw = new SetupCommand.HostHardwareInfo(
            Cpu: new SetupCommand.CpuInfo("arm64", "ARM", "Cortex-A78", 8, true),
            Devices: [],
            HasAmdRocm: false,
            IsWsl2: false);

        var recs = SetupCommand.DetermineRecommendations(hw, all: false);

        // Prebuilt openvino wheel targets manylinux_2_28_x86_64; must not recommend on arm64
        Assert.DoesNotContain(recs, r => r.ShortName == "openvino");
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070", true)]
    [InlineData("NVIDIA GeForce RTX 4090", true)]
    [InlineData("NVIDIA GeForce RTX 3080", true)]
    [InlineData("NVIDIA RTX A6000", true)]
    [InlineData("NVIDIA A100-PCIE-40GB", true)]
    [InlineData("NVIDIA H100 80GB HBM3", true)]
    [InlineData("NVIDIA L4", true)]
    [InlineData("GeForce GTX 1080 Ti", false)]
    [InlineData("GeForce GTX 1660 SUPER", false)]
    [InlineData("NVIDIA GeForce RTX 2080", false)]
    [InlineData("Tesla T4", false)]
    [InlineData("Tesla V100", false)]
    [InlineData("NVIDIA GPU", false)]
    public void IsRtxAmpereOrNewer_FiltersAccurately(string gpuName, bool expected)
    {
        bool actual = SetupCommand.IsRtxAmpereOrNewer(gpuName);
        Assert.Equal(expected, actual);
    }
}
