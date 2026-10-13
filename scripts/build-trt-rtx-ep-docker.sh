#!/usr/bin/env bash
# Build the patched WSL2 TensorRT-RTX plugin EP (scripts/build-trt-rtx-ep.sh)
# inside an ubuntu:22.04 container, so the library needs glibc 2.35 and
# libstdc++ 3.4.30 at most and loads on every distro the stock wheel does.
#
# Why: a build on a current host links that host's symbol versions. The
# 0.4.0-wsl2p1 archive needed GLIBC_2.43 and failed to load on Ubuntu 24.04
# (glibc 2.39). build-trt-rtx-ep.sh now refuses such a library, so a native build
# on a new host fails; use this wrapper.
#
# Needs: docker, the extracted TensorRT-RTX SDK (include/ + lib/, not
# redistributable) and a CUDA 13 toolkit (headers + stubs) on the host. Both are
# mounted read-only; nothing from them ends up in the archive.
#
# Output and install behaviour match build-trt-rtx-ep.sh: the zip + .sha256 land in
# OUT_DIR (default ./out/trt-rtx-ep) and the build is installed into
# ~/.local/linml-deps unless NO_INSTALL=1.
#
# Usage:
#   TRT_RTX_SDK=/path/to/TensorRT-RTX-1.6.1.120 bash scripts/build-trt-rtx-ep-docker.sh
#   (env overrides: TRT_RTX_SDK, CUDA_ROOT, OUT_DIR, DEPS, NO_INSTALL=1, BUILD_IMAGE)

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$REPO_ROOT/out/trt-rtx-ep}"
BUILD_IMAGE="${BUILD_IMAGE:-linml-trt-rtx-ep-build:ubuntu22.04}"

command -v docker >/dev/null 2>&1 || { echo "ERROR: docker not found." >&2; exit 1; }
# The host-side install below unpacks the archive with python3: check before a long build, not after.
if [ "${NO_INSTALL:-0}" != "1" ]; then
    command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 not found (needed to install the build; set NO_INSTALL=1 to skip)." >&2; exit 1; }
fi

TRT_RTX_SDK="${TRT_RTX_SDK:-}"
if [ -z "$TRT_RTX_SDK" ] || [ ! -d "$TRT_RTX_SDK/include" ] || [ ! -d "$TRT_RTX_SDK/lib" ]; then
    echo "ERROR: set TRT_RTX_SDK to an extracted TensorRT-RTX Linux SDK (the directory with include/ and lib/)." >&2
    echo "       See scripts/build-trt-rtx-ep.sh for where to download it." >&2
    exit 2
fi

CUDA_ROOT="${CUDA_ROOT:-}"
if [ -z "$CUDA_ROOT" ]; then
    for cand in /usr/local/cuda-13 /usr/local/cuda; do
        if [ -x "$cand/bin/nvcc" ]; then CUDA_ROOT="$cand"; break; fi
    done
fi
if [ -z "$CUDA_ROOT" ] || [ ! -x "$CUDA_ROOT/bin/nvcc" ]; then
    echo "ERROR: CUDA 13 toolkit not found. Set CUDA_ROOT=/usr/local/cuda-13." >&2
    exit 2
fi

# /usr/local/cuda* are usually symlinks (via /etc/alternatives); mount the real directories.
TRT_RTX_SDK="$(readlink -f "$TRT_RTX_SDK")"
CUDA_ROOT="$(readlink -f "$CUDA_ROOT")"
mkdir -p "$OUT_DIR"
OUT_DIR="$(readlink -f "$OUT_DIR")"

echo "=== Building the TensorRT-RTX EP in ubuntu:22.04 ==="
echo "TRT_RTX_SDK: $TRT_RTX_SDK"
echo "CUDA_ROOT:   $CUDA_ROOT"
echo "OUT_DIR:     $OUT_DIR"
echo ""

docker build -q -t "$BUILD_IMAGE" -f "$REPO_ROOT/scripts/docker/trt-rtx-ep-build.Dockerfile" "$REPO_ROOT/scripts/docker" >/dev/null

# Run as the invoking user so the archive in OUT_DIR is not root-owned. The build
# installs nothing inside the container (NO_INSTALL=1); the host install is below.
docker run --rm \
    --user "$(id -u):$(id -g)" \
    -e HOME=/tmp \
    -e NO_INSTALL=1 \
    -e OUT_DIR=/out \
    -e WORK=/tmp/linml-trt-rtx-ep \
    -e TRT_RTX_SDK=/opt/trt-rtx-sdk \
    -e CUDA_ROOT=/opt/cuda \
    -v "$REPO_ROOT":/src:ro \
    -v "$TRT_RTX_SDK":/opt/trt-rtx-sdk:ro \
    -v "$CUDA_ROOT":/opt/cuda:ro \
    -v "$OUT_DIR":/out \
    "$BUILD_IMAGE" \
    bash /src/scripts/build-trt-rtx-ep.sh

if [ "${NO_INSTALL:-0}" != "1" ]; then
    ARCHIVE="$(ls -t "$OUT_DIR"/linml-trt-rtx-ep-*-linux-x64.zip | head -n1)"
    DEPS="${DEPS:-$HOME/.local/linml-deps}"
    DEST="$DEPS/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep"
    # Unpack beside the install and swap, so a failed unpack leaves the existing build alone.
    STAGE="$DEST.partial"
    rm -rf "$STAGE"
    mkdir -p "$STAGE"
    python3 - "$ARCHIVE" "$STAGE" <<'PYEOF'
import os, sys, zipfile
archive, out = sys.argv[1], sys.argv[2]
prefix = "linml_trt_rtx_ep/"
with zipfile.ZipFile(archive) as z:
    for name in z.namelist():
        if name.startswith(prefix) and not name.endswith("/"):
            dest = os.path.join(out, name[len(prefix):])
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with z.open(name) as src, open(dest, "wb") as dst:
                dst.write(src.read())
PYEOF
    rm -rf "$DEST.previous"
    if [ -e "$DEST" ]; then mv "$DEST" "$DEST.previous"; fi
    if ! mv "$STAGE" "$DEST"; then
        if [ -e "$DEST.previous" ]; then mv "$DEST.previous" "$DEST"; fi
        echo "ERROR: could not move the new build into place; the previous one was left as it was." >&2
        exit 1
    fi
    rm -rf "$DEST.previous"
    echo ""
    echo "Installed: $DEST"
fi
