# Changelog

All notable changes to LinML are documented here. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **QNN in the catalog.** `linml catalog install qnn` installs the Qualcomm QNN plugin (`onnxruntime-qnn` 2.6.0, aarch64) on Snapdragon aarch64 hosts. On other architectures the catalog reports it as not applicable, `catalog install` refuses without downloading it, and `setup --all` and `doctor` skip it with the reason. Not verified on hardware.

## [0.5.1] — 2026-10-12

### Fixed
- **The patched WSL2 TensorRT-RTX build now loads on Ubuntu 24.04 and 22.04.** The `0.4.0-wsl2p1` archive was built on a host with glibc 2.43 and needed `GLIBC_2.43` (also `GLIBCXX_3.4.32`, `CXXABI_1.3.15`), so registration failed with ``libm.so.6: version `GLIBC_2.43' not found`` on every WSL2 distro or container older than the build host, including Docker Desktop containers on Ubuntu 24.04 (glibc 2.39). The pin is now `0.4.0-wsl2p2` (release `ep-trt-rtx-wsl2-0.4.0-wsl2p2`), built in an `ubuntu:22.04` container (glibc 2.35, libstdc++ 3.4.30: the floor of the stock wheel). The zip layout and install path are unchanged. The patched build now declares a glibc 2.35 minimum (the stock wheel is `manylinux_2_28`), so on a WSL2 distro older than that `EnsureReadyAsync` returns `NotAvailable` before downloading it.
- **A vendored `0.4.0-wsl2p1` is replaced.** `p1` and `p2` share one install directory, and the installer treated any library already there as installed, so a host that had `p1` would have kept the broken library. The patched build's `build-info.json` records its `epVersion`; an install of another version is now replaced by the pinned one, with the download verified before the old directory is touched. This applies to `EnsureReadyAsync(AllowDownloads: true)`, `linml setup --trt-rtx-wsl2` and `scripts/fetch-ep-plugins.sh trt-rtx-wsl2`; `linml catalog install trt-rtx` installs only the stock wheel, as before. The replacement is verified before the old directory is touched, and a failed move puts the old build back. With downloads disallowed, or if the download fails, the earlier build stays in use, and if it fails to load the error names the vendored and pinned versions.

### Added
- **Build gate.** `scripts/check-elf-floor.sh` fails if a plugin library needs a newer `GLIBC_` than 2.35, `GLIBCXX_` than 3.4.30 or `CXXABI_` than 1.3.13, and prints the offending symbols. `scripts/build-trt-rtx-ep.sh` runs it on its output, so a build on a new host cannot be packaged. `scripts/build-trt-rtx-ep-docker.sh` runs the build in `ubuntu:22.04` (`scripts/docker/trt-rtx-ep-build.Dockerfile`), with the TensorRT-RTX SDK and CUDA toolkit mounted from the host.
- **Published-pin check.** With `LINML_CATALOG_NETWORK_TESTS=1`, a test downloads the pinned archive, verifies its sha256 and `epVersion`, and applies the same gate.
- `PluginPackageSpec.VersionManifestFile`, an internal opt-in for packages that record their build version. `IExecutionProvider`, `EnsureOptions` and `ProviderEnsureResult` are unchanged.

## [0.5.0] — 2026-10-12

### Added
- **TensorRT-RTX on Linux aarch64 (SBSA).** NVIDIA's TensorRT-RTX 1.6 supports Ubuntu 24.04 SBSA. `PluginPackageSpec.TensorrtRtx` is now the wheel for the host's architecture: `x86_64` as before, and `manylinux_2_35_aarch64` (EP 0.4.0, same bundled TensorRT-RTX 1.6.1 runtime) on aarch64 hosts. The plugin gate follows the spec's architecture, and the WSL2 variant stays x86_64 only. `scripts/fetch-ep-plugins.sh` has a `fetch_trt_rtx_arm64` function, chosen for aarch64 hosts. The aarch64 wheel installs to `ep-plugins/trt-rtx-arm64/`, beside the x86_64 one, so an x86_64 install is never mistaken for it. Both the catalog and the fetch script refuse a glibc older than 2.35 before downloading it. Not yet verified on aarch64 hardware.

### Fixed
- **Provider labels.** With no `--provider`, `run`, `serve` and `--profile` reported `requested: cpu` while running the best available provider. They now report `requested: auto`, and `requestedProvider` is `null` in JSON. Auto selection never counts as a fallback.
- **Explicit requests fall back to CPU.** A failed `--provider X` now falls back to CPU, not to another accelerator such as TensorRT.
- **`providers list` matches `doctor`.** Catalog plugins are registered before discovery, so trt-rtx and OpenVINO appear. A plugin registered with no EP devices is marked as such. `providers list` also gained `--root`.
- **`catalog list` status.** Plugin status is read after the resolvable plugins are registered, so a working trt-rtx reads `Ready`. `catalog install` also prints the active library when it differs from the installed one.
- **`doctor --model`.** The smoke Load phase counts plugin EP devices, as the plain readiness probe does, and each provider gets its catalog libraries. trt-rtx no longer reads NOT READY in smoke mode.
- **Readiness messages.** A plugin that is loaded but has no devices now says so. It no longer suggests a missing library.
- **`serve --port` on `localhost`.** Requests to `http://localhost:<port>` reached a .NET default 404 page. The loopback host is now also registered as a prefix.
- **Usage errors.** Missing or unknown options print one message and exit `2`, as documented, instead of dumping the full help.
- **`--json` help.** It is listed once per command. The global option works before or after the subcommand.
- **Version.** `--version` reports the latest release line (`VersionPrefix` was stuck at 0.2.0).

