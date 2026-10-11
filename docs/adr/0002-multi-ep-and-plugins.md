# ADR 0002: Multi-EP and Plugin Strategy

## Status
Accepted (2026-10-11). Updated 2026-10-11 with the WSL2 TRT-RTX fallback
build (decision 7) — the plugin strategy now has an escape hatch for
platforms where a plugin EP cannot enumerate hardware.

## Context

LinML grew from CPU+CUDA-only (ADR 0001) to supporting six execution
providers across three vendors. That growth surfaced four recurring problems
that each needed a decision:

1. **Package shadowing.** Referencing both `Microsoft.ML.OnnxRuntime`
   (CPU-only) and `Microsoft.ML.OnnxRuntime.Gpu` puts the CPU build's
   `libonnxruntime.so` (29 MB) into the app, shadowing the GPU build's
   (33 MB); ORT then registers only `CPUExecutionProvider` and CUDA
   silently disappears.
2. **Plugin EPs are not name-addressable.** `SessionOptions.AppendExecutionProvider(name, ...)`
   only knows EPs compiled into the loaded ORT build. Plugin EPs (TRT-RTX,
   OpenVINO 1.6.1) must be registered with
   `OrtEnv.RegisterExecutionProviderLibrary` and appended via the V2
   device-based API (`GetEpDevices` → `AppendExecutionProvider(env, devices, opts)`).
3. **Registration name becomes the EP name.** ORT reports plugin EP devices
   under the registration name verbatim. Registering TRT-RTX as
   `nvtensorrtrtx` produced `EpName: nvtensorrtrtx`, which matched nothing.
   Canonical EP names (`NvTensorRTRTXExecutionProvider`) are required.
4. **Two vendoring paths drifted.** `scripts/fetch-ep-plugins.sh` (bash,
   for humans/CI) and `LinML.Catalog`'s `PluginInstaller` (C#, for apps)
   vendor the same wheels into the same layout — with nothing checking they
   agree.

## Decisions

### 1. Gpu-only package
Reference only `Microsoft.ML.OnnxRuntime.Managed` +
`Microsoft.ML.OnnxRuntime.Gpu`. **Never** also reference the CPU-only
`Microsoft.ML.OnnxRuntime` package: its native library shadows the GPU
build's and hides the CUDA EP. (Found Gate D: providers list dropped
`CUDAExecutionProvider` until the CPU package was removed.)

### 2. One process-wide plugin registry
`LinML.Catalog.PluginLibraryRegistry` is the single record of registered
plugin libraries. Semantics:
- Registering the same name+path twice is a no-op (idempotent).
- Same name with a different path throws (operator error, not silent skip).
- A missing library file throws `FileNotFoundException` on **every** call,
  even for an already-registered name — a stale path must not masquerade as
  success. (This caught a real test-pollution bug where `LINML_EP_LIBRARIES`
  pre-registered a name and a later bad path was silently ignored.)
- EP device queries go through the same registry.

The CLI's `PluginEpRegistry` is a thin forwarder kept for API stability;
the catalog's `OrtRegistrar`/`IExecutionProvider.EnsureReadyAsync` and the
CLI's `--ep-library`/`LINML_EP_LIBRARIES` path share it.

### 3. Plugin EPs append via the V2 device API
`EpAppender` (Runtime.Onnx) routes by provider kind:
- CPU — no append (default)
- CUDA, TensorRT — typed append (`OrtCUDAProviderOptions`,
  `OrtTensorRTProviderOptions`)
- plugin EPs (TRT-RTX, OpenVINO) — register, enumerate devices, append V2
- everything else (MIGraphX, QNN, unknown-but-listed) — generic name-based
  append with verbatim options

### 4. Canonical registration names
Plugin libraries register under the canonical EP name
(`NvTensorRTRTXExecutionProvider`, `OpenVINOExecutionProvider`), matching
NVIDIA/Intel docs. `scripts/env-gpu-deps.sh` defaults to these.

