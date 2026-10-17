"""Publish both channels from one tested build; never overwrite a numbered release."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import zipfile


class GitHub:
    def __init__(self):
        self.repo = os.environ["GH_REPO"]
        self.token = os.environ["GH_TOKEN"]
        self.base = os.environ.get("GITHUB_API_URL", "https://api.github.com")

    def api(self, method, endpoint, data=None):
        body = None if data is None else json.dumps(data).encode()
        request = Request(f"{self.base}/repos/{self.repo}/{endpoint}", body, method=method,
                          headers={"Authorization": f"Bearer {self.token}",
                                   "Accept": "application/vnd.github+json",
                                   "Content-Type": "application/json"})
        try:
            with urlopen(request, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if method == "GET" and error.code == 404:
                return None
            raise RuntimeError(f"GitHub API {method} {endpoint}: HTTP {error.code}") from error

    def gh(self, *args):
        subprocess.run(["gh", *map(str, args)], check=True)


def tag_commit(github, tag):
    ref = github.api("GET", f"git/ref/tags/{tag}")
    if ref is None:
        return None
    target = ref["object"]
    for _ in range(8):
        if target["type"] == "commit":
            return target["sha"]
        if target["type"] != "tag":
            break
        target = github.api("GET", f"git/tags/{target['sha']}")["object"]
    raise ValueError(f"Tag {tag} does not resolve to a commit")


def verify_package(folder, source_sha):
    metadata = json.loads((folder / "release-metadata.json").read_text(encoding="utf-8-sig"))
    version = metadata["Version"]
    if not re.fullmatch(r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid release version")
    if not re.fullmatch(r"[0-9a-f]{40}", source_sha) or metadata["Commit"] != source_sha:
        raise ValueError("Release metadata does not match the workflow commit")
    archive = folder / f"SoundboardLab-{version}-windows-x64.zip"
    expected, filename = (folder / "SHA256SUMS.txt").read_text().strip().split()
    digest = hashlib.sha256()
    with archive.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    actual = digest.hexdigest()
    if filename != archive.name or actual != expected:
        raise ValueError("Release ZIP checksum does not match")
    with zipfile.ZipFile(archive) as package:
        info = json.loads(package.read("build-info.json").decode("utf-8-sig"))
        if (info["Version"] != version or info["Commit"] != source_sha
                or not info["CIBuild"] or not info["TestsExecuted"] or info["NativeBuildReused"]):
            raise ValueError("Release ZIP is not the tested clean build for this commit")
    return version, archive


def publish(folder, source_sha, github):
    version, archive = verify_package(folder, source_sha)
    notes = folder / "published-notes.md"
    notes.write_text((folder / "release-notes.md").read_text(encoding="utf-8-sig").strip()
                     + f"\n\nPortable Windows x64 build from commit `{source_sha}`.\n\n"
                     + "Extract the ZIP and run `SoundboardLab.exe`. The .NET runtime is included.\n\n"
                     + "Includes foreground-app and whole-PC audio forwarding with toggle and push-to-forward hotkeys in Preferences. "
                     + "Microphone mixing requires device setup; this development build is unsigned.\n\n"
                     + "CI validates offline audio processing, native callbacks, services and hidden UI layouts. "
                     + "Physical microphone and live forwarding compatibility require separate checks. Verify the ZIP with `SHA256SUMS.txt`.\n",
                     encoding="utf-8")
    # New versions are released automatically after their main build succeeds.
    # Also accept a matching tag pushed atomically with main, including annotated
    # tags. An older version's tag/release must never be moved or overwritten.
    tag = f"v{version}"
    numbered_commit = tag_commit(github, tag)
    existing = github.api("GET", f"releases/tags/{tag}")
    if existing is not None:
        print(f"Keep existing numbered release {tag} unchanged")
    elif numbered_commit is not None and numbered_commit != source_sha:
        print(f"Keep {tag} at its original commit; bump the app version for a new release")
    else:
        if numbered_commit is None:
            github.api("POST", "git/refs", {"ref": f"refs/tags/{tag}", "sha": source_sha})
        flags = ["--prerelease", "--latest=false"] if "-" in version else ["--latest"]
        github.gh("release", "create", tag, "--verify-tag", "--title", f"Soundboard Lab {tag}",
                  "--notes-file", notes, *flags, archive, folder / "SHA256SUMS.txt")

    # Update the rolling channel with exactly the same ZIP. Its tag alone is
    # movable; numbered assets above are only uploaded when first created.
    preview = github.api("GET", "releases/tags/preview")
    if tag_commit(github, "preview") is not None:
        github.api("PATCH", "git/refs/tags/preview", {"sha": source_sha, "force": True})
    if preview is None:
        github.gh("release", "create", "preview", "--target", source_sha, "--prerelease", "--latest=false",
                  "--title", "Latest development preview", "--notes-file", notes, archive, folder / "SHA256SUMS.txt")
    else:
        github.gh("release", "edit", "preview", "--title", "Latest development preview", "--notes-file", notes)
        github.gh("release", "upload", "preview", archive, folder / "SHA256SUMS.txt", "--clobber")


if __name__ == "__main__":
    publish(Path(sys.argv[1]), os.environ["SOURCE_SHA"], GitHub())
