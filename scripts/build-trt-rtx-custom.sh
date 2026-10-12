#!/usr/bin/env bash
# Build the TensorRT-RTX EP plugin from source with the WSL2 fallback patch.
#
# Produces a patched libonnxruntime_providers_nv_tensorrt_rtx.so that enumerates
# GPUs under WSL2's virtual PCI topology (vendor 0x1414 + bus-id mismatch).
# The unmodified upstream build finds zero devices in WSL2; the patched build
# works in WSL2 and behaves identically on native Linux.
#
# Output: ~/.local/linml-deps/ep-plugins/trt-rtx-custom/
#   libonnxruntime_providers_nv_tensorrt_rtx.so
#   PATCHED-BUILD.md
#
# Usage: bash scripts/build-trt-rtx-custom.sh
#   (env overrides: TRT_RTX_SDK, CUDA_ROOT, ORT_SDK, BUILD_DIR, OUT_DIR)

set -euo pipefail

DEPS="${HOME}/.local/linml-deps"
OUT_DIR="${OUT_DIR:-$DEPS/ep-plugins/trt-rtx-custom}"
BUILD_DIR="${BUILD_DIR:-/tmp/opencode/trt-rtx-custom-build}"
EP_REPO="${EP_REPO:-https://github.com/NVIDIA/TensorRT-RTX-EP-ABI.git}"
EP_BRANCH="${EP_BRANCH:-main}"

# --- Locate dependencies -----------------------------------------------------

# TensorRT-RTX SDK: env override, then the copy vendored by fetch-ep-plugins.sh,
# then the Windows-side download (WSL can access /mnt/c)
TRT_RTX_SDK="${TRT_RTX_SDK:-}"
if [ -z "$TRT_RTX_SDK" ]; then
    for cand in "$DEPS/ep-plugins/trt-rtx-sdk" "$DEPS/trt-rtx-sdk" \
                "$HOME/tensorrt-rtx-sdk-1.6.1.120" \
                "/mnt/c/Users/tonyt/Downloads/TensorRT-RTX-1.6.1.120-Linux-x86_64-cuda-13.4-Release-external.tar/TensorRT-RTX-1.6.1.120-Linux-x86_64-cuda-13.4-Release-external/TensorRT-RTX-1.6.1.120"; do
        if [ -d "$cand/lib" ] && [ -d "$cand/include" ]; then
            TRT_RTX_SDK="$cand"
            break
        fi
    done
fi
if [ -z "$TRT_RTX_SDK" ]; then
    echo "ERROR: TensorRT-RTX SDK not found." >&2
    echo "  Run: bash scripts/fetch-ep-plugins.sh trt-rtx-sdk" >&2
    echo "  Or set TRT_RTX_SDK=/path/to/TensorRT-RTX-1.6.1.120-Linux-x86_64-cuda-13.4" >&2
    exit 2
fi

# CUDA toolkit
CUDA_ROOT="${CUDA_ROOT:-}"
if [ -z "$CUDA_ROOT" ]; then
    for cand in /usr/local/cuda-13 /usr/local/cuda-13.3 /usr/local/cuda-13.4 /usr/local/cuda; do
        if [ -x "$cand/bin/nvcc" ]; then
            CUDA_ROOT="$cand"
            break
        fi
    done
fi
if [ -z "$CUDA_ROOT" ]; then
    echo "ERROR: CUDA toolkit not found (need CUDA 12.9+ for the TRT-RTX 1.6.1 SDK)." >&2
    echo "  Set CUDA_ROOT=/usr/local/cuda-13" >&2
    exit 2
fi

# ONNX Runtime SDK (headers + lib for linking the EP plugin)
ORT_SDK="${ORT_SDK:-}"
if [ -z "$ORT_SDK" ]; then
    for cand in "$DEPS/ort-sdk" /tmp/opencode/trtbuild/onnxruntime-linux-x64-1.31.0; do
        if [ -d "$cand/include" ] && [ -d "$cand/lib" ]; then
            ORT_SDK="$cand"
            break
        fi
    done