### 5. Provider-scoped EP options
`--ep-option key=value` is global; `--ep-option provider:key=value` is
scoped (alias or full EP name, case-insensitive). Scoped options are
filtered per attempt so a `trt:*` option never leaks into a CUDA/CPU
fallback attempt. (Found: a bad global TRT option poisoned the whole
fallback chain down to CPU.)

### 6. Vendoring pins are tested, not documented
`ScriptPinAgreementTests` (Catalog.Tests) parses
`scripts/fetch-ep-plugins.sh` and asserts every `KnownPlugins` spec pins
the same wheel URL and sha256. A pin bump on either side fails CI.

### 7. WSL2 TRT-RTX fallback (patched build)
The unmodified TRT-RTX EP enumerates zero devices under WSL2 (virtual PCI
vendor `0x1414`, bus-id rejected by `cudaDeviceGetByPCIBusId` rc=101).
Root cause in `evidence/trt-rtx-wsl2-block.md`; upstream issue
[#13](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/issues/13), fix proposed
in [PR #12](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/pull/12).

Workaround: `scripts/build-trt-rtx-custom.sh` clones NVIDIA/TensorRT-RTX-EP-ABI,
applies a two-part fallback patch, and builds a patched
`libonnxruntime_providers_nv_tensorrt_rtx.so` into
`~/.local/linml-deps/ep-plugins/trt-rtx-custom/`. The CLI picks it up via
`TRT_RTX_CUSTOM_EP` (one env var; `env-gpu-deps.sh` also honors it).
Verified: patched build enumerates the RTX 5070 in WSL2 and runs MNIST with
exact output `[11, 22, 13, 24, 15, 26]`.

The patch is intentionally NOT in the main publish — it is a platform
workaround maintained as a separate artifact. When upstream merges PR #12,
the custom build becomes unnecessary and the vendored wheel is the only path.

### 7. TRT engine cache is an operator choice, not a default
TRT rebuilds engines every process (~44 s for MNIST) unless
`trt_engine_cache_enable`/`trt_engine_cache_path` are set. The CLI does not
set a default cache path (writable-dir policy is the caller's); the
limitation and the fix are documented in `docs/compatibility.md`.

## Consequences

### Positive
- One registry, one set of semantics, no dual bookkeeping.
- Script and programmatic vendoring cannot silently drift.
- Fallback chains are immune to cross-provider option leaks.
- Plugin EPs work identically from CLI flags, env vars, and the catalog API.

### Negative
- `LinML.Runtime.Onnx` now depends on `LinML.Catalog` (registry lives there).
- Canonical-name rule means legacy lowercase registrations won't match;
  `env-gpu-deps.sh` guards against double registration of both spellings.

## Alternatives Considered

| Alternative | Why Rejected |
|-------------|--------------|
| Keep separate CLI and catalog registries | Divergent state; double registration breaks ORT appends ("devices come from different EP factories") |
| Name-based append for plugin EPs | Rejected by ORT: "not supported in this build" |
| Auto-download plugins in the CLI | CLI is offline-friendly; downloads are catalog-only (`EnsureOptions.AllowDownloads`) |
| Default TRT engine cache path in CLI | Writable-dir policy is the caller's; surprise writes to CWD are worse than a documented flag |

## Evidence

- Gate D shadowing: providers list showed only CPU until the CPU-only
  package was removed from `LinML.Runtime.Onnx.csproj`.
- Canonical names: RTX 4090 (Daytona, native Linux) — lowercase
  registration → `providers list` shows the EP but `run --provider trt-rtx
  --require-provider` exits 3; canonical name → 3/3 TrtRtx tests pass,
  MNIST argmax 8, no fallback.
- Option scoping: bad `trt:` option → TRT fails, CUDA succeeds (no leak).
- WSL2 TRT-RTX block: `evidence/trt-rtx-wsl2-block.md` (PCI vendor/bus-id
  mismatch, measured rc codes).
