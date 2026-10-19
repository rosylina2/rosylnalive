# ShaderBuildAndSourceConvention contract

Owner: R4. R4a wrote v1; R4b finalized the SPIR-V binding (`LRM_BUF` on SPIR-V) and added the SPIR-V inspector as v1.1, following R1's ADR (VulkanHeapRecipe, `docs/decisions/R1-heap-recipe.md`). Status: v1.2, additive over v1.1 (which is for the contracts-v1.1 tag): a Linux host compiles with the Linux build of the same DXC. v1.1 is additive for every existing kernel (no blob changed) and tightens the rules for new ones: the SPIR-V inspector, and 64-bit types in the lint. VulkanHeapRecipe v1.1 section 7 describes the argument block below, so the two texts agree for the tag. Consumers: the kernels (K1 to K3), the backends (D1, V1, R5), E1 and X1. Code: `src/Lrm.Shaders/` (`include/lrm.hlsli`, `rootsig/rootsig.hlsl`, `hlsl/**`, `Lrm.Shaders.lrm.targets`, `Shaders.fs`), `src/Lrm.Shaders.Build/` (with the inspector fixtures in `fixtures/spirv/`, and the on-device check `typed-args-spike.ps1`) and `src/Lrm.Gpu.Core/ShaderSet.fs`, `SourceConvention.fs`, `Spirv.fs` (`Spirv`, `SpirvBinding`) and `SpirvInspector.fs`. Tests: `tests/Lrm.Gpu.Core.Tests/ShaderTests.fs`, `SpirvTests.fs`; CI step `src/Lrm.Shaders.Build/check-shaders.ps1`.

Every kernel is written once in HLSL 2021 and compiled by **one pinned compiler** to both targets. The compiled set is embedded in `Lrm.Shaders` with a manifest, and its **ShaderSetHash** enters every GPU ExecutionClass.

## The compiler

- **Only DXC 1.9.2609.5** from `LRM_DATA`, `$(LrmDxcExe)` (DataRootAndToolchain). Never the Windows Kits dxc, which has no SPIR-V backend.
- **On a Linux host** (v1.2) it is the Linux build of the same release, `dxc.linux` in `toolchain.json`: `bin/dxc`, `bin/dxv` and `bin/dxv-3.7`, with `lib/libdxcompiler.so` and `lib/libdxil.so`, which dxc loads through its rpath `$ORIGIN/../lib`. Its version line is `libdxcompiler.so: 1.9(5512-01b62ad4)(1.9.2609.5)`.
- Before compiling, the build checks the SPIR-V arguments against `SpirvBinding.spirvFlagProblems` (the binding, below), then that the file exists, that `dxc --version` prints exactly `$(LrmDxcVersionOutput)`, and that `dxc.exe`, `dxcompiler.dll`, `dxil.dll` and `dxv.exe` match their SHA-256 pins in `tools/bootstrap/toolchain.json` (on Linux, the five files above against the `files` pins of `dxc.linux`). **A missing or different DXC fails the build** with a pointer to `pwsh tools/bootstrap/bootstrap.ps1`.

| Target | Arguments (recorded in the manifest and hashed) |
| --- | --- |
| DXIL | `-T cs_6_6 -E main -HV 2021 -O3 -WX -denorm preserve` |
| SPIR-V | `-T cs_6_6 -E main -HV 2021 -O3 -WX -spirv -fspv-target-env=vulkan1.3` |
| Root signature | `-T rootsig_1_1 -E LRM_ROOTSIG` |

The build adds `-I include`, `-Fo <blob>` and the source path, relative to `src/Lrm.Shaders`; the blobs do not depend on the checkout path (verified with the pinned DXC). `-WX` makes every warning an error. No 16-bit types are enabled.

- **fp32 denormals.** DXIL gets **`-denorm preserve`** (`fp32-denorm-mode=preserve`, shader model 6.2 and later), as on the CPU reference. SPIR-V gets **no `-denorm`**: it would declare `OpCapability DenormPreserve` and `OpExecutionMode main DenormPreserve 32`, which the Vulkan spec allows only on devices with `shaderDenormPreserveFloat32` (VUID-RuntimeSpirv-shaderDenormPreserveFloat32-06297), and the RTX 4080 reports `false` under driver 616.92. The SPIR-V blobs therefore declare no float-controls mode, and each backend measures its denormal mode with the probe (GpuBackendInterface); bit-exact claims involving denormals are class-dependent (KernelAbiAndCatalog). The cpu tier checks that every SPIR-V blob has no denorm capability and LocalSize as its only execution mode.

## The build (LrmCompileShaders)

`src/Lrm.Shaders/Lrm.Shaders.lrm.targets` defines the target, which runs before resources are named in every build of `Lrm.Shaders`. `Lrm.Shaders.Build` is built first through the build-only edge `Lrm.Shaders -> Lrm.Shaders.Build` (ProjectGraph). The tool's `compile` step:

