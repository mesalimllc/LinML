#!/usr/bin/env bash
# LinML Linux Publish Verification
# Gate E: Verify the published output is complete and runnable.

set -euo pipefail

PUBLISH_DIR="${1:-artifacts/publish/linux-x64}"

echo "=== LinML Publish Verification ==="
echo "Publish directory: $PUBLISH_DIR"
echo ""

# Check 1: Executable exists
if [ ! -x "$PUBLISH_DIR/linml" ]; then
    echo "FAIL: Executable not found or not executable: $PUBLISH_DIR/linml"
    exit 1
fi
echo "PASS: Executable exists: $PUBLISH_DIR/linml"

# Check 2: ldd on executable
echo ""
echo "=== ldd on linml ==="
if ldd "$PUBLISH_DIR/linml" 2>&1 | grep -q "not found"; then
    echo "FAIL: Missing dependencies detected:"
    ldd "$PUBLISH_DIR/linml" | grep "not found"
    exit 1
fi
echo "PASS: No missing dependencies in linml"

# Check 3: ldd on libonnxruntime.so
echo ""
echo "=== ldd on libonnxruntime.so ==="
ORT_LIB=$(find "$PUBLISH_DIR" -name "libonnxruntime.so*" | head -1)
if [ -n "$ORT_LIB" ]; then
    if ldd "$ORT_LIB" 2>&1 | grep -q "not found"; then
        echo "FAIL: Missing dependencies in $ORT_LIB:"
        ldd "$ORT_LIB" | grep "not found"
        exit 1
    fi
    echo "PASS: No missing dependencies in $ORT_LIB"
else
    echo "WARN: libonnxruntime.so not found in publish directory"
fi

# Check 4: linml --version
echo ""
echo "=== linml --version ==="
VERSION_OUTPUT=$("$PUBLISH_DIR/linml" --version 2>&1) || {
    echo "FAIL: linml --version failed"
    echo "$VERSION_OUTPUT"
    exit 1
}
echo "$VERSION_OUTPUT"
echo "PASS: linml --version works"

# Check 5: linml providers list --json
echo ""
echo "=== linml providers list --json ==="
PROVIDERS_OUTPUT=$("$PUBLISH_DIR/linml" providers list --json 2>/dev/null) || {
    echo "FAIL: linml providers list --json failed"
    exit 1
}
echo "$PROVIDERS_OUTPUT" | head -20
echo "PASS: linml providers list --json works"

# Check 6: linml run with fixture (if available)
echo ""
echo "=== linml run (CPU inference) ==="
FIXTURE_MODEL="tests/fixtures/model.onnx"
FIXTURE_INPUTS="tests/fixtures/inputs.json"

if [ -f "$FIXTURE_MODEL" ] && [ -f "$FIXTURE_INPUTS" ]; then
    RUN_OUTPUT=$("$PUBLISH_DIR/linml" run \
        --model "$FIXTURE_MODEL" \
        --inputs "$FIXTURE_INPUTS" \
        --provider cpu \
        --json 2>/dev/null) || {
        echo "FAIL: linml run failed"
        exit 1
    }
    echo "$RUN_OUTPUT" | head -30
    echo "PASS: linml run (CPU) works"
else
    echo "SKIP: Fixture not found, skipping run test"
fi

# Check 7: Native libraries present
echo ""
echo "=== Native libraries ==="
find "$PUBLISH_DIR" -name "*.so" -o -name "*.so.*" | while read -r lib; do
    echo "  $lib"
done

echo ""
echo "=== Verification complete ==="
echo "All checks passed for: $PUBLISH_DIR"
