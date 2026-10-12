# Changelog

All notable changes to LinML are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **`linml setup` vendors GPU runtime libraries** — cuDNN 9 and TensorRT 10 (pinned pip wheels, same pins as `scripts/setup-gpu-deps.sh`) are installed into `~/.local/linml-deps` when an NVIDIA GPU is detected, including under WSL2.
- **Vendored GPU libraries load without `LD_LIBRARY_PATH`** — the CLI, `ExecutionProviderCatalog`, session creation and the readiness probes preload them (SONAME-matched; public `GpuDependencyLoader.PreloadVendored()` for apps that open ORT sessions themselves), so `TensorrtExecutionProvider` no longer fails with `libnvinfer.so.10: cannot open shared object file` unless the env script was sourced. A library the loader already resolves by name is left alone.
- **`linml setup --trt-rtx-wsl2`** — opt in to the patched TensorRT-RTX EP for WSL2 from the CLI (SHA-256 verified; installs the stock wheel's runtime libraries first). Detected WSL2 + RTX hosts see it under "Opt-in available"; it is never installed without the flag. On WSL2 the patched build is now preferred over the stock wheel by library resolution and `doctor`, without needing `env-gpu-deps.sh`.
- **`linml doctor`** reports a "GPU runtime libraries" section on NVIDIA hosts.
- `--root` is honoured for GPU libraries: `ExecutionProviderCatalog.Create(root)` and any CLI command given `--root` preload from that root ahead of `LINML_DEPS` and the default root.
- cuDNN setup also preloads the cuBLAS 13 / NVRTC libraries its wheel vendors under `nvidia/cu13/lib` (`env-gpu-deps.sh` now adds that directory too).
- `setup` detects Intel GPUs and NPUs from sysfs, so OpenVINO is recommended next to a non-Intel CPU when an Intel accelerator is present.

### Changed
- **`linml setup` recommends OpenVINO only for Intel hardware.** The plugin EP registers but exposes no devices on AMD CPUs; the skip is printed, and `--all` still installs it.
- Plugin extraction also keeps top-level `*.md` notices (e.g. `PATCH-NOTICE.md`) and the one-level `licenses/` directory, so redistributed plugins keep their licence and third-party notices. Re-run `linml setup --force` (or `--trt-rtx-wsl2 --force`) to add them to an existing install.

### Fixed
- `setup` no longer fails when pip is missing: the GPU library step is reported as `Skipped` and the exit code is unchanged. GPU libraries are only ever recommended on linux-x64 with an NVIDIA GPU, even with `--all`, and the script-only `tensorrt-lean` is no longer installed by `setup`.
- WSL2 eligibility for the patched TRT-RTX plugin, and the stock `trt-rtx` recommendation, consider every NVIDIA device rather than the first.
- On WSL2 an inherited `LINML_EP_LIBRARIES` mapping that points at the stock wheel no longer hides an installed patched build; an explicit `--ep-library`, `TRT_RTX_CUSTOM_EP` or any other custom path still wins. Registering the patched build without the stock runtime reports a clear error instead of a native load failure.
- The CUDA sticky-error clearing retries its CUDA runtime lookup instead of caching an early failure (for example a CPU session created first).
- Re-sourcing `env-gpu-deps.sh` replaces a stale `openvino=` mapping while keeping other mappings and any canonical OpenVINO override.
- **`bench --all-ready` failed the CUDA entry on WSL2** with `CUDA failure 101: invalid device ordinal` whenever TensorRT ran first. The TensorRT EP leaves a handled error latched in the thread's CUDA state (WSL2 virtual PCI bus id lookup) and the CUDA EP then reported it as its own; the stale error is now cleared after each session is created.
- `scripts/env-gpu-deps.sh` registered OpenVINO under the alias `openvino`, which LinML discovery does not attribute to the plugin; it now uses the catalog name `linml.openvino`.

## [0.4.0] — 2026-10-12

### Added
- **Python package** — `linml` with `InferenceClient` and `InferenceSession`, plus a CLI wheel that bundles the `linml` binary. Results expose per-run `LatencyMs` and node placement diagnostics.
- **Patched TensorRT-RTX plugin EP for WSL2 (opt-in)** — fixes the stock plugin registering but exposing zero devices under WSL2 (virtual PCI vendor `0x1414` rejected by `cudaDeviceGetByPCIBusId`). Install with `bash scripts/fetch-ep-plugins.sh trt-rtx-wsl2`; the default `trt-rtx` pin is unchanged. Library resolution is `TRT_RTX_CUSTOM_EP` → vendored WSL2 build → stock wheel, and the CLI now honours `TRT_RTX_CUSTOM_EP`.
- **`scripts/build-trt-rtx-ep.sh`** — reproducible build of the patched plugin.

### Fixed
- **Python**: scalar (rank-0) tensors handled correctly; unsupported boolean tensors raise a clear `ValueError`; `linml.doctor()` validates inputs; the CLI wrapper can no longer recurse when locating the binary.
- **Python**: session manager verifies the server is serving the requested model before reusing it, supports IPv6 hosts, and uses separate startup and request timeouts.
- **ARM64**: `linml setup` dry-run correctly detects already-installed plugins.
- **Release**: self-contained `linux-arm64` publish now restores its runtime identifier explicitly, and publish verification no longer executes the arm64 binary on the x64 runner (arm64 is verified natively in CI).
- **CI (ARM64)**: ONNX Runtime native libs and `LD_LIBRARY_PATH` configured; integration tests excluded from the ARM64 job.

## [0.3.0] — 2026-10-12

### Added
- **`linml inspect`** — inspect ONNX models without inference. Reports model identity (SHA-256, size), IR version, opset imports, producer metadata, input/output tensor shapes & dynamic dimensions, graph structure, and per-EP compatibility warnings.
- **`IModelInspector` / `ModelInspectionReport`** — strongly-typed contracts in `LinML.Contracts` for programmatic inspection.
- **`OnnxModelInspector`** — combines streaming protobuf parsing with `InferenceSession` metadata.
- **`EpCompatibilityAnalyzer`** — static analysis of operators, opsets, and tensor dimensions for EP-specific warnings (CUDA, TensorRT, OpenVINO, MIGraphX).
- **`OnnxProtobufReader`** — zero-dependency streaming Protocol Buffers reader for ONNX ModelProto; safely skips large tensor initializers.
- **ARM64 (aarch64) support** — self-contained `linux-arm64` CLI binary published to releases; CI builds and tests on `ubuntu-24.04-arm` runners.
- **Expanded distro support** — verified on Fedora 40+, RHEL 9 / Rocky / Alma 9, openSUSE Tumbleweed / Leap 15.6+, Arch Linux (all glibc-based x86_64).

### Fixed
- **Memory**: `InspectAsync` streams via `FileStream` instead of `ReadAllBytesAsync` — no OOM on large models.
- **Safety**: `ReadVarint` capped at 10 bytes; all length-delimited seeks validated against stream bounds; malformed input throws `InvalidDataException`.
- **Robustness**: Protobuf parser no longer crashes/loops on corrupted ONNX files.
- **Fallback**: When `InferenceSession` fails to open, inspector attempts protobuf tensor extraction (hook in place; full fallback planned).
- **Output**: Human-readable report now shows symbolic dimensions for **outputs** (previously only inputs).
- **Fixtures**: Test fixture regenerated with `x`/`y` tensor names; `inputs.json`, `fixture-manifest.json`, and all 6 integration test files updated consistently.
- **CI**: GitHub Actions upgraded to latest versions (checkout v7, setup-dotnet v6, upload-artifact v7, download-artifact v8) — no Node.js 20 deprecation warnings.
- **Installer**: `scripts/install.sh` auto-detects x86_64 vs aarch64 and downloads appropriate binary.

## [0.2.0] — 2026-10-12

### Added
- **`linml setup`** — hardware-aware environment bootstrap. Probes host compute accelerators (NVIDIA GPUs, Intel CPUs/GPUs, AMD ROCm GPUs, CPU vector extensions) and automatically installs matching vendor plugin EPs.
- **`linml catalog`** — plugin EP catalog CLI: `list`, `install`, `install all` with SHA-256 verification.
- **`LinML.Catalog` library** — Linux counterpart to Windows ML's `ExecutionProviderCatalog`:
  - Built-in EPs: `CPUExecutionProvider`, `CUDAExecutionProvider`, `TensorrtExecutionProvider`
  - Plugin EPs: `OpenVINOExecutionProvider` (1.6.1), `NvTensorRTRTXExecutionProvider` (0.4.0), `MIGraphXExecutionProvider` (2611.1)
  - Automatic vendoring, SHA-256 verification, dynamic registration, session attachment via `AppendFromCatalog()`
  - Shared `PluginLibraryRegistry` with CLI
- **MIGraphX EP plugin** (CI-built):
  - Source: `onnxruntime-ep-amdgpu`, pinned to 2611.1
  - Built in ROCm 10.0 container (`rocm/dev-ubuntu-24.04:10.0.0-full`)
  - SHA-256 verified artifacts uploaded to GitHub Release `ep-migraphx-<version>`
  - Catalog pins by URL + SHA-256; published release never overwritten

### Fixed
- **Verification**: All plugin EPs verified with SHA-256; catalog refuses mismatched downloads.
- **WSL2**: TensorRT-RTX EP correctly detected as WSL2-incompatible (virtual PCI constraint).
- **Dependencies**: `scripts/setup-gpu-deps.sh` installs cuDNN 9.x + TensorRT 10.x to `~/.local/linml-deps`.
- **Environment**: `scripts/env-gpu-deps.sh` exports `LD_LIBRARY_PATH` and `LINML_EP_LIBRARIES`.

## [0.1.0] — 2026-10-11

### Added
- Initial release: `LinML.Contracts`, `LinML.Policy`, `LinML.Runtime.Onnx`, `LinML.Catalog`, `LinML.Cli`
- Core commands: `linml run`, `linml doctor`, `linml providers list`, `linml bench`, `linml serve`
- Built-in EPs: CPU, CUDA, TensorRT
- Fallback policy (soft CUDA → CPU, hard CUDA → failure)
- Benchmarking suite with throughput, percentiles, cold-start
- Persistent session serving (`linml serve` HTTP REST + JSON Lines stdio)
- WSL2 development workflow scripts

---

For full release notes, see [GitHub Releases](https://github.com/trackdubllc/LinML/releases).
