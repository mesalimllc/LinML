#!/usr/bin/env bash
# LinML CLI Installer
# Installs pre-built LinML CLI binary for Linux x86_64.
#
# Quick install:
#   curl -fsSL https://raw.githubusercontent.com/trackdubllc/LinML/main/scripts/install.sh | bash
#
# With GPU dependencies:
#   curl -fsSL https://raw.githubusercontent.com/trackdubllc/LinML/main/scripts/install.sh | bash -s -- --gpu
#
# Custom options:
#   bash scripts/install.sh --version v0.1.0 --bin-dir /usr/local/bin

set -euo pipefail

REPO="${LINML_REPO:-trackdubllc/LinML}"
VERSION="${LINML_VERSION:-}"
INSTALL_DIR="${LINML_INSTALL_DIR:-$HOME/.local/share/linml}"
BIN_DIR="${LINML_BIN_DIR:-$HOME/.local/bin}"
SETUP_GPU=false

usage() {
    cat <<EOF
LinML CLI Installer

Usage:
  install.sh [options]

Options:
  -v, --version <ver>   Release tag or version to install (default: latest release)
  -f, --file <path>     Install from a local archive instead of downloading
  -t, --target <dir>    Directory to install LinML files to (default: ~/.local/share/linml)
  -b, --bin-dir <dir>   Directory to create the 'linml' symlink in (default: ~/.local/bin)
  -r, --repo <owner/repo> GitHub repository (default: trackdubllc/LinML)
      --gpu             Run GPU setup (cuDNN, TensorRT, plugin EPs) after installing
  -h, --help            Show this help message

Environment variables:
  LINML_VERSION         Version to install
  LINML_ARCHIVE_FILE    Local archive file to install from
  LINML_INSTALL_DIR     Installation directory
  LINML_BIN_DIR         Binary symlink directory
  LINML_REPO            GitHub repository
EOF
    exit 0
}

ARCHIVE_FILE="${LINML_ARCHIVE_FILE:-}"

while [ $# -gt 0 ]; do
    case "$1" in
        -v|--version)
            VERSION="$2"
            shift 2
            ;;
        -f|--file)
            ARCHIVE_FILE="$2"
            shift 2
            ;;
        -t|--target)
            INSTALL_DIR="$2"
            shift 2
            ;;
        -b|--bin-dir)
            BIN_DIR="$2"
            shift 2
            ;;
        -r|--repo)
            REPO="$2"
            shift 2
            ;;
        --gpu)
            SETUP_GPU=true
            shift
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            ;;
    esac
done

# Architecture & OS checks
OS="$(uname -s)"
ARCH="$(uname -m)"

if [ "$OS" != "Linux" ]; then
    echo "Error: LinML currently only supports Linux (detected: $OS)." >&2
    exit 1
fi

if [ "$ARCH" != "x86_64" ]; then
    echo "Error: LinML currently only supports x86_64 architecture (detected: $ARCH)." >&2
    exit 1
fi

has_cmd() {
    command -v "$1" >/dev/null 2>&1
}

download_file() {
    local url="$1"
    local dest="$2"
    if has_cmd curl; then
        curl -fsSL "$url" -o "$dest"
    elif has_cmd wget; then
        wget -qO "$dest" "$url"
    else
        echo "Error: Neither curl nor wget was found. Please install one of them." >&2
        exit 1
    fi
}

echo "=== LinML CLI Installer ==="

TMP_DIR="$(mktemp -d)"
cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

if [ -n "$ARCHIVE_FILE" ]; then
    if [ ! -f "$ARCHIVE_FILE" ]; then
        echo "Error: Specified archive file '$ARCHIVE_FILE' not found." >&2
        exit 1
    fi
    echo "Using local archive: $ARCHIVE_FILE"
    ARCHIVE_PATH="$ARCHIVE_FILE"
