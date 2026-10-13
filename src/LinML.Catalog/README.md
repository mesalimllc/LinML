# LinML.Catalog

Execution provider catalog for ONNX Runtime on Linux.

[![NuGet](https://img.shields.io/nuget/v/LinML.Catalog.svg?label=LinML.Catalog&color=blue)](https://www.nuget.org/packages/LinML.Catalog)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](https://github.com/trackdubllc/LinML/blob/main/LICENSE)

`LinML.Catalog` is the Linux counterpart to the Windows ML (WinML) `ExecutionProviderCatalog`. It automates vendoring, SHA-256 integrity verification, dynamic registration, and session attachment for external plugin execution providers on Linux.

### Supported Execution Providers

`LinML.Catalog` provides unified discovery, readiness probing, and session attachment across both built-in and external plugin execution providers:

#### Built-In Providers (`ProviderSource.BuiltIn`)
- **CPU** (`CPUExecutionProvider`): Baseline execution provider, always ready.
- **CUDA** (`CUDAExecutionProvider`): Probed via host runtime (e.g. `Microsoft.ML.OnnxRuntime.Gpu`) and verified before session append.
- **Classic TensorRT** (`TensorrtExecutionProvider`): Probed via host runtime and verified before session append.

#### Plugin Providers (`ProviderSource.Plugin`)
- **OpenVINO Plugin EP** (`onnxruntime-ep-openvino` 1.6.1): Pinned PyPI wheel automated vendoring, SHA-256 verification, and registration.
- **TensorRT-RTX Plugin EP** (`onnxruntime-ep-nv-tensorrt-rtx` 0.4.0): Pinned PyPI wheel automated vendoring, SHA-256 verification, SONAME symlink management, and registration (on WSL2 with GPU passthrough the catalog installs and registers the LinML-hosted patched build instead, default on; opt out with `LINML_TRT_RTX_WSL2=0`; `TRT_RTX_CUSTOM_EP` overrides both). In containers the install root is `$HOME/.local/linml-deps` unless `Create(root)` is used; mount a volume there to keep installs across runs.
- **MIGraphX Plugin EP** (`onnxruntime-ep-amdgpu` 2611.1): Pinned standalone plugin release archive vendoring, SHA-256 verification, and registration.

---

## Installation

```bash
dotnet add package LinML.Catalog
```

> **Prerequisite:** `LinML.Catalog` references `Microsoft.ML.OnnxRuntime.Managed` to avoid native library conflicts. The consuming host application must supply a native ONNX Runtime package (such as `Microsoft.ML.OnnxRuntime.Gpu` or `Microsoft.ML.OnnxRuntime`).

---

## Usage

```csharp
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

// 1. Get default catalog instance
IExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();

// 2. Discover OpenVINO plugin
IExecutionProvider openVino = catalog.Find(ProviderId.OpenVino)!;

// 3. Ensure plugin binaries are ready (downloads & verifies SHA-256 checksum)
ProviderEnsureResult ensured = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));

// 4. Configure SessionOptions with verified plugin EP
using var sessionOptions = new SessionOptions();
if (ensured.IsReady)
{
    ProviderAppendResult result = sessionOptions.AppendFromCatalog(openVino, [OrtHardwareDeviceType.CPU]);
}

// 5. Create ONNX Runtime session
using var session = new InferenceSession("model.onnx", sessionOptions);
```

---

## Design Principles

- **No Conflicting Binaries**: References only `Microsoft.ML.OnnxRuntime.Managed`. The host application supplies the native ONNX Runtime (e.g. `Microsoft.ML.OnnxRuntime.Gpu`), avoiding duplicate `libonnxruntime.so` collisions.
- **Integrity Verified**: Plugin binaries are verified against strict cryptographic SHA-256 digests.
- **Shared Registry**: Shares the `PluginLibraryRegistry` with the LinML CLI.

---

## Documentation & Repository

For more details, CLI tools, and architecture documentation, visit the [LinML GitHub Repository](https://github.com/trackdubllc/LinML).
