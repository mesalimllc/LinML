#!/usr/bin/env bash
# Build the NVIDIA TensorRT-RTX plugin EP from source with the WSL2
# device-enumeration fix, and package it as the zip archive LinML.Catalog /
# scripts/fetch-ep-plugins.sh download.
#
# Why a LinML-hosted build: the stock PyPI wheel registers but exposes zero
# devices under WSL2 (ORT reports the GPU with the virtual PCI vendor 0x1414
# and a bus id the CUDA driver rejects, cudaDeviceGetByPCIBusId rc=101). The
# fix is upstream PR #12; until it merges this script pins the PR head commit.
# See evidence/trt-rtx-wsl2-block.md and docs/compatibility.md.
#
# The EP source is Apache-2.0 (NVIDIA/TensorRT-RTX-EP-ABI); this build is a
# modified version and carries a change notice (PATCH-NOTICE.md).
#
# Build in an old-glibc environment. A library built on a current distro needs
# that distro's glibc (the 0.4.0-wsl2p1 build required GLIBC_2.43 and loaded
# nowhere older), so the archive must be built where glibc is no newer than the
# oldest host it supports: Ubuntu 22.04, glibc 2.35, the floor of the stock
# wheel. `scripts/build-trt-rtx-ep-docker.sh` does that in an ubuntu:22.04
# container. This script runs scripts/check-elf-floor.sh on its output and fails
# if the library needs a newer GLIBC_, GLIBCXX_ or CXXABI_, so a build on a new
# host cannot be released by mistake.
#
# The TensorRT-RTX SDK (headers + import libs) is NOT redistributable and sits
# behind NVIDIA's license acceptance, so it cannot be downloaded automatically:
# point TRT_RTX_SDK at an extracted SDK. As with the MIGraphX EP, the archive
# contains ONLY the EP .so -- no SDK code. At run time the host needs NVIDIA's
# TensorRT-RTX 1.6.1 runtime libraries (libtensorrt_rtx.so.1,
# libtensorrt_onnxparser_rtx.so.1) plus a CUDA 13 runtime; both ship in the
# official wheel installed by `scripts/fetch-ep-plugins.sh trt-rtx`.
#
# Output (OUT_DIR, default ./out/trt-rtx-ep):
#   linml-trt-rtx-ep-<EP_VERSION>-linux-x64.zip         archive for the catalog
#   linml-trt-rtx-ep-<EP_VERSION>-linux-x64.zip.sha256  its checksum
#
# Usage (inside an old-glibc environment; see above):
#   TRT_RTX_SDK=/path/to/TensorRT-RTX-1.6.1.120 \
#       bash scripts/build-trt-rtx-ep-docker.sh
#   (env overrides: TRT_RTX_SDK, CUDA_ROOT, OUT_DIR, WORK, DEPS, NO_INSTALL=1)

set -euo pipefail

# --- Pins ---------------------------------------------------------------------
# Bump EP_VERSION whenever the emitted .so changes: a published release archive
# is pinned by sha256 and is never overwritten.

EP_VERSION="0.4.0-wsl2p2"
# TODO: switch to NVIDIA/TensorRT-RTX-EP-ABI @ the merged commit once PR #12 lands.
EP_REPO="https://github.com/tonythethompson/TensorRT-RTX-EP-ABI.git"
EP_COMMIT="6777dafa257dd569f489f1708473d5bee0f0a6d7" # PR #12 head (per-GPU registration)
UPSTREAM_REPO="https://github.com/NVIDIA/TensorRT-RTX-EP-ABI"
PR_URL="https://github.com/NVIDIA/TensorRT-RTX-EP-ABI/pull/12"

ORT_VERSION="1.31.0"
ORT_URL="https://github.com/microsoft/onnxruntime/releases/download/v${ORT_VERSION}/onnxruntime-linux-x64-${ORT_VERSION}.tgz"
ORT_SHA256="cc5c72baf5ae5c8238a6841f0897227be2d02826b9cf98eaf02fdefaeeb03a57"

