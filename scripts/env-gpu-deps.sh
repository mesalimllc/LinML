#!/usr/bin/env bash
# LinML GPU dependency environment
# Source this to put cuDNN / TensorRT / plugin EP runtime libraries on the
# loader path. Run scripts/setup-gpu-deps.sh once to populate the deps.
#
# Usage: source scripts/env-gpu-deps.sh

# Dependency root. Shared with scripts/build-trt-rtx-ep.sh so a build installed
# under a custom DEPS is still discovered here; default is the standard layout.
DEPS="${DEPS:-${HOME}/.local/linml-deps}"

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
add_path "$DEPS/cudnn/nvidia/cu13/lib"      # cuBLAS 13 / NVRTC (current wheel layout)
add_path "$DEPS/cudnn/nvidia/cublas/lib"    # older wheel layout

# TensorRT 10.x classic (libnvinfer.so.10) — required by TensorrtExecutionProvider
add_path "$DEPS/tensorrt-libs/tensorrt_libs"
add_path "$DEPS/tensorrt-lean/tensorrt_lean_libs"

# TensorRT-RTX plugin EP (vendored wheel) — the EP .so resolves its bundled
# libtensorrt_rtx via its own directory; add it so dlopen finds them.
if [ "$(uname -m)" = "aarch64" ]; then
    TRT_RTX_DIR="$DEPS/ep-plugins/trt-rtx-arm64/onnxruntime_ep_nv_tensorrt_rtx"
else
    TRT_RTX_DIR="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx"
fi
add_path "$TRT_RTX_DIR"

# OpenVINO runtime (pip wheel), if installed
add_path "$DEPS/openvino/lib"

# ROCm / MIGraphX runtime libraries, if installed
add_path "${ROCM_PATH:-/opt/rocm}/lib"

export LD_LIBRARY_PATH

# Convenience: default plugin EP library registration for TRT-RTX. The library
# is chosen in this order:
#   1. TRT_RTX_CUSTOM_EP — an explicit locally built plugin (e.g. a WSL2 build
#      from scripts/build-trt-rtx-ep.sh or scripts/build-trt-rtx-custom.sh).
#   2. the LinML-hosted WSL2 fallback build, if vendored
#      (scripts/fetch-ep-plugins.sh trt-rtx-wsl2), unless LINML_TRT_RTX_WSL2=0.
#   3. the stock NVIDIA wheel (scripts/fetch-ep-plugins.sh trt-rtx).
if [ "$(uname -m)" = "aarch64" ]; then
    TRT_RTX_DEFAULT_EP="$DEPS/ep-plugins/trt-rtx-arm64/onnxruntime_ep_nv_tensorrt_rtx/libonnxruntime_providers_nv_tensorrt_rtx.so"
else
    TRT_RTX_DEFAULT_EP="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx/libonnxruntime_providers_nv_tensorrt_rtx.so"
fi
TRT_RTX_WSL2_EP="$DEPS/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/libonnxruntime_providers_nv_tensorrt_rtx.so"
TRT_RTX_EP="${TRT_RTX_CUSTOM_EP:-}"
case "${LINML_TRT_RTX_WSL2:-}" in 0|false|off|no) TRT_RTX_WSL2_OPT_OUT=1 ;; *) TRT_RTX_WSL2_OPT_OUT= ;; esac
if [ -z "$TRT_RTX_EP" ] && [ -z "$TRT_RTX_WSL2_OPT_OUT" ] && [ -f "$TRT_RTX_WSL2_EP" ]; then
    TRT_RTX_EP="$TRT_RTX_WSL2_EP"
fi
TRT_RTX_EP="${TRT_RTX_EP:-$TRT_RTX_DEFAULT_EP}"
# Ensure the directory holding the selected EP is on the loader path (it may
# bundle the TensorRT-RTX runtime libraries next to the plugin).
add_path "$(dirname "$TRT_RTX_EP")"
if [ -f "$TRT_RTX_EP" ]; then
    # Replace any TensorRT-RTX mapping from a previous source. Re-sourcing after
    # installing the WSL2 build (stock -> patched) must switch to the new path,
    # while mappings for other providers are preserved. TRT_RTX_CUSTOM_EP above
    # remains the explicit override.
    if [ -n "${LINML_EP_LIBRARIES:-}" ]; then
        _linml_rest=""
        # Peel entries off one at a time rather than word-splitting on IFS, so
        # this also works when sourced from zsh (which does not split $VAR).
        _linml_todo="$LINML_EP_LIBRARIES"
        while [ -n "$_linml_todo" ]; do
            _linml_pair="${_linml_todo%%;*}"
            case "$_linml_todo" in
                *";"*) _linml_todo="${_linml_todo#*;}" ;;
                *) _linml_todo="" ;;
            esac
            [ -n "$_linml_pair" ] || continue
            case "$_linml_pair" in
                NvTensorRTRTXExecutionProvider=*|nvtensorrtrtx=*) ;;  # stale, drop
                *) _linml_rest="${_linml_rest:+$_linml_rest;}$_linml_pair" ;;
            esac
        done
        LINML_EP_LIBRARIES="$_linml_rest"
        unset _linml_pair _linml_rest _linml_todo
    fi
    LINML_EP_LIBRARIES="NvTensorRTRTXExecutionProvider=$TRT_RTX_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}"
    export LINML_EP_LIBRARIES
fi

