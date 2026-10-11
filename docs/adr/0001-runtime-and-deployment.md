# ADR 0001: Runtime and Deployment Strategy

## Status
Proposed

## Context

LinML is a Linux-first inference integration layer prototype. We need to:
1. Execute ONNX models on Linux x64 with CPU (required) and CUDA (optional) providers.
2. Ship app-local native dependencies without system-wide installation.
3. Support development on a Windows host with WSL2 for Linux execution.

Constraints:
- .NET 10 is the target framework (matches Trackdub's existing inference layer).
- Must support NVIDIA RTX 5070 (Blackwell, sm_120) with CUDA 13.4 driver.
- WSL2 Ubuntu 26.04 provides Linux execution environment with GPU passthrough.
- No Python dependency in the deployed product.

## Decision

### Runtime
- **.NET SDK:** 10.0.401 (pinned in `global.json` with `rollForward: latestPatch`)
- **Target framework:** `net10.0`
- **Target RID:** `linux-x64`

### ONNX Runtime Packages
| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.ML.OnnxRuntime` | 1.31.0 | CPU execution provider (managed + native) |
| `Microsoft.ML.OnnxRuntime.Gpu` | 1.31.0 | CUDA execution provider (CUDA 13.0 build, cuDNN 9.x) |

**Rationale:**
- ORT 1.31.0 is the latest stable release (as of 2026-10-10).
- ORT 1.27+ GPU packages default to CUDA 13.0 builds.
- CUDA 13.0 builds are compatible with CUDA 13.4 driver via [NVIDIA minor version compatibility](https://docs.nvidia.com/deploy/cuda-compatibility/#minor-version-compatibility).
- RTX 5070 (Blackwell, sm_120) requires CUDA 12.8+ or 13.0+.

**Fallback:** If the CUDA 13.0 build fails on RTX 5070, use the CUDA 12.x variant of `Microsoft.ML.OnnxRuntime.Gpu` 1.31.0 (requires CUDA 12.8+; we have 12.9 and 13.3 toolkits in WSL).

### Deployment Model
- **Framework-dependent deployment** (not self-contained): requires .NET 10 runtime installed on target.
- **App-local native libraries:** ORT native `.so` files ship alongside the application.
- **No system-wide installation:** No privileged installer, daemon, or system library replacement.

### Build and Test Environment
- **Development host:** Windows 11
- **Build/test environment:** WSL2 Ubuntu 26.04.1 LTS (glibc 2.43, kernel 6.18.40)
- **GPU access:** NVIDIA RTX 5070 via WSL2 GPU passthrough
- **.NET SDK in WSL:** Installed via `scripts/setup-wsl.sh` (channel 10.0)

### GPU Distribution Strategy
Use `Microsoft.ML.OnnxRuntime.Gpu` which includes CPU EP fallback. **Verification required (Gate A step 4):** Confirm the GPU package can execute CPU-only workloads on a host without NVIDIA libraries. If not, ship two native flavors selected at publish time.

## Consequences

### Positive
- Latest stable ORT with CUDA 13.0 support for RTX 5070.
- WSL2 provides real Linux execution with GPU access from Windows host.
- Framework-dependent deployment keeps publish size small.
- Central package management (`Directory.Packages.props`) ensures version consistency.

### Negative
- WSL2 is not bare-metal Linux; performance and some driver behaviors may differ.
- GPU package includes CUDA dependencies (~500MB+ native libraries).
- .NET SDK must be installed separately in WSL (not shared with Windows installation).

### Neutral
- Requires .NET 10 runtime on target Linux systems (documented in README).
- CUDA 13.0 build requires CUDA 13.0+ driver (or 12.8+ for the CUDA 12.x variant).

## Alternatives Considered

| Alternative | Why Rejected |
|-------------|--------------|
| Self-contained deployment | Larger publish size (~100MB+); not needed for prototype |
| `Microsoft.ML.OnnxRuntime` 1.30.0 or earlier | Older; no benefit over 1.31.0 |
| CUDA 12.x build of ORT 1.31.0 | Kept as fallback; CUDA 13.0 is the default and should work |
| Native AOT deployment | ORT native interop complexity; not needed for prototype |
| Python-based fixture generation | Violates "no Python dependency" rule |

## Evidence

**Environment inspection (2026-10-10):**
```bash
# Windows host
dotnet --list-sdks
# 9.0.318, 10.0.303, 10.0.401, 11.0.100-rc.1

# WSL2 Ubuntu
wsl -d Ubuntu -e bash -c "uname -a; lsb_release -a"
# Linux 6.18.40.1-microsoft-standard-WSL2 x86_64
# Ubuntu 26.04.1 LTS, glibc 2.43

wsl -d Ubuntu -e nvidia-smi
# NVIDIA GeForce RTX 5070, Driver 617.42, CUDA 13.4

wsl -d Ubuntu -e bash -c "ls /usr/local/cuda*"
# /usr/local/cuda, cuda-12, cuda-12.9, cuda-13, cuda-13.3
```

**NuGet package versions (2026-10-10):**
```bash
curl -s "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime/index.json"
# Latest: 1.31.0

curl -s "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.gpu/index.json"
# Latest: 1.31.0
```

**ORT CUDA compatibility (from onnxruntime.ai):**
- ORT 1.27+ GPU packages: CUDA 13.0 + cuDNN 9.x (default)
- CUDA 12.x variant available for ORT 1.27-1.31
- Minor version compatibility: CUDA 13.0 build works with CUDA 13.x drivers

## Open Questions

1. Does `Microsoft.ML.OnnxRuntime.Gpu` 1.31.0 execute CPU-only on a CUDA-less host? (Gate A step 4)
2. Does ORT 1.31.0 CUDA 13.0 build support RTX 5070 (sm_120)? (Gate D)
3. What is the exact publish size with GPU package? (Gate E)
