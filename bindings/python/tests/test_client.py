import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from linml.client import InferenceClient

class MockLinMLHandler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass  # Suppress logging

    def do_GET(self):
        if self.path in ("/health", "/v1/health"):
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({
                "service": "LinML",
                "status": "healthy",
                "selectedProvider": "CPUExecutionProvider",
                "model": {"path": "model.onnx"},
                "fallbackChain": []
            }).encode("utf-8"))
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if self.path in ("/v1/models/predict", "/predict"):
            content_length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(content_length).decode("utf-8")
            data = json.loads(body)
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps({
                "selectedProvider": "CPUExecutionProvider",
                "latencyMs": 0.45,
                "fallbackChain": [],
                "outputs": [
                    {
                        "name": "y",
                        "dataType": "float32",
                        "shape": [3, 2],
                        "data": [11.0, 22.0, 13.0, 24.0, 15.0, 26.0]
                    }
                ]
            }).encode("utf-8"))
        elif self.path in ("/shutdown", "/v1/shutdown"):
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b"{}")
        else:
            self.send_response(404)
            self.end_headers()

class TestClient(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = HTTPServer(("127.0.0.1", 0), MockLinMLHandler)
        cls.port = cls.server.server_port
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def test_client_health(self):
        client = InferenceClient(f"http://127.0.0.1:{self.port}")
        health = client.health()
        self.assertEqual(health.status, "healthy")
        self.assertEqual(health.selected_provider, "CPUExecutionProvider")

    def test_client_predict_dict(self):
        client = InferenceClient(f"http://127.0.0.1:{self.port}")
        res = client.predict({"x": [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]})
        self.assertEqual(res.selected_provider, "CPUExecutionProvider")
        self.assertIn("y", res.outputs)
        y = res.outputs["y"].to_numpy()
        self.assertEqual(list(y.flatten()), [11.0, 22.0, 13.0, 24.0, 15.0, 26.0])

    def test_client_predict_kwargs(self):
        client = InferenceClient(f"http://127.0.0.1:{self.port}")
        res = client.predict(x=[[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]])
        self.assertEqual(res.selected_provider, "CPUExecutionProvider")
        self.assertIn("y", res.outputs)

    def test_client_shutdown(self):
        client = InferenceClient(f"http://127.0.0.1:{self.port}")
        ok = client.shutdown()
        self.assertTrue(ok)

if __name__ == "__main__":
    unittest.main()
