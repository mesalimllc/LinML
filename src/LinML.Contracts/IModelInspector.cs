namespace LinML.Contracts;

/// <summary>
/// Service for inspecting ONNX model graph structure, inputs/outputs,
/// metadata, and execution provider compatibility without running inference.
/// </summary>
public interface IModelInspector
{
    /// <summary>
    /// Inspect an ONNX model file and return a detailed diagnostic report.
    /// </summary>
    /// <param name="modelPath">Path to the ONNX model file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model inspection report.</returns>
    Task<ModelInspectionReport> InspectAsync(string modelPath, CancellationToken cancellationToken = default);
}
