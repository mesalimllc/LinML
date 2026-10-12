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

if __name__ == "__main__":
    unittest.main()
