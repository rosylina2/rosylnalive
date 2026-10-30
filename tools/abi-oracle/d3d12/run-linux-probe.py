"""Run with python3 -I tools/abi-oracle/d3d12/run-linux-probe.py on the native build host."""
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import time


def run(argv, **kwargs):
    print(shlex.join(map(str, argv)), flush=True)
    start = time.monotonic()
    try:
        return subprocess.run(list(map(str, argv)), check=True, **kwargs)
    finally:
        print(f"probe-command-wall={time.monotonic() - start:.3f}s", flush=True)


repo = Path(__file__).resolve().parents[3]
manifest = json.loads((repo / "tools/bootstrap/toolchain.json").read_text())
pin = manifest["vkd3dProton"]
source = Path(pin["source"])
build = source / "build.64"
if sys.platform != "linux" or not (source / "include/vkd3d.h").is_file() or not (build / "include/vkd3d_d3d12.h").is_file() or not shutil.which("gcc"):
    print(f"SKIP: Linux ABI probe requires gcc and the pinned native source/generated headers at {source} and {build}")
    sys.exit(0)

commit = run(["git", "-C", source, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
assert commit == pin["commit"], "native source commit differs from the pin"
modules = run(["git", "-C", source, "submodule", "status", "--recursive"], capture_output=True, text=True).stdout
assert all(line.startswith(" ") for line in modules.splitlines()), "submodule checkout differs"
assert {line.split()[1]: line.split()[0] for line in modules.splitlines()} == pin["submodules"], "submodule pins differ"
patch = repo / "tools/bootstrap" / pin["patch"]
assert hashlib.sha256(patch.read_bytes()).hexdigest() == pin["patchSha256"], "visibility patch hash differs"
original = run(["git", "-C", source, "show", "HEAD:include/vkd3d.h"], capture_output=True, text=True).stdout.splitlines(keepends=True)
expected, cursor, in_hunk = [], 0, False
for line in patch.read_text().splitlines(keepends=True):
    if line.startswith("@@"):
        start = int(re.match(r"@@ -(\d+)", line).group(1)) - 1
        expected.extend(original[cursor:start])
        cursor, in_hunk = start, True
    elif in_hunk:
        if line.startswith("+"):
            expected.append(line[1:])
        elif line.startswith("-"):
            cursor += 1
        else:
            expected.append(original[cursor])
            cursor += 1
expected.extend(original[cursor:])
assert (source / "include/vkd3d.h").read_text() == "".join(expected), "native header differs from pinned source plus patch"

install = Path(os.environ.get("LRM_DATA", manifest["dataRoot"])) / pin["install"]
receipt = json.loads((install / ".lrm-native.json").read_text())
assert receipt["schema"] == "lrm.native-toolchain/1", "receipt schema differs"
for key in ("version", "commit", "patchSha256", "submodules", "exports"):
    assert receipt[key] == pin[key], f"receipt {key} differs from pin"
libraries = [install / pin[key] for key in ("coreLibrary", "library")]
for library in libraries:
    relative = library.relative_to(install).as_posix()
    assert hashlib.sha256(library.read_bytes()).hexdigest() == receipt["files"][relative], f"library differs from receipt: {library}"
print("pinned source, patched header and opened library receipt hashes: PASS", flush=True)

output = repo / "artifacts/native-abi/linux-probe"
output.parent.mkdir(parents=True, exist_ok=True)
# Keep the complete compiler invocation here; no installed SDK headers or implicit project tool paths.
run(["gcc", "-std=gnu11", "-O2", "-Wall", "-Wextra", "-Werror",
     "-I", source / "include", "-I", build / "include",
     "-I", source / "include/private", "-I", source / "khronos/Vulkan-Headers/include",
     repo / "tools/abi-oracle/d3d12/linux-probe.c", "-o", output, "-ldl", "-lpthread"])
environment = os.environ.copy()
cache = Path(os.environ.get("LRM_DATA", manifest["dataRoot"])) / "cache/vkd3d-proton" / pin["version"]
cache.mkdir(parents=True, exist_ok=True)
environment.setdefault("VKD3D_SHADER_CACHE_PATH", str(cache))
run([output, *libraries], env=environment)