else
    # Resolve version
    if [ -z "$VERSION" ] || [ "$VERSION" = "latest" ]; then
        echo "Resolving latest release from github.com/${REPO}..."
        TAG=""
        if has_cmd curl; then
            LOCATION="$(curl -fsSLI -o /dev/null -w "%{url_effective}" "https://github.com/${REPO}/releases/latest" 2>/dev/null || true)"
            TAG="$(echo "$LOCATION" | sed 's|.*/tag/||' | tr -d '\r\n')"
        fi

        if [ -z "$TAG" ] || [ "$TAG" = "latest" ]; then
            if has_cmd curl; then
                TAG="$(curl -s "https://api.github.com/repos/${REPO}/releases/latest" 2>/dev/null | grep -m1 '"tag_name":' | cut -d '"' -f 4 || true)"
            fi
        fi

        if [ -z "$TAG" ]; then
            echo "Error: Could not determine latest release version. Specify with --version <vX.Y.Z> or provide --file <archive>." >&2
            exit 1
        fi
        VERSION="$TAG"
    fi

    # Normalize tag and version numbers
    case "$VERSION" in
        v*) TAG="$VERSION"; VER_NUM="${VERSION#v}" ;;
        *)  TAG="v$VERSION"; VER_NUM="$VERSION" ;;
    esac

    echo "Target version: $TAG ($VER_NUM)"
    BASE_URL="https://github.com/${REPO}/releases/download/${TAG}"
    ARCHIVE_NAME="linml-${VER_NUM}-linux-x64.tar.gz"
    GENERIC_ARCHIVE="linml-linux-x64.tar.gz"

    echo "Downloading ${ARCHIVE_NAME}..."
    if ! download_file "${BASE_URL}/${ARCHIVE_NAME}" "${TMP_DIR}/${ARCHIVE_NAME}" 2>/dev/null; then
        echo "Versioned archive not found, trying ${GENERIC_ARCHIVE}..."
        if ! download_file "${BASE_URL}/${GENERIC_ARCHIVE}" "${TMP_DIR}/${ARCHIVE_NAME}"; then
            echo "Error: Failed to download release archive from ${BASE_URL}." >&2
            exit 1
        fi
    fi
    ARCHIVE_PATH="${TMP_DIR}/${ARCHIVE_NAME}"

    # Download checksums if available
    CHECKSUM_URL="${BASE_URL}/SHA256SUMS.txt"
    if download_file "$CHECKSUM_URL" "${TMP_DIR}/SHA256SUMS.txt" 2>/dev/null; then
        echo "Verifying SHA256 checksum..."
        EXPECTED_HASH="$(grep -E "(linml-${VER_NUM}-linux-x64\.tar\.gz|linml-linux-x64\.tar\.gz)" "${TMP_DIR}/SHA256SUMS.txt" | head -1 | awk '{print $1}' || true)"
        if [ -n "$EXPECTED_HASH" ]; then
            ACTUAL_HASH="$(sha256sum "$ARCHIVE_PATH" | awk '{print $1}')"
            if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
                echo "Error: Checksum verification failed!" >&2
                echo "Expected: $EXPECTED_HASH" >&2
                echo "Actual:   $ACTUAL_HASH" >&2
                exit 1
            fi
            echo "Checksum verified: $ACTUAL_HASH"
        fi
    fi
fi

# Extract
echo "Extracting LinML to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
# Clear existing binaries in target directory to avoid stale files
rm -rf "${INSTALL_DIR:?}"/*

tar -xzf "$ARCHIVE_PATH" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/linml"

# Create symlink
mkdir -p "$BIN_DIR"
ln -sf "$INSTALL_DIR/linml" "$BIN_DIR/linml"

# Verify execution
echo "Verifying installation..."
INSTALLED_VER="$("$BIN_DIR/linml" --version 2>&1)" || {
    echo "Error: $BIN_DIR/linml failed to run." >&2
    exit 1
}

echo ""
echo "=== LinML installed successfully! ==="
echo "Version: $INSTALLED_VER"
echo "Binary:  $BIN_DIR/linml"
echo ""

# Check PATH
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *)
        echo "NOTE: $BIN_DIR is not currently in your PATH."
        echo "Add it to your shell configuration (e.g. ~/.bashrc or ~/.zshrc):"
        echo ""
        echo "  export PATH=\"$BIN_DIR:\$PATH\""
        echo ""
        ;;
esac

# GPU setup
if [ "$SETUP_GPU" = true ]; then
    echo "=== Running GPU dependency setup ==="
    if [ -f "$INSTALL_DIR/scripts/setup-gpu-deps.sh" ]; then
        bash "$INSTALL_DIR/scripts/setup-gpu-deps.sh"
        bash "$INSTALL_DIR/scripts/fetch-ep-plugins.sh"
        echo ""
        echo "GPU dependencies installed."
        echo "Remember to run: source $INSTALL_DIR/scripts/env-gpu-deps.sh"
    fi
elif command -v nvidia-smi >/dev/null 2>&1; then
    echo "Tip: NVIDIA GPU detected. For CUDA/TensorRT acceleration, run:"
    echo "  bash $INSTALL_DIR/scripts/setup-gpu-deps.sh"
    echo "  bash $INSTALL_DIR/scripts/fetch-ep-plugins.sh"
    echo "  source $INSTALL_DIR/scripts/env-gpu-deps.sh"
fi

echo "To test providers:"
echo "  linml providers list"
echo "  linml doctor"