fi
if [ -z "$ORT_SDK" ]; then
    echo "ERROR: ONNX Runtime SDK not found (need include/ + lib/ from onnxruntime-linux-x64-1.31.0)." >&2
    echo "  Download from https://github.com/microsoft/onnxruntime/releases/tag/v1.31.0" >&2
    echo "  Or set ORT_SDK=/path/to/onnxruntime-linux-x64-1.31.0" >&2
    exit 2
fi

echo "=== TensorRT-RTX EP custom build (WSL2 fallback) ==="
echo "TRT_RTX_SDK: $TRT_RTX_SDK"
echo "CUDA_ROOT:   $CUDA_ROOT"
echo "ORT_SDK:     $ORT_SDK"
echo "OUT_DIR:     $OUT_DIR"
echo ""

# --- Clone + patch the EP source ----------------------------------------------

rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR"
git clone --quiet --depth 1 --branch "$EP_BRANCH" "$EP_REPO" "$BUILD_DIR/ep-abi"
echo "Cloned $EP_REPO ($EP_BRANCH)"

PATCH_FILE="$BUILD_DIR/ep-abi/src/tensorrt_rtx_provider_factory.cc"
if [ ! -f "$PATCH_FILE" ]; then
    echo "ERROR: $PATCH_FILE not found — upstream layout changed?" >&2
    exit 1
fi

python3 - "$PATCH_FILE" << 'PYEOF'
import sys
path = sys.argv[1]
src = open(path).read()

# Patch 1: PCI bus-id fallback in IsOrtHardwareDeviceSupported (Linux path).
old1 = """    int cuda_device_idx = 0;
    if (cudaDeviceGetByPCIBusId(&cuda_device_idx, pci_bus_id) != cudaSuccess)
    {
        return false;
    }"""
new1 = """    int cuda_device_idx = 0;
    if (cudaDeviceGetByPCIBusId(&cuda_device_idx, pci_bus_id) != cudaSuccess)
    {
        // WSL2 fallback: ORT's PCI discovery reports the WSL virtual bus id
        // (e.g. 176e:00:00.0), which the CUDA driver rejects (rc=101) even
        // though the card is present. Fall back to enumerating CUDA devices
        // and accepting the first one with CC >= 8 (Ampere+), matching the
        // gate used on the successful path below.
        int device_count = 0;
        if (cudaGetDeviceCount(&device_count) != cudaSuccess || device_count == 0)
        {
            return false;
        }
        cudaDeviceProp prop;
        if (cudaGetDeviceProperties(&prop, 0) != cudaSuccess)
        {
            return false;
        }
        if (major != nullptr)
        {
            *major = prop.major;
        }
        if (minor != nullptr)
        {
            *minor = prop.minor;
        }
        return prop.major >= 8;
    }"""
assert src.count(old1) == 1, f"patch 1 anchor not found ({src.count(old1)})"
src = src.replace(old1, new1, 1)

# Patch 2: accept the WSL virtual vendor id in GetSupportedDevicesImpl.
old2 = """            if (factory->ort_api.HardwareDevice_Type(device) == OrtHardwareDeviceType::OrtHardwareDeviceType_GPU &&
                factory->ort_api.HardwareDevice_VendorId(device) == factory->vendor_id_ &&"""
new2 = """            if (factory->ort_api.HardwareDevice_Type(device) == OrtHardwareDeviceType::OrtHardwareDeviceType_GPU &&
                (factory->ort_api.HardwareDevice_VendorId(device) == factory->vendor_id_ ||
                 // WSL2 fallback: ORT's PCI discovery reports the GPU with the
                 // virtual vendor 0x1414 (Microsoft) and a bus id the CUDA
                 // driver rejects. Accept such devices via the CUDA
                 // enumeration fallback in IsOrtHardwareDeviceSupported.
                 factory->ort_api.HardwareDevice_VendorId(device) == 0x1414) &&"""
