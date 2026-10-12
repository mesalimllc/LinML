import os
import unittest
from pathlib import Path
import linml

REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent
TINY_MODEL = REPO_ROOT / "tests" / "fixtures" / "model.onnx"

class TestCliWrappers(unittest.TestCase):
    def test_providers_list(self):
        report = linml.providers_list()
        self.assertIn("runtimeVersion", report)
        self.assertIn("availableProviders", report)
        self.assertIn("CPUExecutionProvider", report["availableProviders"])

    def test_inspect_tiny_model(self):
        if not TINY_MODEL.exists():
            self.skipTest(f"Fixture {TINY_MODEL} not found")

        report = linml.inspect(TINY_MODEL)
        self.assertIsNotNone(report)
        self.assertGreater(report.ir_version, 0)
        self.assertEqual(len(report.inputs), 1)
        self.assertEqual(report.inputs[0].name, "x")
        self.assertEqual(report.inputs[0].shape, [3, 2])
        self.assertEqual(len(report.outputs), 1)
        self.assertEqual(report.outputs[0].name, "y")
        self.assertGreater(report.graph.node_count, 0)

    def test_run_tiny_model(self):
        if not TINY_MODEL.exists():
            self.skipTest(f"Fixture {TINY_MODEL} not found")

        inputs = {"x": [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]}
        result = linml.run(TINY_MODEL, inputs=inputs, provider="cpu")
        self.assertEqual(result.selected_provider, "CPUExecutionProvider")
        self.assertIn("y", result.outputs)
        y = result.outputs["y"].to_numpy()
        self.assertEqual(list(y.flatten()), [11.0, 22.0, 13.0, 24.0, 15.0, 26.0])

        # Test without explicit provider (policy-selected)
        auto_res = linml.run(TINY_MODEL, inputs=inputs)
        self.assertIn(auto_res.selected_provider, ("CPUExecutionProvider", "CUDAExecutionProvider"))
        self.assertIn("y", auto_res.outputs)

if __name__ == "__main__":
    unittest.main()
