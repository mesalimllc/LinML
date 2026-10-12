import os
import tempfile
import unittest
from linml.binary import find_linml_binary, get_linml_binary

class TestBinary(unittest.TestCase):
    def test_find_linml_binary(self):
        cmd = find_linml_binary()
        self.assertIsNotNone(cmd)
        self.assertIsInstance(cmd, list)
        self.assertGreater(len(cmd), 0)

    def test_get_linml_binary(self):
        cmd = get_linml_binary()
        self.assertIsInstance(cmd, list)
        self.assertGreater(len(cmd), 0)

    def test_env_override(self):
        with tempfile.NamedTemporaryFile() as tmp:
            old_val = os.environ.get("LINML_BIN")
            try:
                os.environ["LINML_BIN"] = tmp.name
                cmd = find_linml_binary()
                self.assertIsNotNone(cmd)
                self.assertEqual(cmd, [tmp.name])
            finally:
                if old_val is not None:
                    os.environ["LINML_BIN"] = old_val
                else:
                    os.environ.pop("LINML_BIN", None)

    def test_env_override_nonexistent_raises_value_error(self):
        old_val = os.environ.get("LINML_BIN")
        try:
            os.environ["LINML_BIN"] = "/nonexistent/fake_linml_path"
            with self.assertRaises(ValueError):
                find_linml_binary()
        finally:
            if old_val is not None:
                os.environ["LINML_BIN"] = old_val
            else:
                os.environ.pop("LINML_BIN", None)

    def test_python_wrapper_excluded_from_path(self):
        from pathlib import Path
        with tempfile.TemporaryDirectory() as tmpdir:
            fake_cli = Path(tmpdir) / "linml"
            with open(fake_cli, "w") as f:
                f.write("#!/usr/bin/env python3\nimport sys\n")
            os.chmod(fake_cli, 0o755)

            old_bin = os.environ.pop("LINML_BIN", None)
            old_path = os.environ.get("PATH", "")
            try:
                os.environ["PATH"] = f"{tmpdir}{os.pathsep}{old_path}"
                cmd = find_linml_binary()
                if cmd:
                    self.assertNotEqual(cmd, [str(fake_cli)])
            finally:
                os.environ["PATH"] = old_path
                if old_bin:
                    os.environ["LINML_BIN"] = old_bin

if __name__ == "__main__":
    unittest.main()
