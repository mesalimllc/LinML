from __future__ import annotations

import json
import os
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List, Optional, Union

from .binary import get_linml_binary, run_linml_cli
from .models import InferenceResult, ModelInspectionReport, TensorSpec


def inspect(model_path: Union[str, Path]) -> ModelInspectionReport:
    """Inspect ONNX model graph structure, inputs/outputs, and EP compatibility."""
    model_str = str(Path(model_path).resolve())
    proc = run_linml_cli(["inspect", "--model", model_str, "--json"])
    data = json.loads(proc.stdout)
    return ModelInspectionReport.from_dict(data)


def run(
    model_path: Union[str, Path],
    inputs: Union[str, Path, Dict[str, Any], List[Union[TensorSpec, Dict[str, Any]]]],
    provider: Optional[str] = None,
    require_provider: bool = False,
    profile: bool = False,
    ep_options: Optional[Dict[str, str]] = None,
) -> InferenceResult:
    """Execute one-shot inference using the linml CLI."""
    model_str = str(Path(model_path).resolve())
    temp_inputs_file = None

    if isinstance(inputs, (str, Path)):
        inputs_path = str(Path(inputs).resolve())
    else:
        # Convert in-memory inputs to temporary JSON file
        tensor_specs: List[Dict[str, Any]] = []
        if isinstance(inputs, dict):
            for name, val in inputs.items():
                if isinstance(val, TensorSpec):
                    tensor_specs.append(val.to_dict())
                else:
                    spec = TensorSpec.from_any(name=name, value=val)
                    tensor_specs.append(spec.to_dict())
        elif isinstance(inputs, list):
            for item in inputs:
                if isinstance(item, TensorSpec):
                    tensor_specs.append(item.to_dict())
                elif isinstance(item, dict):
                    tensor_specs.append(item)
                else:
                    raise ValueError(f"Unsupported input item: {type(item)}")
        else:
            raise ValueError("Invalid inputs parameter.")

        temp_inputs_file = tempfile.NamedTemporaryFile(
            mode="w", suffix=".json", delete=False, encoding="utf-8"
        )
        json.dump({"tensors": tensor_specs}, temp_inputs_file)
        temp_inputs_file.close()
        inputs_path = temp_inputs_file.name

    try:
        args = ["run", "--model", model_str, "--inputs", inputs_path, "--json"]
        if provider:
            args.extend(["--provider", provider])
        if require_provider:
            args.append("--require-provider")
        if profile:
            args.append("--profile")
        if ep_options:
            for k, v in ep_options.items():
                args.extend(["--ep-option", f"{k}={v}"])

        proc = run_linml_cli(args)
        data = json.loads(proc.stdout)
        return InferenceResult.from_dict(data)
    finally:
        if temp_inputs_file and os.path.exists(temp_inputs_file.name):
            try:
                os.remove(temp_inputs_file.name)
            except OSError:
                pass


def doctor(
    model: Optional[Union[str, Path]] = None,
    inputs: Optional[Union[str, Path]] = None,
) -> Dict[str, Any]:
    """Run linml doctor diagnostic probe."""
    args = ["doctor", "--json"]
    if model:
        args.extend(["--model", str(Path(model).resolve())])
    if inputs:
        args.extend(["--inputs", str(Path(inputs).resolve())])
    proc = run_linml_cli(args)
    return json.loads(proc.stdout)


def providers_list() -> Dict[str, Any]:
    """Query available execution providers and detected hardware."""
    proc = run_linml_cli(["providers", "list", "--json"])
    return json.loads(proc.stdout)


def setup(
    dry_run: bool = False,
    all_plugins: bool = False,
    force: bool = False,
    root: Optional[Union[str, Path]] = None,
) -> Dict[str, Any]:
    """Run hardware-aware auto-setup."""
    args = ["setup", "--json"]
    if dry_run:
        args.append("--dry-run")
    if all_plugins:
        args.append("--all")
    if force:
        args.append("--force")
    if root:
        args.extend(["--root", str(Path(root).resolve())])
    proc = run_linml_cli(args)
    return json.loads(proc.stdout)


def bench(
    model_path: Union[str, Path],
    inputs_path: Union[str, Path],
    provider: Optional[str] = None,
    all_ready: bool = False,
    warmup: int = 5,
    iterations: int = 50,
) -> Dict[str, Any]:
    """Run inference benchmarks across execution providers."""
    args = [
        "bench",
        "--model", str(Path(model_path).resolve()),
        "--inputs", str(Path(inputs_path).resolve()),
        "--warmup", str(warmup),
        "--iterations", str(iterations),
        "--json",
    ]
    if provider:
        args.extend(["--provider", provider])
    if all_ready:
        args.append("--all-ready")
    proc = run_linml_cli(args)
    return json.loads(proc.stdout)


def main():
    """Forward CLI invocations to the underlying linml binary."""
    import subprocess
    try:
        base_cmd = get_linml_binary()
    except Exception as e:
        sys.stderr.write(f"Error: {e}\n")
        sys.exit(1)

    full_cmd = base_cmd + sys.argv[1:]
    try:
        res = subprocess.run(full_cmd)
        sys.exit(res.returncode)
    except KeyboardInterrupt:
        sys.exit(130)
