"""Exercise the actual PowerShell packager with isolated distribution fixtures."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

SHELL = shutil.which("pwsh") or shutil.which("powershell")
SCRIPT = Path(__file__).resolve().parents[2] / "tools/package-release.ps1"

@unittest.skipUnless(SHELL, "PowerShell is required to run the distribution packager.")
class PackageReleaseTests(unittest.TestCase):
    def package(self, folder):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "tools").mkdir()
            shutil.copyfile(SCRIPT, root / "tools/package-release.ps1")
            release = root / "artifacts/release"
            inputs = ["README.md", "LICENSE", "docs/CONFIGURATION.md", "config/default.json", "python/plc_sim.py", "scripts/counter.py"]
            inputs += (["gui/PlcSim.Slmp.Gui.exe", "gui/library.dll", "host/PlcSim.Slmp.Host.exe", "start_gui.bat", "start_console.bat"] if folder else ["PlcSim.Slmp.Gui.exe", "PlcSim.Slmp.Host.exe"])
            for name in inputs:
                target = release / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(name, encoding="utf-8")
            for name in ["config/default.json", "python/plc_sim.py", "scripts/counter.py"]:
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(name, encoding="utf-8")
            for name in ["config/private.csv", "scripts/deleted.py", "release-notes.md", "old.zip", "old.zip.sha256", "gui/old.pdb"]:
                target = release / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("must not ship", encoding="utf-8")
            result = subprocess.run([SHELL, "-NoProfile", "-File", str(root / "tools/package-release.ps1"), "-SingleFile", "0" if folder else "1"], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            archive = release / "PlcSim.Slmp-win-x64.zip"
            with zipfile.ZipFile(archive) as zipped:
                self.assertEqual(set(zipped.namelist()), {"PlcSim.Slmp/" + name for name in inputs})
            expected = hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n"
            self.assertEqual(archive.with_suffix(".zip.sha256").read_bytes(), expected.encode("utf-8"))

    def test_single_file_excludes_stale_content_and_metadata(self):
        self.package(False)

    def test_folder_mode_keeps_dependencies_and_excludes_debug_output(self):
        self.package(True)
