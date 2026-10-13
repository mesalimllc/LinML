#!/usr/bin/env bash
# LinML EP plugin fetcher
# Vendors plugin EPs from their vendor-published PyPI wheels into
# ~/.local/linml-deps/ep-plugins/:
#
#   trt-rtx   NvTensorRTRTXExecutionProvider (NVIDIA). The wheel bundles the EP
#             plugin .so AND the TensorRT-RTX 1.6.1 runtime libraries, so no
#             separate TensorRT-RTX SDK install is needed. x86_64 hosts get the
#             x86_64 wheel; Linux aarch64 (SBSA) hosts get the aarch64 wheel.
#   openvino  OpenVINOExecutionProvider (Intel onnxruntime-ep-openvino). The
#             wheel bundles the EP plugin .so AND the OpenVINO 2026.2.1 runtime
#             with its CPU/GPU/NPU device plugins. 1.6.1 is pinned because
#             1.7.0 requires an experimental OrtEpApi function the ORT 1.31
#             build LinML uses does not expose, and fails to create the EP.
#
#   migraphx  MIGraphXExecutionProvider (AMD onnxruntime-ep-amdgpu). Standalone
#             plugin EP built by LinML and published to GitHub Releases.
#             Requires ROCm with MIGraphX >= 2.17 on the host.
#
#   trt-rtx-wsl2
#             NvTensorRTRTXExecutionProvider, patched for WSL2. Fetches the
#             stock wheel above first (for the TensorRT-RTX runtime libraries),
#             then vendors LinML's patched EP .so. Opt-in: only pulled when
#             named explicitly. See docs/compatibility.md.
#
#   qnn       QNNExecutionProvider (Qualcomm onnxruntime-qnn 2.6.0). Standalone
#             plugin EP plus the bundled QAIRT runtime, from the linux-aarch64
#             inference tarball. Opt-in and aarch64-only: not in the default set,
#             and unverified on hardware (needs a Snapdragon NPU).
#
# The wheels and archives are vendored native binaries; the deployed product
# has no Python dependency.
#
# Pinned for reproducibility (sha256 verified).
#
# Usage: bash scripts/fetch-ep-plugins.sh [trt-rtx] [openvino] [migraphx] [trt-rtx-wsl2] [qnn]
#        (no arguments fetches the default set)

set -euo pipefail

DEPS="${HOME}/.local/linml-deps"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

fetch_trt_rtx() {
    local OUT="$DEPS/ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx"
    local WHEEL_URL="https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"
    local WHEEL_SHA256="68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19"
    local WHEEL_NAME="onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"
    local TMP="$TMP_ROOT/trt-rtx"

    echo "=== LinML EP plugin fetch (TensorRT-RTX) ==="

    if [ -f "$OUT/libonnxruntime_providers_nv_tensorrt_rtx.so" ]; then
        echo "Already vendored: $OUT"
        return 0
    fi

    # Staged like the OpenVINO fetch: a failed extract never leaves a
    # truncated plugin .so that the check above treats as vendored.
    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/ep.whl" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/ep.whl" | sha256sum -c

    echo "Extracting native libraries..."
    python3 - "$TMP/ep.whl" "$STAGE" << 'PYEOF'
import os, sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "onnxruntime_ep_nv_tensorrt_rtx/"
keep = [n for n in z.namelist()
        if n.startswith(prefix) and "/" not in n[len(prefix):]
        and (n.endswith(".so") or ".so." in n or n.endswith(".h") or n.endswith("README.txt"))]
# Flatten into $OUT (strip the package prefix) so the libraries sit where
# the check above, env-gpu-deps.sh and TrtRtxInferenceTests look for them.
for n in keep:
    with z.open(n) as src, open(os.path.join(out, n[len(prefix):]), "wb") as dst:
        dst.write(src.read())
print(f"extracted {len(keep)} files to {out}")
PYEOF

    # The EP .so links against SONAMEs (libtensorrt_rtx.so.1 etc.); the wheel ships
    # versioned files only. Create the SONAME symlinks next to the libraries.
    local lib
    for lib in libtensorrt_rtx libtensorrt_onnxparser_rtx; do
        if [ -f "$STAGE/${lib}.so.1.6.1" ] && [ ! -e "$STAGE/${lib}.so.1" ]; then
            ln -s "${lib}.so.1.6.1" "$STAGE/${lib}.so.1"
        fi
    done
    if [ -f "$STAGE/libcudart.so.13.4.36" ] && [ ! -e "$STAGE/libcudart.so.13" ]; then
        ln -s "libcudart.so.13.4.36" "$STAGE/libcudart.so.13"
    fi

    rm -rf "$OUT"
    mv "$STAGE" "$OUT"

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
}

