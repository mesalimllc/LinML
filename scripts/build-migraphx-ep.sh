#!/usr/bin/env bash
# Build AMD's MIGraphX plugin execution provider (onnxruntime-ep-amdgpu) for
# linux-x64 and package it as the zip archive LinML.Catalog downloads.
#
# Derived from AMD's scripts/build_migraphx_ep_from_packages.sh (MIT): nothing
# is compiled except the EP itself. Every input is pinned and sha256-checked:
#   - the EP source at a fixed AMD release-branch commit,
#   - the official ONNX Runtime release tarball (headers + import lib only),
#   - AMD's prebuilt migraphx_libs wheel (link-time libmigraphx_c.so.3),
#   - the MIGraphX public C API headers at the matching AMDMIGraphX tag.
# No GPU is needed to build. At run time the plugin needs ROCm and
# MIGraphX >= 2.17 on the host; its RUNPATH is $ORIGIN:/opt/rocm/lib.
#
# Intended to run as root inside rocm/dev-ubuntu-24.04:10.0.0-full (it needs
# ROCm's HIP CMake package), e.g.:
#   docker run --rm -v "$PWD:/w" -w /w rocm/dev-ubuntu-24.04:10.0.0-full \
#       bash scripts/build-migraphx-ep.sh
#
# Output (OUT_DIR, default ./out/migraphx-ep):
#   linml-migraphx-ep-<EP_VERSION>-linux-x64.zip         archive for the catalog
#   linml-migraphx-ep-<EP_VERSION>-linux-x64.zip.sha256  its checksum

set -euo pipefail

# --- Pins ---------------------------------------------------------------------
# Bump together: the EP release branch names the MIGraphX version it targets.

EP_VERSION="2611.1"
EP_REPO="https://github.com/onnxruntime/onnxruntime-ep-amdgpu.git"
EP_COMMIT="a8ffd757e1e042ab368d579518ec0264f8a86fc0" # gpuep-releases/gpuep-rel-2611.1

ORT_VERSION="1.31.0"
ORT_URL="https://github.com/microsoft/onnxruntime/releases/download/v${ORT_VERSION}/onnxruntime-linux-x64-${ORT_VERSION}.tgz"
ORT_SHA256="cc5c72baf5ae5c8238a6841f0897227be2d02826b9cf98eaf02fdefaeeb03a57"

MIGRAPHX_VERSION="2.17.0+rocm10.0.0"
MIGRAPHX_LIBS_URL="https://stable.repo.amd.com/rocm/migraphx/whl-next/migraphx-libs/migraphx_libs-2.17.0%2Brocm10.0.0-py3-none-manylinux_2_28_x86_64.whl"
MIGRAPHX_LIBS_SHA256="70701181e680071f261b4efa04d671aec4aa43f3cb871b4ba98846d4d4c58622"

MIGRAPHX_HEADERS_COMMIT="becdb3da862f2297041b746b90bc6130e2b1d1f7" # AMDMIGraphX tag rocm-10.0 (2.17.0)
MIGRAPHX_H_SHA256="1026dc365e65a8133bf19c78a902d313b66d61f56311f7d20f5b09070b6c9f6d"
MIGRAPHX_HPP_SHA256="beedef15f010ca9c4d48e3e862165ffc43e0b0772ff8f28eb48236a52c72ff46"

CMAKE_VERSION="4.2.3"

# --- Paths --------------------------------------------------------------------

ROCM_PATH="${ROCM_PATH:-/opt/rocm}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${OUT_DIR:-$REPO_ROOT/out/migraphx-ep}"
WORK="${WORK:-/tmp/linml-migraphx-ep}"
EP_SRC="$WORK/onnxruntime-ep-amdgpu"
MGX_HOME="$WORK/migraphx"
ORT_HOME="$WORK/onnxruntime"
PKG_PREFIX="linml_migraphx_ep"
ARCHIVE="linml-migraphx-ep-${EP_VERSION}-linux-x64.zip"

fetch_verified() { # url dest sha256
    curl -fsSL --retry 3 "$1" -o "$2"
    echo "$3  $2" | sha256sum -c -
}

if [ ! -f "$ROCM_PATH/lib/cmake/hip/hip-config.cmake" ]; then
    echo "ERROR: ROCm HIP CMake package not found under $ROCM_PATH (run inside rocm/dev-ubuntu-24.04:10.0.0-full)." >&2
    exit 1
fi

