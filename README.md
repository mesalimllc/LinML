# LinML (Linux ML)

[![CI](https://github.com/mesalimllc/LinML/actions/workflows/ci.yml/badge.svg)](https://github.com/mesalimllc/LinML/actions/workflows/ci.yml)
[![Release](https://github.com/mesalimllc/LinML/actions/workflows/release.yml/badge.svg)](https://github.com/mesalimllc/LinML/actions/workflows/release.yml)
[![GitHub Release](https://img.shields.io/github/v/release/mesalimllc/LinML?color=blue)](https://github.com/mesalimllc/LinML/releases)
[![NuGet Catalog](https://img.shields.io/nuget/v/LinML.Catalog.svg?label=LinML.Catalog&color=blue)](https://www.nuget.org/packages/LinML.Catalog)
[![NuGet Contracts](https://img.shields.io/nuget/v/LinML.Contracts.svg?label=LinML.Contracts&color=blue)](https://www.nuget.org/packages/LinML.Contracts)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> **Notice:** LinML is an independent open-source project and is not affiliated with, endorsed by, or sponsored by Microsoft. Windows ML (WinML) and ONNX Runtime are trademarks of Microsoft.

**LinML** (Linux ML) is built to be the Linux counterpart to **Windows ML (WinML)**.

Just as **WinML** provides a unified, hardware-abstracted execution layer on Windows, **LinML** delivers a Linux-first inference integration layer, CLI, and catalog library for [ONNX Runtime](https://onnxruntime.ai). It brings the ergonomic principles of WinML—such as the `ExecutionProviderCatalog`, honest device discovery, and clean hardware abstraction—to Linux environments, providing **honest execution provider discovery, explicit selection policies, reproducible inference execution, and diagnosable fallback** across CPU, CUDA, TensorRT, TensorRT-RTX, OpenVINO, MIGraphX, and QNN.

LinML also provides `LinML.Catalog`, the Linux counterpart to WinML's `ExecutionProviderCatalog`, which automates vendoring, SHA-256 integrity verification, dynamic registration, and session attachment for external plugin execution providers.

---

## Table of Contents

- [Status](#status)
- [Execution Providers](#execution-providers)
- [Quick Start](#quick-start)
  - [1. Install Pre-built CLI (Recommended)](#1-install-pre-built-cli-recommended)
  - [2. Install C# NuGet Packages](#2-install-c-nuget-packages)
  - [3. Install Python Package (`linml`)](#3-install-python-package-linml)
  - [4. Build from Source](#4-build-from-source)
- [Plugin Execution Providers](#plugin-execution-providers)
- [CLI Reference](#cli-reference)
  - [Global Options & Exit Codes](#global-options--exit-codes)
  - [Provider Discovery (`linml providers list`)](#provider-discovery-linml-providers-list)
  - [Diagnostic Readiness Probe (`linml doctor`)](#diagnostic-readiness-probe-linml-doctor)
  - [Model Inspection (`linml inspect`)](#model-inspection-linml-inspect)
  - [Model Inference (`linml run`)](#model-inference-linml-run)
  - [Benchmarking Suite (`linml bench`)](#benchmarking-suite-linml-bench)
  - [Persistent Session Serving (`linml serve`)](#persistent-session-serving-linml-serve)
- [Python Client Library (`linml`)](#python-client-library-linml)
- [LinML.Catalog Library](#linmlcatalog-library)
- [Architecture](#architecture)
- [Documentation](#documentation)
- [License](#license)

---

## Status

| Component | Version | NuGet | Notes |
|-----------|---------|-------|-------|
| **CLI** (`linml`) | `0.6.0` | — | Self-contained `linux-x64`, `linux-arm64` binaries |
| **LinML.Catalog** | `0.6.0` | [![NuGet Catalog](https://img.shields.io/nuget/v/LinML.Catalog.svg?label=LinML.Catalog&color=blue)](https://www.nuget.org/packages/LinML.Catalog) | Plugin EP vendoring & registration |
| **LinML.Contracts** | `0.6.0` | [![NuGet Contracts](https://img.shields.io/nuget/v/LinML.Contracts.svg?label=LinML.Contracts&color=blue)](https://www.nuget.org/packages/LinML.Contracts) | Pure contracts & data models |
| **Runtime** | — | — | ONNX Runtime 1.31.x |

See [CHANGELOG.md](CHANGELOG.md) for full release history.

---

## Execution Providers

| Provider | Status | Verified Hardware | Notes |
|----------|:------:|-------------------|-------|
| `CPUExecutionProvider` | ✅ Verified | Any x86_64 / aarch64 CPU | Always available, built into standard ORT |
| `CUDAExecutionProvider` | ✅ Verified | NVIDIA GeForce RTX 5070 (x86_64), Jetson Orin (aarch64) | Blackwell (sm_120); requires cuDNN 9.x for Conv ops |
| `TensorrtExecutionProvider` | ✅ Verified | NVIDIA GeForce RTX 5070 (x86_64), Jetson Orin (aarch64) | TensorRT 10.16; enable engine cache for repeated runs |
| `NvTensorRTRTXExecutionProvider` | ✅ Verified (Native Linux) | NVIDIA GeForce RTX 4090 | Verified in Daytona cloud runtime; on WSL2 the stock plugin exposes no devices (virtual PCI constraint), so use the [patched build](#plugin-execution-providers); **x86_64 and Linux aarch64 (SBSA)**; aarch64 not yet verified on hardware; [install](#plugin-execution-providers) |
| `OpenVINOExecutionProvider` | ✅ Verified | Intel Xeon CPU | Plugin EP 1.6.1; dynamically registered at runtime; **x86_64 only**; [install and requirements](#plugin-execution-providers) |
| `MIGraphXExecutionProvider` | ✅ Verified | AMD Instinct MI355X (gfx950) | ROCm / MIGraphX verified in Daytona sandbox; **x86_64 only**; [install and requirements](#plugin-execution-providers) |
| `QNNExecutionProvider` | 🔌 Catalog plugin, unverified | Qualcomm Snapdragon (aarch64) | `linml catalog install qnn`; not verified on hardware (no Snapdragon NPU available); **aarch64 only**; [install](#plugin-execution-providers) |

See the [Compatibility Report](docs/compatibility.md) for detailed hardware matrices, driver requirements, and known constraints.

---

## Quick Start

### 1. Install Pre-built CLI (Recommended)

LinML CLI is distributed as a standalone, self-contained binary for **Linux x86_64 and aarch64 (ARM64)** with all native ONNX Runtime dependencies bundled. **No .NET installation is required.**

```bash
# Install to ~/.local/share/linml and symlink to ~/.local/bin/linml (auto-detects architecture)
curl -fsSL https://raw.githubusercontent.com/mesalimllc/LinML/main/scripts/install.sh | bash

# Or install and automatically set up GPU dependencies (cuDNN, TensorRT, plugins):
curl -fsSL https://raw.githubusercontent.com/mesalimllc/LinML/main/scripts/install.sh | bash -s -- --gpu

# Explicit version and architecture:
bash scripts/install.sh --version v0.6.0 --target ~/.local/share/linml --bin-dir ~/.local/bin
```

Pre-built archives (`linml-<version>-linux-x64.tar.gz`, `linml-<version>-linux-arm64.tar.gz`) and SHA-256 checksums are also downloadable directly from [GitHub Releases](https://github.com/mesalimllc/LinML/releases).

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

### 3. Install Python Package (`linml`)

LinML is also distributed as a Python package with zero required runtime dependencies and optional NumPy integration:

```bash
pip install linml
```

High-level inference with NumPy:
```python
import numpy as np
import linml

with linml.InferenceSession("model.onnx", provider="cuda") as session:
    x = np.array([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]], dtype=np.float32)
    result = session.predict(x=x)
    print("Output:", result.outputs["y"].to_numpy())
```

---

### 4. Build from Source

#### Prerequisites
- **Linux x86_64 / aarch64** with glibc 2.28+ (tested on: Ubuntu 24.04+, Debian 12+, Fedora 40+, RHEL 9 / Rocky / Alma 9, openSUSE Tumbleweed / Leap 15.6+, Arch Linux)
- **.NET 10 SDK** (install via `scripts/setup-wsl.sh` or Microsoft's [dotnet-install](https://dot.net/v1/dotnet-install.sh))
- *(Optional)* NVIDIA GPU with CUDA 12.8+ or 13.0+

> **Note:** Alpine Linux (musl) is **not supported** — ONNX Runtime requires glibc.

```bash
# 1. Setup .NET SDK and verify GPU visibility
bash scripts/setup-wsl.sh

# 2. (Optional) Fetch GPU runtime dependencies (no root required)
bash scripts/setup-gpu-deps.sh     # cuDNN 9.x + TensorRT 10.x into ~/.local/linml-deps
bash scripts/fetch-ep-plugins.sh   # Fetch SHA-256 verified vendor plugins (OpenVINO, TRT-RTX)
bash scripts/fetch-ep-plugins.sh trt-rtx-wsl2   # (WSL2 only) patched TRT-RTX EP (the catalog installs it by default since 0.4.2); TRT_RTX_CUSTOM_EP overrides it
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

## Plugin Execution Providers

OpenVINO, TensorRT-RTX, MIGraphX and QNN are plugin execution providers. LinML does not link them into the CLI. It vendors a pinned build of each into `~/.local/linml-deps`, checks its SHA-256, and registers it with ONNX Runtime at run time. Install them with `linml catalog install`, or with the standalone commands below, which need neither the LinML CLI nor a clone of this repository.

| Plugin | Provider name | Platforms | Pinned artifact | Installed to (under `~/.local/linml-deps`) |
|--------|---------------|-----------|-----------------|--------------------------------------------|
| OpenVINO | `OpenVINOExecutionProvider` | Linux x86_64 with an Intel CPU, GPU or NPU | Intel `onnxruntime-ep-openvino` 1.6.1 wheel from PyPI, with the OpenVINO 2026.2.1 runtime bundled | `ep-plugins/openvino/onnxruntime_ep_openvino/` |
| TensorRT-RTX | `NvTensorRTRTXExecutionProvider` | Linux x86_64; Linux aarch64 (SBSA) | NVIDIA `onnxruntime-ep-nv-tensorrt-rtx-cu13` 0.5.0 wheel from PyPI, with TensorRT-RTX 1.7.1 bundled | `ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx/` (x86_64); `ep-plugins/trt-rtx-arm64/onnxruntime_ep_nv_tensorrt_rtx/` (aarch64) |
| TensorRT-RTX on WSL2 | same | WSL2 with GPU passthrough (`/dev/dxg`) | LinML patched build `0.4.0-wsl2p2` (GitHub release; glibc 2.35+, the same floor as the wheel), used alongside the wheel above | `ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/` |
| MIGraphX | `MIGraphXExecutionProvider` | Linux x86_64 with an AMD GPU and ROCm | LinML build `2611.1` (GitHub release), built from AMD's `onnxruntime-ep-amdgpu` | `ep-plugins/migraphx/linml_migraphx_ep/` |
| QNN | `QNNExecutionProvider` | Linux aarch64 with a Qualcomm Snapdragon NPU | Qualcomm `onnxruntime-qnn` 2.6.0 tarball (GitHub release), with the QAIRT 2.50.40 runtime bundled | `ep-plugins/qnn/onnxruntime_qnn/` |

The CLI finds these in `~/.local/linml-deps` automatically. Pass `--root <dir>` to `linml catalog` or `linml providers list` to use another location.

### Requirements

**OpenVINO**
- An Intel CPU (with AVX), Intel integrated or discrete GPU, or Intel NPU. The plugin loads on other hardware, but it exposes no devices there (observed on an AMD Ryzen host).
- ONNX Runtime 1.23 or newer. LinML ships ONNX Runtime 1.31.
- The plugin is published on PyPI as an x86_64 Linux wheel only.

**TensorRT-RTX**
- An NVIDIA GPU, with a driver that supports CUDA 13. NVIDIA's TensorRT-RTX 1.6 matrix targets compute capabilities 7.5, 8.6, 8.9, 12.0 and 12.13. `linml setup` recommends the plugin for RTX Ampere and newer GPUs.
- ONNX Runtime 1.24 or newer. LinML ships ONNX Runtime 1.31.
- **WSL2:** the stock wheel exposes no GPU devices, because WSL's virtual PCI bus blocks enumeration. Install the patched build as well (see [Standalone download](#standalone-download-no-cli-no-clone)).
- **aarch64:** the wheel is built for `manylinux_2_35` (glibc 2.35 or newer). The aarch64 build is not yet verified on aarch64 hardware.
- The wheel ships NVIDIA's license terms and acknowledgements, and TensorRT-RTX is subject to them.

**QNN**
- Linux aarch64 with a Qualcomm Snapdragon NPU (Hexagon HTP). On x86_64 hosts the catalog reports QNN as not applicable.
- ONNX Runtime 1.24.1 or newer.
- Not verified on hardware: no Snapdragon NPU was available for testing.

**MIGraphX**
- An AMD GPU, with ROCm installed at `/opt/rocm` and MIGraphX 2.17 or newer. The plugin loads `libmigraphx_c.so.3`. Check it with `ls /opt/rocm/lib/libmigraphx_c.so.3`.
- ONNX Runtime 1.24 or newer.
- Linux x86_64 only.

### Install with the CLI

```bash
linml catalog install openvino    # OpenVINO plugin (Intel hardware)
linml catalog install trt-rtx     # TensorRT-RTX wheel for this machine's architecture
linml setup --trt-rtx-wsl2        # WSL2 only: the patched TensorRT-RTX build (also installs the wheel)
linml catalog install migraphx    # MIGraphX (AMD ROCm hosts)
linml catalog install qnn         # QNN (Snapdragon aarch64 hosts)
linml catalog list                # status of each plugin
```

### Standalone download (no CLI, no clone)

These commands use only `curl`, `sha256sum`, `unzip`, and `mktemp`. Each downloads from the pinned URL, verifies the SHA-256, and installs into `~/.local/linml-deps`. Each runs in a subshell, so a failed check stops that block and not your terminal.

**OpenVINO (x86_64):**

```bash
(
  set -euo pipefail
  DEPS="$HOME/.local/linml-deps"
  TMP="$(mktemp -d)"
  WHEEL=onnxruntime_ep_openvino-1.6.1-py3-none-manylinux_2_28_x86_64.whl
  URL=https://files.pythonhosted.org/packages/80/50/691cfc0370ab26a62213741026a8e7a172215e02b37cb2ff73ab1b0e1e60/$WHEEL
  SHA256=a1afbbad0f051523bac0ab10b0aa8f49112f60f2c008f2ffb6c43eaf435412e0
  OUT="$DEPS/ep-plugins/openvino/onnxruntime_ep_openvino"
  curl -fsSL -o "$TMP/$WHEEL" "$URL"
  echo "$SHA256  $TMP/$WHEEL" | sha256sum -c
  mkdir -p "$OUT"
  unzip -q -j -o "$TMP/$WHEEL" 'onnxruntime_ep_openvino/*.so' 'onnxruntime_ep_openvino/*.so.*' 'onnxruntime_ep_openvino/*.json' -d "$OUT"
  rm -rf "$TMP"
  echo "OpenVINO installed to $OUT"
)
```

**TensorRT-RTX wheel (x86_64 or aarch64).** Run this on every TensorRT-RTX host, including WSL2:

```bash
(
  set -euo pipefail
  DEPS="$HOME/.local/linml-deps"
  TMP="$(mktemp -d)"
  case "$(uname -m)" in
    x86_64)
      WHEEL=onnxruntime_ep_nv_tensorrt_rtx_cu13-0.5.0-py3-none-manylinux_2_28_x86_64.whl
      URL=https://files.pythonhosted.org/packages/6b/ea/033a6d73b74a443cbf67247e3700b2a2f5076b37af4b00e433fbbd46fa6d/$WHEEL
      SHA256=32f575c633d171b4edf4b53d3bb22b72a6c9c0a850d2982ce9a788a5d40d3298
      DIR=trt-rtx ;;
    aarch64)
      WHEEL=onnxruntime_ep_nv_tensorrt_rtx_cu13-0.5.0-py3-none-manylinux_2_35_aarch64.whl
      URL=https://files.pythonhosted.org/packages/9c/5e/9b63610651a0f5d2c8712b84f64c8cee3b3b83564cc20be207d00272d982/$WHEEL
      SHA256=1f0bb2b89bb3dfd43096fccb8ec1f581c266c5852c4e6ba672b7bf790c8c96c2
      DIR=trt-rtx-arm64
      # The aarch64 wheel is manylinux_2_35: refuse an older glibc before downloading it.
      GLIBC="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $2}')" || GLIBC=""
      if [ -z "$GLIBC" ] || [ "$(printf '%s\n' 2.35 "$GLIBC" | sort -V | head -n 1)" != "2.35" ]; then
        echo "needs glibc 2.35 or newer; this host has ${GLIBC:-no glibc}" >&2; false
      fi ;;
    *) echo "No TensorRT-RTX build for $(uname -m)" >&2; false ;;
  esac
  OUT="$DEPS/ep-plugins/$DIR/onnxruntime_ep_nv_tensorrt_rtx"
  # Extract into a staging directory and swap it in, so an upgrade does not leave an
  # earlier build's runtime (libtensorrt_rtx.so.1.6.1 from 0.4.0) beside the new one.
  mkdir -p "$(dirname "$OUT")"
  STAGE="$(mktemp -d "$OUT.partial.XXXXXX")"
  curl -fsSL -o "$TMP/$WHEEL" "$URL"
  echo "$SHA256  $TMP/$WHEEL" | sha256sum -c
  unzip -q -j -o "$TMP/$WHEEL" 'onnxruntime_ep_nv_tensorrt_rtx/*' -d "$STAGE"
  # The plugin links SONAMEs, but the wheel ships only versioned files: add the links.
  (cd "$STAGE" &&
    ln -sf libtensorrt_rtx.so.1.7.1 libtensorrt_rtx.so.1 &&
    ln -sf libtensorrt_onnxparser_rtx.so.1.7.1 libtensorrt_onnxparser_rtx.so.1 &&
    ln -sf "$(ls libcudart.so.13.* | head -n 1)" libcudart.so.13)
  chmod 755 "$STAGE"
  rm -rf "$OUT"
  mv "$STAGE" "$OUT"
  rm -rf "$TMP"
  echo "TensorRT-RTX installed to $OUT"
)
```

**TensorRT-RTX patched build (WSL2 only).** Run this after the wheel block above. The patched build links the runtime libraries that only the wheel bundles:

```bash
(
  set -euo pipefail
  DEPS="$HOME/.local/linml-deps"
  TMP="$(mktemp -d)"
  ZIP=linml-trt-rtx-ep-0.4.0-wsl2p2-linux-x64.zip
  URL=https://github.com/mesalimllc/LinML/releases/download/ep-trt-rtx-wsl2-0.4.0-wsl2p2/$ZIP
  SHA256=7575c51157110228d581593c04147473868fbb7eefc79aa1b809c5b8bb66c220
  OUT="$DEPS/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep"
  curl -fsSL -o "$TMP/$ZIP" "$URL"
  echo "$SHA256  $TMP/$ZIP" | sha256sum -c
  unzip -q -o "$TMP/$ZIP" 'linml_trt_rtx_ep/*' -d "$TMP/unpacked"
  rm -rf "$OUT"   # replaces any earlier build, e.g. 0.4.0-wsl2p1, only once the new one has unpacked
  mkdir -p "$OUT"
  cp -r "$TMP/unpacked/linml_trt_rtx_ep/." "$OUT/"
  rm -rf "$TMP"
  echo "Patched TensorRT-RTX installed to $OUT"
)
```

**MIGraphX (AMD ROCm hosts):**

```bash
(
  set -euo pipefail
  DEPS="$HOME/.local/linml-deps"
  TMP="$(mktemp -d)"
  ZIP=linml-migraphx-ep-2611.1-linux-x64.zip
  URL=https://github.com/mesalimllc/LinML/releases/download/ep-migraphx-2611.1/$ZIP
  SHA256=968bdb1aa294c1ab4af04cfec0d4318fb2ffa4c69f0b34bc3b98532a199acd86
  OUT="$DEPS/ep-plugins/migraphx/linml_migraphx_ep"
  curl -fsSL -o "$TMP/$ZIP" "$URL"
  echo "$SHA256  $TMP/$ZIP" | sha256sum -c
  mkdir -p "$OUT"
  unzip -q -j -o "$TMP/$ZIP" 'linml_migraphx_ep/*' -d "$OUT"
  rm -rf "$TMP"
  echo "MIGraphX installed to $OUT"
)
```

Or download the one script that does all of the above, and run it:

```bash
curl -fsSLO https://raw.githubusercontent.com/mesalimllc/LinML/main/scripts/fetch-ep-plugins.sh
bash fetch-ep-plugins.sh openvino       # Intel hardware (x86_64)
bash fetch-ep-plugins.sh trt-rtx-wsl2   # WSL2: the wheel, then the patched build
bash fetch-ep-plugins.sh trt-rtx        # native Linux: the wheel
bash fetch-ep-plugins.sh migraphx       # AMD ROCm hosts
bash fetch-ep-plugins.sh qnn             # Snapdragon aarch64 hosts
```

The script needs `curl`, `sha256sum` and `python3`. Its pins are the ones in the commands above.

### Verify

```bash
linml catalog list           # OpenVINO and TensorRT-RTX read Ready when their hardware is present
linml providers list    # NvTensorRTRTXExecutionProvider appears, with its GPU device
linml doctor            # readiness per provider; MIGraphX reports NOT APPLICABLE without AMD ROCm
linml run --provider openvino --require-provider --model model.onnx --inputs inputs.json
linml run --provider trt-rtx --require-provider --model model.onnx --inputs inputs.json
linml run --provider migraphx --require-provider --model model.onnx --inputs inputs.json
```

`--require-provider` makes a missing plugin fail with exit code `3`, instead of falling back to CPU. A successful TensorRT-RTX run prints `Provider: NvTensorRTRTXExecutionProvider (requested: NvTensorRTRTXExecutionProvider)`.

### Troubleshooting

- **TensorRT-RTX on WSL2 is listed as `registered; no EP devices`, and runs fail with exit code `3`.** The stock wheel alone exposes no devices under WSL2, and `linml doctor` says so. Install the patched build (`linml setup --trt-rtx-wsl2`, or the standalone block above). `LINML_TRT_RTX_WSL2=0` keeps the stock wheel, which still exposes no devices on WSL2.
- **OpenVINO is listed but runs fail with exit code `3`.** The plugin loaded, but this host has no Intel CPU, GPU or NPU. Run it on Intel hardware.
- **`linml doctor` reports MIGraphX as NOT APPLICABLE, or a run fails with `libmigraphx_c.so.3: cannot open shared object file`.** The plugin needs an AMD GPU with ROCm and MIGraphX 2.17 or newer. The host does not have them. With `--require-provider` the run exits with code `3`.
- **`available for linux-x64 only`.** That plugin has no build for this architecture. MIGraphX and OpenVINO are x86_64 only.
- **Remove the plugins:** `rm -rf ~/.local/linml-deps/ep-plugins/{openvino,trt-rtx,trt-rtx-wsl2,migraphx,qnn}`.

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

### Execution Provider Catalog (`linml catalog`)

Inspect the multi-provider catalog and download/install pinned plugin execution providers (OpenVINO, TensorRT-RTX, MIGraphX, QNN) directly through the CLI without external shell or Python dependencies: For OpenVINO, TensorRT-RTX, MIGraphX and QNN requirements and standalone downloads that need no CLI, see [Plugin Execution Providers](#plugin-execution-providers).

```bash
# List all 7 catalog entries (3 built-in, 4 plugins) with type, version, readiness status, and location
linml catalog list

# JSON format
linml catalog list --json

# Install a specific plugin execution provider (download, verify SHA256, create symlinks)
linml catalog install openvino
linml catalog install trt-rtx
linml catalog install migraphx

# Install all plugin execution providers
linml catalog install all

# Force re-installation or specify custom cache root
linml catalog install openvino --force --root ~/.local/linml-deps
```

---

### Hardware Auto-Setup (`linml setup`)

Hardware-aware environment bootstrap and installer. Probes host compute accelerators (NVIDIA GPUs, Intel CPUs/GPUs, AMD ROCm GPUs, and CPU vector extensions) and automatically installs matching catalog execution provider plugins:

```bash
# Preview hardware detection and recommended plugins without installing
linml setup --dry-run

# Automatically install all matching vendor plugins for detected hardware
linml setup

# Install all catalog plugins regardless of hardware
linml setup --all

# Opt in to the patched TensorRT-RTX EP for WSL2 (offered automatically when WSL2 + an RTX GPU are detected)
linml setup --trt-rtx-wsl2

# Machine-readable output with hardware inventory and installation plan
linml setup --dry-run --json
```

#### Example Output:
```text
LinML Hardware Auto-Setup (Dry Run)
Probing host compute accelerators...

Hardware Detected:
  CPU: AMD Ryzen 7 5700X3D 8-Core Processor (x64, 16 logical cores)
  GPU: NVIDIA GeForce RTX 5070 (11.9 GB VRAM)
  Environment: WSL2 (Windows Subsystem for Linux)

Recommended Execution Providers:
  • trt-rtx    [NVIDIA GPU detected (NVIDIA GeForce RTX 5070)]
             Note: WSL2 environment: Built-in CUDA EP is active; TensorRT-RTX EP provides INT4/FP8 acceleration for RTX GPUs.
  • openvino   [CPU vector acceleration detected (AMD Ryzen 7 5700X3D 8-Core Processor)]

Actions:
  • trt-rtx: Already installed (~/.local/linml-deps/ep-plugins/trt-rtx/...)
  • openvino: Already installed (~/.local/linml-deps/ep-plugins/openvino/...)

Dry run complete: 2 up-to-date, 0 would be installed.
Run 'linml setup' to execute the installation.
```

`setup` also vendors the native GPU runtime libraries the NVIDIA providers load (cuDNN 9 and TensorRT 10, via pinned `pip install --target` into `~/.local/linml-deps`) when an NVIDIA GPU is detected. `linml` loads them itself at startup, so `LD_LIBRARY_PATH` / `source scripts/env-gpu-deps.sh` is no longer required for the TensorRT and CUDA EPs; a library already resolvable by name (your own `LD_LIBRARY_PATH` or a system install) is left alone. Requires `pip3` (or `python3 -m pip`); without it that step is reported as skipped and the rest of setup still completes. GPU libraries are only installed on an NVIDIA linux-x64 host, also with `--all`.

OpenVINO is recommended only when an Intel CPU or GPU is detected: the plugin EP registers on other vendors but exposes no devices (observed on AMD Ryzen). `--all` still installs it.

#### Options:
- `--dry-run`: Probe hardware and display proposed installation plan without downloading or modifying files.
- `--force`: Force re-download and re-installation even if already present.
- `--all`: Install all catalog plugins regardless of detected hardware.
- `--trt-rtx-wsl2`: Opt in to the LinML-hosted patched TensorRT-RTX EP for WSL2 (requires WSL2 and an RTX Ampere+ GPU; installs the stock `trt-rtx` wheel first for its runtime libraries). When WSL2 and a supported GPU are detected, `linml setup` lists it under "Opt-in available" but never installs it without this flag. On WSL2 the patched build is preferred over the stock wheel automatically.
- `--root <path>`: Custom catalog install root directory.
- `--json`: Output machine-readable JSON hardware inventory, recommendations, and action plan.

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

Diagnose execution provider readiness before running production models. Detects missing shared libraries, driver mismatches, or missing cuDNN dependencies. Inspects both runtime providers and catalog plugin providers, reporting readiness and providing clear install hints (e.g. `Run 'linml catalog install openvino' to vendor missing libraries`) for uninstalled plugins:

```bash
# Basic provider readiness probe (checks Load/Append without execution, inspects catalog plugins)
linml doctor

# Comprehensive model smoke test (loads model and runs real inference per provider)
linml doctor --model model.onnx --inputs inputs.json --json
```

---

### Model Inspection (`linml inspect`)

Inspect ONNX model graph structure, input/output tensors, opset versions, custom metadata, and execution provider compatibility warnings **without running inference**. Useful for CI validation, model cataloging, and debugging EP suitability before deployment.

```bash
# Human-readable output
linml inspect --model model.onnx

# Machine-readable JSON for tooling/CI
linml inspect --model model.onnx --json
```

#### Example Human-Readable Output:
```text
LinML Model Inspection
----------------------
Model:       /path/to/model.onnx
Size:        2.45 MB (2,567,891 bytes)
SHA-256:     a1b2c3d4e5f6...
IR Version:  v10
Opset:       ai.onnx v17
Producer:    LinML-fixture-gen 1.0

Inputs (1):
  • x: float32 [3, 2] (static)

Outputs (1):
  • y: float32 [3, 2] (static)

Metadata Properties (2):
  • author: linml
  • generated_by: fetch-test-fixture.sh

Graph Structure:
  • Nodes:     2
  • Operators: 2 distinct (Add, MatMul)

Execution Provider Compatibility:
  ⚠ [CUDAExecutionProvider] Model contains convolution operations ('Conv'). CUDAExecutionProvider requires NVIDIA cuDNN (libcudnn.so.9) installed on the system.
  ⚠ [TensorrtExecutionProvider] Model contains dynamic dimensions. TensorRT compiles static execution engines; specify '--ep-option trt:trt_profile_min_shapes=...' and '--ep-option trt:trt_engine_cache_enable=1' for optimal caching.
  ℹ [OpenVINOExecutionProvider] Model uses ONNX opset 17. OpenVINO EP supports up to opset 21; unverified operators may fall back to CPU.
```

#### JSON Output Schema (abridged):
```json
{
  "model": {
    "path": "/path/to/model.onnx",
    "sha256": "a1b2c3d4e5f6...",
    "sizeBytes": 2567891,
    "name": "model"
  },
  "irVersion": 10,
  "opsetImports": [{"domain": "ai.onnx", "version": 17}],
  "producerName": "LinML-fixture-gen",
  "producerVersion": "1.0",
  "inputs": [
    {"name": "x", "dataType": "float32", "shape": [3, 2], "isDynamic": false, "symbolicDimensions": []}
  ],
  "outputs": [
    {"name": "y", "dataType": "float32", "shape": [3, 2], "isDynamic": false, "symbolicDimensions": []}
  ],
  "graph": {"nodeCount": 2, "operators": ["Add", "MatMul"]},
  "compatibilityWarnings": [
    {"provider": "CUDAExecutionProvider", "severity": "Warning", "message": "..."},
    {"provider": "TensorrtExecutionProvider", "severity": "Warning", "message": "..."},
    {"provider": "OpenVINOExecutionProvider", "severity": "Info", "message": "..."}
  ]
}
```

#### Options:
- `--model <path>`: Path to the ONNX model file (required).
- `--json`: Output machine-readable JSON to stdout instead of formatted plain text.

#### Exit Codes:
- `0`: Success
- `1`: Inspection failed (model corrupt, parse error, etc.)
- `2`: Invalid arguments (missing `--model`, file not found)

#### Use Cases:
- **CI gating**: Validate model structure before deployment (`linml inspect --model model.onnx --json | jq '.graph.operators'`)
- **EP suitability**: Quick check if a model will run well on TensorRT, CUDA, OpenVINO, MIGraphX without provisioning hardware
- **Model cataloging**: Record SHA-256, tensor signatures, and opset imports in a model registry
- **Debugging**: Identify dynamic dimensions, unsupported operators, or opset mismatches before runtime failures

---

### Model Inference (`linml run`)

Execute one-shot inference on an ONNX model. If `--provider` requests a catalog plugin (such as `trt-rtx`, `openvino`, or `migraphx`), LinML automatically resolves and registers vendored libraries from the catalog cache (`~/.cache/linml/ep-plugins` or `~/.local/linml-deps`) when `--ep-library` is omitted:

```bash
linml run --model model.onnx --inputs inputs.json [options]
```

#### Options:
- `--provider <name>`: Preferred execution provider. Accepts aliases (`cpu`, `cuda`, `trt`, `trt-rtx`, `openvino`/`ov`, `migraphx`, `qnn`) or full EP names (`CUDAExecutionProvider`). Automatically resolves vendored catalog plugins when `--ep-library` is omitted.
- `--require-provider`: Strict mode — exits with code `3` if the preferred provider cannot be used (disables fallback to CPU). If a requested plugin is missing, suggests `linml catalog install <provider>`.
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

### Benchmarking Suite (`linml bench`)

Benchmark model inference latency, percentiles, throughput, and cold-start overhead across execution providers:

```bash
# Benchmark policy-selected or specific execution provider
linml bench --model model.onnx --inputs inputs.json --provider cuda --warmup 5 --iterations 50

# Benchmark all ready providers side-by-side for a performance leaderboard
linml bench --model model.onnx --inputs inputs.json --all-ready

# Output structured metrics in JSON
linml bench --model model.onnx --inputs inputs.json --all-ready --json
```

#### Example Output:
```text
LinML Inference Benchmark
Model: model.onnx
Inputs: inputs.json
Iterations: 50 | Warmup: 5

PROVIDER                         THROUGHPUT      MEAN        p50         p90         p99         COLD START 
----------------------------------------------------------------------------------------------------------
CUDAExecutionProvider            4821.5 inf/s    0.21 ms     0.19 ms     0.25 ms     0.32 ms     945.12 ms  
CPUExecutionProvider             15420.1 inf/s   0.06 ms     0.03 ms     0.14 ms     0.22 ms     72.10 ms   
```

#### Options:
- `--model <path>`: Path to the ONNX model file (required).
- `--inputs <path>`: Path to the inputs JSON file (required).
- `--provider <name>`: Preferred execution provider to benchmark.
- `--all-ready`: Benchmark all ready providers on the system side-by-side.
- `--warmup <n>`: Number of warmup iterations before timing (default: `5`).
- `--iterations <n>`: Number of timed measurement iterations (default: `50`).
- `--ep-option key=value`: Provider-specific options (repeatable).
- `--ep-library name=path`: Register a dynamic plugin EP shared library (repeatable).
- `--root <path>`: Custom catalog install root directory.
- `--json`: Output machine-readable JSON metrics including cold start, mean, min, max, p50, p90, p95, and p99.

---

### Persistent Session Serving (`linml serve`)

When performing repeated inference, one-shot process startup adds overhead. `linml serve` keeps a long-lived ONNX session warm in memory and serves inference requests either as an **HTTP REST daemon** or over **JSON Lines stdio**:

#### 1. HTTP REST Daemon Mode (`--port <port>`)
Run as a local HTTP microservice:
```bash
linml serve --model model.onnx --provider cuda --port 8080 --warmup-inputs warmup.json
```

- **Endpoints**:
  - `GET /health` or `GET /v1/health`: Readiness probe returning model metadata, selected provider, and fallback chain.
  - `POST /v1/models/predict` or `POST /predict`: Submit JSON tensor inputs `{"tensors":[...]}` and receive structured `InferenceResult`.
  - `POST /shutdown` or `POST /v1/shutdown`: Gracefully shut down the server.
- **Example Usage**:
```bash
# Check health
curl http://localhost:8080/health

# Submit prediction
curl -X POST http://localhost:8080/v1/models/predict \
  -H "Content-Type: application/json" \
  -d @inputs.json

# Graceful shutdown
curl -X POST http://localhost:8080/shutdown
```

#### 2. JSON Lines Stdio Mode (Default)
When `--port` is omitted, requests are streamed over stdin/stdout:
```bash
linml serve --model model.onnx --provider cuda --warmup-inputs warmup.json
```

- **Protocol**:
  - `stdin`: Send one input JSON request per line: `{"tensors":[...]}`
  - `stdout`: Emits one `InferenceResult` JSON object per line.
  - Errors: Emits `{"error":"..."}` for bad inputs without terminating the server.
  - Shutdown: Send EOF (`Ctrl+D` or closing stdin) to shut down cleanly with exit code `0`.

**Latency Advantage**: Post-warmup latency drops from ~2.6 seconds per cold startup down to **~1 ms per request**.

---

## Python Client Library (`linml`)

The `linml` Python package brings LinML's hardware abstraction, daemon serving, and diagnostic inspection to Python ML pipelines and Jupyter notebooks.

### Installation

```bash
pip install linml
```

### 1. In-Memory Inference Session

`linml.InferenceSession` manages an underlying `linml serve` process with an in-memory session, delivering ~1 ms inference latency with automatic resource cleanup:

```python
import numpy as np
import linml

# Execute with GPU acceleration and automatic CPU fallback
with linml.InferenceSession("model.onnx", provider="cuda") as session:
    x = np.array([[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]], dtype=np.float32)
    
    # Keyword arguments or dictionary
    result = session.predict(x=x)
    print(f"Provider: {result.selected_provider}")
    print(f"Latency:  {result.latency_ms:.2f} ms")
    
    # Access outputs as NumPy arrays
    y = result.outputs["y"].to_numpy()

    # Or use standard ONNX Runtime session.run() syntax:
    outputs = session.run(["y"], {"x": x})
```

### 2. HTTP REST Daemon Client (`linml.InferenceClient`)

Connect directly to any remote or containerized `linml serve --port <port>` microservice:

```python
import linml

client = linml.InferenceClient("http://127.0.0.1:8080")

# Check service health and provider readiness
health = client.health()
print(f"Status: {health.status}, Active Provider: {health.selected_provider}")

# Submit prediction
result = client.predict({"x": [[1.0, 2.0], [3.0, 4.0], [5.0, 6.0]]})
print("Result:", result.outputs["y"].data)
```

### 3. Model Inspection & Diagnostics

Inspect model graph topology, inputs/outputs, and execution provider compatibility warnings from Python:

```python
import linml

report = linml.inspect("model.onnx")

print(f"Format: ONNX IR v{report.ir_version}")
print(f"Opset:  {[(op.domain, op.version) for op in report.opsets]}")
for inp in report.inputs:
    print(f"Input:  {inp.name} ({inp.data_type}, shape={inp.shape}, dynamic={inp.is_dynamic})")

for warning in report.compatibility_warnings:
    print(f"Warning [{warning.provider}]: {warning.message}")
```

---

## LinML.Catalog Library

`LinML.Catalog` is the Linux counterpart to the Windows ML `ExecutionProviderCatalog`. It unifies execution provider discovery, readiness probing, vendoring of pinned plugin EPs (with SHA-256 integrity verification), registration, and session attachment.

It covers:
- **Built-in EPs**: `CPUExecutionProvider`, `CUDAExecutionProvider`, and `TensorrtExecutionProvider`
- **Plugin EPs**: `OpenVINOExecutionProvider` (1.6.1), `NvTensorRTRTXExecutionProvider` (0.5.0), and `MIGraphXExecutionProvider` (2611.1)

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
