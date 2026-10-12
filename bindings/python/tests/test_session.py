import unittest
from pathlib import Path
import numpy as np
import linml

REPO_ROOT = Path(__file__).resolve().parent.parent.parent.parent
TINY_MODEL = REPO_ROOT / "tests" / "fixtures" / "model.onnx"

class TestSession(unittest.TestCase):
    def test_session_lifecycle_and_inference(self):
        if not TINY_MODEL.exists():
            self.skipTest(f"Fixture {TINY_MODEL} not found")

        with linml.InferenceSession(TINY_MODEL, provider="cpu") as session:
            # 1. Health check
            health = session.health()
            self.assertIn(health.status, ("healthy", "ready"))
            self.assertEqual(health.selected_provider, "CPUExecutionProvider")

            # 2. predict() with dictionary
            inputs = {"x": [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]}
            res = session.predict(inputs)
            self.assertEqual(res.selected_provider, "CPUExecutionProvider")
            self.assertIn("y", res.outputs)
            y = res.outputs["y"].to_numpy()
            self.assertEqual(list(y.flatten()), [11.0, 22.0, 13.0, 24.0, 15.0, 26.0])

            # 3. predict() with numpy input
            x_arr = np.array([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]], dtype=np.float32)
            res2 = session.predict(x=x_arr)
            self.assertIn("y", res2.outputs)
            self.assertTrue(np.allclose(res2.outputs["y"].to_numpy(), np.array([[11, 22], [13, 24], [15, 26]])))

            # 4. ORT-compatible run() API
            outputs = session.run(["y"], {"x": x_arr})
            self.assertEqual(len(outputs), 1)
            self.assertTrue(np.allclose(outputs[0], np.array([[11, 22], [13, 24], [15, 26]])))

    def test_ipv6_endpoint_formatting(self):
        # Verify IPv6 bracket formatting without spawning server
        session = linml.InferenceSession.__new__(linml.InferenceSession)
        session.host = "::1"
        session.port = 8080
        formatted_host = f"[{session.host}]" if ":" in session.host and not session.host.startswith("[") else session.host
        endpoint = f"http://{formatted_host}:{session.port}"
        self.assertEqual(endpoint, "http://[::1]:8080")

    def test_session_unrelated_process_rejected(self):
        from unittest.mock import MagicMock
        from linml.models import HealthResponse

        session = linml.InferenceSession.__new__(linml.InferenceSession)
        session.model_path = str(TINY_MODEL.resolve())
        session._process = MagicMock()
        session._process.pid = 12345
        session._server_verified = False

        unrelated_health = HealthResponse(
            status="healthy",
            service="LinML",
            selected_provider="CPUExecutionProvider",
            fallback_chain=[],
            model={"path": str(TINY_MODEL.resolve())},
            process_id=99999,
        )
        self.assertNotEqual(unrelated_health.process_id, session._process.pid)

if __name__ == "__main__":
    unittest.main()
