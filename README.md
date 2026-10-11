# LinML

> **Working label.** LinML is a prototype name, not an established project or affiliation with Microsoft.

Linux-first ONNX Runtime inference integration layer. Provides honest provider discovery, explicit selection, ONNX inference, and diagnosable fallback.

## Status

| Gate | Description | Status |
|------|-------------|--------|
| A | Runtime and packaging strategy | ✅ Accepted (locked restore, ADR 0001) |
| B | Contracts and selection policy | ✅ Done (19 policy tests) |
| C | Real CPU inference | ✅ Done (`[11, 22, 13, 24, 15, 26]` verified) |
| D | CUDA readiness and execution | ✅ Done (RTX 5070, profiling evidence) |
| E | CLI and native publishing | ✅ Done (publish verified, 511 MB) |
| F | CI, evidence, and handoff | ✅ Done (CI green, `evidence/`) |

Beyond the gates: plugin EPs (OpenVINO, TensorRT-RTX), the `LinML.Catalog`
library (published as `0.1.0-preview.1`), and 147 tests across five test projects.

## Quick Start

### Prerequisites

- Windows 11 with WSL2 (Ubuntu 24.04+ recommended; tested on 26.04)
- .NET 10 SDK (Windows for editing; WSL for building)
- NVIDIA GPU with CUDA 12.8+ or 13.0+ (optional, for CUDA tests)

### Setup (WSL2)

```bash
# In WSL2 Ubuntu:
bash scripts/setup-wsl.sh
```

### Build

```bash
dotnet restore LinML.slnx --locked-mode
dotnet build LinML.slnx -c Release
```

### Test

```bash
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
linml providers list --json              # List available providers
linml doctor [--model <path> --inputs <path>] --json   # Diagnose readiness (no downloads)
linml run --model <path> --inputs <path> --provider <name> [--require-provider] [--profile] [--json]
linml serve --model <path> [--provider <name>] [--warmup-inputs <path>]   # stdin/stdout, long-lived session
```

Providers: `cpu`, `cuda`, `trt`, `trt-rtx`, `openvino`, `qnn`, `migraphx`.
All commands except `--version` also accept:

- `--ep-library name=path` (repeatable) to register a plugin EP library.
  `LINML_EP_LIBRARIES` (set by `scripts/env-gpu-deps.sh`) does the same.
- `--ep-option key=value` or `provider:key=value` (repeatable) to set provider options.

Plugin EPs are fetched with `scripts/fetch-ep-plugins.sh`; the pins there must
match `LinML.Catalog` (enforced by `ScriptPinAgreementTests`).

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
       └── ONNX Runtime adapter
                ├── CPU, CUDA, TensorRT, MIGraphX (typed appends)
                └── Plugin EPs ── LinML.Catalog
                                   ├── PluginLibraryRegistry (one per process)
                                   └── OpenVINO plugin (pinned, sha256-verified)
```

Dependency direction:
- `LinML.Contracts` — no ORT dependency
- `LinML.Policy` — depends only on Contracts
- `LinML.Runtime.Onnx` — depends on Contracts + Catalog + ORT
- `LinML.Catalog` — depends on Contracts + the managed ORT API only (published package)
- `LinML.Cli` — composition root, depends on all

## LinML.Catalog (library)

`LinML.Catalog` is the Linux counterpart to the Windows ML
`ExecutionProviderCatalog`. It vendors pinned plugin EPs (sha256-verified,
staged install), registers them with ONNX Runtime, and appends their devices
to sessions. It currently covers OpenVINO (`onnxruntime-ep-openvino` 1.6.1).
The host application supplies the native ONNX Runtime (e.g.
`Microsoft.ML.OnnxRuntime.Gpu` 1.31).

Plugin registration goes through `PluginLibraryRegistry`, which the CLI's
`--ep-library` path also uses, so a library is registered with ORT at most
once per process no matter which side registers it first.

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

`LinML.Contracts` and `LinML.Catalog` are published to GitHub Packages by
`.github/workflows/publish-packages.yml`, either when a `vX.Y.Z[-preview.N]`
tag is pushed or by running the workflow manually with a version. The latest
published version is `0.1.0-preview.1`. To consume it, add the feed with a
token that has `read:packages`:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/trackdubllc/index.json" \
  --name github-trackdubllc --username <github-user> --password <token> --store-password-in-clear-text
dotnet add package LinML.Catalog --version 0.1.0-preview.1
```

The opt-in end-to-end test downloads the real plugin and runs MNIST on it:
`LINML_CATALOG_NETWORK_TESTS=1 dotnet test tests/LinML.Catalog.Tests`.

## Documentation

- [ADR 0001: Runtime and Deployment](docs/adr/0001-runtime-and-deployment.md)
- [Compatibility Report](docs/compatibility.md)
- [Diagnostics Schema](docs/diagnostics-schema.md)
- [Input Schema](docs/input-schema.md)
- [WSL Workflow](docs/wsl-workflow.md)

## License

MIT License. See [LICENSE](LICENSE).

## Disclaimer

This is a prototype. It is not affiliated with, endorsed by, or sponsored by Microsoft. ONNX Runtime is a trademark of Microsoft. Windows ML is a trademark of Microsoft.