TRT_RTX_VERSION="1.6.1.120"

# --- Paths --------------------------------------------------------------------

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$REPO_ROOT/out/trt-rtx-ep}"
WORK="${WORK:-/tmp/linml-trt-rtx-ep}"
EP_SRC="$WORK/ep-abi"
ORT_HOME="$WORK/onnxruntime"
BUILD="$WORK/build"
PKG_PREFIX="linml_trt_rtx_ep"
ARCHIVE="linml-trt-rtx-ep-${EP_VERSION}-linux-x64.zip"

fetch_verified() { # url dest sha256
    curl -fsSL --retry 3 "$1" -o "$2"
    echo "$3  $2" | sha256sum -c -
}

# --- Locate TensorRT-RTX SDK (user supplied: not redistributable) ---------------

TRT_RTX_SDK="${TRT_RTX_SDK:-}"
if [ -z "$TRT_RTX_SDK" ] || { [ ! -d "$TRT_RTX_SDK/include" ] || [ ! -d "$TRT_RTX_SDK/lib" ]; }; then
    cat >&2 <<'EOF'
ERROR: TensorRT-RTX SDK not found.

The SDK is licensed by NVIDIA and cannot be fetched automatically. Download the
TensorRT-RTX 1.6.x Linux x86_64 tarball for your CUDA version from
https://developer.nvidia.com/tensorrt-rtx , extract it, and point TRT_RTX_SDK
at the directory that contains include/ and lib/:

  TRT_RTX_SDK=/path/to/TensorRT-RTX-1.6.1.120-linux-x86_64-cuda-13.4 \
      bash scripts/build-trt-rtx-ep.sh
EOF
    exit 2
fi

# --- Locate CUDA toolkit (nvcc + cudart + libcuda headers) ---------------------

CUDA_ROOT="${CUDA_ROOT:-}"
if [ -z "$CUDA_ROOT" ]; then
    for cand in /usr/local/cuda-13 /usr/local/cuda; do
        if [ -x "$cand/bin/nvcc" ]; then
            CUDA_ROOT="$cand"
            break
        fi
    done
fi
if [ -z "$CUDA_ROOT" ]; then
    echo "ERROR: CUDA toolkit not found (need CUDA 12.9+ or 13.x). Set CUDA_ROOT=/usr/local/cuda-13." >&2
    exit 2
fi

# --- Toolchain -----------------------------------------------------------------

for tool in cmake curl git python3 sha256sum strip readelf nm objdump; do
    command -v "$tool" >/dev/null 2>&1 || { echo "ERROR: $tool not found on PATH." >&2; exit 1; }
done

rm -rf "$WORK"
mkdir -p "$WORK" "$OUT_DIR"

echo "=== TensorRT-RTX EP build (WSL2 fix) ==="
echo "EP_VERSION:  $EP_VERSION"
echo "EP_REPO:     $EP_REPO"
echo "EP_COMMIT:   $EP_COMMIT"
echo "TRT_RTX_SDK: $TRT_RTX_SDK"
echo "CUDA_ROOT:   $CUDA_ROOT"
echo "ORT_VERSION: $ORT_VERSION"
echo "OUT_DIR:     $OUT_DIR"
echo ""

# --- EP source (pinned) --------------------------------------------------------

git init -q "$EP_SRC"
git -C "$EP_SRC" remote add origin "$EP_REPO"
git -C "$EP_SRC" fetch -q --depth 1 origin "$EP_COMMIT"
git -C "$EP_SRC" checkout -q FETCH_HEAD
test "$(git -C "$EP_SRC" rev-parse HEAD)" = "$EP_COMMIT"
echo "Checked out $EP_COMMIT"

# The fix is already in this commit; the marker guards against pinning a commit
# that predates it (which would silently produce a WSL2-broken artifact). The
# new header is introduced by the PR.
if [ ! -f "$EP_SRC/src/tensorrt_rtx_device_selection.h" ]; then
    echo "ERROR: pinned commit $EP_COMMIT does not contain the WSL2 fix (marker missing)." >&2
    exit 1