1. verifies the pinned DXC (above);
2. loads the catalog and refuses stale generated files (KernelAbiAndCatalog);
3. requires every `.hlsl` under `hlsl/` to be a catalog kernel at its catalog path; catalog kernels without a source are listed as **missing**;
4. lints every kernel source and every helper header (`.hlsli` under `hlsl/`) against the convention below, and checks that each helper a kernel includes exists beside it and that no helper has the name of a file in `include/`;
5. compiles each kernel to DXIL and SPIR-V, in parallel, through a **content-keyed cache**: the key is `Canonical.hash "lrm.shader-cache/2"` over the DXC version, the target and its flags, and the path and bytes of every file in the kernel's **include closure**: the source, the helpers it includes, and every file under `include/` (subfolders included). The convention makes that list exact: a kernel includes only `lrm.hlsli` and helpers beside it, and helpers include nothing. A kernel whose key is unchanged is not recompiled;
6. checks every blob, cached or not: DXIL is a DXBC container with a DXIL part, **signed** (non-zero container digest, written by the validator), with the catalog's numthreads in its PSV0 part; SPIR-V passes **every rule of the SPIR-V inspector** (below), which includes exactly one `LocalSize` equal to the catalog's threads. A blob that breaks a rule fails the build with one line per finding, and every SPIR-V report is emitted as telemetry;
7. compiles `rootsig/rootsig.hlsl` and fails unless `rootsig.bin` equals the canonical bytes;
8. writes the blobs, `rootsig.bin` and `shaders.manifest.json` to `$(IntermediateOutputPath)shaders/`, and removes blobs of kernels that no longer exist.

The target's inputs are the sources, the helpers, every file under `include/`, the root signature, `catalog.json`, `Kernels.g.fs`, `toolchain.json`, every assembly in the tool's folder (the tool, and `Lrm.Gpu.Core` with the lint, catalog, manifest and SPIR-V inspector code, whose implementation changes reference assemblies would otherwise hide), the DXC executable and two stamps, each rewritten only when its text changes:

- `sources.stamp` lists the input files, so **adding, deleting or renaming** a source or header reruns the compile step, which then prunes the blobs of kernels that are gone (a timestamp check alone never sees a deleted file);
- `dxc.stamp` holds the DXC path, its pinned version line, and the name and write time of every file beside `dxc.exe` (`dxc.exe`, `dxcompiler.dll`, `dxil.dll`, `dxv.exe`, the four SHA-256 pins). On Linux it holds the name and write time of every file in the Linux build's `bin/` and `lib/` (`dxc`, `dxv`, `dxv-3.7`, `libdxcompiler.so`, `libdxil.so`, the five pinned files).

Its output is the manifest. **A no-change rebuild skips the target, so dxc never starts**; pointing the build at another DXC, or replacing a DXC file in place, always reruns the compile step, which then refuses it. So an incremental build embeds the same set as a clean build of the same tree (CI checks this after editing headers and after adding and deleting a file). The outputs are embedded as resources named `lrm.shaders/<path>`: `shaders.manifest.json`, `rootsig.bin`, `dxil/<kernel>.dxil` and `spirv/<kernel>.spv`.

`Lrm.Shaders.Shaders.embedded ()` reads the set once. Loading (`ShaderSet.ofManifest`) verifies every blob's SHA-256 against the manifest, that the root signature is canonical, and that both ShaderSetHash values reproduce; any mismatch raises `GpuContractException`.

## The manifest (schema `lrm.shaders/1`)

```json
{
  "schema": "lrm.shaders/1",
  "dxcVersion": "dxcompiler.dll: 1.9(5512-01b62ad4)(1.9.2609.5) - 1.9.2609.5 (01b62ad47)",
  "catalogHash": "<64 hex>",
  "rootSignature": { "resource": "rootsig.bin", "bytes": 92, "sha256": "<64 hex>" },
  "targets": {
    "dxil":  { "shaderSetHash": "<64 hex>", "flags": [ ... ], "kernels": [ { "name", "resource", "bytes", "sha256", "threads": [x, y, z] } ] },
    "spirv": { ... }
  },
  "missing": [ "<catalog kernels without a source, in catalog order>" ]
}
```

It is deterministic: kernels sorted by name, no timestamps, no paths, UTF-8 with LF line ends. Two clean builds of one commit give the same manifest, byte for byte, and so the same ShaderSetHash (CI checks this on every run).

## ShaderSetHash

There is **one ShaderSetHash per target**, because a backend runs one target and its ExecutionClass should change only when its own blobs change. For target *t*:

`ShaderSetHash(t) = Canonical.hash "lrm.shaderset/1"` over the canonical encoding of, in order:
1. the target name, `dxil` or `spirv` (string);
2. the DXC version line (string);
3. the catalog hash (32 raw bytes);
4. the target's DXC arguments (a sequence of strings);
5. for DXIL, `Some` SHA-256 of `rootsig.bin`; for SPIR-V, `None` (an option of 32 raw bytes);
6. the blobs of *t*, sorted by kernel name (ordinal), as a sequence of (kernel name string, blob SHA-256 as 32 raw bytes).

The GPU fill rule (GpuBackendInterface) puts the hash of the backend's target into its ExecutionClass. Missing kernels are not part of it; when K1 to K3 add kernels, both hashes change, as they should. A set built on Linux hashes the Linux version line, so its two hashes differ from those of a Windows build of the same commit, and a GPU class on a Linux-built set is never the same class as one on a Windows-built set.

## The root signature

`rootsig/rootsig.hlsl` defines `LRM_ROOTSIG` as `RootFlags(CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED), RootConstants(num32BitConstants=32, b0)`: a directly indexed heap and the 128-byte argument block as 32 root constants. The build compiles it with `-T rootsig_1_1 -E LRM_ROOTSIG`, and the result must equal **the canonical 92 bytes** (`RootSignature.canonical`), which D1 passes to `CreateRootSignature` for every PSO. Kernels do not embed a root signature.