# Convenience: default plugin EP library registration for OpenVINO
# (vendored by scripts/fetch-ep-plugins.sh). The plugin resolves its bundled
# OpenVINO runtime via RUNPATH $ORIGIN, so no add_path is needed. Registered
# under the catalog's name ("linml.openvino"); the bare alias "openvino" is not
# recognised by LinML's discovery or doctor.
OPENVINO_EP="$DEPS/ep-plugins/openvino/onnxruntime_ep_openvino/libonnxruntime_providers_openvino_plugin.so"
if [ -f "$OPENVINO_EP" ]; then
    # Drop a stale bare "openvino=" mapping (exported by an older version of this
    # script, which LinML does not recognise) while preserving every other provider
    # mapping, including an intentional canonical OpenVINO override.
    _linml_has_ov=""
    if [ -n "${LINML_EP_LIBRARIES:-}" ]; then
        _linml_rest=""
        # Peel entries off one at a time rather than word-splitting on IFS, so
        # this also works when sourced from zsh (which does not split $VAR).
        _linml_todo="$LINML_EP_LIBRARIES"
        while [ -n "$_linml_todo" ]; do
            _linml_pair="${_linml_todo%%;*}"
            case "$_linml_todo" in
                *";"*) _linml_todo="${_linml_todo#*;}" ;;
                *) _linml_todo="" ;;
            esac
            [ -n "$_linml_pair" ] || continue
            case "$_linml_pair" in
                openvino=*) ;;  # stale alias, drop
                linml.openvino=*|OpenVINOExecutionProvider=*)
                    _linml_has_ov=1
                    _linml_rest="${_linml_rest:+$_linml_rest;}$_linml_pair" ;;
                *) _linml_rest="${_linml_rest:+$_linml_rest;}$_linml_pair" ;;
            esac
        done
        LINML_EP_LIBRARIES="$_linml_rest"
        unset _linml_pair _linml_rest _linml_todo
    fi
    if [ -z "$_linml_has_ov" ]; then
        LINML_EP_LIBRARIES="linml.openvino=$OPENVINO_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}"
    fi
    unset _linml_has_ov
    export LINML_EP_LIBRARIES
fi

# Convenience: default plugin EP library registration for MIGraphX
# (vendored by scripts/fetch-ep-plugins.sh or LinML.Catalog). The plugin links
# against ROCm's libmigraphx_c.so.3, so it is only mapped when that library
# resolves (loader path or ldconfig cache); without ROCm, MIGraphX is not
# applicable on this host (see `linml doctor`) and the plugin cannot load.
_linml_has_migraphx_runtime() {
    # Written to work when sourced from zsh too, which does not word-split $VAR.
    if printf '%s\n' "${LD_LIBRARY_PATH:-}" | tr ':' '\n' | while IFS= read -r _linml_dir; do
            if [ -n "$_linml_dir" ] && [ -e "$_linml_dir/libmigraphx_c.so.3" ]; then echo found; fi
        done | grep -q found; then
        return 0
    fi
    for _linml_ldconfig in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
        if command -v "$_linml_ldconfig" >/dev/null 2>&1; then
            "$_linml_ldconfig" -p 2>/dev/null | grep -q 'libmigraphx_c\.so\.3 '
            _linml_rc=$?
            unset _linml_ldconfig
            return $_linml_rc
        fi
    done
    unset _linml_ldconfig
    return 1
}
MIGRAPHX_EP="$DEPS/ep-plugins/migraphx/linml_migraphx_ep/libmigraphx-ep.so"
if [ -f "$MIGRAPHX_EP" ] && _linml_has_migraphx_runtime; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *";MIGraphXExecutionProvider="*|*";migraphx="*) ;;
        *) LINML_EP_LIBRARIES="MIGraphXExecutionProvider=$MIGRAPHX_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
elif [ -n "${LINML_EP_LIBRARIES:-}" ]; then
    # Drop the vendored mapping a previous source (or an older version of this
    # script) exported; any other MIGraphX mapping is the user's and is kept.
    # Walked entry by entry like the TensorRT-RTX and OpenVINO cleanups above.
    _linml_rest=""
    _linml_todo="$LINML_EP_LIBRARIES"
    while [ -n "$_linml_todo" ]; do
        _linml_pair="${_linml_todo%%;*}"
        case "$_linml_todo" in
            *";"*) _linml_todo="${_linml_todo#*;}" ;;
            *) _linml_todo="" ;;
        esac
        [ -n "$_linml_pair" ] || continue
        case "$_linml_pair" in
            "MIGraphXExecutionProvider=$MIGRAPHX_EP") ;;  # stale vendored mapping, drop
            *) _linml_rest="${_linml_rest:+$_linml_rest;}$_linml_pair" ;;
        esac
    done
    LINML_EP_LIBRARIES="$_linml_rest"
    export LINML_EP_LIBRARIES
    unset _linml_pair _linml_rest _linml_todo
fi
unset -f _linml_has_migraphx_runtime


# Convenience: default plugin EP library registration for Qualcomm QNN
# (vendored by scripts/fetch-ep-plugins.sh qnn, linux-aarch64 only). The plugin
# resolves its bundled QAIRT runtime via RUNPATH $ORIGIN.
QNN_EP="$DEPS/ep-plugins/qnn/onnxruntime_qnn/libonnxruntime_providers_qnn.so"
if [ -f "$QNN_EP" ]; then
    case ";${LINML_EP_LIBRARIES:-};" in
        *";QNNExecutionProvider="*|*";qnn="*) ;;
        *) LINML_EP_LIBRARIES="QNNExecutionProvider=$QNN_EP${LINML_EP_LIBRARIES:+;$LINML_EP_LIBRARIES}" ;;
    esac
    export LINML_EP_LIBRARIES
fi
