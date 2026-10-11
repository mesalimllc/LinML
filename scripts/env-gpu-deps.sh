#!/usr/bin/env bash
# LinML GPU dependency environment
# Source this to put cuDNN / TensorRT / plugin EP runtime libraries on the
# loader path. Run scripts/setup-gpu-deps.sh once to populate the deps.
#
# Usage: source scripts/env-gpu-deps.sh

DEPS="${HOME}/.local/linml-deps"

# Ensure the .NET SDK (installed by scripts/setup-wsl.sh) is on PATH.
if [ -d "${HOME}/.dotnet" ]; then
    case ":${PATH:-}:" in
        *":${HOME}/.dotnet:"*) ;;
        *) PATH="${HOME}/.dotnet${PATH:+:$PATH}" ;;
    esac
    export PATH
fi

add_path() {
    if [ -d "$1" ]; then
        case ":${LD_LIBRARY_PATH:-}:" in
            *":$1:"*) ;;        # already present
            *) LD_LIBRARY_PATH="$1${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ;;
        esac
    fi
}

# cuDNN 9.x (required by CUDA EP Conv kernels) + cuBLAS (pip wheels)
add_path "$DEPS/cudnn/nvidia/cudnn/lib"
add_path "$DEPS/cudnn/nvidia/cublas/lib"

# TensorRT 10.x classic (libnvinfer.so.10) — required by TensorrtExecutionProvider
add_path "$DEPS/tensorrt-libs/tensorrt_libs"
add_path "$DEPS/tensorrt-lean/tensorrt_lean_libs"

# TensorRT-RTX plugin EP (vendored wheel) — the EP .so resolves its bundled
# libtensorrt_rtx via its own directory; add it so dlopen finds them.
add_path "$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx"

# OpenVINO runtime (pip wheel), if installed
add_path "$DEPS/openvino/lib"

export LD_LIBRARY_PATH

# Convenience: default plugin EP library registration for TRT-RTX.
TRT_RTX_EP="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx/libonnxruntime_providers_nv_tensorrt_rtx.so"
if [ -f "$TRT_RTX_EP" ]; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *"nvtensorrtrtx"*) ;;
        *) LINML_EP_LIBRARIES="nvtensorrtrtx=$TRT_RTX_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
fi
