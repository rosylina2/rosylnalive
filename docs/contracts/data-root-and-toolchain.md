# DataRootAndToolchain contract

Owner: S0 (S0a; the tokenizer and SDK entries, local-copy verification and the bootstrap self-test are S0b's). Status: v1.2 (additive over v1.1): the manual native vkd3d-proton source pin and installation receipt. Consumers: every workstream that reads toolchains, weights, goldens, run logs or locks.

## Data root

- **What lives there.** `LRM_DATA` holds everything large or machine-local: `toolchain/`, `downloads/`, `models/`, `golden/`, `runs/`, `locks/`, `cache/`, `venv-golden/` and `spikes-staging/`. Nothing under it is ever committed, and the hygiene size check forbids the usual strays (`tools/ci/hygiene.json`: weights, `vk.xml`, SDK headers, packages, binaries).
- **Default.** It defaults to `C:/Users/end/dev/lrm-data`. The single source of that default is `dataRoot` in `tools/bootstrap/toolchain.json`, read in four places:
  - MSBuild: `build/Toolchain.g.props` resolves `$(LrmData)` from the `LrmData` property, else `LRM_DATA`, else `$(LrmDefaultData)`, the generated copy of `dataRoot`;
  - scripts: `tools/test.ps1` and `tools/bootstrap/bootstrap.ps1` read `toolchain.json`;
  - .NET code: Lrm.Core and Lrm.Testing carry the default as `[assembly: AssemblyMetadata("Lrm.DefaultDataRoot", …)]`. `Lrm.Testing.Env.dataRoot ()` and `Lrm.Core.LrmPaths.dataRoot ()` return `LRM_DATA` or that default.
- **Worktrees** use the same data root by absolute path. Nothing under it is per worktree.
- **On CI** the data root is `$RUNNER_TEMP/lrm-data`. Its `toolchain/` folder is cached and keyed on the hash of `toolchain.json`.

## Toolchain pins

`tools/bootstrap/toolchain.json` (schema `lrm.toolchain/1`) holds every pin: URL, size, hash and path under `LRM_DATA`. `tools/bootstrap/gen-toolchain-props.ps1` turns it into `build/Toolchain.g.props`, which provides these MSBuild properties:

- `$(LrmDxcExe)`, `$(LrmDxcVersion)` and `$(LrmDxcVersionOutput)`;
- `$(LrmVkXml)` and `$(LrmVkXmlVersion)`;
- `$(LrmVkHeaders)` and `$(LrmVkHeadersVersion)`;
- `$(LrmTokenizerJson)` and `$(LrmTokenizerSha256)`.

CI fails when the generated file is out of date. Builds and tools read these properties and never repeat a path, a version or a hash.

On a Linux host, `$(LrmDxcExe)` and `$(LrmDxcVersionOutput)` name the Linux build of the same DXC (`dxc.linux`). The generated file holds them in a group conditioned on `$([MSBuild]::IsOSPlatform('Linux'))`, so it is the same whichever host generates it, and Windows evaluates the entry's own values.

| Entry | Managed by | Kind | Approval | Install path under `LRM_DATA` |
| --- | --- | --- | --- | --- |
| `dxc` | bootstrap | `dxc-nupkg`; on Linux `tar` (`dxc.linux`) | approved (AP-1); CI runners download it | `toolchain/dxc/1.9.2609.5`; on Linux `toolchain/dxc/1.9.2609.5/linux` |
| `vkxml` | bootstrap | `file` | pending (AP-1) | `toolchain/vulkan/1.4.365/vk.xml` |
| `vkheaders` | bootstrap | `zip` | pending (AP-1) | `toolchain/vulkan-headers/1.4.365` |
| `tokenizer` | bootstrap | `file` | pending for CI runners (AP-1); the local copy is verified | `models/Qwen3-0.6B/tokenizer.json` |
| `vulkanSdk` | manual, optional | `installer` | not requested (AP-3, asked when V1 starts) | `toolchain/vulkan-sdk/1.4.363.0` |
| `vkd3dProton` | manual, Linux x64 only | `git-native` | approved recursive clone and native build | `toolchain/vkd3d-proton/3.0.1` |
| `goldenVenv` | manual | | | `venv-golden` |
| `model` | manual | | | `models/Qwen3-0.6B` |

## Bootstrap

`pwsh tools/bootstrap/bootstrap.ps1 [-AllowDownload] [-Only a,b] [-ComputePins]` handles every entry with `managedBy: bootstrap`, according to its `kind`:

| Kind | Entries | Verified on every run | Install |
| --- | --- | --- | --- |
| `dxc-nupkg` | dxc 1.9.2609.5 | SHA-256 of `dxc.exe`, `dxcompiler.dll`, `dxil.dll` and `dxv.exe`; the exact `--version` line; a valid Microsoft Authenticode signature | From the pinned nupkg (size and SHA-512 checked) in `downloads/`, extracted through a staging folder |
| `file` | vkxml 1.4.365, tokenizer | Size and SHA-256 | Downloaded to `.partial`, checked, moved into place |
| `zip` | vkheaders 1.4.365 | Every file against `.lrm-install.json`, which records the archive hash and each file's SHA-256; no extra files; any `files` pins | Archive (size and SHA-256 checked) in `downloads/`; the `extract` patterns, without the top folder, are extracted through a staging folder |
| `tar` | dxc 1.9.2609.5 on Linux (`dxc.linux`) | As `zip`, with the `files` pins of `bin/dxc`, `bin/dxv`, `bin/dxv-3.7`, `lib/libdxcompiler.so` and `lib/libdxil.so`; the exact `--version` line of its `exe`. There is no signature check: Authenticode is Windows-only | Gzip-compressed tar (size and SHA-256 checked) in `downloads/`; the regular files that match the `extract` patterns, without a leading `./`, are extracted with their Unix modes through a staging folder; a link entry that matches an extraction pattern fails the run |

- **An entry's Linux build.** An entry may carry a `linux` object; `dxc` does. On a Linux host, bootstrap installs that object in place of the entry's own fields, with the entry's `version` and `approval`; every other host, Windows included, reads the entry itself. A `linux` object that sets `version` or `approval` itself contradicts its entry, and bootstrap refuses the entry on Linux. `sha256` in a `linux` object of kind `tar` is the archive's, and `files` pins the binaries. The Linux `bin/dxc` and `lib/libdxcompiler.so` carry the rpath `$ORIGIN/../lib`, so dxc loads the pinned `lib/` ahead of any `LD_LIBRARY_PATH`.
- **Approval gates every download.** Only an entry whose `approval` says "approved" is installed. A pending or declined entry is never fetched, not even with `-AllowDownload`.
- **Pinned local copies are verified anyway.** A pending or declined `file` entry with `size` and `sha256` pinned is still verified when a copy is already at its install path:
  - a copy that matches the pins prints `OK (local copy verified; download not approved)`;
  - a copy that does not match fails the run;
  - a missing copy is skipped with a message.

  This is how the tokenizer, which came with the verified weights, is checked on the development machine while its CI download awaits approval. Every other unapproved entry is skipped.
- **Downloads happen only with `-AllowDownload`.** CI uses it, and so does a one-time local install after an approval.
- **Missing pins.** An approved `file`, `zip` or `tar` entry also needs its `url`, `size` and `sha256` pinned. `-ComputePins -AllowDownload` downloads it once into `downloads/pins/`, prints the values to pin, and installs nothing. The integrator checks the values against the upstream release and commits them.
- **Exit code.** It is 0 when every approved entry and every present pinned copy verifies. **A second run downloads nothing.**
- **Manual entries.** Bootstrap never touches `vulkanSdk`, `goldenVenv` or `model`.
  - The SDK entry records the optional AP-3 install: LunarG SDK 1.4.363.0, copied into its install folder with no registry, PATH or system change. Its validation layers are enabled per test process, never system-wide. Its URL, command line, size and hash are confirmed and pinned when AP-3 is asked.
- **Manual native D3D12 layer (Linux only).** `vkd3dProton` pins the repository, tag, full commit and every recursive submodule commit, the visibility-only patch SHA-256 and its four exported functions, the recipe, build prefix, install path and both shared-library paths. The Windows DLL release archive is not source. `tools/bootstrap/build-vkd3d.sh` checks the pristine source, applies the pinned patch with `git apply --check` first, removes the previous prefix and builds without downloads; `python3 -I record-vkd3d.py` (Python standard library plus git and nm, without a time-wrapper dependency) records its source identity, patch hash, export list and the SHA-256 of each installed library in `.lrm-native.json`. Copy or link the complete prefix under `LRM_DATA`. Bootstrap never clones or builds this manual entry: on Linux a missing installation prints a notice and permits CPU-only work; an existing installation must have a receipt matching the source pins and both libraries must match its hashes. Missing or modified files in an existing installation and mismatched versions fail. `-ComputePins` skips this manual entry. On first native load the interop checks receipt version, commit and patch identity; missing libraries raise `DllNotFoundException` naming the entry, path, recipe and bootstrap. Device discovery reports a missing installation as rejected, so the gpu tier's acceptance check still fails. Windows ignores this entry. The interop embeds `toolchain.json` and resolves its Linux libraries below `LrmPaths.dataRoot()`; there is no separate toolchain-path environment override. The native shader cache defaults to `LRM_DATA/cache/vkd3d-proton/<version>` (the layer's `VKD3D_SHADER_CACHE_PATH` may select a cache directory, never a library).
- **Current state (S0b).**
  - DXC is approved (AP-1).
  - vk.xml, Vulkan-Headers and the tokenizer's CI download are pending AP-1.
  - Locally, the tokenizer is verified. On CI runners it is absent until the download is approved.
  - Once an entry is approved, the integrator flips its `approval` and, for vk.xml and the headers, pins the hash with `-ComputePins`.
