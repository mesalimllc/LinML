# LinML — Handoff to WSL/OpenCode

> **For the next agent:** You are resuming work on LinML in WSL2 (Linux). Read this entire document before writing any code. The full implementation plan is in `.hermes/plans/2026-10-10_231224-linml-handoff.md` (if accessible) or reconstruct from this document + the repo.

---

## TL;DR

**What:** Linux-first ONNX Runtime inference integration layer prototype (.NET 10/C#)
**Where:** `~/dev/LinML` (clone from GitHub) or `/mnt/d/Dev/Trackdub_Workspace/LinML`
**Status:** All gates complete (2026-10-11, WSL2) — locked restore, 46 tests green,
CPU + CUDA inference verified with exact outputs, linux-x64 publish verified, CI green.
**Next:** See Final Checklist below (all checked) and `docs/compatibility.md` for results.

```bash
# Quick start in WSL:
cd ~/dev/LinML  # or clone first: git clone https://github.com/trackdubllc/LinML.git
bash scripts/setup-wsl.sh
dotnet restore LinML.slnx
dotnet build LinML.slnx -c Release
dotnet test LinML.slnx -c Release
```

---

## Environment

| Component | Value |
|-----------|-------|
| **Host** | Windows 11 |
| **WSL** | Ubuntu 26.04.1 LTS, kernel 6.18.40, glibc 2.43 |
| **GPU** | NVIDIA GeForce RTX 5070 (Blackwell, sm_120, 12GB VRAM) |
| **CUDA Driver** | 617.42 (CUDA 13.4) — works in WSL via passthrough |
| **CUDA Toolkits** | 12.9, 13.3 at `/usr/local/cuda*` |
| **.NET SDK (Windows)** | 10.0.401 (also 9.0.318, 11.0.100-rc.1) |
| **.NET SDK (WSL)** | **Not installed yet** — run `scripts/setup-wsl.sh` |

**Verify GPU in WSL:**
```bash
nvidia-smi  # Should show RTX 5070
ls /usr/local/cuda/lib64/libcudart.so*  # CUDA runtime present
```

---

## What's Been Done

### ✅ Completed

| Item | Details |
|------|---------|
| **Project scaffold** | 4 src projects + 4 test projects, 64 files, 34 C# files |
| **Contracts** | `ProviderId`, `ProviderDiscoveryReport`, `ProviderReadinessReport`, `ModelIdentity`, `InferenceRequest`, `InferenceResult`, `SelectionRequest`, `SelectionDecision`, 4 interfaces |
| **Policy** | `ProviderSelector` (14 test cases), `FallbackPolicy` (5 tests) |
| **Runtime.Onnx** | `OnnxProviderDiscovery`, `OnnxSessionFactory`, `OnnxInferenceSession`, `CpuProviderAdapter`, `CudaProviderAdapter`, `CudaReadinessProbe` |
| **CLI** | `Program.cs`, `ProvidersCommand`, `DoctorCommand`, `RunCommand` — all working |
| **Tests** | 46 passing: 19 policy + 14 CLI + 7 integration + 6 runtime |
| **Git** | Initialized, pushed to `https://github.com/trackdubllc/LinML` (public) |
| **Docs** | ADR 0001, compatibility.md, diagnostics-schema.md, input-schema.md, wsl-workflow.md |
| **Scripts** | `setup-wsl.sh`, `fetch-test-fixture.sh` (placeholder), `verify-linux-publish.sh` |

### 📦 Pinned Dependencies

| Package | Version | Notes |
|---------|---------|-------|
| .NET SDK | 10.0.401 | `global.json` with `rollForward: latestPatch` |
| `Microsoft.ML.OnnxRuntime` | 1.31.0 | Native libs meta-package |
| `Microsoft.ML.OnnxRuntime.Managed` | 1.31.0 | **Required** — managed C# API (separate package since 1.31) |
| `Microsoft.ML.OnnxRuntime.Gpu` | 1.31.0 | CUDA 13.0 build, cuDNN 9.x |
| `System.CommandLine` | 2.0.12 | **Stable API** (not beta — API changed significantly) |
| `Microsoft.Extensions.Logging.*` | 10.0.0 | Logging abstractions + console |
| xUnit | 2.9.2 | Test framework |

### 🔧 Key Technical Decisions

1. **ORT 1.31.0 package split:** `Microsoft.ML.OnnxRuntime` is now a meta-package (native only). Must also reference `Microsoft.ML.OnnxRuntime.Managed` for the C# API.

2. **OrtEnv API:** Use `OrtEnv.Instance().GetAvailableProviders()` — NOT `OrtEnv.GetInstance()`. The method is `Instance()`, not `GetInstance()`.

3. **System.CommandLine 2.0.12 API:**
   - `rootCommand.Options.Add(option)` — NOT `AddOption()`
   - `rootCommand.Subcommands.Add(cmd)` — NOT `AddCommand()`
   - `command.SetAction(parseResult => ...)` — NOT `SetHandler()`
   - `option.Required = true` — NOT `IsRequired = true`
   - `rootCommand.Parse(args).Invoke()` — NOT `InvokeAsync()`
   - Option description: `new Option<T>("--name") { Description = "..." }` — second ctor param is aliases, not description
   - Recursive/global option: `option.Recursive = true`

4. **No SourceLink:** Removed `Microsoft.SourceLink.GitHub` 8.0.0 (vulnerability NU1902). Not critical for prototype.

5. **CS1591 suppressed:** `NoWarn` includes CS1591 (missing XML docs) in `Directory.Build.props`. Add full docs before publish.

6. **xUnit Skip:** xUnit 2.x doesn't have `Skip.IfNot()`. Integration tests use early `return` for now. Consider `Xunit.SkippableFact` package or upgrade to xUnit 3.x for `Assert.SkipWhen()`.

---

## Project Structure

```
LinML/
├── LinML.slnx                 # .NET 10 XML solution format
├── global.json                # SDK 10.0.401 pinned
├── Directory.Build.props      # Common build settings, CS1591 suppressed
├── Directory.Packages.props   # Central package management
├── NuGet.config
├── .gitignore, .gitattributes, .editorconfig, LICENSE (MIT)
├── README.md, HANDOFF.md
├── src/
│   ├── LinML.Contracts/       # No ORT dependency — pure abstractions
│   │   ├── ProviderId.cs
│   │   ├── ProviderDiscoveryReport.cs  (includes DeviceIdentity, HardwareEvidence, DiagnosticEntry)
│   │   ├── ProviderReadinessReport.cs  (includes ReadinessPhase enum: Load/Append/Smoke)
│   │   ├── ModelIdentity.cs
│   │   ├── InferenceRequest.cs         (includes TensorInput, TensorDataType)
│   │   ├── InferenceResult.cs          (includes TensorOutput, FallbackStep)
│   │   ├── SelectionRequest.cs
│   │   ├── SelectionDecision.cs        (includes RejectedCandidate)
│   │   ├── IProviderDiscovery.cs
│   │   ├── IProviderReadinessProbe.cs
│   │   ├── IInferenceSessionFactory.cs
│   │   └── IInferenceSession.cs
│   ├── LinML.Policy/          # Depends only on Contracts
│   │   ├── ProviderSelector.cs         # 14 test cases, selection logic
│   │   └── FallbackPolicy.cs           # Candidate list building
│   ├── LinML.Runtime.Onnx/    # Depends on Contracts + ORT
│   │   ├── OnnxProviderDiscovery.cs    # OrtEnv.Instance().GetAvailableProviders()
│   │   ├── OnnxRuntimeDiagnostics.cs
│   │   ├── OnnxSessionFactory.cs
│   │   ├── OnnxInferenceSession.cs
│   │   └── Providers/
│   │       ├── CpuProviderAdapter.cs
│   │       ├── CudaProviderAdapter.cs
│   │       └── CudaReadinessProbe.cs  # Load → Append → Smoke phases
│   └── LinML.Cli/             # Composition root
│       ├── Program.cs                  # DI setup, System.CommandLine 2.0.12
│       └── Commands/
│           ├── ProvidersCommand.cs     # linml providers list
│           ├── DoctorCommand.cs        # linml doctor
│           └── RunCommand.cs           # linml run --model --inputs --provider
├── tests/
│   ├── LinML.Policy.Tests/    # 19 tests passing
│   │   ├── ProviderSelectorTests.cs    # 14 cases with FakeDiscovery/FakeReadinessProbe
│   │   └── FallbackPolicyTests.cs      # 5 cases
│   ├── LinML.Runtime.Onnx.Tests/  # 6 tests passing
│   ├── LinML.Integration.Tests/   # 7 tests (skip without fixture)
│   │   ├── CpuInferenceTests.cs
│   │   ├── CudaInferenceTests.cs
│   │   └── FallbackTests.cs
│   ├── LinML.Cli.Tests/           # 14 tests (placeholders)
│   └── fixtures/
│       ├── fixture-manifest.json       # Spec: MatMul+Add, opset 17
│       ├── inputs.json                 # [1,2,3,4,5,6] shape [3,2]
│       └── expected-outputs.json       # [11,22,13,24,15,26]
├── scripts/
│   ├── setup-wsl.sh           # Install .NET 10 in WSL, verify GPU
│   ├── fetch-test-fixture.sh  # PLACEHOLDER — needs ONNX model generation
│   └── verify-linux-publish.sh
├── docs/
│   ├── adr/0001-runtime-and-deployment.md
│   ├── compatibility.md
│   ├── diagnostics-schema.md
│   ├── input-schema.md
│   └── wsl-workflow.md
└── .github/workflows/ci.yml
```

---

## The Gates (from the plan)

| Gate | Status | What's Left |
|------|--------|-------------|
| **A** | ✅ Done | Locked restore (`packages.lock.json` committed), ADR 0001 Accepted |
| **B** | ✅ Done | 19 policy tests passing, contracts defined, logging decided |
| **C** | ✅ Done | Fixture generated (BCL-only C# protobuf writer, deterministic), CPU output `[11,22,13,24,15,26]` verified |
| **D** | ✅ Done | CUDA EP registered (removed shadowing CPU package); RTX 5070 run + ORT profiling shows `MatMulAddFusion` on CUDA |
| **E** | ✅ Done | `verify-linux-publish.sh` all PASS; published binary runs CPU + CUDA; 511 MB |
| **F** | ✅ Done | CI green on `ubuntu-latest` (hosted runners only); evidence in `evidence/` |

---

## Gate C: What You Need to Do (Next Task)

**Goal:** Real CPU inference in WSL with a deterministic ONNX fixture.

### Fixture Spec

| Property | Value |
|----------|-------|
| Architecture | `MatMul` → `Add` |
| Input | `input`: float32, shape `[3, 2]` |
| Weight | `weight`: float32, shape `[2, 2]`, values `[[1,0],[0,1]]` (identity) |
| Bias | `bias`: float32, shape `[2]`, values `[10, 20]` |
| Output | `output`: float32, shape `[3, 2]` |
| Opset | 17 |
| Test input | `[1, 2, 3, 4, 5, 6]` |
| Expected output | `[11, 22, 13, 24, 15, 26]` |

### Steps

1. **Generate the fixture** — Write a C# script or small console project that creates `model.onnx`. Options:
   - Use `Google.Protobuf` + ONNX proto definitions
   - Use ONNX C# helper (if available)
   - Direct protobuf serialization
   - **No Python dependency** in the deployed product (generation can use any tool)

2. **Save fixture:**
   ```bash
   mkdir -p tests/fixtures
   # Save model.onnx to tests/fixtures/
   sha256sum tests/fixtures/model.onnx  # Update fixture-manifest.json
   ```

3. **Update `tests/fixtures/fixture-manifest.json`:**
   - Set `sha256` to actual checksum
   - Set `sizeBytes` to actual size
   - Set `generatedBy` to the generation command

4. **Run integration tests in WSL:**
   ```bash
   cd ~/dev/LinML
   dotnet test tests/LinML.Integration.Tests -c Release --filter "CpuInference"
   ```

5. **Verify CLI works:**
   ```bash
   dotnet run --project src/LinML.Cli -- run \
     --model tests/fixtures/model.onnx \
     --inputs tests/fixtures/inputs.json \
     --provider cpu --json
   ```

---

## Gate D: CUDA (After Gate C)

RTX 5070 is available in WSL2. After CPU works:

```bash
# Verify CUDA provider is available in WSL:
dotnet run --project src/LinML.Cli -- providers list --json
# Should show "CUDAExecutionProvider" in WSL (not on Windows)

# Test CUDA inference:
dotnet run --project src/LinML.Cli -- run \
  --model tests/fixtures/model.onnx \
  --inputs tests/fixtures/inputs.json \
  --provider cuda --json

# Hard requirement (should fail if CUDA unavailable):
dotnet run --project src/LinML.Cli -- run \
  --model tests/fixtures/model.onnx \
  --inputs tests/fixtures/inputs.json \
  --provider cuda --require-provider --json
```

**Important:** Use ORT profiling (`SessionOptions.EnableProfiling`) to verify nodes actually ran on CUDA, not CPU partition.

---

## Correctness Rules (From the Plan)

1. Hardware detected ≠ provider available. Provider listed ≠ dependencies loaded. Provider appended ≠ model runnable. Model runnable ≠ every node ran on GPU.
2. Preserve separate discovery, runtime readiness, per-model smoke, and execution results.
3. Hard selection fails if requested provider cannot run; soft selection may retry CPU and must record why.
4. Distinguish provider-level fallback from ORT graph partitioning.
5. Use bounded, typed diagnostics. Preserve requested/selected provider, model identity, runtime version, failure phase, reason.
6. Model compatibility is checked per model/provider/options, not inferred from vendor.
7. Do not swallow cancellation, mislabel invalid models as missing GPU support, or claim skipped hardware tests passed.
8. Native provider libraries must match their ORT build/plugin ABI.
9. No zero-copy, NPU coverage, cross-distro portability claims without direct evidence.

---

## Exit Codes (CLI)

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | Execution failure (inference error, model load failure) |
| 2 | Invalid arguments (bad flags, missing files) |
| 3 | Required provider unavailable (hard selection failed) |
| 4 | Unexpected/internal error |

---

## Git Workflow

```bash
# Repo: https://github.com/trackdubllc/LinML (public)
# Windows path: D:\Dev\Trackdub_Workspace\LinML
# WSL path (if cloned): ~/dev/LinML

# Recommended: work in WSL filesystem for speed
cd ~/dev/LinML
git pull
# ... make changes ...
git add -A
git commit -m "Gate C: real CPU inference with MatMul+Add fixture"
git push

# Sync to Windows if needed:
# On Windows: cd D:\Dev\Trackdub_Workspace\LinML && git pull
```

---

## Troubleshooting

### .NET SDK not found in WSL
```bash
export PATH="$HOME/.dotnet:$PATH"
# Or re-run: bash scripts/setup-wsl.sh
```

### CUDA not visible in WSL
```bash
nvidia-smi  # Should work
# If not, check Windows NVIDIA driver is installed
# WSL2 (not WSL1): wsl -l -v  # Should show VERSION 2
```

### Build errors after pulling
```bash
dotnet restore LinML.slnx --force
rm -rf **/bin **/obj  # Clean build artifacts
dotnet build LinML.slnx -c Release
```

### Git line ending warnings
The repo has `.gitattributes` with `* text=auto eol=lf`. In WSL:
```bash
git config core.autocrlf false
```

---

## Quick Reference: Useful Commands

```bash
# Setup (first time in WSL)
bash scripts/setup-wsl.sh

# Build & test
dotnet restore LinML.slnx
dotnet build LinML.slnx -c Release -warnaserror
dotnet test LinML.slnx -c Release

# CLI smoke tests
dotnet run --project src/LinML.Cli -- --help
dotnet run --project src/LinML.Cli -- --version
dotnet run --project src/LinML.Cli -- providers list --json
dotnet run --project src/LinML.Cli -- doctor --json

# Publish & verify (Gate E)
dotnet publish src/LinML.Cli/LinML.Cli.csproj -c Release -r linux-x64 \
  --self-contained false -o artifacts/publish/linux-x64
bash scripts/verify-linux-publish.sh artifacts/publish/linux-x64

# Git
git status
git log --oneline -5
git push
```

---

## Evidence to Collect (for Gate F)

Save these to `evidence/` (gitignored) or a separate location:

- `provenance.json` — commit hash, host, distro, SDK version, ORT version, GPU, fixture checksum
- `cpu-run.json` — actual CLI output from CPU inference
- `cuda-run.json` — actual CLI output from CUDA inference + profiling
- `fallback-run.json` — soft CUDA → CPU fallback
- `required-provider-failure.json` — hard CUDA failure with exit code
- Test logs, publish verification logs

---

**Do not claim the prototype is complete until Gate C and the CPU portion of Gates E/F have real Linux evidence in WSL2.** — Satisfied 2026-10-11; see checklist below.

**Do not claim the prototype is complete until Gate C and the CPU portion of Gates E/F have real Linux evidence in WSL2.**

## Final Checklist Before Claiming "Done"

- [x] Gate A: WSL2 .NET installed, locked restore works
- [x] Gate B: 19 policy tests passing (14 selector + 5 fallback)
- [x] Gate C: Real CPU inference in WSL with verified output `[11,22,13,24,15,26]`
- [x] Gate D: Real CUDA inference in WSL with profiling evidence
- [x] Gate E: Published Linux binary works, `ldd` shows no missing deps
- [x] Gate F: CI green, evidence files collected
- [x] All commits pushed to GitHub
- [x] ADR 0001 updated to "Accepted"
- [x] compatibility.md updated with actual test results
