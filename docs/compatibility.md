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

- **Publish size 306 MB (was 511 MB):** `linux-x64` framework-dependent publish.
  Breakdown: `libonnxruntime_providers_cuda.so` 271 MB, `libonnxruntime.so`
  32 MB, managed + deps ~3 MB. The ORT Gpu meta-package also packs Windows
  native DLLs (~206 MB) that flow into Linux publish as content; the CLI
  project strips them post-publish (`RemoveWindowsOrtBinaries` target, Opp 6).
  Remaining bulk is the CUDA EP itself — a CPU-only flavor would be ~35 MB
  (29 MB CPU native lib, measured in Gate D, + managed) but is not needed
  for correctness (the Gpu package runs CPU fine).
- **CUDA 13.0 build is the default; 12.x is the fallback:** ORT 1.31.0 Gpu
  ships a CUDA 13.0 build (cuDNN 9.x) that works with CUDA 13.x drivers via
  minor-version compatibility (proven on driver 617.42 / CUDA 13.4). Hosts
  with CUDA 12.x-only drivers need the CUDA 12.x variant of the same package
  (swap the `Microsoft.ML.OnnxRuntime.Gpu` build; requires CUDA 12.8+ for
  RTX 5070). Not tested — no 12.x-only host available.
- **Bare metal untested:** all Linux evidence is WSL2 with GPU passthrough;
  driver behavior and cold-start timing may differ on bare metal.

- **CUDA cold start (~8s, one-time per process):** measured staged breakdown
  (Opp 5, RTX 5070 WSL2): ORT native load + CUDA append ~4.0s, session
  create ~0.45s, first run (CUDA context + kernel init) ~3.9s, second run
  1ms. Steady-state inference is fast; latency-sensitive hosts should do a
  warmup run and reuse a long-lived session (the CLI is one-shot per
  invocation, so each call pays cold start).

- **WSL2 only:** Tested in WSL2, not bare-metal Linux. Performance may differ.
- **x64 only:** No ARM64 support in MVP.
- **Ubuntu 26.04:** Other distros not tested.
- **CUDA 13.x:** Older CUDA drivers not tested.
- **cuDNN required for CUDA Conv ops:** `CUDAExecutionProvider` runs Gemm-only
  models without cuDNN, but Conv (and similar) kernels fail at run time with
  `dlopen failed for libcudnn.so` when cuDNN 9.x is absent. Proven by MNIST
  (Opp 1): CPU always works; CUDA Conv needs `libcudnn.so` visible via
  system install or `LD_LIBRARY_PATH`. Provider listed ≠ model runnable.
- **OpenVINO EP (Intel CPU/GPU/NPU):** Compilation and inference work via C API (MNIST argmax=8 on AMD CPU). However, **enumeration returns 0 devices** on this platform because the `onnxruntime-openvino` wheel (1.24.1, built for ORT 1.24.1) lacks `plugins.xml` for runtime plugin discovery and has no compile-time plugins. The EP registers with ORT but `GetSupportedDevices` returns zero devices. Works on Intel hardware with proper OpenVINO install. [OpenVINO issue](https://github.com/microsoft/onnxruntime/issues/25557).

## Up next

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
| Integration: OpenVINO | ✓ | ✗ | ✅ CPU inference works (MNIST argmax=8); enumerates 0 devices on AMD CPU (no plugins.xml in wheel) |
| Integration: QNN | ✗ | ✗ | ✅ Wired (provider ID, adapter, readiness); blocked — no Snapdragon NPU, QAIRT SDK needs Qualcomm account |
| Integration: MIGraphX | ✗ | ✗ | ✅ Wired (provider ID, adapter, readiness); blocked — no AMD GPU, ROCm-on-WSL available but no AMD hardware |
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
