#!/usr/bin/env bash
# LinML WSL2 Setup Script
# Gate A prerequisite: Install .NET 10 SDK in WSL2 and verify GPU visibility.
# Run this inside WSL2 Ubuntu: bash scripts/setup-wsl.sh

set -euo pipefail

echo "=== LinML WSL2 Setup ==="
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "Host: $(uname -a)"
echo ""

# Check if running in WSL
if ! grep -qi microsoft /proc/version 2>/dev/null; then
    echo "WARNING: This does not appear to be WSL. Proceeding anyway..."
fi

# 1. Install .NET 10 SDK
echo "=== Installing .NET 10 SDK ==="
if command -v dotnet &> /dev/null; then
    echo "dotnet already installed: $(dotnet --version)"
else
    echo "Downloading dotnet-install.sh..."
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    echo "Installing .NET 10 SDK..."
    /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"

    # Add to PATH for current session
    export PATH="$HOME/.dotnet:$PATH"

    # Add to shell profile for future sessions
    if ! grep -q '.dotnet' "$HOME/.bashrc" 2>/dev/null; then
        echo 'export PATH="$HOME/.dotnet:$PATH"' >> "$HOME/.bashrc"
        echo "Added .dotnet to PATH in ~/.bashrc"
    fi

    echo "dotnet installed: $(dotnet --version)"
fi

echo ""
echo "=== Verifying .NET SDKs ==="
dotnet --list-sdks

echo ""
echo "=== Verifying GPU visibility ==="
if command -v nvidia-smi &> /dev/null; then
    nvidia-smi
else
    echo "WARNING: nvidia-smi not found. GPU may not be available in WSL."
    echo "Ensure NVIDIA drivers are installed on Windows host."
fi

echo ""
echo "=== Verifying CUDA toolkits ==="
for cuda_dir in /usr/local/cuda*; do
    if [ -d "$cuda_dir" ]; then
        echo "Found: $cuda_dir"
        if [ -f "$cuda_dir/version.json" ]; then
            cat "$cuda_dir/version.json" | head -5
        fi
    fi
done

echo ""
echo "=== Checking CUDA libraries ==="
ls -la /usr/local/cuda/lib64/libcudart.so* 2>/dev/null || echo "No libcudart found in /usr/local/cuda/lib64"

echo ""
echo "=== Environment summary ==="
echo "OS: $(lsb_release -d 2>/dev/null || cat /etc/os-release | head -1)"
echo "Kernel: $(uname -r)"
echo "glibc: $(ldd --version | head -1)"
echo "Architecture: $(uname -m)"
echo ".NET SDK: $(dotnet --version 2>/dev/null || echo 'not found')"

echo ""
echo "=== Setup complete ==="
echo "Next steps:"
echo "  1. cd to the LinML project directory"
echo "  2. dotnet restore LinML.slnx"
echo "  3. dotnet build LinML.slnx -c Release"
echo "  4. dotnet test LinML.slnx -c Release"
