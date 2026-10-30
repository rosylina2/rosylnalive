# GpuBackendInterface contract

Owner: R4 (R4a); this contract also holds the GPU fill rule of the ExecutionClass contract. Status: v1.1, additive over v1 frozen at the contracts-v1 tag: native Linux D3D12 through vkd3d-proton. Consumers: the D3D12 and Vulkan backends (D1, V1, R5), the CPU device (E1), the executor (X1), serving (C2), and the apps and tests that wire backends. Code: `src/Lrm.Gpu.Core/Backend.fs`, `Dispatch.fs`, `RecordingBackend.fs`, `ShaderSet.fs`. Tests: `tests/Lrm.Gpu.Core.Tests/BackendTests.fs`.

Everything above the GPU APIs talks to a device through these interfaces, and only through them. They are small on purpose: one heap written once, one kernel set, command lists of dispatches, barriers, copies and timers, and a fence per submission. `Lrm.Gpu.Exec` never references a concrete backend; only apps and tests wire them (ProjectGraph).

## Interfaces

```fsharp
type IGpuBackend =
    inherit IDisposable
    abstract Info: DeviceInfo
    abstract ExecutionClass: ExecutionClass
    abstract CreateHeap: layout: HeapLayout -> IGpuHeap
    abstract LoadKernels: shaders: ShaderSet -> IKernelSet
    abstract Begin: unit -> ICommandList
    abstract Submit: commands: ICommandList -> Submission
    abstract Wait: submission: Submission -> Completion

type IGpuHeap =
    abstract Layout: HeapLayout
    abstract WriteTable: table: SealedTable -> unit
    abstract Table: SealedTable option
    abstract Upload: slot: int * offset: int64 * data: ReadOnlySpan<byte> -> unit
    abstract UploadFrom: slot: int * offset: int64 * source: Stream * length: int64 -> unit
    abstract Download: slot: int * offset: int64 * destination: Span<byte> -> unit

type ICommandList =
    abstract Dispatch: kernel: KernelId * args: ArgBlock -> unit
    abstract Barrier: unit -> unit
    abstract Copy: source: BufferRange * destination: BufferRange -> unit
    abstract BeginTimer: name: string * attrs: Attr list -> TimerId
    abstract EndTimer: timer: TimerId -> unit

type IKernelSet =
    abstract ShaderSetHash: ContentId
    abstract Contains: kernel: KernelId -> bool
```

### IGpuBackend

- **Threading.** Calls on one backend come from one thread at a time. Different backends are independent.
- **`Info`** describes the device (below).
- **`CreateHeap layout`** creates every page of the layout and the table buffer, and writes all 64 descriptors once (HeapLayoutAndTensorTable). **Exactly once per backend**; a second call raises.
- **`LoadKernels shaders`** creates one pipeline per blob of the backend's target in the set: DXIL for `d3d12` and `d3d12-warp`, SPIR-V for `vulkan`. **Exactly once per backend.** It refuses a set built for another catalog (`CatalogHash` differs from `Kernels.catalog`). D3D12 builds every PSO with the set's `rootsig.bin`.
- **`ExecutionClass`** is known once kernels are loaded: shader backends raise before `LoadKernels`. It follows the GPU fill rule below. A CPU backend (CpuDevice) returns `ExecutionClass.forCpu`.
- **`Begin`** returns a new, empty command list.
- **`Submit commands`** closes the list and submits it. It returns the submission's fence value: 1, 2, 3, ... per backend. A list is submitted once; recording into it or submitting it again raises, and so does a list from another backend.
- **`Wait submission`** blocks until that submission has completed and returns its `Completion`: the number of dispatches and the timer results. Waiting again returns the same completion. On completion the backend emits the timers as telemetry (below).
- **`Dispose`** waits for outstanding work and frees the device.

### IGpuHeap

