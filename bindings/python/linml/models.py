from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Union

try:
    import numpy as np

    HAS_NUMPY = True
except ImportError:
    HAS_NUMPY = False


@dataclass
class TensorSpec:
    name: str
    data_type: str = "float32"
    shape: List[int] = field(default_factory=list)
    data: List[Union[float, int, bool]] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "name": self.name,
            "type": self.data_type,
            "shape": self.shape,
            "data": self.data,
        }

    @classmethod
    def from_any(cls, name: str, value: Any, data_type: Optional[str] = None) -> "TensorSpec":
        """Convert a list, scalar, or numpy array to TensorSpec."""
        if HAS_NUMPY and isinstance(value, np.ndarray):
            shape = list(value.shape)
            flat = value.flatten().tolist()
            if data_type is None:
                if np.issubdtype(value.dtype, np.floating):
                    data_type = "float16" if value.dtype == np.float16 else "float32"
                elif np.issubdtype(value.dtype, np.integer):
                    data_type = "int64" if value.dtype == np.int64 else "int32"
                elif np.issubdtype(value.dtype, np.bool_):
                    data_type = "bool"
                else:
                    data_type = "float32"
            return cls(name=name, data_type=data_type, shape=shape, data=flat)

        if isinstance(value, (list, tuple)):
            # Calculate shape and flatten
            shape, flat = _infer_shape_and_flatten(value)
            dtype = data_type or ("int64" if all(isinstance(x, int) for x in flat) else "float32")
            return cls(name=name, data_type=dtype, shape=shape, data=flat)

        # Scalar
        dtype = data_type or ("int64" if isinstance(value, int) else "float32")
        return cls(name=name, data_type=dtype, shape=[1], data=[value])


def _infer_shape_and_flatten(nested: Any) -> tuple[List[int], List[Any]]:
    if not isinstance(nested, (list, tuple)):
        return [], [nested]
    shape = [len(nested)]
    if len(nested) == 0:
        return shape, []

    first = nested[0]
    if isinstance(first, (list, tuple)):
        sub_shape, _ = _infer_shape_and_flatten(first)
        shape.extend(sub_shape)

    flat: List[Any] = []

    def _flatten(item: Any):
        if isinstance(item, (list, tuple)):
            for sub in item:
                _flatten(sub)
        else:
            flat.append(item)

    _flatten(nested)
    return shape, flat


@dataclass
class TensorOutput:
    name: str
    data_type: str
    shape: List[int]
    data: List[Union[float, int, bool]]

    def to_numpy(self) -> Any:
        if not HAS_NUMPY:
            raise RuntimeError("NumPy is required to use to_numpy(). Install with 'pip install numpy'.")
        dtype_map = {
            "float32": np.float32,
            "float": np.float32,
            "float16": np.float16,
            "float64": np.float64,
            "double": np.float64,
            "int64": np.int64,
            "long": np.int64,
            "int32": np.int32,
            "int": np.int32,
            "int16": np.int16,
            "int8": np.int8,
            "uint8": np.uint8,
            "bool": bool,
        }
        np_dtype = dtype_map.get(self.data_type.lower(), np.float32)
        arr = np.array(self.data, dtype=np_dtype)
        if self.shape:
            return arr.reshape(self.shape)
        return arr


@dataclass
class InferenceResult:
    selected_provider: str
    latency_ms: float
    fallback_chain: List[Dict[str, Any]]
    outputs: Dict[str, TensorOutput]
    node_placements: List[Dict[str, Any]] = field(default_factory=list)
    raw_response: Dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "InferenceResult":
        outputs_map: Dict[str, TensorOutput] = {}
        for out in data.get("outputs", []):
            name = out.get("name", "")
            dtype = out.get("dataType", out.get("type", "float32"))
            shape = out.get("shape", [])
            tensor_data = out.get("data", [])
            outputs_map[name] = TensorOutput(
                name=name, data_type=dtype, shape=shape, data=tensor_data
            )

        return cls(
            selected_provider=data.get("selectedProvider", ""),
            latency_ms=float(data.get("latencyMs", 0.0)),
            fallback_chain=data.get("fallbackChain", []),
            outputs=outputs_map,
            node_placements=data.get("nodePlacements", []),
            raw_response=data,
        )


