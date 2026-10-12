from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, List, Optional, Union

from .models import HealthResponse, InferenceResult, TensorSpec


class InferenceClient:
    """Client for communicating with a running LinML serve HTTP daemon."""

    def __init__(self, endpoint: str = "http://127.0.0.1:8080", timeout: float = 30.0):
        self.endpoint = endpoint.rstrip("/")
        self.timeout = timeout

    def health(self) -> HealthResponse:
        """Query the /health endpoint of the LinML server."""
        url = f"{self.endpoint}/health"
        req = urllib.request.Request(url, method="GET")
        req.add_header("Accept", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                body = resp.read().decode("utf-8")
                data = json.loads(body)
                return HealthResponse.from_dict(data)
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8")
            raise RuntimeError(f"HTTP {e.code} checking server health: {err_body}") from e

    def predict(
        self,
        inputs: Optional[Union[Dict[str, Any], List[Union[TensorSpec, Dict[str, Any]]]]] = None,
        **kwargs: Any,
    ) -> InferenceResult:
        """Submit tensor inputs to the LinML server for inference.

        Args:
            inputs: Dictionary of input names to arrays/lists, or list of TensorSpec objects.
            **kwargs: Alternate syntax passing input names as kwargs: client.predict(x=arr, mask=arr2).

        Returns:
            InferenceResult with output tensors, provider info, and latency metrics.
        """
        tensor_specs: List[Dict[str, Any]] = []

        # Combine inputs and kwargs
        combined: Dict[str, Any] = {}
        if isinstance(inputs, dict):
            combined.update(inputs)
        combined.update(kwargs)

        if combined:
            for name, val in combined.items():
                if isinstance(val, TensorSpec):
                    tensor_specs.append(val.to_dict())
                else:
                    spec = TensorSpec.from_any(name=name, value=val)
                    tensor_specs.append(spec.to_dict())
        elif isinstance(inputs, list):
            for item in inputs:
                if isinstance(item, TensorSpec):
                    tensor_specs.append(item.to_dict())
                elif isinstance(item, dict):
                    tensor_specs.append(item)
                else:
                    raise ValueError(f"Unsupported input item in list: {type(item)}")
        else:
            raise ValueError("No tensor inputs provided to predict().")

        payload = json.dumps({"tensors": tensor_specs}).encode("utf-8")

        url = f"{self.endpoint}/v1/models/predict"
        req = urllib.request.Request(url, data=payload, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json")

        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                body = resp.read().decode("utf-8")
                data = json.loads(body)
                return InferenceResult.from_dict(data)
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8")
            raise RuntimeError(f"HTTP {e.code} during inference: {err_body}") from e

    def shutdown(self) -> bool:
        """Send graceful shutdown signal to the LinML HTTP daemon."""
        url = f"{self.endpoint}/shutdown"
        req = urllib.request.Request(url, data=b"{}", method="POST")
        req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return resp.status == 200
        except urllib.error.HTTPError as e:
            if e.code == 403:
                raise PermissionError("Shutdown route rejected: remote clients cannot trigger shutdown.") from e
            return False
        except Exception:
            return False

    def close(self):
        """Close client resources."""
        pass

    def __enter__(self) -> "InferenceClient":
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()