- **`WriteTable table`** writes the sealed table image (1 MiB) into slot 0, **exactly once**, before the first dispatch. The table must have been built against this heap's layout. A dispatch before it raises. Nothing else writes slot 0.
- **`Upload (slot, offset, data)`** copies host bytes into a page (slot 1..63). Offset and size are multiples of 4 and inside the page. Slot 0 is refused: the table goes through `WriteTable`.
- **`UploadFrom (slot, offset, stream, length)`** is `Upload` from a stream (for example a memory-mapped safetensors file), staged in chunks of **at most 64 MiB**, so a 1 GiB page never needs a 1 GiB staging buffer.
- **`Download (slot, offset, destination)`** copies bytes out of a page, or out of the table (slot 0). Same alignment and bounds rules.
- **Ordering.** `Upload`, `UploadFrom` and `Download` first wait for all submitted work, then transfer, and return when the transfer is complete. They are never recorded into a command list.

### ICommandList

- **`Dispatch (kernel, args)`** records one catalog kernel. The backend calls `Dispatch.prepare` with the heap's table: it checks the block and the tokens (KernelAbiAndCatalog, dispatch rules), computes the catalog grid, folds it above 65,535 groups, and writes argument word 0. A kernel missing from the loaded set raises.
- **`Barrier ()`** is a global barrier: every earlier command finishes its writes, and they are visible, before any later command starts. Without one, consecutive dispatches may overlap, so **every dependent dispatch is preceded by a barrier** (the executor inserts one between dependent dispatches). On Vulkan it is a global memory barrier between compute stages; on D3D12, a UAV barrier on all resources.
- **`Copy (source, destination)`** copies bytes between pages. Both ranges are in page slots (1..63, never the table), with positive equal sizes, offsets and sizes multiples of 4, inside their pages, and not overlapping. **Copy is state-correct on every backend and acts as a full barrier on both sides**: it sees every earlier command's writes, and every later command sees its writes. D3D12 tracks resource state per page and inserts the UAV-to-COPY transitions and back.
- **`BeginTimer (name, attrs)` / `EndTimer timer`** bracket GPU-timed regions with timestamps. `name` is a valid telemetry name, conventionally `gpu.*`. Timers nest and close in reverse order of opening; closing another timer, or submitting with a timer open, raises.

### IKernelSet

`ShaderSetHash` is the hash of the backend's target in the loaded set (ShaderBuildAndSourceConvention). `Contains kernel` is false for catalog kernels whose source is still missing.

## Errors

A broken rule raises **`GpuContractException`** on every backend, through the shared checks (`Dispatch.prepare`, `Dispatch.checkCopy`, `Dispatch.checkTransfer`) and the once-only rules. Tests can therefore expect the same exception from RecordingBackend, CpuDevice, D3D12 and Vulkan. Device loss and API failures are not contract violations; backends raise them as their own exceptions with the diagnostics (D3D12 removed reason and DRED, Vulkan device fault).

## Devices, providers and factories

```fsharp
type DeviceInfo =
    { Backend: ExecutionBackend; Index: int; Name: string
      VendorId: uint32; PciDeviceId: uint32; Driver: string
      MemoryBytes: int64; Details: (string * string) list }

type DeviceCandidate = { Info: DeviceInfo; Rejections: string list }   // Accepted = Rejections.IsEmpty

type DeviceOptions = { Backend: ExecutionBackend; Index: int option; Debug: bool; TimeDispatches: bool }

type IGpuBackendProvider =
    abstract Backend: ExecutionBackend
    abstract Enumerate: unit -> DeviceCandidate list
    abstract Create: device: DeviceInfo * options: DeviceOptions -> IGpuBackend
```