| Bytes | Hex | Meaning |
| --- | --- | --- |
| 0..3 | `44584243` | `DXBC` |
| 4..19 | `14462d179f897b3ba160caa35fcb04a6` | container digest |
| 20..23 | `01000000` | version 1.0 |
| 24..27 | `5c000000` | 92 bytes |
| 28..31 | `01000000` | 1 part |
| 32..35 | `24000000` | part 0 at byte 36 |
| 36..43 | `52545330 30000000` | `RTS0`, 48 bytes |
| 44..55 | `02000000 01000000 18000000` | version 1.1; 1 parameter at byte 24 |
| 56..63 | `00000000 30000000` | 0 static samplers at byte 48 |
| 64..67 | `00040000` | flags `CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED` (0x400) |
| 68..79 | `01000000 00000000 24000000` | parameter 0: 32-bit constants, visibility all, payload at byte 36 |
| 80..91 | `00000000 00000000 20000000` | register b0, space 0, 32 values |

These are also the bytes the planning probe compiled (`LRM_DATA/spikes-staging/bindtest/rs.bin`), and `tools/bootstrap/dxc-regression.ps1` checks the 92-byte length on every CI run.

## lrm.hlsli

The one header every kernel includes first.

| Name | What it is |
| --- | --- |
| `LRM_ARGS(kernel)` | Declares `lrm_args` of type `lrm_args_<kernel>` (generated): a `ConstantBuffer` at `b0` on DXIL, a `[[vk::push_constant]]` block on SPIR-V |
| `LRM_NUMTHREADS(kernel)` | `[numthreads(...)]` from the catalog (`LRM_THREADS_<kernel>`) |
| `LRM_BUF(slot)` | The buffer in a heap slot: `ResourceDescriptorHeap[slot]` on DXIL; on SPIR-V `g_buf[slot]`, with `[[vk::binding(0, 0)]] RWByteAddressBuffer g_buf[] : register(u0, space0)` |
| `lrm_buf(slot)` | The same as a function returning `RWByteAddressBuffer` |
| `lrm_tensor(token)` | The decoded table row of a token (`lrm_tensor_t`, the 16 words in row order) |
| `LRM_GROUP_LINEAR(gid)` | `gid.y * lrm_args.lrm_groups_x + gid.x`, the logical group of a linear kernel |
| `lrm_load_f32`, `lrm_store_f32`, `lrm_load4_f32`, `lrm_store4_f32`, `lrm_load_u32`, `lrm_store_u32`, `lrm_load_i32`, `lrm_store_i32`, `lrm_load_bf16`, `lrm_load_bf16_bits`, `lrm_load2_bf16`, `lrm_bf16_widen` | Element access by index from the tensor's start; bf16 through u32 words, with no 16-bit types |
| `LRM_TABLE_SLOT`, `LRM_ROW_BYTES`, `LRM_DTYPE_*`, `LRM_FLAG_*`, `LRM_F32_QUIET_NAN_BITS` | The contract constants |

- **No `NonUniformResourceIndex`.** Every heap index comes from a table row selected by a token in the arguments, so it is dynamically uniform.

## The binding (final in v1.1)

R1's spike ran both SPIR-V binding variants on the RTX 4080 (driver 616.92) through the `VK_EXT_descriptor_heap` set/binding → heap mapping path. Both were bit-exact against the CPU and identical to D3D12, and the ADR chose the explicit array (VulkanHeapRecipe). v1.1 makes that binding final. Kernels do not change: they use only `LRM_BUF`, `lrm_buf`, `lrm_tensor` and `LRM_ARGS`.

| | DXIL (`cs_6_6`) | SPIR-V (`vulkan1.3`) |
| --- | --- | --- |
| Heap | `LRM_BUF(slot)` is `ResourceDescriptorHeap[slot]` (`CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED`) | `LRM_BUF(slot)` is `g_buf[slot]`, with `[[vk::binding(0, 0)]] RWByteAddressBuffer g_buf[] : register(u0, space0)`: the only resource variable, a runtime array of raw buffers at set 0, binding 0 |
| How a slot reaches the heap | Descriptor index *slot* of the one shader-visible heap | V1 maps set 0, binding 0 onto the heap (below), so `g_buf[slot]` is the descriptor at byte *slot* × `bufferDescriptorSize` |
| Arguments | `lrm_args`: `ConstantBuffer<lrm_args_<kernel>> : register(b0, space0)`, the 32 root constants | The same declaration with `[[vk::push_constant]]` (under `__spirv__` only, because DXIL rejects the attribute under `-WX`): push data at offset 0 |
| Argument word *i* | Byte 4·*i* of the cbuffer | Push-block member *i*, at `Offset` 4·*i* |

**The mapping values.** They are `SpirvBinding` in `Lrm.Gpu.Core`. V1 passes them in `VkDescriptorSetAndBindingMappingEXT` and `vkCmdPushDataEXT`, laid out as VulkanHeapRecipe section 5 shows.