fi

# --- ONNX Runtime headers / import library (public) ----------------------------

fetch_verified "$ORT_URL" "$WORK/ort.tgz" "$ORT_SHA256"
mkdir -p "$ORT_HOME"
tar -xzf "$WORK/ort.tgz" -C "$ORT_HOME" --strip-components=1

# --- Build ---------------------------------------------------------------------

cmake -B "$BUILD" \
    -DONNXRUNTIME_ROOT="$ORT_HOME" \
    -DTRT_RTX_ROOT="$TRT_RTX_SDK" \
    -DCUDAToolkit_ROOT="$CUDA_ROOT" \
    -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
    -DCMAKE_INSTALL_RPATH='$ORIGIN' \
    -S "$EP_SRC" > "$WORK/cmake-config.log" 2>&1 || {
        echo "ERROR: cmake configure failed -- see $WORK/cmake-config.log" >&2
        tail -30 "$WORK/cmake-config.log" >&2
        exit 1
    }

# Only the plugin: the EP's unit-test binary needs a CUDA driver (libcuda.so.1) to be
# linked and discovered, which a build container does not have, and it is not shipped.
cmake --build "$BUILD" --target TensorRTRtxEp -j "$(nproc)" > "$WORK/cmake-build.log" 2>&1 || {
    echo "ERROR: build failed -- see $WORK/cmake-build.log" >&2
    tail -30 "$WORK/cmake-build.log" >&2
    exit 1
}

BUILT="$(find "$BUILD" -name libonnxruntime_providers_nv_tensorrt_rtx.so -type f | head -n1)"
if [ -z "$BUILT" ]; then
    echo "ERROR: libonnxruntime_providers_nv_tensorrt_rtx.so was not produced." >&2
    exit 1
fi

# --- Package -------------------------------------------------------------------

STAGE="$WORK/stage/$PKG_PREFIX"
mkdir -p "$STAGE/licenses"
cp "$BUILT" "$STAGE/libonnxruntime_providers_nv_tensorrt_rtx.so"

# The build-time RUNPATH is CMAKE_INSTALL_RPATH ($ORIGIN); force it with patchelf
# when available and verify it either way, so we never ship a .so whose runpath
# points at build-machine paths.
EP_SO="$STAGE/libonnxruntime_providers_nv_tensorrt_rtx.so"
if command -v patchelf >/dev/null 2>&1; then
    patchelf --set-rpath '$ORIGIN' "$EP_SO"
fi
# readelf/nm output is captured before grepping: `grep -q` exits on the first
# match, which can SIGPIPE the producer, and under `set -o pipefail` that turns a
# successful match into a spurious pipeline failure.
DYNAMIC="$(readelf -d "$EP_SO")"
if ! grep -q '\$ORIGIN' <<< "$DYNAMIC"; then
    echo "ERROR: RUNPATH is not \$ORIGIN; install patchelf (apt-get install -y patchelf) and retry." >&2
    grep -E 'RUNPATH|RPATH' <<< "$DYNAMIC" >&2
    exit 1
fi
strip --strip-unneeded "$EP_SO"

# The glibc / libstdc++ floor: the library must load wherever the stock wheel does.
echo "--- symbol version floor"
bash "$REPO_ROOT/scripts/check-elf-floor.sh" "$EP_SO" || {
    echo "ERROR: $EP_SO needs a newer glibc / libstdc++ than the supported floor; not packaging it." >&2
    exit 1
}

echo "--- dynamic section"
grep -E 'NEEDED|RUNPATH|RPATH' <<< "$DYNAMIC"
EXPORTS="$(nm -D --defined-only "$EP_SO")"
for sym in CreateEpFactories ReleaseEpFactory; do
    grep -qw "$sym" <<< "$EXPORTS" \
        || { echo "ERROR: $sym is not exported." >&2; exit 1; }
done

cp "$EP_SRC/LICENSE" "$STAGE/licenses/LICENSE"
if [ -f "$EP_SRC/ThirdPartyNotices.txt" ]; then
    cp "$EP_SRC/ThirdPartyNotices.txt" "$STAGE/licenses/ThirdPartyNotices.txt"
