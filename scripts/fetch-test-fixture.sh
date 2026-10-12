#!/usr/bin/env bash
# LinML Test Fixture Fetcher
# Gate C: Generate the deterministic test fixture (MatMul+Add, opset 17).
#
# Uses an embedded BCL-only C# generator (minimal protobuf encoding) so there is
# no Python dependency and no NuGet download beyond the .NET SDK itself.
# The generated model is committed at tests/fixtures/model.onnx; this script is
# the reproducible record of how it was built.
#
# Usage: bash scripts/fetch-test-fixture.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
FIXTURE_DIR="$PROJECT_ROOT/tests/fixtures"
MANIFEST="$FIXTURE_DIR/fixture-manifest.json"

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

command -v dotnet >/dev/null 2>&1 || {
    echo "ERROR: dotnet SDK not found. Run scripts/setup-wsl.sh first." >&2
    exit 1
}

echo "Generating fixture model (MatMul+Add, opset 17)..."
echo ""

# Create a temporary C# project to generate the ONNX model.
# Pure BCL: hand-rolled protobuf encoding, zero package references.
GEN_DIR="$(mktemp -d)"
trap 'rm -rf "$GEN_DIR"' EXIT

cat > "$GEN_DIR/GenFixture.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
EOF

cat > "$GEN_DIR/Program.cs" << 'CSEOF'
// LinML fixture generator: emits model.onnx (MatMul+Add, opset 17, IR 10)
// using only BCL protobuf encoding. No external packages, no Python.
// Model: output = x @ weight + bias, weight=I(2x2), bias=[10,20].
// input/output: float32 [3,2].
using System.Buffers.Binary;
using System.Security.Cryptography;

var outputPath = args.Length > 0 ? args[0] : throw new InvalidOperationException("usage: GenFixture <output.onnx>");

var graph = Concat(
    Msg(1, Node(["x", "weight"], ["mm_out"], "MatMul", "MatMul_0")),
    Msg(1, Node(["mm_out", "bias"], ["y"], "Add", "Add_1")),
    Str(2, "matmul-add-tiny"),
    Msg(5, Tensor("weight", [2, 2], [1, 0, 0, 1])),
    Msg(5, Tensor("bias", [2], [10, 20])),
    Msg(11, ValueInfo("x", [3, 2])),
    Msg(12, ValueInfo("y", [3, 2])));

var model = Concat(
    Varint(1, 10),
    Str(2, "LinML-fixture-gen"),
    Str(3, "1.0"),
    Msg(7, graph),
    Msg(8, Concat(Str(1, ""), Varint(2, 17))));

await File.WriteAllBytesAsync(outputPath, model);

var bytes = await File.ReadAllBytesAsync(outputPath);
var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
Console.WriteLine($"Wrote {outputPath} ({bytes.Length} bytes, sha256={sha})");

byte[] Node(string[] inputs, string[] outputs, string opType, string name)
{
    var parts = new List<byte[]>();
    foreach (var i in inputs) parts.Add(Str(1, i));
    foreach (var o in outputs) parts.Add(Str(2, o));
    parts.Add(Str(3, name));
    parts.Add(Str(4, opType));
    return Concat([.. parts]);
}

byte[] Tensor(string name, long[] dims, float[] floats)
{
    return Concat(
        PackedVarints(1, dims),
        Varint(2, 1),
        PackedFloats(4, floats),
        Str(8, name));
}

byte[] ValueInfo(string name, long[] dims)
{
    var dimMsgs = new List<byte[]>();
    foreach (var d in dims) dimMsgs.Add(Msg(1, Varint(1, (ulong)d)));
    var shape = Msg(2, Concat([.. dimMsgs]));
    var tensorType = Msg(1, Concat(Varint(1, 1), shape));
    return Concat(Str(1, name), Msg(2, tensorType));
}

