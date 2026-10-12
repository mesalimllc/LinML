# LinML Compatibility Report

> **Status:** Current as of 2026-10-11. All gates complete; EPs verified per table below.

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
| TensorrtExecutionProvider | ✅ Verified (RTX 5070) | `evidence/trt-run.json`, `evidence/trt-mnist-run.json` — exact logits, TRTKernel placement; TF32 default, use `trt:trt_engine_cache_*` for repeated runs |
| NvTensorRTRTXExecutionProvider | ✅ Verified (native Linux, RTX 4090 Daytona) | TrtRtxInferenceTests 3/3; **WSL2-blocked** — virtual PCI vendor 0x1414 + bus-id mismatch, root-caused in `evidence/trt-rtx-wsl2-block.md`, upstream issue [#13](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/issues/13), fix proposed in PR [#12](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/pull/12). **Workaround:** `scripts/build-trt-rtx-custom.sh` builds the EP with the WSL2 fallback patch; set `TRT_RTX_CUSTOM_EP` to use it. Two tiers — `local` (upstream `main` + enumeration patch) and `upstream` (PR head, includes per-GPU registration `6777daf`) |
| OpenVINOExecutionProvider | ✅ Verified (plugin EP 1.6.1, Intel Xeon) | MNIST argmax=8, no fallback; `onnxruntime-ep-openvino` wheel |
| MIGraphXExecutionProvider | ✅ Verified (Daytona MI355X gfx950) | `evidence/migraphx-run.json`, MGXKernel on MIGraphXExecutionProvider, exact logits |
| QNNExecutionProvider | 🔌 Wired, blocked | No Snapdragon NPU; QAIRT SDK needs Qualcomm account |

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

- **CUDA cold start, corrected:** ORT 1.31 ships **precompiled sm_120 cubins
  (317, zero PTX)** — ORT's own kernels need no JIT. The ~16s first-touch cost
  is **cuBLAS/cuDNN JIT-compiling for sm_120** (proven: `CUDA_CACHE_DISABLE=1`
  → 19.7s wall / 16.5s inference vs 2.6s / 0.36s cached). The driver cache
  (`~/.nv/ComputeCache`, 1.1 GB here) **does work across processes** —
  the earlier "every call pays full cost" claim was wrong; it held only
  because each measurement run was compared against a cold cache. Steady
  per-process floor is ~2.6s wall (CLR + 300 MB native load + CUDA context
  creation; CPU-only is ~1.0s), none of which is cacheable. First-ever run
  on a machine pays ~16s JIT once, then ~2.6s per invocation.
- **TensorRT rebuilds engines every run (~44s) unless cached:** without
  `trt_engine_cache_enable`, each CLI invocation pays a full engine build
  (MNIST: 44s wall vs 0.09s inference). With
  `--ep-option trt:trt_engine_cache_enable=1 --ep-option
  trt:trt_engine_cache_path=<dir>`, the second run loads the cached
  `*_sm120.engine` in ~5.7s wall. Always enable the engine cache for
  repeated TRT runs; cache files are GPU-arch-specific.

- **WSL2 only:** Tested in WSL2, not bare-metal Linux. Performance may differ.
- **x64 only:** No ARM64 support in MVP.
- **Ubuntu 26.04:** Other distros not tested.
- **CUDA 13.x:** Older CUDA drivers not tested.
- **cuDNN required for CUDA Conv ops:** `CUDAExecutionProvider` runs Gemm-only
  models without cuDNN, but Conv (and similar) kernels fail at run time with
  `dlopen failed for libcudnn.so` when cuDNN 9.x is absent. Proven by MNIST
  (Opp 1): CPU always works; CUDA Conv needs `libcudnn.so` visible via
  system install or `LD_LIBRARY_PATH`. Provider listed ≠ model runnable.
- **OpenVINO EP (Intel CPU/GPU/NPU):** Use Intel's standalone plugin EP,
  `onnxruntime-ep-openvino` **1.6.1**, vendored by `scripts/fetch-ep-plugins.sh`
  and registered by `scripts/env-gpu-deps.sh`. It uses only the stable plugin
  EP API, so it loads into the ORT 1.31 LinML ships: MNIST runs on
  `OpenVINOExecutionProvider` with no fallback (argmax=8, Intel Xeon CPU,
  Linux). Notes:
  - `onnxruntime-ep-openvino` 1.7.0 fails to create the EP on ORT 1.31
    (`Experimental function OrtEpApi_ReleaseEpContextConfig_SinceV28 is not
    available in this build`); LinML falls back cleanly.
  - The older `onnxruntime-openvino` 1.24.1 package does **not** work with ORT
    1.31: its EP uses ORT's internal provider-bridge interface, which is not
    ABI-stable across versions, and it segfaults in the EP constructor.
  - Earlier reports of "0 devices" were not caused by a missing `plugins.xml`
    (neither wheel ships one; OpenVINO discovers its device plugins without
    it). An EP only appears in `GetEpDevices` once its library is registered
    (`--ep-library` / `LINML_EP_LIBRARIES`).
  - Only the CPU device is verified; GPU/NPU need Intel hardware to test.
- **MIGraphX plugin EP (AMD GPU):** AMD publishes no prebuilt plugin, so
  LinML builds `libmigraphx-ep.so` from
  [onnxruntime-ep-amdgpu](https://github.com/onnxruntime/onnxruntime-ep-amdgpu)
  with `scripts/build-migraphx-ep.sh`. The `MIGraphX EP plugin` workflow runs
  that script in `rocm/dev-ubuntu-24.04:10.0.0-full`, and a manual run with
  `publish` attaches the archive to an `ep-migraphx-<version>` release. Every
  input is pinned by commit or sha256: the EP release-branch commit, the ORT
  1.31.0 release tarball, the `migraphx_libs` 2.17.0 wheel and the MIGraphX
  headers. Notes:
  - Building needs no GPU. At run time the host needs ROCm with MIGraphX
    **>= 2.17** (`libmigraphx_c.so.3`). Older MIGraphX lacks symbols the EP uses
    and fails to load. The plugin's RUNPATH is `$ORIGIN:/opt/rocm/lib`.
  - The EP is built against the ORT 1.31.0 headers and negotiates the plugin
    API down to v24.
  - Not yet verified on AMD hardware in this repo.

## Up next

- ARM64 architecture
- Other Linux distributions (Debian, Fedora, etc.)
- CUDA 11.x, 12.x (unless using CUDA 12.x ORT variant)
- Self-contained deployment
- System-wide installation

## Test Matrix

| Suite | Tests | Status |
|-------|-------|--------|
| Policy | 23 | ✅ green |
| Runtime.Onnx | 22 | ✅ green |
| Catalog | 31 | ✅ green (incl. script/catalog pin agreement) |
| Integration | 20 | ✅ green (CPU/CUDA/TRT/TRT-RTX/OpenVINO/MIGraphX where hardware allows; honest skips elsewhere) |
| CLI | 69 | ✅ green (incl. 6 catalog and 6 serve tests) |
| **Total** | **165** | ✅ green with GPU env and with `CUDA_VISIBLE_DEVICES=''` |

| Integration area | CPU | CUDA | Status |
|------------------|-----|------|--------|
| CPU inference | ✓ | N/A | ✅ 3/3, output `[11, 22, 13, 24, 15, 26]` (Gate C) |
| CUDA inference | N/A | ✓ | ✅ RTX 5070 (Gate D) |
| TensorRT | ✓ (GPU) | — | ✅ RTX 5070, exact logits, TRTKernel placement |
| TensorRT-RTX | — | ✅ (native Linux) | ✅ RTX 4090 Daytona; WSL2 honestly skipped |
| OpenVINO | ✓ | ✗ | ✅ plugin EP 1.6.1, Intel Xeon CPU; GPU/NPU untested |
| MIGraphX | ✅ (Daytona) | N/A | ✅ MI355X gfx950, exact logits |
| Fallback | ✓ | ✓ | ✅ soft/hard selection, execution-time fallback |
| serve (long-lived) | ✓ | ✓ | ✅ warmup, reuse, error envelopes, reopen-after-poison |
| Publish verification | ✓ | ✓ | ✅ `verify-linux-publish.sh` all PASS |

## Evidence Files

Evidence is generated during gate execution and stored in `evidence/` (gitignored):

- `provenance.json` — commit, host, SDK, ORT versions, driver, fixture checksum
- `cpu-run.json` — actual CPU inference output and diagnostics
- `cuda-run.json` — actual CUDA inference output and profiling evidence
- `fallback-run.json` — CUDA unavailable with explicit CPU fallback
- `required-provider-failure.json` — hard selection failure and exit code
- Test logs, publish verification logs
