"""Offline publishing checks: no GitHub writes, Windows builds or desktop input."""
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("publish_release", ROOT / "ci/publish_release.py")
PUBLISHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLISHER)
SHA = "a" * 40
OLD_SHA = "b" * 40


class FakeGitHub:
    def __init__(self):
        self.refs = {}
        self.tags = {}
        self.releases = {}
        self.calls = []
        self.commands = []

    def api(self, method, endpoint, data=None):
        self.calls.append((method, endpoint, data))
        if method == "GET":
            if endpoint.startswith("git/ref/tags/"):
                return self.refs.get(endpoint.removeprefix("git/ref/tags/"))
            if endpoint.startswith("git/tags/"):
                return self.tags[endpoint.removeprefix("git/tags/")]
            if endpoint.startswith("releases/tags/"):
                return self.releases.get(endpoint.removeprefix("releases/tags/"))
        if method == "POST" and endpoint == "git/refs":
            self.refs[data["ref"].removeprefix("refs/tags/")] = {"object": {"type": "commit", "sha": data["sha"]}}
            return {}
        if method == "PATCH" and endpoint == "git/refs/tags/preview":
            self.ref("preview", data["sha"])
            return {}
        raise AssertionError((method, endpoint, data))

    def gh(self, *args):
        self.commands.append(tuple(map(str, args)))

    def ref(self, name, sha):
        self.refs[name] = {"object": {"type": "commit", "sha": sha}}


class PublishingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.github = FakeGitHub()
        self.package()

    def package(self, version="0.1.3", **overrides):
        self.version = version
        self.archive = self.folder / f"SoundboardLab-{version}-windows-x64.zip"
        info = dict(Version=version, Commit=SHA, CIBuild=True, TestsExecuted=True, NativeBuildReused=False)
        info.update(overrides)
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("build-info.json", json.dumps(info))
        self.hash_zip()
        (self.folder / "release-metadata.json").write_text(json.dumps(dict(Version=version, Commit=SHA)))
        (self.folder / "release-notes.md").write_text("Fix recording-device selection.\n")

    def hash_zip(self):
        digest = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        (self.folder / "SHA256SUMS.txt").write_text(f"{digest}  {self.archive.name}\n")

    def publish(self):
        PUBLISHER.publish(self.folder, SHA, self.github)

    def creations(self):
        return [cmd for cmd in self.github.commands if cmd[:2] == ("release", "create")]

    def test_one_push_trigger(self):
        workflow = (ROOT / ".github/workflows/build-release.yml").read_text()
        push = re.search(r"^  push:\n(.*?)(?=^  \w|\Z)", workflow, re.S | re.M).group(1)
        self.assertIn("branches: [main]", push)
        self.assertNotIn("tags:", push, "Pushing main plus a tag must not start duplicate runs")
        self.assertEqual(workflow.count("run: .\\build.ps1 -CI"), 1)

    def test_new_version_publishes_both_channels_from_same_zip(self):
        self.publish()
        self.assertEqual([cmd[2] for cmd in self.creations()], ["v0.1.3", "preview"])
        for cmd in self.creations():
            self.assertIn(str(self.archive), cmd)
            self.assertIn(str(self.folder / "SHA256SUMS.txt"), cmd)
        self.assertIn("--latest", self.creations()[0])
        self.assertEqual(self.github.refs["v0.1.3"]["object"]["sha"], SHA)

    def test_matching_tag_from_atomic_push_is_reused(self):
        self.github.ref("v0.1.3", SHA)
        self.publish()
        self.assertEqual(self.creations()[0][2], "v0.1.3")
        self.assertIn("--verify-tag", self.creations()[0])
        self.assertFalse(any(call[0] == "POST" for call in self.github.calls))

    def test_annotated_tag_is_peeled_to_commit(self):
        self.github.refs["v0.1.3"] = {"object": {"type": "tag", "sha": "tag-object"}}
        self.github.tags["tag-object"] = {"object": {"type": "commit", "sha": SHA}}
        self.publish()
        self.assertEqual(self.creations()[0][2], "v0.1.3")

    def test_old_numbered_tag_is_never_moved(self):
        self.github.ref("v0.1.3", OLD_SHA)
        self.publish()
        self.assertEqual([cmd[2] for cmd in self.creations()], ["preview"])
        self.assertEqual(self.github.refs["v0.1.3"]["object"]["sha"], OLD_SHA)

    def test_existing_numbered_release_is_not_overwritten_on_rerun(self):
        self.github.ref("v0.1.3", SHA)
        self.github.releases["v0.1.3"] = {"id": 123}
        self.publish()
        self.assertFalse(any("v0.1.3" in cmd for cmd in self.github.commands))

    def test_preview_update_uses_same_artifact_and_only_moves_preview(self):
        self.github.ref("preview", OLD_SHA)
        self.github.releases["preview"] = {"id": 456}
        self.publish()
        self.assertEqual(self.github.refs["preview"]["object"]["sha"], SHA)
        upload = next(cmd for cmd in self.github.commands if cmd[:2] == ("release", "upload"))
        self.assertEqual(upload[2], "preview")
        self.assertIn(str(self.archive), upload)
        self.assertIn("--clobber", upload)

    def test_prerelease_never_becomes_latest_stable(self):
        self.package("0.2.0-rc.1")
        self.publish()
        self.assertIn("--prerelease", self.creations()[0])
        self.assertIn("--latest=false", self.creations()[0])
        self.assertNotIn("--latest", self.creations()[0])

    def test_corrupt_zip_is_rejected_before_github_writes(self):
        self.archive.write_bytes(b"corrupt ZIP")
        with self.assertRaisesRegex(ValueError, "checksum"):
            self.publish()
        self.assertFalse(self.github.calls)

    def test_wrong_source_is_rejected_before_github_writes(self):
        with self.assertRaisesRegex(ValueError, "workflow commit"):
            PUBLISHER.publish(self.folder, OLD_SHA, self.github)
        self.assertFalse(self.github.calls)

    def test_zip_metadata_must_match_clean_tested_build(self):
        for change in [dict(Version="9.9.9"), dict(Commit=OLD_SHA), dict(CIBuild=False),
                       dict(TestsExecuted=False), dict(NativeBuildReused=True)]:
            with self.subTest(change=change):
                self.package(**change)
                with self.assertRaisesRegex(ValueError, "tested clean build"):
                    self.publish()
        self.assertFalse(self.github.calls)


if __name__ == "__main__":
    unittest.main()
