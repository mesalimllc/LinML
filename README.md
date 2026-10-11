# LinML (Linux Machine Learning)

[![CI](https://github.com/trackdubllc/LinML/actions/workflows/ci.yml/badge.svg)](https://github.com/trackdubllc/LinML/actions/workflows/ci.yml)
[![Release](https://github.com/trackdubllc/LinML/actions/workflows/release.yml/badge.svg)](https://github.com/trackdubllc/LinML/actions/workflows/release.yml)
[![GitHub Release](https://img.shields.io/github/v/release/trackdubllc/LinML?color=blue)](https://github.com/trackdubllc/LinML/releases)
[![NuGet Catalog](https://img.shields.io/nuget/v/LinML.Catalog.svg?label=LinML.Catalog&color=blue)](https://www.nuget.org/packages/LinML.Catalog)
[![NuGet Contracts](https://img.shields.io/nuget/v/LinML.Contracts.svg?label=LinML.Contracts&color=blue)](https://www.nuget.org/packages/LinML.Contracts)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **Notice:** LinML is an independent open-source project and is not affiliated with, endorsed by, or sponsored by Microsoft. Windows Machine Learning (WinML) and ONNX Runtime are trademarks of Microsoft.

**LinML** (**Lin**ux **M**achine **L**earning) is built to be the Linux counterpart to **Windows Machine Learning (WinML)**.

Just as **WinML** provides a unified, hardware-abstracted execution layer on Windows, **LinML** delivers a Linux-first inference integration layer, CLI, and catalog library for [ONNX Runtime](https://onnxruntime.ai). It brings the ergonomic principles of WinML—such as the `ExecutionProviderCatalog`, honest device discovery, and clean hardware abstraction—to Linux environments, providing **honest execution provider discovery, explicit selection policies, reproducible inference execution, and diagnosable fallback** across CPU, CUDA, TensorRT, TensorRT-RTX, OpenVINO, MIGraphX, and QNN.

LinML also provides `LinML.Catalog`, the Linux counterpart to WinML's `ExecutionProviderCatalog`, which automates vendoring, SHA-256 integrity verification, dynamic registration, and session attachment for external plugin execution providers.

---

## Table of Contents

- [Status](#status)
- [Execution Providers](#execution-providers)
- [Quick Start](#quick-start)
  - [1. Install Pre-built CLI (Recommended)](#1-install-pre-built-cli-recommended)
  - [2. Install C# NuGet Packages](#2-install-c-nuget-packages)
  - [3. Build from Source](#3-build-from-source)
- [CLI Reference](#cli-reference)
  - [Global Options & Exit Codes](#global-options--exit-codes)
  - [Provider Discovery (`linml providers list`)](#provider-discovery-linml-providers-list)
  - [Diagnostic Readiness Probe (`linml doctor`)](#diagnostic-readiness-probe-linml-doctor)
  - [Model Inference (`linml run`)](#model-inference-linml-run)
  - [Persistent Session Serving (`linml serve`)](#persistent-session-serving-linml-serve)
- [LinML.Catalog Library](#linmlcatalog-library)
- [Architecture](#architecture)
- [Documentation](#documentation)
- [License](#license)

---

## Status

| Gate | Description | Status |
|:---:|-------------|:------:|
| **A** | Runtime and packaging strategy | ✅ Accepted (locked restore, ADR 0001) |
| **B** | Contracts and selection policy | ✅ Done (23 policy tests) |
| **C** | Real CPU inference | ✅ Done (`[11, 22, 13, 24, 15, 26]` verified) |
| **D** | CUDA readiness and execution | ✅ Done (RTX 5070, profiling evidence) |
| **E** | CLI and native publishing | ✅ Done (publish verified, self-contained linux-x64) |
| **F** | CI, evidence, and release flow | ✅ Done (CI green, GitHub Releases + NuGet.org OIDC) |

**147 tests passing** across Catalog (25), Runtime (22), Policy (23), Integration (20), and CLI (57).

---

## Execution Providers

| Provider | Status | Verified Hardware | Notes |
|----------|:------:|-------------------|-------|
| `CPUExecutionProvider` | ✅ Verified | Any x86_64 CPU | Always available, built into standard ORT |
| `CUDAExecutionProvider` | ✅ Verified | NVIDIA GeForce RTX 5070 | Blackwell (sm_120); requires cuDNN 9.x for Conv ops |
| `TensorrtExecutionProvider` | ✅ Verified | NVIDIA GeForce RTX 5070 | TensorRT 10.16; enable engine cache for repeated runs |
| `NvTensorRTRTXExecutionProvider` | ✅ Verified (Native Linux) | NVIDIA GeForce RTX 4090 | Verified in Daytona cloud runtime; [WSL2 blocked](docs/compatibility.md) due to virtual PCI constraint |
| `OpenVINOExecutionProvider` | ✅ Verified | Intel Xeon CPU | Plugin EP 1.6.1; dynamically registered at runtime |
| `MIGraphXExecutionProvider` | ✅ Verified | AMD Instinct MI355X (gfx950) | ROCm / MIGraphX verified in Daytona sandbox |
| `QNNExecutionProvider` | 🔌 Wired / Gated | Qualcomm Snapdragon | Contract and probe wired; blocked on physical hardware / QAIRT SDK access |

See the [Compatibility Report](docs/compatibility.md) for detailed hardware matrices, driver requirements, and known constraints.

---

## Quick Start

### 1. Install Pre-built CLI (Recommended)

LinML CLI is distributed as a standalone, self-contained binary for Linux x86_64 with all native ONNX Runtime dependencies bundled. **No .NET installation is required.**

```bash
# Install to ~/.local/share/linml and symlink to ~/.local/bin/linml
curl -fsSL https://raw.githubusercontent.com/trackdubllc/LinML/main/scripts/install.sh | bash

# Or install and automatically set up GPU dependencies (cuDNN, TensorRT, plugins):
curl -fsSL https://raw.githubusercontent.com/trackdubllc/LinML/main/scripts/install.sh | bash -s -- --gpu
```

Pre-built archives (`linml-<version>-linux-x64.tar.gz`) and SHA-256 checksums are also downloadable directly from [GitHub Releases](https://github.com/trackdubllc/LinML/releases).

Verify the installation:
```bash
linml --version
linml providers list
linml doctor
```

---

### 2. Install C# NuGet Packages

`LinML.Catalog` and `LinML.Contracts` are published to [NuGet.org](https://www.nuget.org/packages/LinML.Catalog) and GitHub Packages:

```bash
# Add from public NuGet.org:
dotnet add package LinML.Catalog
dotnet add package LinML.Contracts
```

---

### 3. Build from Source

#### Prerequisites
- **Ubuntu 24.04+ or Debian 12+** (tested on Ubuntu 24.04 and 26.04)
- **.NET 10 SDK** (install via `scripts/setup-wsl.sh` or Microsoft's [dotnet-install](https://dot.net/v1/dotnet-install.sh))
- *(Optional)* NVIDIA GPU with CUDA 12.8+ or 13.0+

```bash
# 1. Setup .NET SDK and verify GPU visibility
bash scripts/setup-wsl.sh

# 2. (Optional) Fetch GPU runtime dependencies (no root required)
bash scripts/setup-gpu-deps.sh     # cuDNN 9.x + TensorRT 10.x into ~/.local/linml-deps
bash scripts/fetch-ep-plugins.sh   # Fetch SHA-256 verified vendor plugins (OpenVINO, TRT-RTX)
source scripts/env-gpu-deps.sh     # Export LD_LIBRARY_PATH and LINML_EP_LIBRARIES

# 3. Restore, Build, and Test
dotnet restore LinML.slnx --locked-mode
dotnet build LinML.slnx -c Release
dotnet test LinML.slnx -c Release

# 4. Publish CLI
dotnet publish src/LinML.Cli/LinML.Cli.csproj -c Release -r linux-x64 --self-contained true -o artifacts/publish/linux-x64
bash scripts/verify-linux-publish.sh artifacts/publish/linux-x64
```

---

## CLI Reference

### Global Options & Exit Codes

All subcommands accept:
- `--json`: Output machine-readable JSON to stdout instead of formatted plain text.

#### Exit Codes
| Code | Meaning |
|:---:|---------|
| `0` | **Success**: Operation completed normally. |
| `1` | **Execution Failure**: Inference failed or error occurred during execution. |
| `2` | **Invalid Arguments**: Bad option, missing required parameter, or input file not found. |
| `3` | **Required Provider Unavailable**: Preferred provider was missing when `--require-provider` was specified. |
| `4` | **Internal Error**: Unhandled exception or unexpected internal error. |

---

### Provider Discovery (`linml providers list`)

Inspect the ONNX Runtime version, available execution providers, detected accelerators, and hardware diagnostics:

```bash
# Human-readable output
linml providers list

# Machine-readable JSON output
linml providers list --json
```

Output includes runtime version, registered providers (including dynamic plugins), and hardware evidence from `nvidia-smi` or sysfs:

```json
{
  "runtimeVersion": "1.31.0.0",
  "availableProviders": [
    "TensorrtExecutionProvider",
    "CUDAExecutionProvider",
    "CPUExecutionProvider"
  ],
  "devices": [
    {
      "id": "gpu0",
      "name": "NVIDIA GeForce RTX 5070",
      "vendor": "NVIDIA",
      "memoryBytes": 12820938752
    }
  ]
}
```

---

### Diagnostic Readiness Probe (`linml doctor`)

Diagnose execution provider readiness before running production models. Detects missing shared libraries, driver mismatches, or missing cuDNN dependencies:

```bash
# Basic provider readiness probe (checks Load/Append without execution)
linml doctor

# Comprehensive model smoke test (loads model and runs real inference per provider)
linml doctor --model model.onnx --inputs inputs.json --json
```

---

### Model Inference (`linml run`)

Execute one-shot inference on an ONNX model:

```bash
linml run --model model.onnx --inputs inputs.json [options]
```

#### Options:
- `--provider <name>`: Preferred execution provider. Accepts aliases (`cpu`, `cuda`, `trt`, `trt-rtx`, `openvino`/`ov`, `migraphx`, `qnn`) or full EP names (`CUDAExecutionProvider`).
- `--require-provider`: Strict mode — exits with code `3` if the preferred provider cannot be used (disables fallback to CPU).
- `--profile`: Enables ONNX Runtime profiling and reports per-node operator placement.
- `--ep-option key=value` or `--ep-option provider:key=value`: Provider-specific options (repeatable, e.g. `--ep-option trt:trt_engine_cache_enable=1`).
- `--ep-library name=path`: Register a dynamic plugin EP shared library (repeatable, e.g. `--ep-library NvTensorRTRTXExecutionProvider=/path/to/lib.so`).
- `--json`: Output result as JSON.

#### Inputs JSON Schema:
```json
{
  "tensors": [
    {
      "name": "input",
      "type": "float32",
      "shape": [3, 2],
      "data": [1.0, 2.0, 3.0, 4.0, 5.0, 6.0]
    }
  ]
}
```
*(Supports `float32`, `float16`, `int64`, and `int32`)*. See [Input Schema](docs/input-schema.md).

---

### Persistent Session Serving (`linml serve`)

When performing repeated inference, one-shot process startup adds overhead. `linml serve` keeps a long-lived ONNX session warm in memory and serves inference requests over `stdin`/`stdout` using JSON Lines (streaming protocol):

```bash
linml serve --model model.onnx --provider cuda --warmup-inputs warmup.json
```

- **Protocol**:
  - `stdin`: Send one input JSON request per line: `{"tensors":[...]}`
  - `stdout`: Emits one `InferenceResult` JSON object per line.
  - Errors: Emits `{"error":"..."}` for bad inputs without terminating the server.
  - Shutdown: Send EOF (`Ctrl+D` or closing stdin) to shut down cleanly with exit code `0`.
- **Latency Advantage**: Post-warmup latency drops from ~2.6 seconds per cold startup down to **~1 ms per request**.

---

## LinML.Catalog Library

`LinML.Catalog` is the Linux counterpart to the Windows ML `ExecutionProviderCatalog`. It vendors pinned plugin EPs (with SHA-256 integrity verification), extracts them into a clean staging area, registers them with ONNX Runtime, and binds devices to sessions.

It natively covers:
- **OpenVINO Plugin EP** (`onnxruntime-ep-openvino` 1.6.1)
- **TensorRT-RTX Plugin EP** (`onnxruntime-ep-nv-tensorrt-rtx` 0.4.0)

```csharp
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

// 1. Obtain default catalog
IExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();

// 2. Discover OpenVINO plugin
IExecutionProvider openVino = catalog.Find(ProviderId.OpenVino)!;

// 3. Ensure plugin binaries are ready (downloads & verifies SHA-256 if allowed)
ProviderEnsureResult ensured = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

// 4. Configure SessionOptions with verified plugin EP
using var sessionOptions = new SessionOptions();
if (ensured.IsReady)
{
    ProviderAppendResult result = sessionOptions.AppendFromCatalog(openVino, [OrtHardwareDeviceType.CPU]);
}

using var session = new InferenceSession("model.onnx", sessionOptions);
```

The CLI and library share a unified plugin registry (`PluginLibraryRegistry`), so libraries configured via `--ep-library` or `LINML_EP_LIBRARIES` are immediately visible to the catalog and vice versa.

---

## Architecture

LinML enforces strict dependency separation across its components:

```
┌────────────────────────────────────────────────────────┐
│                   CLI / Application                    │
│                       LinML.Cli                        │
└────────┬───────────────────┬───────────────────┬───────┘
         │                   │                   │
         ▼                   ▼                   ▼
┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│  LinML.Policy   │ │  LinML.Catalog  │ │LinML.Runtime.Onnx│
│(Selection rules)│ │ (Plugin vendoring│ │ (Native session │
│                 │ │  & registration)│ │   & execution)  │
└────────┬────────┘ └────────┬────────┘ └────────┬────────┘
         │                   │                   │
         └───────────────────┼───────────────────┘
                             ▼
                    ┌─────────────────┐
                    │ LinML.Contracts │
                    │(Pure interfaces │
                    │   & data models)│
                    └─────────────────┘
```

- **`LinML.Contracts`**: Pure contracts, model abstractions, request/response models, and readiness types. **Zero external dependencies on ONNX Runtime.**
- **`LinML.Policy`**: Implementation of provider selection rules and fallback policies. Depends only on `LinML.Contracts`.
- **`LinML.Catalog`**: Library for vendoring, staging, and registering plugin EPs. Published package on NuGet.
- **`LinML.Runtime.Onnx`**: Adapter bridging `Contracts` to native ONNX Runtime sessions, probes, and hardware discovery.
- **`LinML.Cli`**: Composition root combining all layers into the `linml` executable.

---

## Documentation

- [ADR 0001: Runtime and Deployment Strategy](docs/adr/0001-runtime-and-deployment.md)
- [ADR 0002: Multi-EP and Plugin Strategy](docs/adr/0002-multi-ep-and-plugins.md)
- [Hardware Compatibility Report](docs/compatibility.md)
- [Diagnostics Schema & Serve Protocol](docs/diagnostics-schema.md)
- [Input Schema Reference](docs/input-schema.md)
- [WSL2 Development Workflow](docs/wsl-workflow.md)

---

## License

This project is licensed under the [MIT License](LICENSE).

---

## Disclaimer

This is a prototype. It is not affiliated with, endorsed by, or sponsored by Microsoft. ONNX Runtime is a trademark of Microsoft. Windows ML is a trademark of Microsoft.
