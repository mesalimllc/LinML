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
        Assert.True(recs.GetArrayLength() > 0);

        var actions = root.GetProperty("actions");
        Assert.True(actions.GetArrayLength() > 0);
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
        string tempDir = Path.Combine(Path.GetTempPath(), "linml-test-setup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var spec = KnownPlugins.OpenVino;
            string targetDir = spec.InstallDirectory(tempDir);
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(spec.LibraryPath(tempDir), "fake-so");

            var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
                "setup", "--root", tempDir, "--dry-run"));

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
        Assert.Contains(recs, r => r.ShortName == "openvino");
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
        Assert.Contains(recs, r => r.ShortName == "openvino");
        Assert.DoesNotContain(recs, r => r.ShortName == "trt-rtx");
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