## [0.4.2] — 2026-10-12

### Changed
- **`EnsureReadyAsync` now installs and registers the patched TensorRT-RTX build on WSL2.** On WSL2 with GPU passthrough (`/dev/dxg` present), `ExecutionProviderCatalog` vendors the stock wheel (for its runtime libraries) and then the patched build when `AllowDownloads` is true, and registers only the patched build. The stock plugin is never registered on that path, so ORT's one-registration-per-EP-name rule no longer leaves a zero-device registration behind. A clean WSL2 host reaches `Downloaded` with the GPU exposed, with no `linml setup --trt-rtx-wsl2`, `LD_LIBRARY_PATH`, `LINML_EP_LIBRARIES` or restart. With downloads disallowed and a build missing it returns `DownloadDisallowed`; a zero-device result reports a WSL2-specific `NoDevices` and does not fall back to stock.
- **Default on, opt-out.** Set `LINML_TRT_RTX_WSL2=0` to keep the stock wheel on WSL2. Existing script/CLI installs are reused unchanged. WSL1 (no `/dev/dxg`) and non-WSL hosts behave as before.

### Added
- `TRT_RTX_CUSTOM_EP` is honoured by the catalog (previously CLI only) and outranks the WSL2 build.

## [0.4.1] — 2026-10-12

### Added
- **`linml setup` vendors GPU runtime libraries** — cuDNN 9 and TensorRT 10 (pinned pip wheels, same pins as `scripts/setup-gpu-deps.sh`) are installed into `~/.local/linml-deps` when an NVIDIA GPU is detected, including under WSL2.
- **Vendored GPU libraries load without `LD_LIBRARY_PATH`** — the CLI, `ExecutionProviderCatalog`, session creation and the readiness probes preload them (SONAME-matched; public `GpuDependencyLoader.PreloadVendored()` for apps that open ORT sessions themselves), so `TensorrtExecutionProvider` no longer fails with `libnvinfer.so.10: cannot open shared object file` unless the env script was sourced. A library the loader already resolves by name is left alone.
- **`linml setup --trt-rtx-wsl2`** — opt in to the patched TensorRT-RTX EP for WSL2 from the CLI (SHA-256 verified; installs the stock wheel's runtime libraries first). Detected WSL2 + RTX hosts see it under "Opt-in available"; it is never installed without the flag. On WSL2 the patched build is now preferred over the stock wheel by library resolution and `doctor`, without needing `env-gpu-deps.sh`.
- **`linml doctor`** reports a "GPU runtime libraries" section on NVIDIA hosts.
- **`linml doctor` hint** when vendored GPU libraries or plugin EPs exist on disk but a provider still fails with a native library-load error: it suggests upgrading `linml` or sourcing `env-gpu-deps.sh`, and exposes a `hints` array in `--json` output.
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
