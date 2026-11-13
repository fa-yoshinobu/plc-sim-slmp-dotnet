"""Run the batch control flow with isolated outputs and injected tool failures."""
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest


BATCH = Path(__file__).resolve().parents[2] / "build.bat"
STAGES = ["restore", "build", "test", "publish_gui", "publish_host",
          "config", "python", "scripts", "readme", "license", "configuration", "package"]


@unittest.skipUnless(os.name == "nt", "cmd.exe is required")
class BuildBatchTests(unittest.TestCase):
    def run_batch(self, fail=None, missing=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            release = root / "artifacts/release"
            required = ["PlcSim.Slmp.Gui.exe", "PlcSim.Slmp.Host.exe",
                        "config/default.json", "python/plc_sim.py", "docs/CONFIGURATION.md"]
            for name in required:
                if name != missing:
                    target = release / name
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_text("fixture", encoding="utf-8")
            (root / "tool.py").write_text(
                "import os, pathlib, sys\n"
                "stage = sys.argv[1]\n"
                "with open('stages.txt', 'a') as log: log.write(stage + '\\n')\n"
                "if stage == os.environ.get('FAIL_STAGE'): sys.exit(23)\n"
                "if stage == 'package' and not os.environ.get('OMIT_ZIP'):\n"
                "    pathlib.Path('artifacts/release/PlcSim.Slmp-win-x64.zip').touch()\n",
                encoding="utf-8")
            stages = iter(STAGES)
            pattern = r'(?m)^(\s*)(?:dotnet (?:restore|build|test|publish)\b|xcopy\b|copy\b|powershell\b)[^\r\n]*'

            def replace(match):
                # Preserve each original failure branch: losing one must fail this test.
                suffix = " || goto :fail" if "|| goto :fail" in match[0] else ""
                return f'{match[1]}"{sys.executable}" "%~dp0tool.py" {next(stages)}{suffix}'

            batch = re.sub(pattern, replace, BATCH.read_text(encoding="utf-8"))
            batch = batch.replace("where dotnet >nul 2>nul", "cmd /c exit 0")
            (root / "build.bat").write_text(batch, encoding="utf-8", newline="\r\n")
            env = dict(os.environ, FAIL_STAGE=fail or "", OMIT_ZIP="1" if missing == "zip" else "")
            result = subprocess.run(["cmd", "/d", "/c", "build.bat"], cwd=root,
                                    env=env, capture_output=True)
            executed = (root / "stages.txt").read_text().splitlines()
            return result.returncode, result.stdout, executed

    def test_each_tool_failure_stops_immediately(self):
        for index, stage in enumerate(STAGES):
            with self.subTest(stage=stage):
                code, output, executed = self.run_batch(fail=stage)
                self.assertEqual(code, 1, output)
                self.assertIn(b"[error]", output)
                self.assertEqual(executed, STAGES[:index + 1])

    def test_missing_distribution_files_fail(self):
        for name in ["PlcSim.Slmp.Gui.exe", "PlcSim.Slmp.Host.exe",
                     "config/default.json", "python/plc_sim.py", "docs/CONFIGURATION.md", "zip"]:
            with self.subTest(name=name):
                code, output, executed = self.run_batch(missing=name)
                self.assertEqual(code, 1, output)
                self.assertEqual(executed, STAGES if name == "zip" else STAGES[:-1])

    def test_success_runs_each_stage_once(self):
        code, output, executed = self.run_batch()
        self.assertEqual(code, 0, output)
        self.assertEqual(executed, STAGES)
