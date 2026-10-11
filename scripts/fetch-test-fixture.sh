#!/usr/bin/env bash
# LinML Test Fixture Fetcher
# Gate C: Generate or download the deterministic test fixture.
# This script generates the fixture using a C# script (no Python dependency).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
FIXTURE_DIR="$PROJECT_ROOT/tests/fixtures"

echo "=== LinML Fixture Setup ==="
echo "Project root: $PROJECT_ROOT"
echo "Fixture dir: $FIXTURE_DIR"
echo ""

# Check if fixture already exists
if [ -f "$FIXTURE_DIR/model.onnx" ]; then
    echo "Fixture already exists: $FIXTURE_DIR/model.onnx"
    sha256sum "$FIXTURE_DIR/model.onnx"
    exit 0
fi

echo "Generating fixture model..."
echo ""

# Create a temporary C# project to generate the ONNX model
GEN_DIR="$(mktemp -d)"
trap 'rm -rf "$GEN_DIR"' EXIT

cat > "$GEN_DIR/GenerateFixture.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.ML.OnnxRuntime" Version="1.31.0" />
  </ItemGroup>
</Project>
EOF

cat > "$GEN_DIR/Program.cs" << 'CSEOF'
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;

// This is a placeholder. The actual fixture generation will use ONNX protobuf
// or Microsoft.ML.Onnx to create a MatMul+Add model.
// For now, we create a minimal valid ONNX model programmatically.

var outputPath = args[0];

// Create a simple MatMul + Add model using ONNX protobuf
// Model: output = input @ weight + bias
// weight = [[1, 0], [0, 1]] (identity)
// bias = [10, 20]

Console.WriteLine($"Generating fixture model at: {outputPath}");

// TODO: Implement actual ONNX model generation
// This requires either:
// 1. Microsoft.ML.Onnx package (deprecated)
// 2. Google.Protobuf + ONNX proto definitions
// 3. ONNX C# helper library
//
// For Gate C, implement this properly using one of the above approaches.

// Placeholder: write a minimal file
await File.WriteAllTextAsync(outputPath, "PLACEHOLDER: implement fixture generation");
Console.WriteLine("ERROR: Fixture generation not yet implemented. See Gate C step 1.");
Environment.Exit(1);
CSEOF

echo "Building fixture generator..."
cd "$GEN_DIR"
dotnet build -c Release -v quiet 2>&1 | tail -5

echo ""
echo "Running fixture generator..."
dotnet run -c Release --no-build -- "$FIXTURE_DIR/model.onnx" || {
    echo ""
    echo "Fixture generation requires implementation. See Gate C step 1 in the plan."
    echo "The generator project is a placeholder — implement ONNX model creation using:"
    echo "  - Google.Protobuf + ONNX proto definitions, or"
    echo "  - ONNX C# helper library, or"
    echo "  - Direct protobuf serialization"
    exit 1
}

# Compute checksum
if [ -f "$FIXTURE_DIR/model.onnx" ]; then
    echo ""
    echo "=== Fixture generated ==="
    sha256sum "$FIXTURE_DIR/model.onnx"
    ls -la "$FIXTURE_DIR/model.onnx"

    # Update manifest with checksum
    SHA256=$(sha256sum "$FIXTURE_DIR/model.onnx" | cut -d' ' -f1)
    SIZE=$(stat -c%s "$FIXTURE_DIR/model.onnx" 2>/dev/null || stat -f%z "$FIXTURE_DIR/model.onnx")

    echo ""
    echo "Update tests/fixtures/fixture-manifest.json with:"
    echo "  sha256: $SHA256"
    echo "  sizeBytes: $SIZE"
fi