- **Self-test.** `tools/bootstrap/test-bootstrap.ps1` runs bootstrap in child processes, against a fixture `toolchain.json` (`-ToolchainPath`), a temporary data root and a loopback HTTP server that counts requests. CI's build-test job runs it after bootstrap. It checks the following:
  - an approved file downloads once, and the second run makes no request;
  - a pending entry is never requested;
  - a corrupted pending copy fails;
  - a missing approved file fails without `-AllowDownload`;
  - a download that misses its pin fails and leaves no `.partial` behind;
  - a `linux` object that sets its own approval never gets its pending entry fetched: Linux refuses the entry, every other host skips it as pending;
  - an installed tool's `--version` line must equal its pin exactly: one that differs only in case fails.
- **DXC regression.** CI runs `tools/bootstrap/dxc-regression.ps1` after bootstrap; on Linux it runs the Linux build (`dxc.linux`). It checks the pinned DXC's version line, then compiles `tools/bootstrap/dxc-regression/*.hlsl` and checks the facts the plan relies on (ADR `docs/decisions/0001-shader-toolchain.md`):
  - validated DXIL `cs_6_6`;
  - SPIR-V with one descriptor variable at set 0 binding 0 and one push-constant block;
  - the 92-byte `rootsig_1_1` root signature.