| Value | Constant | Meaning |
| --- | --- | --- |
| 0 | `SpirvBinding.DescriptorSet` | `descriptorSet`, the set of `g_buf[]` |
| 0 | `SpirvBinding.Binding`, `SpirvBinding.FirstBinding` | The binding of `g_buf[]`, and the mapping's `firstBinding` |
| 1 | `SpirvBinding.BindingCount` | `bindingCount` |
| `0xC0` | `SpirvBinding.ResourceMask` | READ_ONLY_STORAGE_BUFFER \| READ_WRITE_STORAGE_BUFFER |
| 0 | `SpirvBinding.MappingSourceHeapWithConstantOffset` | `source` = `HEAP_WITH_CONSTANT_OFFSET` |
| 0 | `SpirvBinding.HeapOffset` | `heapOffset` |
| `bufferDescriptorSize` | `SpirvBinding.heapArrayStride` | `heapArrayStride`, queried from the device (16 on the RTX 4080) |
| 0, 128 | `SpirvBinding.PushDataOffset`, `SpirvBinding.PushDataBytes` | Push data: the whole 128-byte ArgBlock at offset 0, whatever size the kernel's block declares |

`SpirvBinding.resolve size slot` is heapOffset + (binding − firstBinding + *slot*) × heapArrayStride, which is *slot* × `bufferDescriptorSize`. The geometry of the heap itself (the reserved range and the heap size) is `Heap.vulkanGeometry` (HeapLayoutAndTensorTable).

