import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest

UPDATER = Path(sys.argv.pop(1)).resolve()
APP = Path(sys.argv.pop(1)).resolve()


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="Quartermaster updater ü ")
        self.addCleanup(self.cleanup_temp)
        self.root = Path(self.temp.name)
        self.source = self.root / "staged"
        self.target = self.root / "installed"
        self.source.mkdir()
        self.target.mkdir()
        self.name = APP.name
        shutil.copy2(APP, self.source / self.name)
        (self.target / self.name).write_bytes(b"previous application")
        (self.source / "winupdater").mkdir()
        shutil.copy2(UPDATER, self.source / "winupdater" / UPDATER.name)
        (self.target / "keep.txt").write_text("user data")
        self.parent = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        self.addCleanup(self.stop_parent)

    def cleanup_temp(self):
        # A successfully restarted test app can briefly hold its executable open on Windows.
        deadline = time.monotonic() + 5
        while True:
            try:
                self.temp.cleanup()
                return
            except PermissionError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.02)

    def stop_parent(self):
        if self.parent.poll() is None:
            self.parent.terminate()
        self.parent.wait(timeout=5)

    def command(self, pid=None, timeout=3000, source=None, helper=None):
        return [str(helper or UPDATER), str(pid or self.parent.pid),
                str(source or self.source), str(self.target), self.name, str(timeout)]

    def run_update(self, **kwargs):
        return subprocess.run(self.command(**kwargs), capture_output=True, text=True, timeout=10)

    def assert_unchanged(self):
        self.assertEqual((self.target / self.name).read_bytes(), b"previous application")
        self.assertEqual((self.target / "keep.txt").read_text(), "user data")
        self.assertFalse((self.target / "restarted.txt").exists())
        self.assertFalse(list(self.target.glob(".quartermaster-update-*")))

    def test_waits_then_replaces_and_restarts_with_unicode_paths(self):
        (self.source / "data").mkdir()
        (self.source / "data" / "new.txt").write_text("new data")
        if os.name != "nt":
            # ZIP extraction may not retain executable permissions.
            (self.source / self.name).chmod(0o600)
        process = subprocess.Popen(self.command(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            time.sleep(0.15)
            self.assertIsNone(process.poll())
            self.assert_unchanged()
            self.stop_parent()
            _, error = process.communicate(timeout=10)
            self.assertEqual(process.returncode, 0, error)
        finally:
            if process.poll() is None:
                process.kill()
            process.communicate(timeout=5)
        deadline = time.monotonic() + 5
        while not (self.target / "restarted.txt").exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertTrue((self.target / "restarted.txt").exists())
        self.assertEqual((self.target / self.name).read_bytes(), APP.read_bytes())
        self.assertEqual((self.target / "data" / "new.txt").read_text(), "new data")
        self.assertEqual((self.target / "keep.txt").read_text(), "user data")
        self.assertEqual((self.target / "winupdater" / UPDATER.name).read_bytes(), UPDATER.read_bytes())
        self.assertFalse(list(self.target.glob(".quartermaster-update-*")))

    def test_timeout_does_not_modify_installation(self):
        result = self.run_update(timeout=100)
        self.assertEqual(result.returncode, 1)
        self.assertIn("timeout", result.stderr)
        self.assert_unchanged()

    def test_already_exited_parent_is_accepted(self):
        self.stop_parent()
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_restart_failure_restores_overwritten_files_and_removes_new_files(self):
        self.stop_parent()
        (self.source / self.name).write_bytes(b"not an executable")
        (self.source / "new-file.txt").write_text("remove on rollback")
        (self.source / "new-directory").mkdir()
        (self.source / "new-directory" / "new.txt").write_text("remove directory on rollback")
        (self.target / "winupdater").mkdir()
        (self.target / "winupdater" / UPDATER.name).write_bytes(b"previous updater")
        result = self.run_update()
        self.assertEqual(result.returncode, 1)
        self.assert_unchanged()
        self.assertFalse((self.target / "new-file.txt").exists())
        self.assertFalse((self.target / "new-directory").exists())
        self.assertEqual((self.target / "winupdater" / UPDATER.name).read_bytes(), b"previous updater")

    def test_mid_replacement_failure_restores_the_installation(self):
        import ctypes
        from ctypes import wintypes
        self.stop_parent()
        (self.source / "zz-blocked").mkdir()
        (self.source / "zz-blocked" / "data.txt").write_text("new contents")
        blocked = self.target / "zz-blocked"
        blocked.mkdir()
        (blocked / "data.txt").write_text("original contents")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                     wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        # Allow reads but deny deletion/rename of this file.
        handle = kernel.CreateFileW(str(blocked / "data.txt"), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, wintypes.HANDLE(-1).value)
        try:
            result = self.run_update()
            self.assertEqual(result.returncode, 1)
            self.assert_unchanged()
            self.assertEqual((blocked / "data.txt").read_text(), "original contents")
            self.assertFalse((self.target / "winupdater").exists())
        finally:
            kernel.CloseHandle(handle)

    def test_invalid_layouts_are_rejected_before_waiting(self):
        for source in (self.target, self.target / "nested"):
            source.mkdir(exist_ok=True)
            result = self.run_update(source=source)
            self.assertEqual(result.returncode, 1)
            self.assertIn("overlap", result.stderr)
            self.assert_unchanged()
        (self.source / self.name).unlink()
        result = self.run_update()
        self.assertEqual(result.returncode, 1)
        self.assertIn("missing", result.stderr)
        self.assert_unchanged()

    def test_running_from_installation_is_rejected(self):
        helper = self.target / UPDATER.name
        shutil.copy2(UPDATER, helper)
        result = self.run_update(helper=helper)
        self.assertEqual(result.returncode, 1)
        self.assertIn("temporary copy", result.stderr)
        self.assert_unchanged()

    @unittest.skipIf(os.name == "nt", "Creating symlinks on Windows may require privileges")
    def test_symlinks_are_rejected(self):
        (self.source / "link").symlink_to(self.target / "keep.txt")
        result = self.run_update()
        self.assertEqual(result.returncode, 1)
        self.assertIn("symbolic link", result.stderr)
        self.assert_unchanged()
        (self.source / "link").unlink()
        (self.target / self.name).unlink()
        (self.target / self.name).symlink_to(self.target / "keep.txt")
        result = self.run_update()
        self.assertEqual(result.returncode, 1)
        self.assertIn("symbolic link", result.stderr)
        self.assertEqual((self.target / "keep.txt").read_text(), "user data")

    def test_log_file_survives_the_callers_exit_and_records_failure(self):
        self.stop_parent()
        (self.source / self.name).write_bytes(b"not an executable")
        log = self.root / "update log ü.txt"
        result = subprocess.run(self.command() + [str(log)], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stderr, "")
        self.assertIn("Quartermaster update failed", log.read_text())
        self.assert_unchanged()

    def test_arguments_are_validated(self):
        for pid in ("0", "-1", "123junk", "999999999999999999999"):
            command = self.command()
            command[1] = pid
            result = subprocess.run(command, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 1)
            self.assert_unchanged()
        command = self.command()
        command[4] = "../outside"
        result = subprocess.run(command, capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 1)
        self.assert_unchanged()


if __name__ == "__main__":
    unittest.main()
