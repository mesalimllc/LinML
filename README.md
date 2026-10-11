# LinML

> **Working label.** LinML is a prototype name, not an established project or affiliation with Microsoft.

Linux-first ONNX Runtime inference integration layer. Provides honest provider discovery, explicit selection, ONNX inference, and diagnosable fallback — across CPU, CUDA, TensorRT, TensorRT-RTX, OpenVINO, MIGraphX, and QNN.

## Status

| Gate | Description | Status |
|------|-------------|--------|
| A | Runtime and packaging strategy | ✅ Accepted (locked restore, ADR 0001) |
| B | Contracts and selection policy | ✅ Done (23 policy tests) |
| C | Real CPU inference | ✅ Done (`[11, 22, 13, 24, 15, 26]` verified) |
| D | CUDA readiness and execution | ✅ Done (RTX 5070, profiling evidence) |
| E | CLI and native publishing | ✅ Done (publish verified, 306 MB) |
| F | CI, evidence, and handoff | ✅ Done (CI green, `evidence/`) |

**147 tests passing** (25 Catalog + 22 Runtime + 23 Policy + 20 Integration + 57 CLI).

## Execution Providers

| Provider | Status | Notes |
|----------|--------|-------|
| CPUExecutionProvider | ✅ Verified | Always works |
| CUDAExecutionProvider | ✅ Verified | RTX 5070; needs cuDNN for Conv |
| TensorrtExecutionProvider | ✅ Verified | RTX 5070; enable engine cache for repeated runs |
| NvTensorRTRTXExecutionProvider | ✅ Verified (native Linux) | RTX 4090 (Daytona); **WSL2-blocked** (virtual PCI, root-caused) |
| OpenVINOExecutionProvider | ✅ Verified (plugin EP 1.6.1) | Intel Xeon CPU; GPU/NPU untested |
| MIGraphXExecutionProvider | ✅ Verified (Daytona MI355X) | gfx950, exact logits |
| QNNExecutionProvider | 🔌 Wired, blocked | No Snapdragon hardware; QAIRT SDK gated |

## Quick Start

### Prerequisites

- Windows 11 with WSL2 (Ubuntu 24.04+ recommended; tested on 26.04)
- .NET 10 SDK (Windows for editing; WSL for building)
- NVIDIA GPU with CUDA 12.8+ or 13.0+ (optional, for CUDA/TRT tests)

### Setup (WSL2)

```bash
# In WSL2 Ubuntu:
bash scripts/setup-wsl.sh          # .NET 10 SDK + GPU visibility check
```

### GPU dependencies (optional — for CUDA/TRT/OpenVINO/TRT-RTX)

```bash
bash scripts/setup-gpu-deps.sh     # cuDNN + TensorRT libs into ~/.local/linml-deps (pip wheels, no sudo)
bash scripts/fetch-ep-plugins.sh   # vendor plugin EPs (OpenVINO, TensorRT-RTX), sha256-verified
source scripts/env-gpu-deps.sh     # LD_LIBRARY_PATH + default LINML_EP_LIBRARIES
```

### Build & Test

```bash
dotnet restore LinML.slnx --locked-mode
dotnet build LinML.slnx -c Release
dotnet test LinML.slnx -c Release
```

### Run

```bash
# Publish
dotnet publish src/LinML.Cli/LinML.Cli.csproj -c Release -r linux-x64 --self-contained false -o artifacts/publish/linux-x64

# Verify
bash scripts/verify-linux-publish.sh artifacts/publish/linux-x64

# Use
./artifacts/publish/linux-x64/linml --version
./artifacts/publish/linux-x64/linml providers list --json
./artifacts/publish/linux-x64/linml doctor --json
./artifacts/publish/linux-x64/linml run --model model.onnx --inputs inputs.json --provider cpu --json
```

## CLI Reference

```bash
linml --version                          # Print version info
linml providers list [--json]            # List available providers
linml doctor [--model <path> --inputs <path>] [--json]   # Diagnose readiness; real smoke with --model
linml run --model <path> --inputs <path> [--provider <p>] [--require-provider] [--profile] [--json]
linml serve --model <path> [--provider <p>] [--warmup-inputs <path>]   # long-lived session, JSON-lines over stdio
```

`--provider` accepts aliases: `cpu`, `cuda`, `trt`/`tensorrt`, `trt-rtx`, `openvino`/`ov`, `qnn`, `migraphx`/`amd` — or a raw ORT EP name.

