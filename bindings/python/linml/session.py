from __future__ import annotations

import os
import socket
import subprocess
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Union

from .binary import get_dotnet_env, get_linml_binary
from .client import InferenceClient
from .models import InferenceResult, TensorOutput, TensorSpec


def _get_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class InferenceSession:
    """Manages an active ONNX Runtime session via an embedded linml serve daemon."""

    def __init__(
        self,
        model_path: Union[str, Path],
        provider: Optional[str] = None,
        require_provider: bool = False,
        ep_options: Optional[Dict[str, str]] = None,
        host: str = "127.0.0.1",
        port: Optional[int] = None,
        startup_timeout: float = 15.0,
    ):
        self.model_path = str(Path(model_path).resolve())
        if not os.path.isfile(self.model_path):
            raise FileNotFoundError(f"Model file not found: {self.model_path}")

        self.provider = provider
        self.require_provider = require_provider
        self.ep_options = ep_options or {}
        self.host = host
        self.port = port or _get_free_port()
        self.startup_timeout = startup_timeout

        self._process: Optional[subprocess.Popen] = None
        self._client: Optional[InferenceClient] = None
        self._closed = False

        self._start_server()

    def _start_server(self):
        base_cmd = get_linml_binary()
        cmd = base_cmd + [
            "serve",
            "--model", self.model_path,
            "--host", self.host,
            "--port", str(self.port),
        ]

        if self.provider:
            cmd.extend(["--provider", self.provider])
        if self.require_provider:
            cmd.append("--require-provider")
        for k, v in self.ep_options.items():
            cmd.extend(["--ep-option", f"{k}={v}"])

        self._process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=get_dotnet_env(),
        )

        endpoint = f"http://{self.host}:{self.port}"
        self._client = InferenceClient(endpoint=endpoint, timeout=self.startup_timeout)

        # Wait for daemon readiness
        deadline = time.time() + self.startup_timeout
        started = False
        last_err: Optional[Exception] = None

        while time.time() < deadline:
            # Check if process crashed early
            if self._process.poll() is not None:
                stderr = self._process.stderr.read() if self._process.stderr else ""
                raise RuntimeError(
                    f"LinML serve process exited prematurely with code {self._process.returncode}: {stderr}"
                )
            try:
                health = self._client.health()
                if health.status in ("healthy", "ready"):
                    started = True
                    break
            except Exception as e:
                last_err = e
                time.sleep(0.05)

        if not started:
            self.close()
            raise TimeoutError(
                f"Timed out waiting for LinML server to start on {endpoint}: {last_err}"
            )

    def predict(
        self,
        inputs: Optional[Union[Dict[str, Any], List[Union[TensorSpec, Dict[str, Any]]]]] = None,
        **kwargs: Any,
    ) -> InferenceResult:
        """Run model inference on the active session."""
        if self._closed or not self._client:
            raise RuntimeError("InferenceSession is closed.")
        return self._client.predict(inputs=inputs, **kwargs)

    def run(
        self,
        output_names: Optional[List[str]] = None,
        input_feed: Optional[Dict[str, Any]] = None,
    ) -> List[Any]:
        """ONNX Runtime-compatible API: session.run([output_names], input_feed).

        Returns list of NumPy arrays (or list data if NumPy is not installed) in order of output_names.
        """
        if input_feed is None:
            raise ValueError("input_feed dictionary must be provided.")

        result = self.predict(input_feed)

        if output_names:
            ordered_outputs: List[Any] = []
            for name in output_names:
                if name not in result.outputs:
                    raise KeyError(f"Requested output '{name}' not found in model outputs: {list(result.outputs.keys())}")
                tensor_out = result.outputs[name]
                try:
                    ordered_outputs.append(tensor_out.to_numpy())
                except RuntimeError:
                    ordered_outputs.append(tensor_out.data)
            return ordered_outputs

        # Default: return all outputs in dict order
        out_list: List[Any] = []
        for tensor_out in result.outputs.values():
            try:
                out_list.append(tensor_out.to_numpy())
            except RuntimeError:
                out_list.append(tensor_out.data)
        return out_list

    def health(self):
        """Query server health and selected provider status."""
        if self._closed or not self._client:
            raise RuntimeError("InferenceSession is closed.")
        return self._client.health()

    def close(self):
        """Shut down the background server and cleanup resources."""
        if self._closed:
            return
        self._closed = True

        if self._client:
            try:
                self._client.shutdown()
            except Exception:
                pass
            self._client = None

        if self._process:
            if self._process.stdout:
                try:
                    self._process.stdout.close()
                except Exception:
                    pass
            if self._process.stderr:
                try:
                    self._process.stderr.close()
                except Exception:
                    pass
            try:
                self._process.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                self._process.terminate()
                try:
                    self._process.wait(timeout=1.0)
                except subprocess.TimeoutExpired:
                    self._process.kill()
            self._process = None

    def __enter__(self) -> "InferenceSession":
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
