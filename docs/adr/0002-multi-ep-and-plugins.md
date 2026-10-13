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

### 7. WSL2 TRT-RTX fallback is an out-of-tree build (default on since 0.4.2, opt-out)
The unmodified TRT-RTX EP enumerates zero devices under WSL2 (virtual PCI
vendor `0x1414`, bus-id rejected by `cudaDeviceGetByPCIBusId` rc=101; CUDA
sees the real device as `0000:0B:00.0`). Root cause in
`evidence/trt-rtx-wsl2-block.md`; upstream issue
[#13](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/issues/13), fix proposed
in [PR #12](https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/pull/12).

We ship a patched build as a **GitHub release**
(`ep-trt-rtx-wsl2-0.4.0-wsl2p1`), mirroring the MIGraphX EP (`ep-migraphx-*`):

- `scripts/build-trt-rtx-ep.sh` pins the PR head commit, builds, and packages a
  byte-stable `linml-trt-rtx-ep-<ver>-linux-x64.zip` (EP `.so`, Apache-2.0
  license, change notice, `build-info.json`).
- `scripts/fetch-ep-plugins.sh trt-rtx-wsl2` vendors it into
  `ep-plugins/trt-rtx-wsl2/` after fetching the stock wheel, which supplies the
  TensorRT-RTX runtime libraries.
- `env-gpu-deps.sh` resolves the TRT-RTX library as
  `TRT_RTX_CUSTOM_EP` → vendored WSL2 build → stock wheel; installing the
  release selects it. Since 0.4.2 the catalog also selects and installs it by default
  on WSL2 with `/dev/dxg` (opt out with `LINML_TRT_RTX_WSL2=0`), as a variant of
  `TensorrtRtx` (same EP name) so the stock plugin is never registered there; the
  removal path is to bump the stock pin once NVIDIA ships the fix and delete the variant.
- `ProviderArgs.ResolveEpLibraries` honours `TRT_RTX_CUSTOM_EP` so the CLI
  picks the patched `.so` for `--provider trt-rtx`.

The catalog is deliberately unchanged: `KnownPlugins.TensorrtRtx` still pins
the stock wheel. The patched build is a variant of the *same* `ProviderId`, and
a second spec would collide with the one-spec-per-provider lookups
(`FirstOrDefault(p => p.Id == ...)`) used across the CLI and catalog.

The archive carries only the Apache-2.0 EP `.so` (it dynamically links
`libtensorrt_rtx.so.1` / CUDA 13, not the SDK). It is **not** built in CI: the
TensorRT-RTX SDK is license-gated (no public tarball; the PyPI packages are
wrappers), unlike AMD's public ROCm image for MIGraphX, so the release is built
where the SDK is present and published manually. Verified: the patched build is
*selected* in WSL2 (no fallback) and MNIST returns the exact output
`[11, 22, 13, 24, 15, 26]`.

For local development, `scripts/build-trt-rtx-custom.sh` builds the same EP
directly into `~/.local/linml-deps/ep-plugins/trt-rtx-custom/`, also selected
with `TRT_RTX_CUSTOM_EP`. It has two tiers, because upstream PR #12 has two
commits:

| Tier | Source | Contains | When to use |
|------|--------|----------|-------------|
| `local` (default) | upstream `main` + local patch | first fix only — device enumeration (`bd8604b`) | PR #12 not merged; single display adapter |
| `upstream` | PR head `wsl2-cuda-fallback` | both fixes, incl. per-GPU registration (`6777daf`) | WSL2 with multiple display adapters; want the complete fix |

It auto-detects the tier (applying the local patch only when the source lacks
`src/tensorrt_rtx_device_selection.h`); build the complete tier with:

```bash
EP_REPO=https://github.com/tonythethompson/TensorRT-RTX-EP-ABI.git \
EP_BRANCH=wsl2-cuda-fallback \
OUT_DIR=$HOME/.local/linml-deps/ep-plugins/trt-rtx-custom-pr \
  bash scripts/build-trt-rtx-custom.sh
```

When PR #12 merges (or NVIDIA ships a fixed wheel) the patched build is
unnecessary; the vendored wheel becomes the only path.

### 8. TRT engine cache is an operator choice, not a default
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
  mismatch, measured rc codes). The patched PR-head build (both changes) is
  *selected* in WSL2 with no fallback and MNIST exact output
  `[11, 22, 13, 24, 15, 26]`; the enumeration-only patch alone still fell back.
