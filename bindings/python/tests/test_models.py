import unittest
import numpy as np
from linml.models import (
    TensorSpec,
    TensorOutput,
    InferenceResult,
    HealthResponse,
    ModelInspectionReport,
)

class TestModels(unittest.TestCase):
    def test_tensor_spec_from_scalar(self):
        spec = TensorSpec.from_any("val", 42)
        self.assertEqual(spec.name, "val")
        self.assertEqual(spec.shape, [1])
        self.assertEqual(spec.data, [42])
        self.assertEqual(spec.data_type, "int64")

    def test_tensor_spec_from_list(self):
        nested = [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]
        spec = TensorSpec.from_any("x", nested)
        self.assertEqual(spec.name, "x")
        self.assertEqual(spec.shape, [3, 2])
        self.assertEqual(spec.data, [1.0, 2.0, 3.0, 4.0, 5.0, 6.0])
        self.assertEqual(spec.data_type, "float32")

    def test_tensor_spec_from_numpy(self):
        arr = np.array([[11, 22], [33, 44]], dtype=np.int32)
        spec = TensorSpec.from_any("tensor_a", arr)
        self.assertEqual(spec.name, "tensor_a")
        self.assertEqual(spec.shape, [2, 2])
        self.assertEqual(spec.data, [11, 22, 33, 44])
        self.assertEqual(spec.data_type, "int32")

    def test_tensor_output_to_numpy(self):
        out = TensorOutput(
            name="y",
            data_type="float32",
            shape=[2, 3],
            data=[1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
        )
        arr = out.to_numpy()
        self.assertIsInstance(arr, np.ndarray)
        self.assertEqual(arr.shape, (2, 3))
        self.assertEqual(arr.dtype, np.float32)
        self.assertEqual(arr[1, 2], 6.0)

    def test_inference_result_parsing(self):
        raw = {
            "selectedProvider": "CPUExecutionProvider",
            "latencyMs": 1.25,
            "fallbackChain": [{"provider": "CUDA", "status": "Failed"}],
            "outputs": [
                {
                    "name": "y",
                    "dataType": "float32",
                    "shape": [3, 2],
                    "data": [11.0, 22.0, 13.0, 24.0, 15.0, 26.0]
                }
            ]
        }
        res = InferenceResult.from_dict(raw)
        self.assertEqual(res.selected_provider, "CPUExecutionProvider")
        self.assertEqual(res.latency_ms, 1.25)
        self.assertIn("y", res.outputs)
        y = res.outputs["y"].to_numpy()
        self.assertEqual(list(y.flatten()), [11.0, 22.0, 13.0, 24.0, 15.0, 26.0])

    def test_health_response_parsing(self):
        raw = {
            "service": "LinML",
            "status": "healthy",
            "selectedProvider": "CPUExecutionProvider",
            "model": {"path": "model.onnx"},
            "fallbackChain": []
        }
        health = HealthResponse.from_dict(raw)
        self.assertEqual(health.service, "LinML")
        self.assertEqual(health.status, "healthy")
        self.assertEqual(health.selected_provider, "CPUExecutionProvider")

    def test_model_inspection_report_parsing(self):
        raw = {
            "model": {"path": "/tmp/test.onnx", "sha256": "abc123", "sizeBytes": 1024},
            "irVersion": 7,
            "opsets": [{"domain": "ai.onnx", "version": 13}],
            "inputs": [{"name": "x", "dataType": "float32", "shape": [3, 2], "isDynamic": False}],
            "outputs": [{"name": "y", "dataType": "float32", "shape": [3, 2], "isDynamic": False}],
            "graph": {"nodeCount": 3, "operators": ["Add", "Relu"]},
            "compatibilityWarnings": [{"provider": "Cuda", "severity": "Warning", "message": "cuDNN required"}]
        }
        rep = ModelInspectionReport.from_dict(raw)
        self.assertEqual(rep.model.path, "/tmp/test.onnx")
        self.assertEqual(rep.ir_version, 7)
        self.assertEqual(rep.inputs[0].name, "x")
        self.assertEqual(rep.graph.node_count, 3)
        self.assertEqual(rep.compatibility_warnings[0].provider, "Cuda")

if __name__ == "__main__":
    unittest.main()