fetch_trt_rtx_arm64() {
    local OUT="$DEPS/ep-plugins/trt-rtx-arm64/onnxruntime_ep_nv_tensorrt_rtx"
    local WHEEL_URL="https://files.pythonhosted.org/packages/41/63/ef110d73730969957740d770b29c158ca79a9df32c65ee081b01f8b64124/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_35_aarch64.whl"
    local WHEEL_SHA256="0ccf6a636f9f49d74e3e7e3b1b90287aa1c818db770b47fad6e8aa344f4290a5"
    local WHEEL_NAME="onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_35_aarch64.whl"
    local TMP="$TMP_ROOT/trt-rtx"

    echo "=== LinML EP plugin fetch (TensorRT-RTX) ==="

    # The wheel is manylinux_2_35: refuse an older glibc before downloading it.
    local GLIBC
    GLIBC="$(getconf GNU_LIBC_VERSION 2>/dev/null | awk '{print $2}')" || GLIBC=""
    if [ -z "$GLIBC" ] || [ "$(printf '%s\n' 2.35 "$GLIBC" | sort -V | head -n 1)" != "2.35" ]; then
        echo "TensorRT-RTX (aarch64) needs glibc 2.35 or newer; this host has ${GLIBC:-no glibc}." >&2
        return 1
    fi

    if [ -f "$OUT/libonnxruntime_providers_nv_tensorrt_rtx.so" ]; then
        echo "Already vendored: $OUT"
        return 0
    fi

    # Staged like the OpenVINO fetch: a failed extract never leaves a
    # truncated plugin .so that the check above treats as vendored.
    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/ep.whl" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/ep.whl" | sha256sum -c

    echo "Extracting native libraries..."
    python3 - "$TMP/ep.whl" "$STAGE" << 'PYEOF'
import os, sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "onnxruntime_ep_nv_tensorrt_rtx/"
keep = [n for n in z.namelist()
        if n.startswith(prefix) and "/" not in n[len(prefix):]
        and (n.endswith(".so") or ".so." in n or n.endswith(".h") or n.endswith("README.txt"))]
# Flatten into $OUT (strip the package prefix) so the libraries sit where
# the check above, env-gpu-deps.sh and TrtRtxInferenceTests look for them.
for n in keep:
    with z.open(n) as src, open(os.path.join(out, n[len(prefix):]), "wb") as dst:
        dst.write(src.read())
print(f"extracted {len(keep)} files to {out}")
PYEOF

    # The EP .so links against SONAMEs (libtensorrt_rtx.so.1 etc.); the wheel ships
    # versioned files only. Create the SONAME symlinks next to the libraries.
    local lib
    for lib in libtensorrt_rtx libtensorrt_onnxparser_rtx; do
        if [ -f "$STAGE/${lib}.so.1.6.1" ] && [ ! -e "$STAGE/${lib}.so.1" ]; then
            ln -s "${lib}.so.1.6.1" "$STAGE/${lib}.so.1"
        fi
    done
    # The aarch64 wheel ships libcudart.so.13.4.46; link whatever patch version it has.
    if [ ! -e "$STAGE/libcudart.so.13" ]; then
        cudart="$(compgen -G "$STAGE/libcudart.so.13.*" | head -n 1 || true)"
        if [ -n "$cudart" ]; then
            ln -s "$(basename "$cudart")" "$STAGE/libcudart.so.13"
        fi
    fi

    rm -rf "$OUT"
    mv "$STAGE" "$OUT"

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
}