assert src.count(old2) == 1, f"patch 2 anchor not found ({src.count(old2)})"
src = src.replace(old2, new2, 1)

open(path, 'w').write(src)
print("patched tensorrt_rtx_provider_factory.cc (WSL2 fallback)")
PYEOF

# --- Build --------------------------------------------------------------------

cmake -B "$BUILD_DIR/build" \
    -DONNXRUNTIME_ROOT="$ORT_SDK" \
    -DTRT_RTX_ROOT="$TRT_RTX_SDK" \
    -DCUDAToolkit_ROOT="$CUDA_ROOT" \
    -S "$BUILD_DIR/ep-abi" > "$BUILD_DIR/cmake-config.log" 2>&1 || {
    echo "ERROR: cmake configure failed — see $BUILD_DIR/cmake-config.log" >&2
    tail -20 "$BUILD_DIR/cmake-config.log" >&2
    exit 1
}

cmake --build "$BUILD_DIR/build" -j "$(nproc)" > "$BUILD_DIR/cmake-build.log" 2>&1 || {
    echo "ERROR: build failed — see $BUILD_DIR/cmake-build.log" >&2
    tail -20 "$BUILD_DIR/cmake-build.log" >&2
    exit 1
}

PLUGIN="$BUILD_DIR/build/libonnxruntime_providers_nv_tensorrt_rtx.so"
if [ ! -f "$PLUGIN" ]; then
    echo "ERROR: plugin .so not found at $PLUGIN" >&2
    exit 1
fi

# --- Install + provenance ------------------------------------------------------

mkdir -p "$OUT_DIR"
cp "$PLUGIN" "$OUT_DIR/libonnxruntime_providers_nv_tensorrt_rtx.so"

GIT_COMMIT=$(git -C "$BUILD_DIR/ep-abi" rev-parse HEAD 2>/dev/null || echo "unknown")
BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ)

cat > "$OUT_DIR/PATCHED-BUILD.md" << EOF
# TensorRT-RTX EP — patched build (WSL2 fallback)

Built from NVIDIA/TensorRT-RTX-EP-ABI @ ${GIT_COMMIT} (${EP_BRANCH}) with the
WSL2 device-enumeration fallback (upstream PR
https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/pull/12).

## Why
Unmodified, the EP registers but exposes zero devices under WSL2: ORT's PCI
discovery reports the GPU as vendor 0x1414 (Microsoft virtual PCI) with a bus
id the CUDA driver rejects (cudaDeviceGetByPCIBusId rc=101). The patch adds a
CUDA-enumeration fallback (first device with CC >= 8) so the EP works under
WSL2 and behaves identically on native Linux.

## Reproduce
\`\`\`bash
bash scripts/build-trt-rtx-custom.sh
# or with overrides:
TRT_RTX_SDK=/path/to/sdk CUDA_ROOT=/usr/local/cuda-13 \\
  ORT_SDK=/path/to/onnxruntime-linux-x64-1.31.0 \\
  bash scripts/build-trt-rtx-custom.sh
\`\`\`

## Use
\`\`\`bash
export LINML_EP_LIBRARIES="NvTensorRTRTXExecutionProvider=$OUT_DIR/libonnxruntime_providers_nv_tensorrt_rtx.so"
linml run --provider trt-rtx --model model.onnx --inputs inputs.json
\`\`\`

## Verification (2026-10-11, WSL2 Ubuntu 26.04, RTX 5070 sm_120, driver 617.42)
- Unmodified source build: zero EP devices (confirms binary isn't the issue)
- Patched build: EP enumerates, session runs, MNIST output exact
  \`[11, 22, 13, 24, 15, 26]\`
EOF

echo ""
echo "=== Built ==="
ls -la "$OUT_DIR"
echo ""
echo "Next: source scripts/env-gpu-deps.sh"
echo "  linml providers list   # patched EP appears after registration"
echo "  linml run --provider trt-rtx --model ... --inputs ..."