**The argument block type.**
- LRM uses the generated per-kernel blocks of KernelAbiAndCatalog: `lrm_args_<kernel>`, with `uint lrm_groups_x` and then one 32-bit scalar per catalog field. Argument word *k* is member *k*, at byte 4·*k* in the D3D cbuffer layout and in DXC's push-constant layout.
- Push data and root constants are always the whole 128-byte ArgBlock, whatever size the block declares. A block of fewer than 32 words is legal on both APIs: D3D12 sets all 32 root constants, and Vulkan pushes all 128 bytes.
- The typed block names and types every word, and the inspector checks those against the catalog (rule `push-block`).
- **VulkanHeapRecipe v1.1 agrees.** Its v1 section 7 described the spike's own block, `struct { uint4 d[8]; }` read as `d[k >> 2][k & 3]`, with one push member at Offset 0, ArrayStride 16, 128 bytes. Both layouts put word *k* at byte 4·*k*, so the device reads the same bytes (the run below), but a module that followed v1's rule 2 would fail `push-block`. VulkanHeapRecipe v1.1 (section 7, and the matching lines of R1's ADR) now names these blocks: the push block is `lrm_args_<kernel>`, member *i* at Offset 4·*i*, at most 128 bytes, and push data is always the whole 128 bytes. The two texts agree, so contracts-v1.1 can be tagged.
- The spike's pitfall, a flat `uint d[32]` that DXIL packs into 500 bytes, concerns arrays. Scalar members pack at 4-byte offsets in the D3D cbuffer layout: DXC reports `args_echo`'s block as 128 bytes, with word 31 at offset 124.
- **Run on the device.** `src/Lrm.Shaders.Build/typed-args-spike.ps1` runs LRM's blocks through R1's spike, and anyone can rerun it:
  1. It exports R1's spike with `git archive`: HEAD once R1 has merged, `origin/ws/R1-vk-heap-spike` before that. The checkout does not change.
  2. It replaces the spike's `scale_f32.hlsl` and `args_echo.hlsl` with LRM's selftests and puts `lrm.hlsli` and `lrm_args.g.hlsli` beside them. Their typed blocks are 20 and 128 bytes. `table_echo` and `bf16_unpack` stay the spike's own, because their arguments and offsets differ.
  3. It builds the spike and runs its gpu tier. The mutexes are taken through Lrm.Testing, and the GPU part takes about 5 s.

  **Result** on the RTX 4080 (Vulkan driver 616.92, D3D12 UMD 32.0.16.1692), with the spike at `41df1b0`: run `20261018-021142-c850f044`, spike ShaderSetHash `39566d87…`, gpu tier 124 of 124.
  - All 85 gating checks are bit-exact against the CPU, with one digest per case across D3D12 and the 8 Vulkan configurations (two binding variants by three heap placements, and two mapping controls).
  - No poison word is left, every case is identical across its two runs, and all 106 negative controls are caught.
  - The only case that differs from the CPU is the informational `scale_f32.edges` (denormals and NaN; KernelAbiAndCatalog's denormal rule).
  - The spike's report shows that the swap took effect: its emulated variant now has 1 aliased variable in `args_echo` and `scale_f32`, against 2 and 3 with the spike's own sources.
  - An earlier run from a hand-made copy (`20261018-014350-8e09b518`) gave the same result.

**Flags.** The SPIR-V arguments must contain `-spirv` and `-fspv-target-env=vulkan1.3`. They must contain none of the flags below (`SpirvBinding.forbiddenSpirvFlags`), and the build refuses them before it compiles.

| Flag | Why |
| --- | --- |
| `-fvk-bind-resource-heap` | The binding is decided in `lrm.hlsli`, not by an emulation flag. The emulated form gives one aliased variable per access (fixture `emulated_heap`) |
| `-fspv-use-descriptor-heap` | Direct heap indexing (`SPV_EXT_descriptor_heap`) does not support UAVs in the pinned DXC |
| `-enable-16bit-types` | Kernels use no 16-bit types |
| `-denorm` | A SPIR-V denormal mode needs `shaderDenormPreserveFloat32` (fixture `denorm`) |
| `-fvk-use-dx-layout`, `-fvk-use-scalar-layout`, `-fvk-use-gl-layout` | The push block uses DXC's default Vulkan layout, which the offsets above assume |
| `-fspv-reflect` | No reflection decorations |

## The SPIR-V inspector

`SpirvInspector.inspect kernel bytes` reads one SPIR-V blob word by word and checks it against the kernel's catalog entry and the binding above.
- **Reading.** `Spirv.parse` reads the 5-word header, then instructions whose word counts must tile the module exactly. Only little-endian modules are accepted.
- **Result.** It returns every finding in rule order, each tagged with its rule. `SpirvInspector.check` raises `GpuContractException` listing them.
- **Every rule must hold for every SPIR-V blob.**

| Rule | What must hold | Why |
| --- | --- | --- |
| `module` | A well-formed module: version 1.6 (`0x00010600`, which `-fspv-target-env=vulkan1.3` emits), schema 0, exactly one `OpMemoryModel Logical GLSL450`, every literal string terminated, every instruction with its operands. Every id the inspector reads is above 0 and below the header's bound: the result ids of types, constants, variables, functions, labels and the instructions it follows, and the targets of names, decorations, the entry point and execution modes. Each of those result ids is defined once, and no decoration is applied twice to one target or member | Another target or addressing model leaves the binding model, for example `PhysicalStorageBuffer64` from `vk::RawBufferLoad`. A repeated decoration could carry a second `Binding`, `DescriptorSet` or `Offset` that disagrees with the one the rules read. An id outside the bound marks an invalid module |
| `capabilities` | Every capability is one of Shader, RuntimeDescriptorArray and StorageBufferArrayDynamicIndexing; Shader and RuntimeDescriptorArray are present | The recipe's device enables exactly its five features (VulkanHeapRecipe section 1). Any other capability (Float16, Int64, ShaderNonUniform, GroupNonUniform*, DenormPreserve, Image1D, ...) could need a feature the device lacks |
| `extensions` | Only `OpExtension "SPV_EXT_descriptor_indexing"` and `OpExtInstImport "GLSL.std.450"` | DXC declares descriptor indexing for `g_buf[]` (core since SPIR-V 1.5). GLSL.std.450 holds `exp`, `rsqrt`, `mad` (Fma) and the other math |
| `entry-point` | Exactly one `OpEntryPoint`: GLCompute, named `main`, naming a function of the module | One kernel per blob, with entry point `main` |
| `execution-mode` | Exactly one execution mode: `LocalSize` on the entry point, equal to the catalog's threads. Nothing is decorated `BuiltIn WorkgroupSize`, whatever the target | numthreads must match the catalog. A `WorkgroupSize` constant takes precedence over `LocalSize`, so it would set another size. No float-controls mode (`DenormPreserve`) and no `LocalSizeId` |
| `resource` | Exactly one module-scope variable has a `DescriptorSet` or `Binding` decoration, and it is `g_buf[]`: storage class StorageBuffer; set 0, binding 0 (`SpirvBinding`); not `NonWritable`, on the variable or on a member of its block; a pointer to a runtime array of `Block` structs whose one member, at `Offset` 0, is a runtime array of u32 with `ArrayStride` 4 (`RWByteAddressBuffer`). There is no other StorageBuffer variable | The mapping covers set 0, binding 0 only, so any other variable would be unbound |
| `push-block` | Exactly one PushConstant variable: a `Block` struct named `type.ConstantBuffer.lrm_args_<kernel>`, with 1 + F members for F catalog fields. Member *i* is at `Offset` 4·*i*, is named `lrm_groups_x` (*i* = 0) or after the catalog field, and is u32 (word 0, `token`, `u32`), i32 (`i32`) or f32 (`f32`). It spans at most 128 bytes | The push data is the ArgBlock. Offsets, names and types tie the blob to the catalog and to `LRM_ARGS(<kernel>)` |
| `storage-class` | Module-scope variables only in StorageBuffer, PushConstant, Input, Workgroup and Private storage; variables inside functions only in Function storage | No Uniform (cbuffers), UniformConstant (images), Output or other storage |
| `built-ins` | Every Input variable is a built-in: `WorkgroupId`, `LocalInvocationId` or `LocalInvocationIndex`. Every other `BuiltIn` decoration is on an Input variable or a member of an Input block (WorkgroupSize falls under `execution-mode`) | Kernels take only `SV_GroupID`, `SV_GroupThreadID` and `SV_GroupIndex`. `GlobalInvocationId` (`SV_DispatchThreadID`) is wrong on folded grids |
| `float-atomics` | No atomic instruction on a float: `OpAtomicFAddEXT`, `OpAtomicFMinEXT`, `OpAtomicFMaxEXT`, or any other atomic whose result, pointee or value is a float | The order of float atomics is not deterministic. `InterlockedExchange` on a float compiles to `OpAtomicExchange %float`, which needs no extra capability, so only this rule sees it (fixture `float_atomic`) |
| `atomics` | No other atomic instruction (`OpAtomicLoad` to `OpAtomicXor`, `OpAtomicFlagTestAndSet`, `OpAtomicFlagClear`) | KernelAbiAndCatalog v1 allows no atomics at all |
| `16-bit-types` | No `OpTypeInt 16` or `OpTypeFloat 16`, and no 16-bit capability (`Float16Buffer`, `Float16`, `Int16`, the 16-bit storage capabilities) | No 16-bit types |
| `scalar-width` | Every other `OpTypeInt` and `OpTypeFloat` is 32 bits | No 8-bit or 64-bit scalars: `shaderInt64` and `shaderFloat64` are not recipe features |
| `wave-ops` | No group, non-uniform group or subgroup instruction (opcodes 261 to 271, 333 to 366 and 4421 to 4432) | The wave size differs between vendors |
| `typed-views` | No `OpTypeImage`, `OpTypeSampler` or `OpTypeSampledImage` | No typed views or samplers |
| `workgroup-size` | The Workgroup variables total at most 32,768 bytes: scalars by width, vectors and arrays by count, structs by the sum of their members, with overflow checks, so a huge nested array cannot wrap around to a small size. A size that cannot be computed is a finding: a spec-constant length, a runtime array, a length below 1, or more than 2^63 − 1 bytes | KernelAbiAndCatalog's 32 KiB. DXC's SPIR-V backend does not enforce it, and the RTX 4080 would allow 48 KiB (fixture `groupshared_40k`) |

**Facts.** A report also carries what the inspector read (`SpirvFacts`):
- the version, the capabilities, the extensions and the instruction sets;
- `LocalSize`, the resource variables and the built-ins;
- the push block's name, its members (name, offset, type) and its size in bytes;
- the Workgroup bytes.

`SpirvInspector.markdown` renders them as a table, which CI puts in its step summary.

**Where it runs.**
1. **The shader build.** The compile step inspects every SPIR-V blob it compiles or takes from its cache, so no blob that breaks a rule is ever embedded. It prints `SPIR-V inspector: N blob(s) pass all 16 rules`.
2. **The cpu tier.** `SpirvTests.fs` inspects every embedded blob and checks its facts against the catalog and `SpirvBinding`. It also makes every rule fire on a word-level mutation of a real blob; each mutation must break exactly its rules, and no DXC is needed.
3. **CI**, in `check-shaders.ps1` after the Release build:
   - `Lrm.Shaders.Build inspect-spirv --dir <shaders>` runs over the built set, and its table goes into the step summary;
   - `Lrm.Shaders.Build spirv-fixtures` compiles the fixtures below with the pinned DXC.

**Fixtures.** The files `src/Lrm.Shaders.Build/fixtures/spirv/*.hlsl` are not kernels, and the shader build never sees them. Each header has three lines:
- `// kernel:` names the catalog kernel the fixture is inspected as;
- `// expect:` names the rules it must break, exactly, or `none`;
- `// flags:` (optional) adds DXC arguments.

`spirv-fixtures` compiles each fixture with the SPIR-V flags plus its own, inspects it, and fails unless the broken rules equal the expected ones. The fixtures are vectors of this contract:

| Fixture | Flags | Rules broken |
| --- | --- | --- |
| `cbuffer` | | `resource`, `storage-class` |
| `denorm` | `-denorm preserve` | `capabilities`, `execution-mode` |
| `device_address` | | `module`, `capabilities`, `scalar-width` |
| `dispatch_thread_id` | | `built-ins` |
| `emulated_heap` | `-fvk-bind-resource-heap 0 0` | `resource` (3 variables at set 0 binding 0) |
| `extra_binding` | | `resource` |
| `float_atomic` | | `float-atomics` |
| `groupshared_32k` | | none (exactly 32,768 bytes) |
| `groupshared_40k` | | `workgroup-size` |
| `half` | `-enable-16bit-types` | `capabilities`, `16-bit-types` |
| `int64` | | `capabilities`, `scalar-width` |
| `int_atomic` | | `atomics` |
| `nonuniform` | | `capabilities` (ShaderNonUniform) |
| `texture` | | `capabilities`, `resource`, `storage-class`, `typed-views` |
| `wave` | | `capabilities`, `wave-ops` |
| `wrong_args` | | `push-block` (`LRM_ARGS(add)` inspected as `scale_f32`) |
| `wrong_set` | | `resource` (set 1) |
| `wrong_threads` | | `execution-mode` |

**Telemetry.** The shader build calls `SpirvInspector.record` for every SPIR-V blob. The records use reserved `gpu.*` names (TelemetryFacade):

| Name | Kind | Value | Attributes |
| --- | --- | --- | --- |
| `gpu.shader.spirv_check` | Check | 1 when every rule holds | `kernel`, `target` (`spirv`), `findings` (a count), `rules` (the broken rules, comma-separated) |
| `gpu.shader.push_bytes` | Gauge, bytes | The push block's size | `kernel`, `target` |
| `gpu.shader.workgroup_bytes` | Gauge, bytes | The Workgroup bytes, against 32 KiB | `kernel`, `target` |

## The source convention

One kernel per file, at its catalog path, with entry point `main`. A kernel may include **helper headers**: `.hlsli` files beside it, a family's own helpers (for example `hlsl/kernels/basic/rows.hlsli`). The build lints every kernel (`SourceConvention.lint`) and every helper under `hlsl/` (`SourceConvention.lintHelper`), on the text without comments. Both follow the **code rules**:

- the only directives are `#include` (kernels only) and `#define`; no `#pragma`, no `#undef`, no other directive, and **no conditional compilation** (`#if`, `#ifdef`, `#ifndef`, `#elif`, `#else`, `#endif`), so both targets compile one body;
- no macro named `LRM_*` or `lrm_*`, and no `[[vk::...]]` attributes;
- none of these identifiers: `ResourceDescriptorHeap`, `SamplerDescriptorHeap`, `NonUniformResourceIndex`, `register`, `cbuffer`, `tbuffer`, `ConstantBuffer`, `ByteAddressBuffer`, `StructuredBuffer`, `RWStructuredBuffer`, `AppendStructuredBuffer`, `ConsumeStructuredBuffer`, `Buffer`, `RWBuffer`, `SamplerState`, `IncrementCounter`, `DecrementCounter`, `SV_DispatchThreadID`, `numthreads`, `vk` (SPIR-V-only intrinsics such as `vk::RawBufferLoad`);
- none of these scalar types, nor their vector (`T2` to `T4`) or matrix (`T1x1` to `T4x4`) forms: the 16-bit `half`, `min16float`, `min10float`, `min16int`, `min12int`, `min16uint`, `float16_t`, `int16_t` and `uint16_t`, and (v1.1) the 64-bit `double`, `float64_t`, `int64_t` and `uint64_t`. The SPIR-V inspector refuses the same widths (`16-bit-types`, `scalar-width`), so a kernel that uses one fails the lint before DXC runs, the same way for both targets;
- no identifier starting with `Wave`, `Quad`, `Interlocked`, `Texture`, `RWTexture` or `__` (compiler macros such as `__spirv__`).

A **kernel** also:

- includes `"lrm.hlsli"` first and once; every other include is a helper beside the kernel, named by a plain file name that ends in `.hlsli` and does not start with `lrm` (never a path), and the build checks that the file exists;
- uses `LRM_ARGS(<kernel>)` and `LRM_NUMTHREADS(<kernel>)` each exactly once, for this kernel, and defines exactly one `void main(...)`.

A **helper** also:

- has a file name that ends in `.hlsli`, does not start with `lrm` and is not the name of a file in `include/` (DXC searches the including file's folder first, so a helper may never shadow `lrm.hlsli` or `lrm_args.g.hlsli`);
- **includes nothing**: the kernel includes `lrm.hlsli` first and then each helper it uses;
- has no `LRM_ARGS`, no `LRM_NUMTHREADS` and no `main`.

So a kernel's include closure is its source, its helpers and `include/`, which is exactly what the cache key hashes. A kernel reaches arguments only through `LRM_ARGS`, memory only through `LRM_BUF`, `lrm_buf` and `lrm_tensor`, and groups only through `SV_GroupID` (with `LRM_GROUP_LINEAR`), `SV_GroupThreadID` and `SV_GroupIndex`. DXC enforces the rest: groupshared at most 32 KiB (the validator for `cs_6_6`; SPIR-V compiles the same body, and the SPIR-V inspector also sums its Workgroup variables) and warnings as errors. The inspector checks the compiled SPIR-V against the same rules from the other side, so a construct the lint misses still fails the build.

## Vectors

**S1, ShaderSetHash of a synthetic set.** DxcVersion `d`; CatalogHash SHA-256("c"); DXIL arguments `["-O3"]`; rootsig bytes `rs`; DXIL blobs `a` = SHA-256("A") and `b` = SHA-256("B"); nothing for SPIR-V. Computed independently in Python; `ShaderTests.fs` checks it.
- DXIL: `cdd90f127b63cf16e1c99616e7327d93de08e6aa9816fd5aeea2d5dbfca75241`
- SPIR-V: `ef3e37506c8bcee5603764779da39299a62edf3216681cc775d67637cc930e97`

**The contracts-v1 build** (the five selftests; the nine K1 to K3 kernels are missing), recomputed independently in Python from the manifest:
- DXIL ShaderSetHash `b12cabbf300be63253a5e5bf1dbba6aa6fd34d0ef3a6f4303e712ae6f148f4be`
- SPIR-V ShaderSetHash `a1a15c99c3fb545f908085c72e58afeeca4563dee6e546eca0e359d4c06064c9` (built without `-denorm preserve`; the earlier draft's `1314aecf...` had it)
- `rootsig.bin` SHA-256 `a54491c90354dcc8625ff97a8828a57eb4ad512ff605af44011067b236cc6972`

These two hashes change with every kernel K1 to K3 add; they are recorded here as the contracts-v1 baseline, not as constants.

**The contracts-v1.1 build.** Finalizing the binding changed no blob. Both ShaderSetHash values above, every blob's SHA-256 and `rootsig.bin` are byte-identical to contracts-v1; in `lrm.hlsli` only the comments and the explicit `space0` of `LRM_ARGS` changed. The inspector's facts of the five selftests, which `SpirvTests.fs` checks:

| Kernel | LocalSize | Push block: words, bytes | Built-ins | Capabilities | Workgroup bytes |
| --- | --- | --- | --- | --- | ---: |
| `args_echo` | 32x1x1 | 32, 128 | LocalInvocationId | Shader, RuntimeDescriptorArray | 0 |
| `bf16_unpack` | 256x1x1 | 4, 16 | WorkgroupId, LocalInvocationId | Shader, RuntimeDescriptorArray | 0 |
| `groups_echo` | 1x1x1 | 3, 12 | WorkgroupId | Shader, RuntimeDescriptorArray | 0 |
| `scale_f32` | 256x1x1 | 5, 20 | WorkgroupId, LocalInvocationId | Shader, RuntimeDescriptorArray | 0 |
| `table_echo` | 16x1x1 | 3, 12 | LocalInvocationId | Shader, RuntimeDescriptorArray | 0 |

- Every blob is SPIR-V 1.6, declares `OpExtension "SPV_EXT_descriptor_indexing"`, and has one resource variable, `g_buf`, at set 0 binding 0.
- The enumerant values the inspector uses were cross-checked against DXC's own disassembly (`-Fc`) of these blobs and of the fixtures. Among them: capabilities Shader 1, RuntimeDescriptorArray 5302, Float16 9 and GroupNonUniformArithmetic 63; built-ins WorkgroupId 26, LocalInvocationId 27 and GlobalInvocationId 28; opcodes OpAtomicExchange 229, OpAtomicIAdd 234 and OpGroupNonUniformFAdd 350; storage class Workgroup 4.
- With stub sources for all nine K1 to K3 kernels in a scratch tree, all 14 blobs pass. That covers the f32 fields `eps` and `scale`, and the 16x16x1 threads of `matmul_bf16w`.

## Verification

- **cpu tier, `ShaderTests.fs`** (v1). It checks:
  - the canonical root signature: the constant, the embedded bytes and the decoded fields;
  - that the embedded set loads and verifies, and that every catalog kernel is built or missing;
  - that every DXIL blob is a signed DXBC container with the catalog's numthreads, and that every SPIR-V blob has the catalog's LocalSize;
  - that the DXIL flags declare `-denorm preserve`, and that the SPIR-V flags and blobs declare no denormal mode;
  - that the manifest regenerates byte for byte; vector S1; that the hash covers blobs, compiler, catalog and flags; and that a tampered blob or hash is refused;
  - that the selftests are lint-clean, and that the kernel and helper lints reject each forbidden construct, among them a wave op and a path include in a helper.
- **cpu tier, `SpirvTests.fs`** (v1.1). It checks:
  - the `SpirvBinding` values and `resolve`, and the SPIR-V flag rules;
  - that `lrm.hlsli` declares the final binding, without the provisional marker;
  - that the reader parses every embedded blob and writes it back byte for byte, and rejects truncated, misaligned, big-endian, zero-count and magic-less input; and literal strings;
  - that every embedded blob passes, and that its facts match the catalog and the binding; and the selftest facts above;
  - word-level mutations of a real blob, at least one per rule (66 at v1.1), each of which must break exactly its rules;
  - a blob inspected as another kernel, and a blob of a kernel outside the catalog;
  - the rule names, the telemetry records and the Markdown table.
- **CI** (`check-shaders.ps1`, after the Release build):
  1. generated files are current;
  2. a clean rebuild into a fresh folder gives a byte-identical manifest (ShaderSetHash reproduces);
  3. a no-change rebuild skips `LrmCompileShadersCore`;
  4. adding a helper file reruns the target, and deleting it reruns the target again, after which the manifest equals a clean compile;
  5. on a scratch copy of the shader tree: editing a header in an `include/` subfolder, and editing a kernel's helper, each gives an incremental compile equal to a clean compile; deleting a kernel source prunes its blobs; and the build refuses a helper that uses a wave op or includes a path;
  6. a missing DXC and a DXC with another version line both fail;
  7. the SPIR-V inspector passes every built SPIR-V blob (`inspect-spirv`), and its table goes into the step summary;
  8. every fixture breaks exactly the rules it expects (`spirv-fixtures`).

  The DXC regression step keeps checking the bindless facts this design relies on.
- **gpu, local only** (`typed-args-spike.ps1`): LRM's typed argument blocks through R1's spike, as "The argument block type" describes. CI has no GPU, so the result is recorded there and in PR #12.

## Change log

- **v1** (R4a, PR #10): first version, with the SPIR-V binding provisional.
- **v1.1** (R4b, PR #12). Additive for every existing kernel: no blob changed, and both ShaderSetHash values are the v1 ones. It tightens the rules for new kernels:
  - the SPIR-V binding is final (R1's ADR, VulkanHeapRecipe): `g_buf[]` at set 0 binding 0, the `SpirvBinding` mapping values and the forbidden flags;
  - the typed per-kernel argument blocks stay, with the reasons above and the on-device check `typed-args-spike.ps1`. VulkanHeapRecipe v1.1 amends its section 7 to match (`int/wave1-fixes`);
  - the SPIR-V inspector (16 rules) runs in the shader build, the cpu tier and CI, with 18 DXC fixtures and 66 mutations, and emits `gpu.shader.spirv_check`, `gpu.shader.push_bytes` and `gpu.shader.workgroup_bytes`;
  - the source lint also refuses the 64-bit scalar types, and the vector and matrix forms of the 16-bit and 64-bit types.
- **v1.2** (`int/linux-host`). Additive: a Linux host compiles with the Linux build of the same DXC (`dxc.linux`). The build, `check-shaders.ps1` and the cpu tier's shader-set test read its pins and its version line there, and `dxc.stamp` lists the files in its `bin/` and `lib/`. Windows is unchanged. A Linux-built set's ShaderSetHash values hash the Linux version line.
