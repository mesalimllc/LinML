"""LinML: Linux ONNX Runtime inference integration layer and client library."""

from .binary import find_linml_binary, get_linml_binary
from .client import InferenceClient
from .cli import bench, doctor, inspect, providers_list, run, setup
from .models import (
    CompatibilityFinding,
    HealthResponse,
    InferenceResult,
    ModelGraphMetadata,
    ModelIdentity,
    ModelInspectionReport,
    ModelTensorMetadata,
    OpsetImport,
    TensorOutput,
    TensorSpec,
)
from .session import InferenceSession

__version__ = "0.3.0"

__all__ = [
    "InferenceClient",
    "InferenceSession",
    "InferenceResult",
    "TensorSpec",
    "TensorOutput",
    "HealthResponse",
    "ModelInspectionReport",
    "ModelIdentity",
    "ModelTensorMetadata",
    "ModelGraphMetadata",
    "OpsetImport",
    "CompatibilityFinding",
    "find_linml_binary",
    "get_linml_binary",
    "inspect",
    "run",
    "doctor",
    "providers_list",
    "setup",
    "bench",
    "__version__",
]
