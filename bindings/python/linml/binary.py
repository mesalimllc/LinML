from __future__ import annotations

import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Dict, List, Optional


def get_dotnet_env() -> Dict[str, str]:
    """Return an environment dictionary with DOTNET_ROOT and PATH configured if ~/.dotnet exists."""
    env = os.environ.copy()
    user_dotnet = Path.home() / ".dotnet"
    if user_dotnet.is_dir():
        if "DOTNET_ROOT" not in env:
            env["DOTNET_ROOT"] = str(user_dotnet)
        current_path = env.get("PATH", "")
        if str(user_dotnet) not in current_path.split(os.pathsep):
            env["PATH"] = f"{user_dotnet}{os.pathsep}{current_path}"
    return env


def find_linml_binary() -> Optional[List[str]]:
    """Locate the linml executable or dotnet command to run the CLI.

    Returns:
        List of command arguments (e.g. ['/path/to/linml'] or ['dotnet', '/path/to/linml.dll'])
        or None if not found.
    """
    # 1. Explicit environment override
    env_bin = os.environ.get("LINML_BIN")
    if env_bin and os.path.exists(env_bin):
        if env_bin.endswith(".dll"):
            dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet" / "dotnet")
            return [dotnet, str(Path(env_bin).resolve())]
        return [str(Path(env_bin).resolve())]

    # 2. Packaged binary within python wheel directory
    pkg_dir = Path(__file__).resolve().parent
    bundled_bin = pkg_dir / "bin" / "linml"
    if bundled_bin.is_file() and os.access(bundled_bin, os.X_OK):
        return [str(bundled_bin)]

    # 3. System PATH lookup. The installed Python console script is also
    # called ``linml``; never select it as the native CLI.
    path_bin = shutil.which("linml")
    if path_bin:
        path_path = Path(path_bin)
        wrapper_paths = set()
        if sys.argv and sys.argv[0]:
            wrapper_paths.add(Path(sys.argv[0]).resolve())
        wrapper_paths.add((Path(sys.executable).parent / "linml").resolve())

        is_python_wrapper = path_path.resolve() in wrapper_paths
        if not is_python_wrapper:
            try:
                with path_path.open("rb") as stream:
                    shebang = stream.readline(256).lower()
                is_python_wrapper = shebang.startswith(b"#!") and b"python" in shebang
            except (OSError, UnicodeError):
                pass
        if not is_python_wrapper:
            return [path_bin]

    # 4. Standard Linux user install directories
    user_candidates = [
        Path.home() / ".local" / "bin" / "linml",
        Path.home() / ".local" / "share" / "linml" / "linml",
    ]
    for cand in user_candidates:
        if cand.is_file() and os.access(cand, os.X_OK):
            return [str(cand)]

    # 5. Repository local build outputs
    repo_root = pkg_dir.parent.parent.parent

    # Self-contained published native binary (no external runtime required)
    published_bin = repo_root / "artifacts" / "publish" / "linux-x64" / "linml"
    if published_bin.is_file() and os.access(published_bin, os.X_OK):
        return [str(published_bin)]

    # 6. Repository compiled .dll via dotnet host
    dotnet = shutil.which("dotnet")
    if not dotnet:
        user_dotnet_bin = Path.home() / ".dotnet" / "dotnet"
        if user_dotnet_bin.is_file() and os.access(user_dotnet_bin, os.X_OK):
            dotnet = str(user_dotnet_bin)

    if dotnet:
        dll_candidates = [
            repo_root / "src" / "LinML.Cli" / "bin" / "Release" / "net10.0" / "linml.dll",
            repo_root / "src" / "LinML.Cli" / "bin" / "Debug" / "net10.0" / "linml.dll",
            repo_root / "artifacts" / "publish" / "linux-x64" / "linml.dll",
        ]
        for dll in dll_candidates:
            if dll.is_file():
                return [dotnet, str(dll)]

    # 7. Framework-dependent apphost binary fallback
    apphost_candidates = [
        repo_root / "src" / "LinML.Cli" / "bin" / "Release" / "net10.0" / "linml",
        repo_root / "src" / "LinML.Cli" / "bin" / "Debug" / "net10.0" / "linml",
    ]
    for cand in apphost_candidates:
        if cand.is_file() and os.access(cand, os.X_OK):
            return [str(cand)]

    return None


def get_linml_binary() -> List[str]:
    """Return the linml command or raise RuntimeError if not found."""
    cmd = find_linml_binary()
    if not cmd:
        raise RuntimeError(
            "Could not find the 'linml' executable. "
            "Please ensure 'linml' is in your PATH, set the LINML_BIN environment variable, "
            "or run 'bash scripts/install.sh'."
        )
    return cmd


def run_linml_cli(args: List[str], check: bool = True) -> subprocess.CompletedProcess:
    """Execute linml CLI command with arguments."""
    base_cmd = get_linml_binary()
    full_cmd = base_cmd + args
    return subprocess.run(
        full_cmd,
        capture_output=True,
        text=True,
        check=check,
        env=get_dotnet_env(),
    )
