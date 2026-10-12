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

# ROCm / MIGraphX runtime libraries, if installed
add_path "${ROCM_PATH:-/opt/rocm}/lib"

export LD_LIBRARY_PATH

# Convenience: default plugin EP library registration for TRT-RTX.
TRT_RTX_EP="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx/libonnxruntime_providers_nv_tensorrt_rtx.so"
if [ -f "$TRT_RTX_EP" ]; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *"NvTensorRTRTXExecutionProvider="*|*"nvtensorrtrtx="*) ;;
        *) LINML_EP_LIBRARIES="NvTensorRTRTXExecutionProvider=$TRT_RTX_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
fi

# Convenience: default plugin EP library registration for OpenVINO
# (vendored by scripts/fetch-ep-plugins.sh). The plugin resolves its bundled
# OpenVINO runtime via RUNPATH $ORIGIN, so no add_path is needed.
OPENVINO_EP="$DEPS/ep-plugins/openvino/onnxruntime_ep_openvino/libonnxruntime_providers_openvino_plugin.so"
if [ -f "$OPENVINO_EP" ]; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *";openvino="*) ;;
        *) LINML_EP_LIBRARIES="openvino=$OPENVINO_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
fi

# Convenience: default plugin EP library registration for MIGraphX
# (vendored by scripts/fetch-ep-plugins.sh or LinML.Catalog).
MIGRAPHX_EP="$DEPS/ep-plugins/migraphx/linml_migraphx_ep/libmigraphx-ep.so"
if [ -f "$MIGRAPHX_EP" ]; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *";MIGraphXExecutionProvider="*|*";migraphx="*) ;;
        *) LINML_EP_LIBRARIES="MIGraphXExecutionProvider=$MIGRAPHX_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
fi