fetch_openvino() {
    local OUT="$DEPS/ep-plugins/openvino/onnxruntime_ep_openvino"
    local WHEEL_URL="https://files.pythonhosted.org/packages/80/50/691cfc0370ab26a62213741026a8e7a172215e02b37cb2ff73ab1b0e1e60/onnxruntime_ep_openvino-1.6.1-py3-none-manylinux_2_28_x86_64.whl"
    local WHEEL_SHA256="a1afbbad0f051523bac0ab10b0aa8f49112f60f2c008f2ffb6c43eaf435412e0"
    local WHEEL_NAME="onnxruntime_ep_openvino-1.6.1-py3-none-manylinux_2_28_x86_64.whl"
    local TMP="$TMP_ROOT/openvino"

    echo "=== LinML EP plugin fetch (OpenVINO) ==="

    if [ -f "$OUT/libonnxruntime_providers_openvino_plugin.so" ]; then
        echo "Already vendored: $OUT"
        return 0
    fi

    # Extract into a sibling staging dir and rename it into place only once
    # every file is written, so an interrupted extract (e.g. disk full) never
    # leaves a truncated plugin .so that the check above treats as vendored.
    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/ep.whl" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/ep.whl" | sha256sum -c

    # The wheel ships every bundled SONAME as a regular file, so unlike
    # TensorRT-RTX no symlinks are needed. cache.json is OpenVINO runtime data.
    echo "Extracting native libraries..."
    python3 - "$TMP/ep.whl" "$STAGE" << 'PYEOF'
import os, sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "onnxruntime_ep_openvino/"
keep = [n for n in z.namelist()
        if n.startswith(prefix) and "/" not in n[len(prefix):]
        and (n.endswith(".so") or ".so." in n or n.endswith(".json"))]
# Flatten into $OUT (strip the package prefix) so the libraries sit where
# env-gpu-deps.sh looks for them.
for n in keep:
    with z.open(n) as src, open(os.path.join(out, n[len(prefix):]), "wb") as dst:
        dst.write(src.read())
print(f"extracted {len(keep)} files to {out}")
PYEOF

    rm -rf "$OUT"
    mv "$STAGE" "$OUT"

    echo ""
    echo "=== Vendored ==="
    ls -la "$OUT"
    echo ""
    echo "Next: source scripts/env-gpu-deps.sh"
    echo "  linml providers list            # after registration the EP appears via GetEpDevices"
    echo "  linml run --provider openvino --model ... --inputs ..."
}

fetch_migraphx() {
    local OUT="$DEPS/ep-plugins/migraphx/linml_migraphx_ep"
    local WHEEL_URL="https://github.com/trackdubllc/LinML/releases/download/ep-migraphx-2611.1/linml-migraphx-ep-2611.1-linux-x64.zip"
    local WHEEL_SHA256="968bdb1aa294c1ab4af04cfec0d4318fb2ffa4c69f0b34bc3b98532a199acd86"
    local WHEEL_NAME="linml-migraphx-ep-2611.1-linux-x64.zip"
    local TMP="$TMP_ROOT/migraphx"

    echo "=== LinML EP plugin fetch (MIGraphX) ==="

    if [ -f "$OUT/libmigraphx-ep.so" ]; then
        echo "Already vendored: $OUT"
        return 0
    fi

    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/$WHEEL_NAME" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/$WHEEL_NAME" | sha256sum -c

    echo "Extracting native libraries..."
    python3 - "$TMP/$WHEEL_NAME" "$STAGE" << 'PYEOF'
import os, sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "linml_migraphx_ep/"
keep = [n for n in z.namelist()
        if n.startswith(prefix) and not n.endswith("/")]
for n in keep:
    dest = os.path.join(out, n[len(prefix):])
    # The archive nests licenses/ under the top-level dir, so create parents.
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with z.open(n) as src, open(dest, "wb") as dst:
        dst.write(src.read())
print(f"extracted {len(keep)} files to {out}")
PYEOF

    rm -rf "$OUT"
    mv "$STAGE" "$OUT"

    echo ""
    echo "=== Vendored ==="
    ls -la "$OUT"
    echo ""
    echo "Next: source scripts/env-gpu-deps.sh"
    echo "  linml providers list"
    echo "  linml run --provider migraphx --model ... --inputs ..."
}

