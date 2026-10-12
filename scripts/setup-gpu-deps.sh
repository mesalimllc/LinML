#!/usr/bin/env bash
# LinML GPU dependency setup
# Installs the native GPU libraries the EPs need at runtime, into
# ~/.local/linml-deps (no sudo, no system-wide install):
#   - cuDNN 9.x + cuBLAS (CUDA 13) — required by the CUDA EP Conv kernels
#   - TensorRT 10.x libs (CUDA 13) — required by TensorrtExecutionProvider
#
# Afterwards: source scripts/env-gpu-deps.sh
#
# Versions are pinned for reproducibility.

set -euo pipefail

DEPS="${HOME}/.local/linml-deps"
CUDNN_PKG="nvidia-cudnn-cu13==9.27.0.42"
TRT_LIBS_PKG="tensorrt-cu13-libs==10.16.1.11"
TRT_LEAN_PKG="tensorrt-lean-cu13-libs==10.16.1.11"
TRT_LIBS_INDEX="https://pypi.nvidia.com/"

echo "=== LinML GPU dependency setup ==="
echo "Target dir: $DEPS"

mkdir -p "$DEPS"

if [ ! -f "$DEPS/cudnn/nvidia/cudnn/lib/libcudnn.so.9" ]; then
    echo "Installing cuDNN 9.27 (CUDA 13) via pip wheel..."
    pip3 install --target "$DEPS/cudnn" "$CUDNN_PKG"
else
    echo "cuDNN already present."
fi

if [ ! -f "$DEPS/tensorrt-libs/tensorrt_libs/libnvinfer.so.10" ]; then
    echo "Installing TensorRT 10.16 libs (CUDA 13) from pypi.nvidia.com..."
    pip3 install --target "$DEPS/tensorrt-libs" --index-url "$TRT_LIBS_INDEX" "$TRT_LIBS_PKG"
else
    echo "TensorRT libs already present."
fi

if [ ! -f "$DEPS/tensorrt-lean/tensorrt_lean_libs/libnvinfer_lean.so.10" ]; then
    echo "Installing TensorRT lean libs (CUDA 13)..."
    pip3 install --target "$DEPS/tensorrt-lean" "$TRT_LEAN_PKG"
else
    echo "TensorRT lean libs already present."
fi

echo ""
echo "=== Done. Next steps ==="
echo "  source scripts/env-gpu-deps.sh"
echo "  bash scripts/fetch-ep-plugins.sh   # TensorRT-RTX, OpenVINO, MIGraphX plugin EPs (or name one)"
