import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("refresh_repatcher_data", Path(__file__).resolve().parent / "refresh-repatcher-data.py")
refresh = importlib.util.module_from_spec(spec)
spec.loader.exec_module(refresh)


class RefreshTests(unittest.TestCase):
    def sources(self):
        return {
            "armor_mappings.merged.json": {"Example Armor": {"slim left arm": 2**64 - 1}},
            "helmet_mappings.json": {"Example Helmet": {"Helmet": 123}},
            "archivehashes.json": {
                "Armor": {"ABCDEF0123456789": "Example Armor", "5717aeb23774a749": "Updated BFM name"},
                "Helmet": {"0123456789abcdef": "Example Helmet"},
                "Cape": {"123": "Example Cape"},
            },
        }

    def download(self, values):
        def get(url):
            if url.startswith("https://api.github.com/"):
                return json.dumps({"sha": "a" * 40 if refresh.MIGRATOR in url else "b" * 40}).encode()
            return json.dumps(values[url.rsplit("/", 1)[1]]).encode()
        return get

    def test_immutable_downloads_preserve_supplements_and_upstream_names(self):
        with patch.object(refresh, "download", side_effect=self.download(self.sources())) as get, contextlib.redirect_stdout(io.StringIO()):
            files = refresh.prepare("main", "main")
        urls = [call.args[0] for call in get.call_args_list if "raw.githubusercontent.com" in call.args[0]]
        self.assertTrue(all("/" + ("a" if refresh.MIGRATOR in url else "b") * 40 + "/" in url for url in urls))
        index = json.loads(files["archivehashes.json"])
        self.assertEqual("Example Armor", index["Armor"]["abcdef0123456789"])
        self.assertEqual("Example Cape", index["Cape"]["0000000000000123"])
        self.assertEqual("Updated BFM name", index["Armor"]["5717aeb23774a749"])
        self.assertEqual("BFM-220 Ironclad", index["Armor"]["5ad0c7bf483d382a"])
        self.assertIn(b"Source SHA-256:", files["SOURCES.md"])

    def test_check_reports_changes_without_creating_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "data"
            with patch.object(refresh, "download", side_effect=self.download(self.sources())), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, refresh.main(["--check", "--output-dir", str(output)]))
            self.assertFalse(output.exists())

    def test_bad_downloads_never_replace_existing_catalogs(self):
        for bad_id in (True, -1, 2**64, "123"):
            with self.subTest(bad_id=bad_id), tempfile.TemporaryDirectory() as directory:
                output = Path(directory)
                original = output / "armor_mappings.merged.json"
                original.write_bytes(b"existing catalog")
                values = self.sources()
                values["armor_mappings.merged.json"]["Example Armor"]["slim left arm"] = bad_id
                with patch.object(refresh, "download", side_effect=self.download(values)), contextlib.redirect_stderr(io.StringIO()):
                    self.assertEqual(2, refresh.main(["--output-dir", str(output)]))
                self.assertEqual(b"existing catalog", original.read_bytes())
                self.assertEqual([original], list(output.iterdir()))

    def test_duplicate_json_keys_are_rejected_before_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(refresh, "download", return_value=b'{"name": 1, "name": 2}'), contextlib.redirect_stderr(io.StringIO()):
                result = refresh.main(["--migrator-ref", "a" * 40, "--index-ref", "b" * 40, "--output-dir", directory])
            self.assertEqual(2, result)
            self.assertEqual([], list(Path(directory).iterdir()))

    def test_write_failure_rolls_back_previously_replaced_files(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            (output / "first.json").write_bytes(b"old")
            real_replace = os.replace
            calls = 0

            def replace(source, target):
                nonlocal calls
                calls += 1
                if calls == 2:
                    raise OSError("simulated write failure")
                real_replace(source, target)

            with patch.object(refresh.os, "replace", side_effect=replace), self.assertRaises(OSError):
                refresh.publish(output, {"first.json": b"new", "second.json": b"new"})
            self.assertEqual(b"old", (output / "first.json").read_bytes())
            self.assertFalse((output / "second.json").exists())

    def test_hashes_that_collide_after_normalization_are_rejected(self):
        values = self.sources()
        values["archivehashes.json"]["Cape"]["0000000000000123"] = "Conflicting Cape"
        with patch.object(refresh, "download", side_effect=self.download(values)), self.assertRaisesRegex(ValueError, "duplicate archive hash"):
            refresh.prepare("a" * 40, "b" * 40)


if __name__ == "__main__":
    unittest.main()
