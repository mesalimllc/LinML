#!/usr/bin/env bash
# LinML EP plugin fetcher
# Vendors the TensorRT-RTX plugin EP (NvTensorRTRTXExecutionProvider) from the
# NVIDIA-published PyPI wheel into ~/.local/linml-deps/ep-plugins/trt-rtx.
# The wheel bundles the EP plugin .so AND the TensorRT-RTX 1.6.1 runtime
# libraries, so no separate TensorRT-RTX SDK install is needed.
#
# The wheel is a Python artifact, but only its native binaries are used; the
# deployed product has no Python dependency.
#
# Pinned for reproducibility (sha256 verified).

set -euo pipefail

DEPS="${HOME}/.local/linml-deps"
OUT="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx"

WHEEL_URL="https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"
WHEEL_SHA256="68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19"
WHEEL_NAME="onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"

echo "=== LinML EP plugin fetch (TensorRT-RTX) ==="

if [ -f "$OUT/libonnxruntime_providers_nv_tensorrt_rtx.so" ]; then
    echo "Already vendored: $OUT"
    exit 0
fi

mkdir -p "$OUT"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Downloading $WHEEL_NAME ..."
curl -fsSL -o "$TMP/ep.whl" "$WHEEL_URL"

echo "Verifying sha256..."
echo "$WHEEL_SHA256  $TMP/ep.whl" | sha256sum -c

echo "Extracting native libraries..."
python3 - "$TMP/ep.whl" "$OUT" << 'PYEOF'
import sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "onnxruntime_ep_nv_tensorrt_rtx/"
keep = [n for n in z.namelist()
        if n.startswith(prefix)
        and (n.endswith(".so") or ".so." in n or n.endswith(".h") or n.endswith("README.txt"))]
for n in keep:
    z.extract(n, out)
print(f"extracted {len(keep)} files to {out}")
PYEOF

# The EP .so links against SONAMEs (libtensorrt_rtx.so.1 etc.); the wheel ships
# versioned files only. Create the SONAME symlinks next to the libraries.
cd "$OUT"
for lib in libtensorrt_rtx libtensorrt_onnxparser_rtx; do
    if [ -f "${lib}.so.1.6.1" ] && [ ! -e "${lib}.so.1" ]; then
        ln -s "${lib}.so.1.6.1" "${lib}.so.1"
    fi
done
if [ -f "libcudart.so.13.4.36" ] && [ ! -e "libcudart.so.13" ]; then
    ln -s "libcudart.so.13.4.36" "libcudart.so.13"
fi

echo ""
echo "=== Vendored ==="
ls -la "$OUT"
echo ""
echo "Next: source scripts/env-gpu-deps.sh"
echo "  linml providers list            # after registration the EP appears via GetEpDevices"
echo "  linml run --provider trt-rtx --model ... --inputs ..."
echo ""
echo "Note: in WSL2 the EP registers but exposes zero devices (WSL virtual PCI"
echo "bus id is rejected by cudaDeviceGetByPCIBusId). Works on native Windows /"
echo "bare-metal Linux. See docs/compatibility.md."
