# LinML Compatibility Report

> **Status:** Initial scaffold. Updated as gates complete.

## Supported Environments

| Component | Version | Status |
|-----------|---------|--------|
| OS | Ubuntu 26.04.1 LTS (WSL2) | Target |
| Architecture | x64 (amd64) | Required |
| glibc | 2.43 | Verified |
| .NET SDK | 10.0.401 | Pinned |
| .NET Runtime | 10.0.x | Required on target |
| ONNX Runtime | 1.31.0 | Pinned |
| CUDA Driver | 617.42 (CUDA 13.4) | Verified in WSL2 |
| CUDA Toolkit | 12.9, 13.3 | Available in WSL2 |
| GPU | NVIDIA GeForce RTX 5070 (Blackwell, sm_120, 12GB) | Verified |

## Execution Providers

| Provider | Status | Evidence |
|----------|--------|----------|
| CPUExecutionProvider | ✅ Verified (Gate C) | `evidence/cpu-run.json`, `CpuInference_ReturnsExpectedOutput` |
| CUDAExecutionProvider | ✅ Verified (Gate D, RTX 5070) | `evidence/cuda-run.json`, ORT profile shows `MatMul_0/MatMulAddFusion` on `CUDAExecutionProvider` |
| TensorrtExecutionProvider | Listed by ORT, no readiness probe (out of scope) | `linml doctor` reports NOT READY with reason |

## Known Limitations

- **WSL2 only:** Tested in WSL2, not bare-metal Linux. Performance may differ.
- **x64 only:** No ARM64 support in MVP.
- **Ubuntu 26.04:** Other distros not tested.
- **CUDA 13.x:** Older CUDA drivers not tested.
- **Framework-dependent:** Requires .NET 10 runtime on target.

## Unsupported (Deferred)

- AMD MIGraphX, Intel OpenVINO, QNN/NPU, TensorRT-RTX
- ARM64 architecture
- Other Linux distributions (Debian, Fedora, etc.)
- CUDA 11.x, 12.x (unless using CUDA 12.x ORT variant)
- Self-contained deployment
- System-wide installation

## Test Matrix

| Test | CPU | CUDA | Status |
|------|-----|------|--------|
| Unit tests (Policy) | ✓ | N/A | ✅ 19/19 pass |
| Unit tests (Runtime) | ✓ | ✓ | ✅ 6/6 pass |
| Integration: CPU inference | ✓ | N/A | ✅ 3/3 pass, output `[11, 22, 13, 24, 15, 26]` (Gate C) |
| Integration: CUDA inference | N/A | ✓ | ✅ 2/2 pass on RTX 5070 (Gate D) |
| Integration: Fallback | ✓ | ✓ | ✅ 2/2 pass (soft CUDA path on this host) |
| CLI tests | ✓ | ✓ | ✅ 14/14 pass (placeholder assertions, Gate E) |
| Publish verification | ✓ | ✓ | ✅ `verify-linux-publish.sh` all PASS; published `linml` runs CPU + CUDA (Gate E) |

## Evidence Files

Evidence is generated during gate execution and stored in `evidence/` (gitignored):

- `provenance.json` — commit, host, SDK, ORT versions, driver, fixture checksum
- `cpu-run.json` — actual CPU inference output and diagnostics
- `cuda-run.json` — actual CUDA inference output and profiling evidence
- `fallback-run.json` — CUDA unavailable with explicit CPU fallback
- `required-provider-failure.json` — hard selection failure and exit code
- Test logs, publish verification logs
