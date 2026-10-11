# LinML

> **Working label.** LinML is a prototype name, not an established project or affiliation with Microsoft.

Linux-first ONNX Runtime inference integration layer. Provides honest provider discovery, explicit selection, ONNX inference, and diagnosable fallback.

## Status

| Gate | Description | Status |
|------|-------------|--------|
| A | Runtime and packaging strategy | Pending |
| B | Contracts and selection policy | Pending |
| C | Real CPU inference | Pending |
| D | CUDA readiness and execution | Expected to pass (RTX 5070 available) |
| E | CLI and native publishing | Pending |
| F | CI, evidence, and handoff | Pending |

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
linml doctor --json                      # Diagnose readiness (no downloads)
linml run --model <path> --inputs <path> --provider <cpu|cuda> [--require-provider] [--json]
```

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
                ├── CPU
                └── CUDA, optional until hardware-verified
```

Dependency direction:
- `LinML.Contracts` — no ORT dependency
- `LinML.Policy` — depends only on Contracts
- `LinML.Runtime.Onnx` — depends on Contracts + ORT
- `LinML.Cli` — composition root, depends on all

## Documentation

- [ADR 0001: Runtime and Deployment](docs/adr/0001-runtime-and-deployment.md)
- [Compatibility Report](docs/compatibility.md)
- [Diagnostics Schema](docs/diagnostics-schema.md)
- [Input Schema](docs/input-schema.md)

## License

MIT License. See [LICENSE](LICENSE).

## Disclaimer

This is a prototype. It is not affiliated with, endorsed by, or sponsored by Microsoft. ONNX Runtime is a trademark of Microsoft. Windows ML is a trademark of Microsoft.