rm -rf "$WORK"
mkdir -p "$WORK" "$OUT_DIR"

# --- Toolchain ------------------------------------------------------------------

export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y --no-install-recommends \
    ca-certificates curl git patchelf python3 python3-venv unzip zip binutils

python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install --no-cache-dir "cmake==${CMAKE_VERSION}" ninja "packaging>=24.2"
export PATH="$WORK/venv/bin:$PATH"
export CXXFLAGS="-D__HIP_PLATFORM_AMD__=1 -w"

# --- EP source --------------------------------------------------------------------

git init -q "$EP_SRC"
git -C "$EP_SRC" remote add origin "$EP_REPO"
git -C "$EP_SRC" fetch -q --depth 1 origin "$EP_COMMIT"
git -C "$EP_SRC" checkout -q FETCH_HEAD
test "$(git -C "$EP_SRC" rev-parse HEAD)" = "$EP_COMMIT"

# --- MIGraphX: synthetic CMake prefix around the prebuilt wheel ---------------------
# The wheel ships only runtime libraries; headers come from the AMDMIGraphX tag
# and config.h/version.h/export.h/migraphx-config.cmake stand in for the files
# MIGraphX's own CMake build would generate (as AMD's script does).

mkdir -p "$MGX_HOME/include/migraphx/api" "$MGX_HOME/lib/cmake/migraphx" "$WORK/wheel"
fetch_verified "$MIGRAPHX_LIBS_URL" "$WORK/migraphx_libs.whl" "$MIGRAPHX_LIBS_SHA256"
unzip -q "$WORK/migraphx_libs.whl" 'migraphx_libs/*.so*' -d "$WORK/wheel"
cp -a "$WORK/wheel/migraphx_libs/." "$MGX_HOME/lib/"
ln -sf libmigraphx_c.so.3 "$MGX_HOME/lib/libmigraphx_c.so"

HDR_BASE="https://raw.githubusercontent.com/ROCm/AMDMIGraphX/${MIGRAPHX_HEADERS_COMMIT}/src/api/include/migraphx"
fetch_verified "$HDR_BASE/migraphx.h" "$MGX_HOME/include/migraphx/migraphx.h" "$MIGRAPHX_H_SHA256"
fetch_verified "$HDR_BASE/migraphx.hpp" "$MGX_HOME/include/migraphx/migraphx.hpp" "$MIGRAPHX_HPP_SHA256"

cat > "$MGX_HOME/include/migraphx/config.h" <<'EOF'
#ifndef MIGRAPHX_GUARD_CONFIG_H
#define MIGRAPHX_GUARD_CONFIG_H
#define MIGRAPHX_ENABLE_ONNX
#define MIGRAPHX_ENABLE_TENSORFLOW
#endif
EOF

MGX_CORE="${MIGRAPHX_VERSION%%+*}"
IFS=. read -r MGX_MAJOR MGX_MINOR MGX_PATCH <<<"$MGX_CORE"
cat > "$MGX_HOME/include/migraphx/version.h" <<EOF
#define MIGRAPHX_VERSION_MAJOR ${MGX_MAJOR}
#define MIGRAPHX_VERSION_MINOR ${MGX_MINOR}
#define MIGRAPHX_VERSION_PATCH ${MGX_PATCH}
#define MIGRAPHX_VERSION_TWEAK "${MIGRAPHX_VERSION#*+}"
#define MIGRAPHX_SO_MAJOR_VERSION (${MGX_MAJOR} * 1000 * 1000 + ${MGX_MINOR} * 1000 + ${MGX_PATCH})
EOF

cat > "$MGX_HOME/include/migraphx/api/export.h" <<'EOF'
#ifndef MIGRAPHX_API_EXPORT_H
#define MIGRAPHX_API_EXPORT_H
#define MIGRAPHX_C_EXPORT __attribute__((visibility("default")))
#define MIGRAPHX_C_NO_EXPORT __attribute__((visibility("hidden")))
#define MIGRAPHX_C_DEPRECATED __attribute__((__deprecated__))
#define MIGRAPHX_C_DEPRECATED_EXPORT MIGRAPHX_C_EXPORT MIGRAPHX_C_DEPRECATED
#define MIGRAPHX_C_DEPRECATED_NO_EXPORT MIGRAPHX_C_NO_EXPORT MIGRAPHX_C_DEPRECATED
#endif
EOF

