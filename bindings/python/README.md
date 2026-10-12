# linml (Python Bindings)

[![PyPI](https://img.shields.io/pypi/v/linml.svg)](https://pypi.org/project/linml/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Python client library and CLI wrapper for **LinML** (Linux ML), the Linux counterpart to Windows ML for [ONNX Runtime](https://onnxruntime.ai).

## Features

- **Direct In-Memory Inference Sessions**: `linml.InferenceSession("model.onnx")` executes ONNX models with hardware-abstracted execution providers (`cpu`, `cuda`, `tensorrt`, `trt-rtx`, `openvino`, `migraphx`).
- **HTTP REST Microservice Client**: `linml.InferenceClient("http://localhost:8080")` interacts with high-performance `linml serve` daemons at ~1 ms latency.
- **NumPy Integration**: Pass numpy arrays directly as dictionary kwargs or lists; outputs convert seamlessly back to NumPy `ndarray`.
- **Model Graph & EP Diagnostics**: `linml.inspect("model.onnx")` inspects model graph topology, dynamic shapes, and execution provider compatibility warnings.
- **Hardware Probing & Auto-Setup**: `linml.setup()` and `linml.doctor()` discover and verify host accelerators directly from Python.

## Quick Start

### 1. Installation

```bash
pip install linml
```

### 2. High-Level Inference Session

```python
import numpy as np
import linml

# Launch an in-process or daemon-managed session
with linml.InferenceSession("model.onnx", provider="cuda") as session:
    x = np.array([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]], dtype=np.float32)
    result = session.predict(x=x)
    
    print(f"Provider: {result.selected_provider}")
    print(f"Latency:  {result.latency_ms:.2f} ms")
    
    # Access output tensor as NumPy array
    y = result.outputs["y"].to_numpy()
    print("Output shape:", y.shape)
```

### 3. HTTP Microservice Client (`linml serve`)

When running `linml serve --port 8080 --model model.onnx`:

```python
import linml

client = linml.InferenceClient("http://localhost:8080")

# Check service health and provider readiness
health = client.health()
print(f"Status: {health.status}, Provider: {health.selected_provider}")

# Submit prediction
result = client.predict({"x": [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]})
print(result.outputs["y"].data)

client.close()
```

### 4. Model Inspection & Diagnostics

```python
import linml

# Inspect model inputs, outputs, opset, and EP compatibility
report = linml.inspect("model.onnx")

print(f"Format: ONNX IR v{report.ir_version}, Opset: {report.opsets}")
for inp in report.inputs:
    print(f"Input: {inp.name} ({inp.data_type}, shape={inp.shape})")

for warning in report.compatibility_warnings:
    print(f"Warning [{warning.provider}]: {warning.message}")
```

## License

MIT License. See [LICENSE](https://github.com/trackdubllc/LinML/blob/main/LICENSE) for details.
