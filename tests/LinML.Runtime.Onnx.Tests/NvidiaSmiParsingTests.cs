using LinML.Contracts;
using LinML.Runtime.Onnx;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>Parsing of <c>nvidia-smi --query-gpu=name,driver_version,memory.total</c> output.</summary>
public class NvidiaSmiParsingTests
{
    private static List<HardwareEvidence> Smi(string output) =>
        [new HardwareEvidence { Kind = "nvidia-smi", Description = output, Source = "nvidia-smi" }];

    [Fact]
    public void SingleGpu_ParsesNameAndMemory()
    {
        var devices = OnnxProviderDiscovery.ExtractDevices(Smi("NVIDIA GeForce RTX 5070, 617.42, 12228"));

        var device = Assert.Single(devices);
        Assert.Equal("gpu0", device.Id);
        Assert.Equal("NVIDIA GeForce RTX 5070", device.Name);
        Assert.Equal("NVIDIA", device.Vendor);
        Assert.Equal(12228L * 1024 * 1024, device.MemoryBytes);
    }

    [Fact]
    public void MultipleGpus_YieldOneDevicePerLine()
    {
        // Regression: the whole multi-line output used to be split on commas,
        // yielding one device with the second GPU's name inside its memory field.
        var devices = OnnxProviderDiscovery.ExtractDevices(Smi(
            "NVIDIA GeForce RTX 5070, 617.42, 12228\nNVIDIA GeForce RTX 4090, 617.42, 24564\n"));

        Assert.Equal(2, devices.Count);
        Assert.Equal(["gpu0", "gpu1"], devices.Select(d => d.Id));
        Assert.Equal("NVIDIA GeForce RTX 4090", devices[1].Name);
        Assert.Equal(24564L * 1024 * 1024, devices[1].MemoryBytes);
    }

    [Fact]
    public void UnparseableMemory_LeavesMemoryNull_AndShortLinesAreSkipped()
    {
        var devices = OnnxProviderDiscovery.ExtractDevices(Smi(
            "NVIDIA A100, 550.54, [N/A]\nnot a gpu line\r\n"));

        var device = Assert.Single(devices);
        Assert.Equal("NVIDIA A100", device.Name);
        Assert.Null(device.MemoryBytes);
    }

    [Fact]
    public void NonNvidiaSmiEvidence_IsIgnored()
    {
        var devices = OnnxProviderDiscovery.ExtractDevices(
            [new HardwareEvidence { Kind = "lspci", Description = "VGA, NVIDIA, 1" }]);

        Assert.Empty(devices);
    }
}