- **Providers.** Each backend project exposes one provider. Apps and tests register it with `Backend.register`; libraries never do (ProjectGraph).
- **`Backend.enumerate ()`** lists every device of every registered provider, accepted or rejected, in `ExecutionBackend` order and then the provider's own stable order. Rejected devices carry their reasons, so device checks can print ACCEPTED or the reasons.
- **`Backend.tryCreate options`** creates a backend on device `Index`, which must be accepted, or else on the first accepted device of that backend. It returns `Error` with the reasons when there is none. `BackendRegistry` is the same as a value, for tests.
- **`DeviceOptions`.** `Debug` enables the API's debug or validation layer when it is installed. `TimeDispatches` brackets every dispatch with timestamps and reports it as `gpu.dispatch`.
- **Requirements** (`Requirements`, also the rejection texts):
  - Vulkan: `VK_EXT_descriptor_heap` (required; devices without it are rejected), the features of R1's ADR (VulkanHeapRecipe), buffer descriptors of at most 16 bytes, and 128 bytes of push data. fp32 denormal support is **not** a requirement: the SPIR-V blobs declare no float-controls execution mode (ShaderBuildAndSourceConvention), so a device such as the RTX 4080, which reports `shaderDenormPreserveFloat32 = false` under driver 616.92, is accepted. Its denormal behaviour is measured instead (below).
  - D3D12: shader model 6.6 and resource binding tier 3 (`ResourceDescriptorHeap` indexing), and the 32 root constants of the canonical root signature. Every DXIL blob declares `fp32-denorm-mode=preserve` (shader model 6.2 and later); the probe below confirms it.

### fp32 denormals (measured, never a rejection)

Whether a device keeps fp32 denormals in kernel arithmetic is fixed by its device, driver and blobs, so it is a property of the ExecutionClass. Every shader backend measures it right after `LoadKernels`, with the probe in `Fp32Denormals`:

1. Dispatch `scale_f32` with `a = Fp32Denormals.ProbeScale` (0.5) over the 7 inputs `Fp32Denormals.probeInputs`, and download the 7 results.
2. `Fp32Denormals.classify` returns `Preserve` when the bits equal the CPU's (`expected Preserve`), `Flush` when they equal the CPU's with every denormal input and result replaced by zero of the same sign (`expected Flush`), and `Mixed` otherwise. Wrong normal controls raise `GpuContractException`, because then `scale_f32` itself failed.
3. Emit `gpu.fp32_denormals` and keep the mode in the run's device record, next to the ExecutionClass. `Fp32Denormals.recordProbe backend results` does steps 2 and 3 in one call and records the words too (`inputs`, `results`, `expected_preserve`, `expected_flush`, each as 7 space-separated 8-digit hex words), so the kernels report shows the bit table; backends use it. `Fp32Denormals.record backend mode` records the mode alone.

| Input bits | `Preserve` result | `Flush` result |
| --- | --- | --- |
| `3f800000` (1.0, control) | `3f000000` | `3f000000` |
| `01000000` (control) | `00800000` | `00800000` |
| `00800000` | `00400000` | `00000000` |
| `80800000` | `80400000` | `80000000` |
| `00000002` | `00000001` | `00000000` |
| `807ffffe` | `803fffff` | `80000000` |
| `00400000` | `00200000` | `00000000` |

D1 expects `Preserve` (the blobs declare it) and reports any other mode to the integrator as a finding. V1 records whatever the device gives. Bit-exact claims involving denormals are class-dependent (KernelAbiAndCatalog, kernel rules), so neither mode fails a gate by itself.

## The GPU fill rule (ExecutionClass)

A shader backend fills its `ExecutionClass` (type and id: ExecutionClass contract, S1) with `GpuClass.fill device shaders`:

| Field | Rule | RTX 4080, driver 616.92 |
| --- | --- | --- |
| `Backend` | `d3d12`, `d3d12-warp` or `vulkan` | |
| `DeviceId` | `pci:<vendor>:<device>`, four lowercase hex digits each, from the PCI ids (`DXGI_ADAPTER_DESC1.VendorId`/`DeviceId`; `VkPhysicalDeviceProperties.vendorID`/`deviceID`) | `pci:10de:2704` |
| `Driver`, D3D12 | The user-mode driver version from `IDXGIAdapter::CheckInterfaceSupport(__uuidof(IDXGIDevice))`, a LARGE_INTEGER, as four decimal 16-bit parts from the high word down (`GpuClass.d3d12Driver`) | `32.0.16.1692` |
| `Driver`, Vulkan | `VkPhysicalDeviceDriverProperties.driverInfo`, trimmed, when it is 1 to 256 printable ASCII characters; otherwise `driverVersion` as `0x` and 8 hex digits (`GpuClass.vulkanDriver`) | `616.92` |
| `ShaderSetHash` | The ShaderSetHash of the target the backend runs: DXIL for D3D12 and WARP, SPIR-V for Vulkan | per build |
| `Arch` | The host's OS and process architecture, for example `win-x64`, without CPU feature flags; for `d3d12-warp`, `ExecutionClass.hostArch ()` with its flags, because WARP rasterizes on the CPU (`GpuClass.arch`) | `win-x64` |

Bit-identity claims hold only within one class (ExecutionClass contract). A driver update therefore changes the class, and gates re-baseline on the driver recorded in the run manifest (dev plan, section 8).

### Linux D3D12 (v1.1)

On Linux the D3D12 backend enumerates Vulkan physical devices through its own minimal interop surface and creates a native vkd3d-proton device on the selected physical device. Selection is rechecked against the UUID and the Vulkan LUID before creation; when Vulkan reports an invalid LUID, the first eight UUID bytes are read little-endian and bit 32 is set, giving a non-zero high part. The resulting LUID is recorded and checked against `GetAdapterLuid()` after creation. The UUID is carried in the existing `DeviceInfo.Details` map as `device_uuid`, available through an accessor; `AdapterIdentity` remains source-compatible. DXGI is not loaded. The same shader-model and resource-binding requirements apply, and missing optional feature queries remain absent capabilities.

The layer's `Driver` follows ExecutionClass v1.2; `GpuClass.fill` supplies every class field, including the unchanged PCI model `DeviceId`. The backend records the layer and Vulkan driver in device reports. Windows debug-layer and DRED settings are unavailable on this native route and are reported as such. Linux fence waits poll `GetCompletedValue`, yielding and then sleeping one millisecond between reads, with the same deadline and timeout error as Windows (30 seconds in the backend, 10 seconds in the interop gpu tests). A CLR wait handle cannot implement a native event on Linux. The measured `SetEventOnCompletion(value, NULL)` blocking form is reserved for callers without a timeout, such as the ABI probes. Windows waits are unchanged.

Dedicated video memory is the largest Vulkan DEVICE_LOCAL heap, avoiding double-counting a separate BAR heap. The layer honours independent devices unless `ENABLE_VULKAN_RENDERDOC_CAPTURE=1` forces its LUID-keyed singleton path. Its `Forcing singleton device due to RenderDoc being enabled` INFO message is unconditional inside the independent-device branch and does not establish that singleton forcing occurred. The pinned recipe keeps `enable_renderdoc` false.

`VKD3D_CONFIG` and any set `VKD3D_SHADER_*` variables reach the native layer and are recorded by name and value in adapter `Details`. The gate sets none of these variables; the interop's default shader-cache path is also recorded after initialization. A missing native installation is a bootstrap notice, while device discovery reports a rejection naming the missing path and the build/bootstrap remedy. The gpu tier still fails when no adapter is accepted.

Known validation differences through the layer:

- vkd3d-proton 3.0.1 accepts `CreateCommittedResource` for a zero-width buffer with `S_OK`, retaining width zero and a healthy device, instead of returning `E_INVALIDARG`. This was reproduced by a standalone native C call. The interop invalid-input test preserves its Windows assertions and skips only this zero-width block when the adapter's recorded driver identifies this layer version, printing the gap and version; its DEFAULT-heap `Map` failure assertion still runs.

Later validation divergences follow the same rule: record evidence and a reason naming the affected layer version, without weakening the Windows assertion or applying a behavioral patch to the layer. Kernel failures remain failures.