fetch_qnn() {
    local OUT="$DEPS/ep-plugins/qnn/onnxruntime_qnn"
    local WHEEL_URL="https://github.com/onnxruntime/onnxruntime-qnn/releases/download/v2.6.0/onnxruntime-qnn-2.6.0-linux-aarch64.tgz"
    local WHEEL_SHA256="c61507ec7f9fa0eb2c0c1cefe5a0e8bdf48f185504de224917b23a389f362b38"
    local WHEEL_NAME="onnxruntime-qnn-2.6.0-linux-aarch64.tgz"
    local TMP="$TMP_ROOT/qnn"

    echo "=== LinML EP plugin fetch (Qualcomm QNN) ==="

    # The linux-x64 tarball is AOT-compilation only (no inference), so only the
    # aarch64 inference package is pinned.
    if [ "$(uname -m)" != "aarch64" ]; then
        echo "QNN inference needs a Linux aarch64 host (this is $(uname -m)); skipping." >&2
        return 1
    fi

    if [ -f "$OUT/libonnxruntime_providers_qnn.so" ]; then
        echo "Already vendored: $OUT"
        return 0
    fi

    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE/licenses" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/$WHEEL_NAME" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/$WHEEL_NAME" | sha256sum -c

    # The tarball is flat. Vendor the plugin and the bundled QAIRT runtime
    # (*.so), and keep the licence and notice files with the binaries.
    echo "Extracting native libraries..."
    tar -xzf "$TMP/$WHEEL_NAME" -C "$STAGE" --wildcards '*.so'
    tar -xzf "$TMP/$WHEEL_NAME" -C "$STAGE/licenses" LICENSE Qualcomm_LICENSE.pdf ThirdPartyNotices.txt

    rm -rf "$OUT"
    mv "$STAGE" "$OUT"

    echo ""
    echo "=== Vendored ==="
    ls -la "$OUT"
    echo ""
    echo "Next: source scripts/env-gpu-deps.sh"
    echo "  linml providers list"
    echo "  linml run --provider qnn --ep-option qnn:backend_path=$OUT/libQnnHtp.so --model ... --inputs ..."
    echo ""
    echo "Unverified on hardware: needs a Snapdragon device with an NPU."
}