fi

# Apache-2.0 section 4(b): a modified build must carry prominent notices of the
# changes.
cat > "$STAGE/PATCH-NOTICE.md" <<EOF
# Modification notice

This is a modified build of NVIDIA's TensorRT-RTX plugin execution provider
(${UPSTREAM_REPO}), distributed under the Apache License 2.0.

Source commit: ${EP_REPO} @ ${EP_COMMIT}
Changes: WSL2 device-enumeration fix from upstream PR
${PR_URL}. Under WSL2, ONNX Runtime reports the GPU with the virtual PCI
vendor id 0x1414 and a bus id the CUDA driver rejects; the fix pairs Microsoft
virtual adapters with visible CUDA devices and falls back to CUDA enumeration
so the EP exposes the real GPU. No other files are modified.
EOF

cat > "$STAGE/build-info.json" <<EOF
{
  "ep": "NvTensorRTRTXExecutionProvider",
  "library": "libonnxruntime_providers_nv_tensorrt_rtx.so",
  "epVersion": "${EP_VERSION}",
  "epSource": "${UPSTREAM_REPO}",
  "epCommit": "${EP_COMMIT}",
  "epBuildRepo": "${EP_REPO}",
  "patch": "${PR_URL}",
  "onnxRuntimeHeaders": "${ORT_VERSION}",
  "tensorrtRtx": "${TRT_RTX_VERSION}",
  "runtimeRequires": "TensorRT-RTX ${TRT_RTX_VERSION} runtime (libtensorrt_rtx.so.1, libtensorrt_onnxparser_rtx.so.1) + CUDA 13; provided by the official wheel (fetch-ep-plugins.sh trt-rtx)",
  "wsl2": true,
  "runpath": "\$ORIGIN"
}
EOF

# Build the archive with fixed entry timestamps, sorted names and a fixed mode
# so a rebuild that changes nothing produces the same bytes (and checksum).
# Uses only the Python standard library (no zip(1) dependency).
python3 - "$WORK/stage" "$PKG_PREFIX" "$OUT_DIR/$ARCHIVE" <<'PYEOF'
import os, sys, zipfile

stage, prefix, out = sys.argv[1], sys.argv[2], sys.argv[3]
root = os.path.join(stage, prefix)
entries = []
for dirpath, _dirnames, filenames in os.walk(root):
    for name in filenames:
        full = os.path.join(dirpath, name)
        entries.append((os.path.relpath(full, stage).replace(os.sep, "/"), full))
entries.sort()

with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for arcname, full in entries:
        info = zipfile.ZipInfo(arcname, date_time=(1980, 1, 1, 0, 0, 0))
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        with open(full, "rb") as fh:
            zf.writestr(info, fh.read())
        print(arcname)
PYEOF

(cd "$OUT_DIR" && sha256sum "$ARCHIVE" > "$ARCHIVE.sha256" && cat "$ARCHIVE.sha256")
python3 -m zipfile -l "$OUT_DIR/$ARCHIVE"

# Install into the local deps layout so `source scripts/env-gpu-deps.sh` picks
# it up (env-gpu-deps.sh resolves TRT_RTX_CUSTOM_EP, then this directory, then
# the stock wheel). Set NO_INSTALL=1 to build the archive only.
DEPS="${DEPS:-$HOME/.local/linml-deps}"
WSL2_OUT="$DEPS/ep-plugins/trt-rtx-wsl2/$PKG_PREFIX"
if [ "${NO_INSTALL:-0}" != "1" ]; then
    rm -rf "$WSL2_OUT"
    mkdir -p "$WSL2_OUT"
    cp -a "$STAGE/." "$WSL2_OUT/"
    echo ""
    echo "Installed: $WSL2_OUT"
fi

echo ""
echo "Next: source scripts/env-gpu-deps.sh"
echo "  linml providers list"
echo "  linml run --provider trt-rtx --model ... --inputs ..."