## Telemetry

Every backend reports through the ambient telemetry facade with reserved names (TelemetryFacade), so T1's reports and the dashboard can chart memory, transfers and kernel time. The helpers are in `GpuTelemetry`; attribute `backend` is the ExecutionBackend name.

| Name | Kind | When | Attributes |
| --- | --- | --- | --- |
| `mem.arena` | Gauge, bytes | `CreateHeap`: one per arena with pages, and arena `table` for slot 0 | `backend`, `arena` |
| `gpu.heap.slots` | Gauge, count | `CreateHeap`: slots holding the table or a page | `backend` |
| `gpu.bytes_moved` | Meter, bytes | every `WriteTable`, `Upload`, `UploadFrom`, `Download` and recorded `Copy` | `backend`, `direction` (`upload`, `download`, `copy`) |
| `gpu.dispatches` | Counter | each submission: its dispatch count | `backend` |
| `gpu.dispatch` | GpuInterval | each dispatch, with `TimeDispatches` | `backend`, `kernel` |
| timer names (`gpu.*`) | GpuInterval | each timer, at `Wait` | `backend`, the timer's attributes |
| `gpu.wait` | Span | host time blocked in `Wait` | `backend` |
| `gpu.fp32_denormals` | Event | once per shader backend, after the denormal probe | `backend`, `mode` (`preserve`, `flush`, `mixed`), and from `recordProbe` `inputs`, `results`, `expected_preserve`, `expected_flush` |

`Completion.Timers` lists the same results: timers in the order they were opened, then the `gpu.dispatch` timings. Telemetry never changes a value.

## RecordingBackend

`RecordingBackend` implements the interfaces for unit tests and planners. It enforces every rule above with the shared checks and keeps page memory on the host, so `Upload`, `Download` and `Copy` move real bytes, but it **never runs a kernel**: each dispatch is checked by `Dispatch.prepare` and recorded with its grids and its final argument block (`Submissions`, `Recording.dispatches`). Timers get deterministic synthetic timestamps of 1,000 ns per command. Its default device is d3d12-like, with PCI ids 0 and driver `recording`. `Recording.provider` wraps a device for registry tests. E1's CpuDevice is the executing counterpart.

## Vectors

**G1, the d3d12 class of an RTX 4080** under driver 616.92 (UMD LARGE_INTEGER 9007199255791260 = `0x002000000010069c`), with SHA-256("abc") standing in for the ShaderSetHash: `{ d3d12; pci:10de:2704; 32.0.16.1692; Some SHA-256("abc"); win-x64 }`. Computed independently in Python; `BackendTests.fs` checks it.

| Field | Bytes (hex) |
| --- | --- |
| backend `d3d12` | `05000000 6433643132` |
| DeviceId `pci:10de:2704` | `0d000000 7063693a313064653a32373034` |
| Driver `32.0.16.1692` | `0c000000 33322e302e31362e31363932` |
| ShaderSetHash Some | `01 ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad` |
| Arch `win-x64` | `07000000 77696e2d783634` |

The id is `8a35b90b41224bbd538c3da9841e754973ae0358dff3bc2908d807cd0825034f`.

Driver strings: `d3d12Driver 9007199255791260` = `32.0.16.1692`; `vulkanDriver "616.92" 0x9a2e0000` = `616.92`; `vulkanDriver " " 0x9a2e0000` = `0x9a2e0000`.

## Verification

The cpu tier checks, through RecordingBackend and the embedded shader set: vector G1 and the driver strings; that `fill` takes the hash of the backend's own target; the class before and after `LoadKernels`; the once-only rules; recorded dispatches with folded grids and word 0; every dispatch rule; aliasing; byte-exact uploads, downloads and copies; the transfer and copy rules; single-use command lists; nested timers with their deterministic timings; the telemetry names and values; the registry; and the denormal probe's expected results, classification and event. D1 and V1 run the same interface rules on hardware, E1 on CpuDevice.
