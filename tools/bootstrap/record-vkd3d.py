"""Verify the manual native build's source pins and record its installed libraries."""
import hashlib
import json
import re
from pathlib import Path
import subprocess
import sys


def git(source, *args):
    return subprocess.check_output(
        ["git", "-C", str(source), *args], text=True
    ).strip()


pins = json.loads(Path(sys.argv[1]).read_text())["vkd3dProton"]
source = Path(sys.argv[2])
prefix = Path(sys.argv[3])
patch = Path(sys.argv[1]).parent / pins["patch"]
if hashlib.sha256(patch.read_bytes()).hexdigest() != pins["patchSha256"]:
    raise SystemExit("vkd3d-proton visibility patch does not match its pin")
if prefix != Path(pins["prefix"]):
    raise SystemExit("native build prefix differs from the pin")
if git(source, "rev-parse", "HEAD") != pins["commit"]:
    raise SystemExit("vkd3d-proton source HEAD does not match the pin")
if git(source, "rev-parse", pins["tag"] + "^{commit}") != pins["commit"]:
    raise SystemExit("vkd3d-proton tag does not match the pinned commit")
checking_source = len(sys.argv) == 5 and sys.argv[4] == "--check-source"
if checking_source:
    git(source, "diff", "--quiet", "--ignore-submodules=untracked", "HEAD", "--")
else:
    original = git(source, "show", "HEAD:include/vkd3d.h").splitlines(keepends=True)
    # Apply the pinned unified diff in memory and compare the entire resulting header, so a receipt cannot
    # silently bless any source change besides the visibility patch.
    expected = []
    cursor = 0
    in_hunk = False
    for line in patch.read_text().splitlines(keepends=True):
        if line.startswith("@@"):
            start = int(re.match(r"@@ -(\d+)", line).group(1)) - 1
            expected.extend(original[cursor:start])
            cursor = start
            in_hunk = True
        elif in_hunk:
            if line.startswith("+"):
                expected.append(line[1:])
            elif line.startswith("-"):
                cursor += 1
            else:
                expected.append(original[cursor])
                cursor += 1
    expected.extend(original[cursor:])
    if (source / "include/vkd3d.h").read_text().rstrip() != "".join(expected).rstrip():
        raise SystemExit("vkd3d-proton header differs from the pinned source plus visibility patch")
    if git(source, "diff", "--name-only", "HEAD", "--") != "include/vkd3d.h":
        raise SystemExit("unexpected native source modifications")
actual = {}
for line in git(source, "submodule", "status", "--recursive").splitlines():
    if line.startswith(("+", "-", "U")):
        raise SystemExit("vkd3d-proton submodule checkout differs from its recorded commit")
    commit, path, *_ = line.split()
    actual[path] = commit
if actual != pins["submodules"]:
    raise SystemExit("vkd3d-proton recursive submodules do not match the pins")
for path in actual:
    git(source / path, "diff", "--quiet", "--ignore-submodules=untracked", "HEAD", "--")
if checking_source:
    print("vkd3d-proton source pins: OK")
    sys.exit(0)

files = {
    pins[key]: hashlib.sha256((prefix / pins[key]).read_bytes()).hexdigest()
    for key in ("library", "coreLibrary")
}
symbols = subprocess.check_output(
    ["nm", "-D", "--defined-only", str(prefix / pins["coreLibrary"])], text=True
)
exports = {line.split()[-1] for line in symbols.splitlines()}
if not set(pins["exports"]).issubset(exports):
    raise SystemExit("native build is missing a pinned export")
receipt = {
    "schema": "lrm.native-toolchain/1",
    "version": pins["version"],
    "commit": pins["commit"],
    "submodules": actual,
    "patchSha256": pins["patchSha256"],
    "exports": pins["exports"],
    "files": files,
}
(prefix / ".lrm-native.json").write_text(json.dumps(receipt, indent=2) + "\n")
print("vkd3d-proton native build receipt: OK")
