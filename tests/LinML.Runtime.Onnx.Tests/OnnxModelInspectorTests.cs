using LinML.Contracts;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

public class OnnxModelInspectorTests
{
    private static string Fixture(string relativePath)
    {
        var local = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (File.Exists(local)) return local;

        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var fallback = Path.Combine(solutionRoot, "tests", relativePath);
        if (File.Exists(fallback)) return fallback;

        return local;
    }

    [Fact]
    public async Task InspectAsync_ValidTinyModel_ReturnsExpectedMetadata()
    {
        var modelPath = Fixture("fixtures/model.onnx");
        if (!File.Exists(modelPath)) return;

        var inspector = new OnnxModelInspector();
        var report = await inspector.InspectAsync(modelPath);

        Assert.NotNull(report);
        Assert.Equal(Path.GetFullPath(modelPath), report.Model.Path);
        Assert.True(report.Model.SizeBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(report.Model.Sha256));
        Assert.True(report.IrVersion >= 3);
        Assert.NotEmpty(report.OpsetImports);

        // Inputs
        Assert.Single(report.Inputs);
        var input = report.Inputs[0];
        Assert.Equal("x", input.Name);
        Assert.Equal("float32", input.DataType);
        Assert.Equal([3, 2], input.Shape);
        Assert.False(input.IsDynamic);

        // Outputs
        Assert.Single(report.Outputs);
        var output = report.Outputs[0];
        Assert.Equal("y", output.Name);
        Assert.Equal("float32", output.DataType);
        Assert.Equal([3, 2], output.Shape);
        Assert.False(output.IsDynamic);

        // Graph
        Assert.True(report.Graph.NodeCount > 0);
        Assert.NotEmpty(report.Graph.Operators);
    }

    [Fact]
    public async Task InspectAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        var inspector = new OnnxModelInspector();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            inspector.InspectAsync("non_existent_model_file.onnx"));
    }

    [Fact]
    public async Task InspectAsync_RealMnistModel_DetectsConvolutionAndOps()
    {
        var modelPath = Fixture("fixtures/real/mnist-8.onnx");
        if (!File.Exists(modelPath)) return;

        var inspector = new OnnxModelInspector();
        var report = await inspector.InspectAsync(modelPath);

        Assert.NotNull(report);
        Assert.Contains(report.Graph.Operators, op => op.Equals("Conv", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.CompatibilityWarnings, w => w.Provider == ProviderId.Cuda && w.Message.Contains("cuDNN"));
    }
}
