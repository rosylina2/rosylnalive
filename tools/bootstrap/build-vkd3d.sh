#!/bin/sh
# Native development build from the recursive source revision pinned in toolchain.json.
# Run on the build host after verifying HEAD and every submodule; no downloads.
set -eu
source_dir=$1
prefix=$2
recipe_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
python3 -I "$recipe_dir/record-vkd3d.py" "$recipe_dir/toolchain.json" "$source_dir" "$prefix" --check-source
cd "$source_dir"
trap 'rc=$?; echo "LAYER-END rc=$rc"; date -u +%s.%N' EXIT
echo LAYER-START
date -u +%s.%N
time git apply --check "$recipe_dir/vkd3d-native-exports.patch"
time git apply "$recipe_dir/vkd3d-native-exports.patch"
time rm -rf -- "$prefix"
# enable_tests and enable_extras default to false in this revision.
time meson setup --buildtype release --wrap-mode nodownload --prefix "$prefix" build.64
time ninja -C build.64 install
python3 -I "$recipe_dir/record-vkd3d.py" "$recipe_dir/toolchain.json" "$source_dir" "$prefix"