fetch_trt_rtx_wsl2() {
    local OUT="$DEPS/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep"
    local EP_VERSION="0.4.0-wsl2p2"
    local WHEEL_URL="https://github.com/trackdubllc/LinML/releases/download/ep-trt-rtx-wsl2-0.4.0-wsl2p2/linml-trt-rtx-ep-0.4.0-wsl2p2-linux-x64.zip"
    local WHEEL_SHA256="7575c51157110228d581593c04147473868fbb7eefc79aa1b809c5b8bb66c220"
    local WHEEL_NAME="linml-trt-rtx-ep-0.4.0-wsl2p2-linux-x64.zip"
    local TMP="$TMP_ROOT/trt-rtx-wsl2"

    echo "=== LinML EP plugin fetch (TensorRT-RTX, WSL2 patch) ==="

    # The patched EP dynamically links the TensorRT-RTX 1.6.1 runtime libraries,
    # which only the stock wheel bundles; vendor the wheel first.
    fetch_trt_rtx

    # The library alone does not say which build it is: an earlier one sits in the
    # same directory (0.4.0-wsl2p1 needs a newer glibc than most hosts have), so
    # compare the epVersion the archive records in build-info.json with the pin.
    if [ -f "$OUT/libonnxruntime_providers_nv_tensorrt_rtx.so" ]; then
        if python3 -c 'import json, sys; sys.exit(0 if json.load(open(sys.argv[1])).get("epVersion") == sys.argv[2] else 1)' \
            "$OUT/build-info.json" "$EP_VERSION" 2>/dev/null; then
            echo "Already vendored: $OUT"
            return 0
        fi
        echo "Vendored build at $OUT is not $EP_VERSION; replacing it."
    fi

    # Stage and rename like the other fetches: an interrupted extract never
    # leaves a truncated plugin .so that the check above treats as vendored, and
    # a failed download leaves the build that is there in place.
    local STAGE="$OUT.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE" "$TMP"

    echo "Downloading $WHEEL_NAME ..."
    curl -fsSL -o "$TMP/$WHEEL_NAME" "$WHEEL_URL"

    echo "Verifying sha256..."
    echo "$WHEEL_SHA256  $TMP/$WHEEL_NAME" | sha256sum -c

    echo "Extracting patched EP..."
    python3 - "$TMP/$WHEEL_NAME" "$STAGE" << 'PYEOF'
import os, sys, zipfile
wheel, out = sys.argv[1], sys.argv[2]
z = zipfile.ZipFile(wheel)
prefix = "linml_trt_rtx_ep/"
keep = [n for n in z.namelist() if n.startswith(prefix) and not n.endswith("/")]
for n in keep:
    dest = os.path.join(out, n[len(prefix):])
    # The archive nests licenses/ under the top-level dir, so create parents.
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    with z.open(n) as src, open(dest, "wb") as dst:
        dst.write(src.read())
print(f"extracted {len(keep)} files to {out}")
PYEOF

    # Replacing an earlier build: move it aside rather than delete it, so a failed
    # move leaves it in place and the plugin is missing only for the instant
    # between the two renames.
    local PREVIOUS="$OUT.previous"
    rm -rf "$PREVIOUS"
    if [ -e "$OUT" ]; then
        mv "$OUT" "$PREVIOUS"
    fi
    if ! mv "$STAGE" "$OUT"; then
        if [ -e "$PREVIOUS" ]; then
            mv "$PREVIOUS" "$OUT"
        fi
        echo "ERROR: could not move the new build into place; the previous one was left as it was." >&2
        return 1
    fi
    rm -rf "$PREVIOUS"

    echo ""
    echo "=== Vendored ==="
    ls -la "$OUT"
    echo ""
    echo "Next: source scripts/env-gpu-deps.sh"
    echo "  linml providers list            # the RTX GPU is enumerated"
    echo "  linml run --provider trt-rtx --model ... --inputs ..."
    echo ""
    echo "This is opt-in: the default 'trt-rtx' pin still points at NVIDIA's wheel."
}

PLUGINS=("$@")
if [ ${#PLUGINS[@]} -eq 0 ]; then
    PLUGINS=(trt-rtx openvino migraphx)
fi
for plugin in "${PLUGINS[@]}"; do
    case "$plugin" in
        trt-rtx)
            if [ "$(uname -m)" = "aarch64" ]; then fetch_trt_rtx_arm64; else fetch_trt_rtx; fi
            ;;
        trt-rtx-wsl2) fetch_trt_rtx_wsl2 ;;
        openvino) fetch_openvino ;;
        migraphx) fetch_migraphx ;;
        qnn) fetch_qnn ;;
        *)
            echo "Unknown plugin '$plugin' (expected: trt-rtx, trt-rtx-wsl2, openvino, migraphx, qnn)" >&2
            exit 2
            ;;
    esac
done
