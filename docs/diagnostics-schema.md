# LinML Diagnostics Schema

All JSON output from LinML CLI follows these schemas.

## Common Enums

### DiagnosticSeverity
```json
"Info" | "Warning" | "Error"
```

### ReadinessPhase
```json
"Load" | "Append" | "Smoke"
```

### TensorDataType
```json
"Float32" | "Float16" | "Int64" | "Int32"
```

## ProviderDiscoveryReport

Returned by `linml providers list --json`.

```json
{
  "availableProviders": ["CPUExecutionProvider", "CUDAExecutionProvider"],
  "devices": [
    {
      "id": "gpu0",
      "name": "NVIDIA GeForce RTX 5070",
      "vendor": "NVIDIA",
      "memoryBytes": 12884901888
    }
  ],
  "hardwareEvidence": [
    {
      "kind": "nvidia-smi",
      "description": "NVIDIA GeForce RTX 5070, 617.42, 12228",
      "source": "nvidia-smi"
    }
  ],
  "runtimeVersion": "1.31.0",
  "diagnostics": [
    {
      "phase": "discovery",
      "message": "Found ORT provider: CPUExecutionProvider",
      "severity": "Info"
    }
  ]
}
```

## ProviderReadinessReport

Returned by `linml doctor --json`.

```json
{
  "provider": "CUDAExecutionProvider",
  "isReady": true,
  "failedPhase": null,
  "failureReason": null,
  "phaseResults": [
    { "phase": "Load", "success": true, "error": null },
    { "phase": "Append", "success": true, "error": null },
    { "phase": "Smoke", "success": true, "error": null }
  ],
  "phaseDurations": {
    "Load": "00:00:00.0123456",
    "Append": "00:00:00.0234567",
    "Smoke": "00:00:00"
  }
}
```

## InferenceResult

Returned by `linml run --json`.

```json
{
  "requestedProvider": "CUDAExecutionProvider",
  "selectedProvider": "CPUExecutionProvider",
  "fallbackOccurred": true,
  "fallbackChain": [
    {
      "provider": "CUDAExecutionProvider",
      "reason": "CUDA provider failed readiness at phase Load: CUDAExecutionProvider not available"
    }
  ],
  "outputs": [
    {
      "name": "output",
      "dataType": "Float32",
      "shape": [3, 2],
      "data": [11.0, 22.0, 13.0, 24.0, 15.0, 26.0]
    }
  ],
  "duration": "00:00:00.1234567",
  "model": {
    "path": "/path/to/model.onnx",
    "sha256": "abc123...",
    "sizeBytes": 512,
    "name": null
  },
  "runtimeVersion": "1.31.0",
  "diagnostics": []
}
```

With `linml run --profile`, per-node provider placement is appended to `diagnostics`:

```json
"diagnostics": [
  {
    "phase": "profile",
    "message": "ORT profile written to onnxruntime_profile__2026-10-11_03-08-00_814.json",
    "severity": "Info"
  },
  {
    "phase": "profile",
    "message": "Node 'MatMul_0/MatMulAddFusion_kernel_time' ran on CUDAExecutionProvider",
    "severity": "Info"
  }
]
```

## SelectionDecision

Internal contract used by policy layer.

```json
{
  "success": true,
  "selectedProvider": "CUDAExecutionProvider",
  "rejectedCandidates": [],
  "failureReason": null,
  "fallbackUsed": false
}
```

## Error Response

When a command fails, the error is written to stderr (not stdout).
Exit codes:

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Execution failure |
| 2 | Invalid arguments |
| 3 | Required provider unavailable |
| 4 | Unexpected/internal error |
