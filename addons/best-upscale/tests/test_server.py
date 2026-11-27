import json
import os
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def run(cmds):
    p = subprocess.run(
        [sys.executable, "-m", "portal_best_upscale"],
        input="\n".join(json.dumps(c) for c in cmds) + "\n",
        capture_output=True, text=True, cwd=ROOT, timeout=60,
        env={**os.environ, "PYTHONPATH": ROOT},
    )
    return [json.loads(l) for l in p.stdout.splitlines() if l.strip()]


class ServerTests(unittest.TestCase):
    def test_probe_reports_cleanly_without_torch(self):
        ev = run([{"op": "probe"}, {"op": "quit"}])
        self.assertEqual(ev[0]["event"], "ready")
        probe = next(e for e in ev if e["event"] == "probe")
        if not probe["ok"]:
            self.assertIn(probe["reason"], ("torch_missing", "no_cuda", "cuda_error"))

    def test_missing_input_dir_is_an_error(self):
        with tempfile.TemporaryDirectory() as t:
            ev = run([{"op": "upscale", "input_dir": os.path.join(t, "nope"), "output_dir": os.path.join(t, "o")}, {"op": "quit"}])
        self.assertTrue(any(e.get("code") == "bad_input" for e in ev))

    def test_unknown_op(self):
        ev = run([{"op": "zzz"}, {"op": "quit"}])
        self.assertTrue(any(e.get("code") == "bad_command" for e in ev))


if __name__ == "__main__":
    unittest.main()
