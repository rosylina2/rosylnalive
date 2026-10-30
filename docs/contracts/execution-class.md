# ExecutionClass contract

Owner: S1 owns the type, the id and the CPU fill rule, all in Lrm.Core. R4a owns the GPU fill rule. Status: v1.2 (additive over v1.1): the GPU fill rule names native vkd3d-proton on the Linux x64 host as a separate class. Consumers: the models (M2), the cluster and serving (C1, C2), the GPU backends (D1, V1, R5), the executor (X1), run manifests (T1) and the gates (I1 to I3). Code: `src/Lrm.Core/ExecutionClass.fs`. Tests: `tests/Lrm.Core.Tests/ExecutionClassTests.fs`.

An ExecutionClass names everything that can change the bits a computation produces. **Bit-identity claims hold only between runs of one class.** Across classes, results are compared with tolerances (ToleranceSpec, KernelBounds). The type lives in Lrm.Core so that Models.Qwen and Cluster can name it without referencing a GPU project.

## The type

```fsharp
type ExecutionClass =
    { Backend: ExecutionBackend
      DeviceId: string
      Driver: string
      ShaderSetHash: ContentId option
      Arch: string }
```

| Field | Meaning |
| --- | --- |
| `Backend` | The backend that executes the work (table below) |
| `DeviceId` | The device model, stable across machines that have the same hardware. It is `cpu` for CPU backends |
| `Driver` | The driver or runtime version that executes the work |
| `ShaderSetHash` | The hash of the compiled shader set. Backends that run shaders must have it |
| `Arch` | The architecture and instruction-set features that can change results |

| Backend | Name | Runs shaders |
| --- | --- | --- |
| `CpuF32` | `cpu-f32` | no: the F# CPU reference in float32 |
| `CpuF64` | `cpu-f64` | no: the F# CPU reference in float64 |
| `CpuDevice` | `cpu-device` | no: the CPU emulation of the kernel catalog |
| `Vulkan` | `vulkan` | yes |
| `D3D12` | `d3d12` | yes |
| `D3D12Warp` | `d3d12-warp` | yes: Direct3D 12 on WARP |

The names are frozen: they are encoded into the id. `ExecutionBackend.tryParse` matches names ordinally.

## Validation

`ExecutionClass.validate` accepts a class when:
- **text fields** (`DeviceId`, `Driver` and `Arch`) are each 1 to 256 characters, with no control characters and no leading or trailing white space;
- **shader backends** (`vulkan`, `d3d12` and `d3d12-warp`) have a `ShaderSetHash`.

`ExecutionClass.id` raises `ArgumentException` for a class that does not validate.

## The id

`ExecutionClass.id c` is `ExecutionClassId(Canonical.hash "lrm.xclass/1" payload)`. The payload is the canonical encoding (LrmCore contract) of these fields, in this order:

1. the backend name, as a string;
2. `DeviceId`, as a string;
3. `Driver`, as a string;
4. `ShaderSetHash`, as an option of 32 raw bytes;
5. `Arch`, as a string.

Rules for using the id:
- **Comparison.** `ExecutionClass.same a b` compares ids.
- **Where it is recorded.** Run manifests, work units and results record the id. The cluster reissues a unit only to a node of the same class.
- **Encoding changes.** Any change to this encoding is a new tag, `lrm.xclass/2`.

## Fill rules

**CPU backends (S1).** `ExecutionClass.forCpu backend` fills a class for `cpu-f32`, `cpu-f64` or `cpu-device` on the current host:
- `DeviceId` = `cpu`.
- `Driver` = `RuntimeInformation.FrameworkDescription`, for example `.NET 10.0.13`.
- `ShaderSetHash` = None.
- `Arch` = `<os>-<arch>`, then `+avx2`, `+fma` and `+avx512f` for each feature the CPU has. `<os>` is `win` on Windows and `linux` on Linux, the two supported hosts, both x64. For example `win-x64+avx2+fma` or `linux-x64+avx2+fma`.

The runtime version and the ISA flags are part of the class because the runtime's and the C runtime's math functions can dispatch on them, so they can change the bits of `exp`, `sin` and similar functions. Plain IEEE arithmetic is the same everywhere; the class stays conservative so that bit-identity claims are never wrong.

**GPU backends (R4a).** The backends fill `DeviceId`, `Driver` and `ShaderSetHash` by R4a's rule, which is part of the GpuBackendInterface contract. `ShaderSetHash` is required, and `forCpu` refuses GPU backends.

### Linux D3D12 through vkd3d-proton (v1.2)

The backend remains `d3d12`. For the native Linux layer, `Driver` is `vkd3d-proton <version> (<source commit>) / <Vulkan driver name> <driver version> Vulkan <apiVersion>`, for example `vkd3d-proton 3.0.1 (3b10bd7a7ec6a7347e616cf8bea59333afec2255) / NVIDIA 610.57.04 Vulkan 1.4.341`. `DeviceId` keeps the R4a PCI model rule, `pci:<vendor>:<device>`, as on Windows. Identical GPUs with the same driver, layer and shader set share one class. The Vulkan UUID identifies the adapter in `DeviceInfo.Details` under `device_uuid`, outside the class. `Arch` keeps the GPU host rule (`linux-x64`); `ShaderSetHash` identifies the DXIL set.

The native layer version and source are pinned by DataRootAndToolchain. Its visibility-only patch exports the native entry points without changing computation. A change to the layer's computation requires a distinct layer identity. These classes differ from native Windows D3D12 even on the same hardware. Bit-identity claims never cross that boundary; the Windows fill rule and all frozen vectors remain unchanged.

## Vectors

The ids were computed independently with `sha256sum` over SHA-256(`lrm.xclass/1` || 00 || payload); the cpu tier checks them.

**X1, a CPU class:** `{ cpu-f32; "cpu"; ".NET 10.0.0"; None; "win-x64+avx2+fma" }`.

| Field | Bytes (hex) |
| --- | --- |
| backend `cpu-f32` | `07000000 6370752d663332` |
| DeviceId `cpu` | `03000000 637075` |
| Driver `.NET 10.0.0` | `0b000000 2e4e45542031302e302e30` |
| ShaderSetHash None | `00` |
| Arch `win-x64+avx2+fma` | `10000000 77696e2d7836342b617678322b666d61` |

The id is `2143ca35d54af553475a125be6abf185e914ecbe41eb400a80093b060c057eb0`.

**X2, a GPU class.** Its values are illustrative, because the real fill rule is R4a's: `{ vulkan; "pci:10de:2704"; "616.92"; Some SHA-256("abc"); "win-x64" }`.

| Field | Bytes (hex) |
| --- | --- |
| backend `vulkan` | `06000000 76756c6b616e` |
| DeviceId `pci:10de:2704` | `0d000000 7063693a313064653a32373034` |
| Driver `616.92` | `06000000 3631362e3932` |
| ShaderSetHash Some | `01 ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad` |
| Arch `win-x64` | `07000000 77696e2d783634` |

The id is `d79c93db855714890ff41356c0fd4dcb61e048b1fa346c87b7ca140506a3b057`.

## Verification

The tests in `ExecutionClassTests.fs` (cpu tier) check:
- that the backend names are frozen;
- vectors X1 and X2, both payload and id;
- that changing any one field changes the id;
- the validation rules;
- the CPU fill rule on the host, whose `Arch` starts with the running OS's x64 string (`win-x64` on Windows, `linux-x64` on Linux).

On the development machine the CPU class is `cpu-f32 cpu, .NET 10.0.13, win-x64+avx2+fma`.