cat > "$MGX_HOME/lib/cmake/migraphx/migraphx-config.cmake" <<EOF
if(NOT TARGET migraphx::c)
    add_library(migraphx::c SHARED IMPORTED)
    set_target_properties(migraphx::c PROPERTIES
        IMPORTED_LOCATION "$MGX_HOME/lib/libmigraphx_c.so.3"
        IMPORTED_SONAME "libmigraphx_c.so.3"
        INTERFACE_INCLUDE_DIRECTORIES "$MGX_HOME/include")
endif()
EOF

# --- ONNX Runtime headers / import library ------------------------------------------

fetch_verified "$ORT_URL" "$WORK/ort.tgz" "$ORT_SHA256"
mkdir -p "$ORT_HOME"
tar -xzf "$WORK/ort.tgz" -C "$ORT_HOME" --strip-components=1
# Release tarballs' CMake targets point at lib64/ and include/onnxruntime/
# (onnxruntime#22267, #23642); add the aliases they expect.
[ -e "$ORT_HOME/lib64" ] || ln -s lib "$ORT_HOME/lib64"
if [ ! -e "$ORT_HOME/include/onnxruntime" ]; then
    mkdir -p "$ORT_HOME/include/onnxruntime"
    for h in "$ORT_HOME"/include/*.h; do ln -sf "../$(basename "$h")" "$ORT_HOME/include/onnxruntime/"; done
fi

# --- Build ------------------------------------------------------------------------

cd "$EP_SRC"
./build.sh --config Release \
    --cmake_generator Ninja \
    --onnxrt_home "$ORT_HOME" \
    --use_migraphx \
    --migraphx_home "$MGX_HOME" \
    --compile_no_warning_as_error \
    --parallel "$(nproc)" \
    --build_dir build.EP.MGX \
    --hip_path "$ROCM_PATH"

LIB="$(find "$EP_SRC/build.EP.MGX/Release" -name libmigraphx-ep.so -type f | head -n1)"
if [ -z "$LIB" ]; then
    echo "ERROR: libmigraphx-ep.so was not produced." >&2
    exit 1
fi

# --- Package ----------------------------------------------------------------------

STAGE="$WORK/stage/$PKG_PREFIX"
mkdir -p "$STAGE/licenses"
cp "$LIB" "$STAGE/libmigraphx-ep.so"
# The build-tree RUNPATH points at CI-only paths; ship one that finds a system
# ROCm/MIGraphX install.
patchelf --set-rpath '$ORIGIN:/opt/rocm/lib' "$STAGE/libmigraphx-ep.so"
strip --strip-unneeded "$STAGE/libmigraphx-ep.so"

echo "--- dynamic section"
readelf -d "$STAGE/libmigraphx-ep.so" | grep -E 'NEEDED|RUNPATH|RPATH'
for sym in CreateEpFactories ReleaseEpFactory; do
    nm -D --defined-only "$STAGE/libmigraphx-ep.so" | grep -qw "$sym" \
        || { echo "ERROR: $sym is not exported." >&2; exit 1; }
done

cp "$EP_SRC/LICENSE" "$STAGE/licenses/LICENSE"
if [ -d "$EP_SRC/LICENSES" ]; then
    cp "$EP_SRC"/LICENSES/* "$STAGE/licenses/"
fi

cat > "$STAGE/build-info.json" <<EOF
{
  "ep": "MIGraphXExecutionProvider",
  "library": "libmigraphx-ep.so",
  "epVersion": "${EP_VERSION}",
  "epSource": "${EP_REPO}",
  "epCommit": "${EP_COMMIT}",
  "onnxRuntimeHeaders": "${ORT_VERSION}",
  "migraphx": "${MIGRAPHX_VERSION}",
  "runtimeRequires": "ROCm with MIGraphX >= ${MGX_CORE} (libmigraphx_c.so.3)",
  "runpath": "\$ORIGIN:/opt/rocm/lib"
}
EOF

# Fixed timestamps and sorted entries keep the archive byte-stable for a given
# library, so a rebuild that changes nothing produces the same checksum.
find "$WORK/stage" -exec touch -h -d '2000-01-01T00:00:00Z' {} +
(cd "$WORK/stage" && find "$PKG_PREFIX" -type f | LC_ALL=C sort | TZ=UTC zip -X -D -q "$OUT_DIR/$ARCHIVE" -@)
(cd "$OUT_DIR" && sha256sum "$ARCHIVE" > "$ARCHIVE.sha256" && cat "$ARCHIVE.sha256")
unzip -l "$OUT_DIR/$ARCHIVE"