@dataclass
class HealthResponse:
    status: str
    service: str
    selected_provider: str
    fallback_chain: List[Dict[str, Any]]
    model: Dict[str, Any]
    active_requests: int = 0
    raw: Dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "HealthResponse":
        return cls(
            status=data.get("status", "unknown"),
            service=data.get("service", "LinML"),
            selected_provider=data.get("selectedProvider", ""),
            fallback_chain=data.get("fallbackChain", []),
            model=data.get("model", {}),
            active_requests=data.get("activeRequests", 0),
            raw=data,
        )


@dataclass
class OpsetImport:
    domain: str
    version: int


@dataclass
class ModelTensorMetadata:
    name: str
    data_type: str
    shape: List[int]
    is_dynamic: bool
    symbolic_dimensions: List[str] = field(default_factory=list)


@dataclass
class ModelGraphMetadata:
    node_count: int
    operators: List[str] = field(default_factory=list)


@dataclass
class CompatibilityFinding:
    provider: str
    severity: str
    message: str


@dataclass
class ModelIdentity:
    path: str
    sha256: str
    size_bytes: int
    name: Optional[str] = None


@dataclass
class ModelInspectionReport:
    model: ModelIdentity
    ir_version: int
    opsets: List[OpsetImport]
    inputs: List[ModelTensorMetadata]
    outputs: List[ModelTensorMetadata]
    graph: ModelGraphMetadata
    compatibility_warnings: List[CompatibilityFinding]
    metadata: Dict[str, str] = field(default_factory=dict)
    producer_name: Optional[str] = None
    producer_version: Optional[str] = None
    domain: Optional[str] = None
    model_version: int = 0
    description: Optional[str] = None

    @classmethod
    def from_dict(cls, data: Dict[str, Any]) -> "ModelInspectionReport":
        model_data = data.get("model", {})
        model_id = ModelIdentity(
            path=model_data.get("path", ""),
            sha256=model_data.get("sha256", ""),
            size_bytes=model_data.get("sizeBytes", 0),
            name=model_data.get("name"),
        )
        opsets = [
            OpsetImport(domain=o.get("domain", ""), version=o.get("version", 0))
            for o in data.get("opsetImports", data.get("opsets", []))
        ]
        inputs = [
            ModelTensorMetadata(
                name=i.get("name", ""),
                data_type=i.get("dataType", ""),
                shape=i.get("shape", []),
                is_dynamic=i.get("isDynamic", False),
                symbolic_dimensions=i.get("symbolicDimensions", []),
            )
            for i in data.get("inputs", [])
        ]
        outputs = [
            ModelTensorMetadata(
                name=o.get("name", ""),
                data_type=o.get("dataType", ""),
                shape=o.get("shape", []),
                is_dynamic=o.get("isDynamic", False),
                symbolic_dimensions=o.get("symbolicDimensions", []),
            )
            for o in data.get("outputs", [])
        ]
        graph_data = data.get("graph", {})
        graph = ModelGraphMetadata(
            node_count=graph_data.get("nodeCount", 0),
            operators=graph_data.get("operators", []),
        )
        warnings = [
            CompatibilityFinding(
                provider=w.get("provider", ""),
                severity=w.get("severity", "Warning"),
                message=w.get("message", ""),
            )
            for w in data.get("compatibilityWarnings", [])
        ]
        return cls(
            model=model_id,
            ir_version=data.get("irVersion", 0),
            opsets=opsets,
            inputs=inputs,
            outputs=outputs,
            graph=graph,
            compatibility_warnings=warnings,
            metadata=data.get("metadataProps", data.get("metadata", {})),
            producer_name=data.get("producerName"),
            producer_version=data.get("producerVersion"),
            domain=data.get("domain"),
            model_version=data.get("modelVersion", 0),
            description=data.get("description"),
        )