## Vectors

The pins below are the known answers at v1, and the Linux DXC rows since v1.1. `toolchain.json` is authoritative, and a change to any of them is a change to this contract.

| Pin | Value |
| --- | --- |
| Default data root | `C:/Users/end/dev/lrm-data` |
| DXC version line | `dxcompiler.dll: 1.9(5512-01b62ad4)(1.9.2609.5) - 1.9.2609.5 (01b62ad47)` |
| DXC nupkg | 53,395,273 bytes; SHA-512 (base64) `gV/8G+1EOTpuiDaIBe53sB75lABIcZrXWqUGAvNSNg6qW8u+kP9CVM5nA0MbUkdP8N5CiUU/BEPTRXR2AL6r4w==` |
| `dxc.exe` SHA-256 | `aa7965b3763d9c710baaa5a28cedcc633449962a75d1391d161a85304a41b3e2` |
| Linux DXC version line | `libdxcompiler.so: 1.9(5512-01b62ad4)(1.9.2609.5)` |
| Linux DXC archive | `linux_dxc_2026_09_28.x86_x64.tar.gz` of release v1.9.2609; 13,297,763 bytes; SHA-256 `96faadc7f5c282d2ffda49804beb4c3ee38127bc252b723234e3c5cdf7aa39a1` |
| Linux `bin/dxc` SHA-256 | `3dfc856b76291ffc7e45e21fd37ab80e85b34101de76a03253df6608785c92a0` |
| Qwen3-0.6B revision | `c1899de289a04d12100db370d81485cdf75e47ca` |
| `tokenizer.json` | 11,422,654 bytes; SHA-256 `aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4`. TokenizerApi checks the same hash |
| `model.safetensors` SHA-256 | `f47f71177f32bcd101b7573ec9171e6a57f4f4d31148d38e382306f42996874b` |