byte[] Varint(int field, ulong value)
{
    using var ms = new MemoryStream();
    WriteVarint(ms, ((ulong)field << 3) | 0);
    WriteVarint(ms, value);
    return ms.ToArray();
}

byte[] Str(int field, string value)
{
    var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
    return Concat(Tag(field, 2), RawVarint((ulong)utf8.Length), utf8);
}

byte[] Msg(int field, byte[] body) => Concat(Tag(field, 2), RawVarint((ulong)body.Length), body);

byte[] PackedFloats(int field, float[] values)
{
    using var ms = new MemoryStream();
    Span<byte> b = stackalloc byte[4];
    foreach (var v in values)
    {
        BinaryPrimitives.WriteSingleLittleEndian(b, v);
        ms.Write(b);
    }
    return Concat(Tag(field, 2), RawVarint((ulong)ms.Length), ms.ToArray());
}

byte[] PackedVarints(int field, long[] values)
{
    using var ms = new MemoryStream();
    foreach (var v in values) WriteVarint(ms, (ulong)v);
    return Concat(Tag(field, 2), RawVarint((ulong)ms.Length), ms.ToArray());
}

byte[] Tag(int field, int wire) => RawVarint(((ulong)field << 3) | (ulong)wire);

byte[] RawVarint(ulong value)
{
    using var ms = new MemoryStream();
    WriteVarint(ms, value);
    return ms.ToArray();
}

void WriteVarint(Stream s, ulong value)
{
    while (value >= 0x80) { s.WriteByte((byte)(value | 0x80)); value >>= 7; }
    s.WriteByte((byte)value);
}

byte[] Concat(params byte[][] parts)
{
    using var ms = new MemoryStream();
    foreach (var p in parts) ms.Write(p, 0, p.Length);
    return ms.ToArray();
}
CSEOF

echo "Building fixture generator (BCL-only, no package downloads)..."
dotnet build "$GEN_DIR/GenFixture.csproj" -c Release -v quiet --nologo

echo ""
echo "Running fixture generator..."
dotnet "$GEN_DIR/bin/Release/net10.0/GenFixture.dll" "$FIXTURE_DIR/model.onnx"

# Compute checksum and update the manifest
if [ -f "$FIXTURE_DIR/model.onnx" ]; then
    echo ""
    echo "=== Fixture generated ==="
    sha256sum "$FIXTURE_DIR/model.onnx"
    ls -la "$FIXTURE_DIR/model.onnx"

    SHA256=$(sha256sum "$FIXTURE_DIR/model.onnx" | cut -d' ' -f1)
    SIZE=$(stat -c%s "$FIXTURE_DIR/model.onnx")

    if command -v python3 >/dev/null 2>&1; then
        SHA256="$SHA256" SIZE="$SIZE" python3 - "$MANIFEST" << 'PYEOF'
# Targeted in-place patch: preserves all other manifest formatting.
import os, re, sys
path = sys.argv[1]
with open(path) as f:
    text = f.read()
text = re.sub(r'"sha256": "[^"]*"', f'"sha256": "{os.environ["SHA256"]}"', text)
text = re.sub(r'"sizeBytes": \d+', f'"sizeBytes": {os.environ["SIZE"]}', text)
text = re.sub(
    r'"generatedBy": "[^"]*"',
    '"generatedBy": "bash scripts/fetch-test-fixture.sh (embeds a BCL-only C# generator; MatMul+Add, opset 17, IR 10)"',
    text)
with open(path, "w") as f:
    f.write(text)
print(f"Updated {path}: sha256={os.environ['SHA256']} sizeBytes={os.environ['SIZE']}")
PYEOF
    else
        echo ""
        echo "python3 not found; update $MANIFEST manually with:"
        echo "  sha256: $SHA256"
        echo "  sizeBytes: $SIZE"
    fi

    echo ""
    echo "Verify with:"
    echo "  dotnet test tests/LinML.Integration.Tests -c Release --filter CpuInference"
fi