`--ep-option key=value` (global) or `--ep-option provider:key=value` (scoped, repeatable) passes provider options, e.g. `--ep-option trt:trt_engine_cache_enable=1`.

`--ep-library name=path` (repeatable) or `LINML_EP_LIBRARIES="name=path;..."` registers plugin EP libraries before discovery.

### `linml serve` protocol

One JSON request per line on stdin, one `InferenceResult` per line on stdout:

```bash
printf '%s\n%s\n' "$REQ1" "$REQ2" | linml serve --model model.onnx --provider cuda --warmup-inputs warm.json
```

- Request: `{"tensors":[...]}` (same schema as `--inputs` files)
- Response: compact `InferenceResult` (same schema as `run --json`)
- Error: `{"error":"..."}` — server keeps serving; EOF exits 0
- Post-warmup: ~1 ms per request (vs ~2.6 s per one-shot call)

### Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Execution failure |
| 2 | Invalid arguments |
| 3 | Required provider unavailable |
| 4 | Unexpected/internal error |

## Architecture

```
CLI / application
        │
        ├── Contracts: requests, results, readiness, diagnostics
        ├── Policy: provider selection and explicit fallback
        ├── Catalog: plugin EP vendoring + registration (published package)
        └── ONNX Runtime adapter
                 ├── built-in: CPU, CUDA, TensorRT
                 └── plugin: TensorRT-RTX, OpenVINO (registered at runtime)
```

Dependency direction:
- `LinML.Contracts` — no ORT dependency
- `LinML.Policy` — depends only on Contracts
- `LinML.Catalog` — depends on Contracts + the managed ORT API only (published package)
- `LinML.Runtime.Onnx` — depends on Contracts + ORT + Catalog (shared plugin registry)
- `LinML.Cli` — composition root, depends on all

## LinML.Catalog (library)

`LinML.Catalog` is the Linux counterpart to the Windows ML
`ExecutionProviderCatalog`. It vendors pinned plugin EPs (sha256-verified,
staged install), registers them with ONNX Runtime, and appends their devices
to sessions. It covers OpenVINO (`onnxruntime-ep-openvino` 1.6.1) and
TensorRT-RTX (`onnxruntime-ep-nv-tensorrt-rtx` 0.4.0). The host application
supplies the native ONNX Runtime (e.g. `Microsoft.ML.OnnxRuntime.Gpu` 1.31).

```csharp
IExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
IExecutionProvider openVino = catalog.Find(ProviderId.OpenVino)!;

ProviderEnsureResult ensured = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));
using var options = new SessionOptions();
if (ensured.IsReady)
{
    ProviderAppendResult appended = options.AppendFromCatalog(openVino, [OrtHardwareDeviceType.CPU]);
}
```

The CLI and the catalog share one plugin registry
(`PluginLibraryRegistry`), so a plugin registered via `--ep-library` /
`LINML_EP_LIBRARIES` is visible to the catalog and vice versa.
`ScriptPinAgreementTests` asserts the bash vendoring scripts and the C#
catalog pin the same wheel URLs and digests.

Packages are published to GitHub Packages by
`.github/workflows/publish-packages.yml` when a `vX.Y.Z` tag is pushed. To
consume them, add the feed with a token that has `read:packages`:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/trackdubllc/index.json" \
  --name github-trackdubllc --username <github-user> --password <token> --store-password-in-clear-text
```

The opt-in end-to-end test downloads the real plugin and runs MNIST on it:
`LINML_CATALOG_NETWORK_TESTS=1 dotnet test tests/LinML.Catalog.Tests`.

## Documentation

- [ADR 0001: Runtime and Deployment](docs/adr/0001-runtime-and-deployment.md)
- [ADR 0002: Multi-EP and Plugin Strategy](docs/adr/0002-multi-ep-and-plugins.md)
- [Compatibility Report](docs/compatibility.md)
- [Diagnostics Schema (incl. serve protocol)](docs/diagnostics-schema.md)
- [Input Schema](docs/input-schema.md)
- [WSL Workflow](docs/wsl-workflow.md)

## License

MIT License. See [LICENSE](LICENSE).

## Disclaimer

This is a prototype. It is not affiliated with, endorsed by, or sponsored by Microsoft. ONNX Runtime is a trademark of Microsoft. Windows ML is a trademark of Microsoft.
