/// gpu tier (d3d12): the device check on the real adapter, the refcount leak test, and the interop's own smoke
/// tests (a copy round trip and a ResourceDescriptorHeap dispatch with root constants and timestamps), each run
/// twice with a byte comparison. They exercise every slot D1 depends on first.
module Lrm.Gpu.D3D12.Interop.Tests.GpuTests

#nowarn "9"
#nowarn "51"

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open Expecto
open Lrm.Core
open Lrm.Gpu.D3D12.Interop
open Lrm.Testing
open Microsoft.FSharp.NativeInterop

/// The first accepted adapter's report, discovered once.
let private discovered = lazy (DeviceCheck.enumerate ())

let private accepted () =
    match DeviceCheck.firstAccepted discovered.Value with
    | Some r -> r
    | None -> failtestf "no D3D12 adapter is accepted: %A" (discovered.Value |> List.map (fun r -> r.Verdict))

/// A device on the first accepted adapter (high-performance order).
let private createDevice () : ComPtr<ID3D12Device> =
    let r = accepted ()

    if OperatingSystem.IsLinux() then
        LinuxAdapters.createDevice<ID3D12Device>
            r.Adapter.Index
            (DeviceCheck.deviceUuid r.Adapter).Value
            r.Adapter.Luid
            DeviceCheck.CreationFeatureLevel
    else
        use factory = DxgiApi.createFactory<IDXGIFactory6> 0u

        use adapter: ComPtr<IDXGIAdapter1> =
            factory.EnumAdapterByGpuPreference(uint32 r.Adapter.Index, DXGI_GPU_PREFERENCE.HIGH_PERFORMANCE)

        D3D12Api.createDevice<IDXGIAdapter1, ID3D12Device> adapter DeviceCheck.CreationFeatureLevel

/// A compute queue with one allocator, list and fence.
type private Context(dev: ComPtr<ID3D12Device>) =
    let qdesc = Desc.commandQueue D3D12_COMMAND_LIST_TYPE.COMPUTE
    let queue: ComPtr<ID3D12CommandQueue> = dev.CreateCommandQueue(&qdesc)

    let allocator: ComPtr<ID3D12CommandAllocator> =
        dev.CreateCommandAllocator D3D12_COMMAND_LIST_TYPE.COMPUTE

    let list: ComPtr<ID3D12GraphicsCommandList> =
        dev.CreateCommandList(0u, D3D12_COMMAND_LIST_TYPE.COMPUTE, allocator, ComPtr<ID3D12PipelineState>.Null)

    let fence: ComPtr<ID3D12Fence> = dev.CreateFence(0UL, D3D12_FENCE_FLAGS.NONE)
    let event = new AutoResetEvent(false)
    let mutable value = 0UL

    member _.Device = dev
    member _.Queue = queue
    member _.List = list

    /// Closes the list, runs it, waits (10 s at most), checks the removed reason, then resets for reuse.
    member _.SubmitAndWait() =
        list.Close()
        queue.ExecuteCommandLists list
        value <- value + 1UL
        queue.Signal(fence, value)

        if fence.GetCompletedValue() < value then
            if OperatingSystem.IsLinux() then
                let clock = Stopwatch.StartNew()
                let timeout = TimeSpan.FromSeconds 10.0

                while fence.GetCompletedValue() < value && clock.Elapsed < timeout do
                    Thread.Yield() |> ignore
                    Thread.Sleep 1

                if fence.GetCompletedValue() < value then
                    failtestf
                        "GPU work did not finish within 10 s (removed reason %s)"
                        (HResult.describe (dev.GetDeviceRemovedReason()))
            else
                fence.SetEventOnCompletion(value, event.SafeWaitHandle.DangerousGetHandle())

                if not (event.WaitOne(TimeSpan.FromSeconds 10.0)) then
                    failtestf
                        "GPU work did not finish within 10 s (removed reason %s)"
                        (HResult.describe (dev.GetDeviceRemovedReason()))

        Expect.equal (dev.GetDeviceRemovedReason()) HResult.S_OK "the device is not removed"
        allocator.Reset()
        list.Reset(allocator, ComPtr<ID3D12PipelineState>.Null)

    member _.Buffer(kind: D3D12_HEAP_TYPE, bytes: uint64, state: D3D12_RESOURCE_STATES) : ComPtr<ID3D12Resource> =
        let props = Desc.heapProperties kind

        let flags =
            if kind = D3D12_HEAP_TYPE.DEFAULT then
                D3D12_RESOURCE_FLAGS.ALLOW_UNORDERED_ACCESS
            else
                D3D12_RESOURCE_FLAGS.NONE

        let desc = Desc.buffer bytes flags
        dev.CreateCommittedResource(&props, D3D12_HEAP_FLAGS.NONE, &desc, state, 0n)

    member _.Barrier(r: ComPtr<ID3D12Resource>, before, after) =
        let b = Desc.transition r before after
        list.ResourceBarrier &b

    interface IDisposable with
        member _.Dispose() =
            event.Dispose()
            fence.Dispose()
            list.Dispose()
            allocator.Dispose()
            queue.Dispose()

let private write (r: ComPtr<ID3D12Resource>) (data: byte[]) =
    let p = r.Map(0u, 0n)
    Marshal.Copy(data, 0, p, data.Length)
    r.Unmap(0u, 0n)

let private read (r: ComPtr<ID3D12Resource>) (bytes: int) =
    let mutable range = D3D12_RANGE(Begin = 0un, End = unativeint bytes)
    let p = r.Map(0u, NativePtr.toNativeInt &&range)
    let out = Array.zeroCreate<byte> bytes
    Marshal.Copy(p, out, 0, bytes)
    let mutable written = D3D12_RANGE(Begin = 0un, End = 0un)
    r.Unmap(0u, NativePtr.toNativeInt &&written)
    out

/// A deterministic test pattern.
let private pattern (n: int) =
    Array.init n (fun i -> byte ((uint32 i * 2654435761u) >>> 24))

let private record () =
    let r = accepted ()
    Devices.record Backend.D3D12 r.Adapter.Description r.Adapter.DriverVersion None

let private copyRoundTrip () : byte[] =
    let n = 1 <<< 20
    let data = pattern n
    use dev = createDevice ()
    use ctx = new Context(dev)

    use upload =
        ctx.Buffer(D3D12_HEAP_TYPE.UPLOAD, uint64 n, D3D12_RESOURCE_STATES.GENERIC_READ)

    use gpu =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 n, D3D12_RESOURCE_STATES.COMMON)

    use readback =
        ctx.Buffer(D3D12_HEAP_TYPE.READBACK, uint64 n, D3D12_RESOURCE_STATES.COPY_DEST)

    write upload data
    ctx.Barrier(gpu, D3D12_RESOURCE_STATES.COMMON, D3D12_RESOURCE_STATES.COPY_DEST)
    ctx.List.CopyBufferRegion(gpu, 0UL, upload, 0UL, uint64 n)
    ctx.Barrier(gpu, D3D12_RESOURCE_STATES.COPY_DEST, D3D12_RESOURCE_STATES.COPY_SOURCE)
    ctx.List.CopyBufferRegion(readback, 0UL, gpu, 0UL, uint64 n)
    ctx.SubmitAndWait()
    let back = read readback n
    Expect.equal back data "the bytes came back unchanged"

    Telemetry.meter
        "gpu.bytes_moved"
        (float (2 * n))
        TelemetryUnits.Bytes
        [
            Attr.str TelemetryKeys.Backend "d3d12"
            Attr.str "case" "interop.copy_round_trip"
        ]

    back

/// The pinned DXC from tools/bootstrap/toolchain.json under LRM_DATA.
let private dxc () =
    let repo = Env.repoRoot ()

    use doc =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tools", "bootstrap", "toolchain.json")))

    let entry = doc.RootElement.GetProperty("dxc")

    let pin =
        if OperatingSystem.IsLinux() then
            entry.GetProperty("linux")
        else
            entry

    let rel = pin.GetProperty("exe").GetString()
    let exe = Path.Combine(Env.dataRoot (), rel)

    if not (File.Exists exe) then
        failtestf "pinned DXC not found at %s (run tools/bootstrap/bootstrap.ps1)" exe

    exe

/// The smoke kernel: C = A + B over float32, the buffers named by descriptor-heap indices in root constants
/// (the shape every LRM kernel has: SM 6.6 ResourceDescriptorHeap, 32 root constants at b0).
let private addSource =
    String.concat
        "\n"
        [
            "#define RS \"RootFlags(CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED), RootConstants(num32BitConstants=32, b0)\""
            "struct Args { uint a; uint b; uint c; uint n; };"
            "ConstantBuffer<Args> args : register(b0);"
            "[RootSignature(RS)]"
            "[numthreads(64, 1, 1)]"
            "void main(uint3 id : SV_DispatchThreadID) {"
            "  if (id.x >= args.n) return;"
            "  RWByteAddressBuffer A = ResourceDescriptorHeap[args.a];"
            "  RWByteAddressBuffer B = ResourceDescriptorHeap[args.b];"
            "  RWByteAddressBuffer C = ResourceDescriptorHeap[args.c];"
            "  C.Store(id.x * 4, asuint(asfloat(A.Load(id.x * 4)) + asfloat(B.Load(id.x * 4))));"
            "}"
            ""
        ]

let private compiledAdd =
    lazy
        (let dir = Path.Combine(Env.repoRoot (), "artifacts", "d3d12-interop-tests")
         Directory.CreateDirectory dir |> ignore
         let src = Path.Combine(dir, "add.hlsl")
         let out = Path.Combine(dir, "add.dxil")
         File.WriteAllText(src, addSource)

         let psi =
             ProcessStartInfo(dxc (), sprintf "-nologo -T cs_6_6 -E main -Fo \"%s\" \"%s\"" out src)

         psi.UseShellExecute <- false
         psi.RedirectStandardOutput <- true
         psi.RedirectStandardError <- true
         use p = Process.Start psi
         let err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd()
         p.WaitForExit()

         if p.ExitCode <> 0 then
             failtestf "dxc failed (%d): %s" p.ExitCode err

         File.ReadAllBytes out)

let private dispatchAdd () : byte[] =
    let n = 4096
    let bytes = n * 4
    let a = Array.init n (fun i -> float32 i)
    let b = Array.init n (fun i -> 0.5f + float32 (i % 7))
    let dxil = compiledAdd.Value
    use dev = createDevice ()
    use ctx = new Context(dev)

    let toBytes (xs: float32[]) =
        MemoryMarshal.AsBytes(ReadOnlySpan xs).ToArray()

    use upload =
        ctx.Buffer(D3D12_HEAP_TYPE.UPLOAD, uint64 (2 * bytes), D3D12_RESOURCE_STATES.GENERIC_READ)

    use bufA =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 bytes, D3D12_RESOURCE_STATES.COMMON)

    use bufB =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 bytes, D3D12_RESOURCE_STATES.COMMON)

    use bufC =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 bytes, D3D12_RESOURCE_STATES.COMMON)

    use readback =
        ctx.Buffer(D3D12_HEAP_TYPE.READBACK, uint64 bytes, D3D12_RESOURCE_STATES.COPY_DEST)

    use stamps =
        ctx.Buffer(D3D12_HEAP_TYPE.READBACK, 16UL, D3D12_RESOURCE_STATES.COPY_DEST)

    write upload (Array.append (toBytes a) (toBytes b))

    // Root signature (embedded in the DXIL) and pipeline.
    use pin = fixed dxil
    let p = NativePtr.toNativeInt pin

    use rootSig: ComPtr<ID3D12RootSignature> =
        dev.CreateRootSignature(0u, ReadOnlySpan dxil)

    let psoDesc = Desc.computePipeline rootSig p (unativeint dxil.Length)
    use pso: ComPtr<ID3D12PipelineState> = dev.CreateComputePipelineState(&psoDesc)

    // Three raw UAVs at descriptor indices 0, 1, 2 of a shader-visible heap.
    let heapDesc = Desc.descriptorHeap D3D12_DESCRIPTOR_HEAP_TYPE.CBV_SRV_UAV 64u true
    use heap: ComPtr<ID3D12DescriptorHeap> = dev.CreateDescriptorHeap(&heapDesc)

    let inc =
        dev.GetDescriptorHandleIncrementSize D3D12_DESCRIPTOR_HEAP_TYPE.CBV_SRV_UAV

    let start = heap.GetCPUDescriptorHandleForHeapStart()

    for (k, r) in List.indexed [ bufA; bufB; bufC ] do
        let mutable uav = Desc.rawBufferUav 0UL (uint32 n)
        let at = D3D12_CPU_DESCRIPTOR_HANDLE(ptr = start.ptr + unativeint (k * int inc))
        dev.CreateUnorderedAccessView(r, ComPtr<ID3D12Resource>.Null, NativePtr.toNativeInt &&uav, at)

    let qdesc = Desc.queryHeap D3D12_QUERY_HEAP_TYPE.TIMESTAMP 2u
    use queries: ComPtr<ID3D12QueryHeap> = dev.CreateQueryHeap(&qdesc)

    let l = ctx.List
    ctx.Barrier(bufA, D3D12_RESOURCE_STATES.COMMON, D3D12_RESOURCE_STATES.COPY_DEST)
    ctx.Barrier(bufB, D3D12_RESOURCE_STATES.COMMON, D3D12_RESOURCE_STATES.COPY_DEST)
    l.CopyBufferRegion(bufA, 0UL, upload, 0UL, uint64 bytes)
    l.CopyBufferRegion(bufB, 0UL, upload, uint64 bytes, uint64 bytes)

    // Three transitions in one call, through a span (the single-element form is used everywhere else).
    l.ResourceBarrier(
        ReadOnlySpan
            [|
                Desc.transition bufA D3D12_RESOURCE_STATES.COPY_DEST D3D12_RESOURCE_STATES.UNORDERED_ACCESS
                Desc.transition bufB D3D12_RESOURCE_STATES.COPY_DEST D3D12_RESOURCE_STATES.UNORDERED_ACCESS
                Desc.transition bufC D3D12_RESOURCE_STATES.COMMON D3D12_RESOURCE_STATES.UNORDERED_ACCESS
            |]
    )

    // Directly indexed heaps must be bound before the root signature.
    l.SetDescriptorHeaps heap
    l.SetComputeRootSignature rootSig
    l.SetPipelineState pso
    l.SetComputeRoot32BitConstants(0u, ReadOnlySpan [| 0u; 1u; 2u; uint32 n |], 0u)
    l.EndQuery(queries, D3D12_QUERY_TYPE.TIMESTAMP, 0u)
    l.Dispatch(uint32 (n / 64), 1u, 1u)
    l.EndQuery(queries, D3D12_QUERY_TYPE.TIMESTAMP, 1u)
    l.ResolveQueryData(queries, D3D12_QUERY_TYPE.TIMESTAMP, 0u, 2u, stamps, 0UL)
    ctx.Barrier(bufC, D3D12_RESOURCE_STATES.UNORDERED_ACCESS, D3D12_RESOURCE_STATES.COPY_SOURCE)
    l.CopyBufferRegion(readback, 0UL, bufC, 0UL, uint64 bytes)
    ctx.SubmitAndWait()

    let out = read readback bytes
    let c = MemoryMarshal.Cast<byte, float32>(ReadOnlySpan out).ToArray()

    for i in 0 .. n - 1 do
        if c[i] <> a[i] + b[i] then
            failtestf "C[%d] = %g, expected %g" i c[i] (a[i] + b[i])

    let ticks = MemoryMarshal.Cast<byte, uint64>(ReadOnlySpan(read stamps 16)).ToArray()
    let freq = ctx.Queue.GetTimestampFrequency()
    Expect.isGreaterThan freq 0UL "timestamp frequency"
    Expect.isGreaterThanOrEqual ticks[1] ticks[0] "timestamps are ordered"
    let toNs (t: uint64) = int64 (float t * 1e9 / float freq)

    Telemetry.gpuInterval
        "gpu.dispatch"
        (toNs ticks[0])
        (toNs (ticks[1] - ticks[0]))
        [
            Attr.str TelemetryKeys.Backend "d3d12"
            Attr.str TelemetryKeys.Kernel "interop.selftest_add"
            Attr.int "groups" (n / 64)
        ]

    out

let private leakTest () =
    let before = Com.liveReferences ()
    let mutable dev = createDevice ()

    do
        use ctx = new Context(dev)
        use r = ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, 65536UL, D3D12_RESOURCE_STATES.COMMON)
        let heapDesc = Desc.descriptorHeap D3D12_DESCRIPTOR_HEAP_TYPE.CBV_SRV_UAV 64u true
        use heap: ComPtr<ID3D12DescriptorHeap> = dev.CreateDescriptorHeap(&heapDesc)
        use asObject = r.QueryInterface<ID3D12Object>()
        use again = dev.AddRef()
        use child: ComPtr<ID3D12Device> = r.GetDevice()
        Expect.equal child.Ptr dev.Ptr "GetDevice returns the same device"
        r.SetName "lrm.interop.leak_test"
        heap.SetName "lrm.interop.leak_test.heap"
        ctx.SubmitAndWait()

    Expect.equal (Com.liveReferences ()) (before + 1L) "only the device is still owned"
    let remaining = dev.Release()
    dev <- ComPtr<ID3D12Device>.Null
    Expect.equal remaining 0u "the device's last Release returns 0: no reference leaked"
    Expect.equal (Com.liveReferences ()) before "no live references"

let private failures () =
    use dev = createDevice ()

    Expect.equal (dev.GetDeviceRemovedReason()) HResult.S_OK "a healthy device"
    let props = Desc.heapProperties D3D12_HEAP_TYPE.DEFAULT

    // Skip only the reproduced validation gap; the resource Map failure below still runs.
    if (accepted ()).Adapter.DriverVersion.StartsWith("vkd3d-proton 3.0.1 (", StringComparison.Ordinal) then
        printfn
            "SKIP: vkd3d-proton 3.0.1 zero-width validation only: CreateCommittedResource accepts a zero-width buffer instead of E_INVALIDARG (reproduced in native C)"
    else
        // A zero-width buffer is invalid: the device itself reports the failure.
        let bad = Desc.buffer 0UL D3D12_RESOURCE_FLAGS.NONE

        let e =
            try
                use _r: ComPtr<ID3D12Resource> =
                    dev.CreateCommittedResource(&props, D3D12_HEAP_FLAGS.NONE, &bad, D3D12_RESOURCE_STATES.COMMON, 0n)

                failtest "a zero-width buffer was created"
            with :? D3D12Exception as e ->
                e

        Expect.equal e.Code HResult.E_INVALIDARG "E_INVALIDARG"
        Expect.equal e.RemovedReason (Some HResult.S_OK) "the removed reason comes from the device"
        Expect.isFalse e.DeviceLost "not lost"

    // Mapping a default-heap buffer fails on the resource: the reason is found through ID3D12DeviceChild.
    let good = Desc.buffer 256UL D3D12_RESOURCE_FLAGS.NONE

    use r: ComPtr<ID3D12Resource> =
        dev.CreateCommittedResource(&props, D3D12_HEAP_FLAGS.NONE, &good, D3D12_RESOURCE_STATES.COMMON, 0n)

    let e =
        try
            r.Map(0u, 0n) |> ignore
            failtest "mapping a default-heap buffer succeeded"
        with :? D3D12Exception as e ->
            e

    Expect.equal e.Call "ID3D12Resource::Map" "call"
    Expect.equal e.RemovedReason (Some HResult.S_OK) "the removed reason comes from the owning device"
    Expect.equal (dev.GetDeviceRemovedReason()) HResult.S_OK "still healthy"

/// Struct returns whose hidden RetVal pointer is followed by other parameters, on the real device: D1 sizes placed
/// resources with GetResourceAllocationInfo.
let private retVal () =
    use dev = createDevice ()
    let d = Desc.buffer (1UL <<< 20) D3D12_RESOURCE_FLAGS.ALLOW_UNORDERED_ACCESS
    let one = dev.GetResourceAllocationInfo(0u, &d)
    Expect.equal one.SizeInBytes (1UL <<< 20) "a 1 MiB buffer takes 1 MiB"
    Expect.equal one.Alignment 65536UL "D3D12_DEFAULT_RESOURCE_PLACEMENT_ALIGNMENT"
    let two = dev.GetResourceAllocationInfo(0u, ReadOnlySpan [| d; d |])
    Expect.equal two.SizeInBytes (2UL <<< 20) "two buffers back to back"

    let p = dev.GetCustomHeapProperties(0u, D3D12_HEAP_TYPE.UPLOAD)
    Expect.equal p.Type D3D12_HEAP_TYPE.CUSTOM "an upload heap as a custom heap"
    Expect.equal p.CPUPageProperty D3D12_CPU_PAGE_PROPERTY.WRITE_COMBINE "write-combined"
    Expect.equal p.MemoryPoolPreference D3D12_MEMORY_POOL.L0 "system memory on a discrete GPU"
    Expect.equal (dev.GetDeviceRemovedReason()) HResult.S_OK "still healthy"

/// DRED, which D1 reads after a device removal. The settings come from D3D12GetDebugInterface in the in-box runtime
/// and apply to devices created afterwards in this process only; the test forces auto-breadcrumbs on for one device,
/// reads its DRED data, then restores the defaults.
let private dred () =
    match D3D12Api.tryGetDebugInterface<ID3D12DeviceRemovedExtendedDataSettings1>() with
    | ValueNone ->
        let reason =
            "ID3D12DeviceRemovedExtendedDataSettings1 is not available from this runtime"

        if OperatingSystem.IsLinux() then
            printfn "SKIP: %s" reason

        skiptest reason
    | ValueSome settings ->
        use settings = settings

        let set (e: D3D12_DRED_ENABLEMENT) =
            settings.SetAutoBreadcrumbsEnablement e
            settings.SetPageFaultEnablement e
            settings.SetBreadcrumbContextEnablement e

        set D3D12_DRED_ENABLEMENT.FORCED_ON

        try
            use dev = createDevice ()
            use data = dev.QueryInterface<ID3D12DeviceRemovedExtendedData1>()
            let mutable out = D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT1()
            let hr = data.GetAutoBreadcrumbsOutput1Raw(NativePtr.toNativeInt &&out)
            // A healthy device has nothing to report yet: S_OK with an empty list, or NOT_CURRENTLY_AVAILABLE.
            Expect.isTrue
                (hr = HResult.S_OK || hr = HResult.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
                (sprintf "GetAutoBreadcrumbsOutput1 with breadcrumbs on returned %s" (HResult.describe hr))

            Expect.equal (dev.GetDeviceRemovedReason()) HResult.S_OK "still healthy"
        finally
            set D3D12_DRED_ENABLEMENT.SYSTEM_CONTROLLED

// ---- the fp32 denormal probe (GpuBackendInterface contract), on the real scale_f32 DXIL blob ---------------------
//
// R1's spike saw fp32 denormals flushed on both GPUs with blobs built without a denormal mode. R4a's DXIL blobs are
// built with -denorm preserve (fp32-denorm-mode=preserve). This runs the probe the contract defines, through this
// interop and the embedded shader set, before D1 exists: scale_f32 with a = 0.5 over Fp32Denormals.probeInputs, with
// LRM's heap layout (slot 0 the tensor table, slot 1 a page) and the canonical root signature. Integrator ADR
// int-0001 records the result. The measurement goes to a run log (kind kernels) as gpu.fp32_denormals with the probe's
// words and a gpu.dispatch interval, so `lrm report` renders the bit table. A control runs the same probe on scale_f32
// compiled with the pinned DXC and the same flags without -denorm preserve, as R1's spike compiled its sources, so the
// run shows what the flag decides on this device and driver.

/// The probe's run log, created on first use; Main finishes it (finishRun).
let private probeRun =
    lazy
        (let r = accepted ()
         let shaders = Lrm.Shaders.Shaders.embedded ()

         let info: Lrm.Gpu.Core.DeviceInfo =
             {
                 Backend = ExecutionBackend.D3D12
                 Index = r.Adapter.Index
                 Name = r.Adapter.Description
                 VendorId = r.Adapter.VendorId
                 PciDeviceId = r.Adapter.DeviceId
                 Driver = defaultArg r.Adapter.UmdVersion r.Adapter.DriverVersion
                 MemoryBytes = int64 r.Adapter.DedicatedVideoMemory
                 Details = DeviceCheck.detailsOf r.Adapter
             }

         let flags =
             Lrm.Gpu.Core.ShaderSet.flagsFor Lrm.Gpu.Core.ShaderTarget.Dxil shaders
             |> String.concat " "

         let klass = Lrm.Gpu.Core.GpuClass.fill info shaders

         let run =
             Lrm.Telemetry.RunContext.Create(
                 { Lrm.Telemetry.RunOptions.create "kernels" "D3D12 fp32 denormal probe on scale_f32 (int-0001)" with
                     ExecutionClass = Some klass
                     GitSha = Env.commit ()
                     Params =
                         [
                             "probe", "scale_f32, a = 0.5, Fp32Denormals.probeInputs (GpuBackendInterface)"
                             "dxil_flags", flags
                             "driver", r.Adapter.DriverVersion
                             "source", "tests/Lrm.Gpu.D3D12.Interop.Tests (gpu tier)"
                         ]
                 }
             )

         printfn "denormal probe run log: %s" run.Directory
         run)

/// Finishes the probe's run log if the probe ran: ok when the exe passed, failed otherwise.
let finishRun (exitCode: int) =
    if probeRun.IsValueCreated then
        let run = probeRun.Value

        run.Finish(
            if exitCode = 0 then
                Lrm.Telemetry.RunStatus.Ok
            else
                Lrm.Telemetry.RunStatus.Failed
        )

/// The poison of the probe's output words: signalling-NaN patterns, which no multiply produces, different per run.
let private poisonWord (run: int) (i: int) = 0x7FA00000u + uint32 (run * 16 + i)

let mutable private probeRuns = 0

/// The DXIL flags of the embedded shader set.
let private dxilFlags () =
    Lrm.Gpu.Core.ShaderSet.flagsFor Lrm.Gpu.Core.ShaderTarget.Dxil (Lrm.Shaders.Shaders.embedded ())

/// The control blob: hlsl/selftest/scale_f32.hlsl compiled now with the pinned DXC (tools/bootstrap/toolchain.json under
/// LRM_DATA) and the shader set's DXIL flags without "-denorm preserve". Error with the reason when DXC is missing or
/// fails, and the test then skips.
let private controlBlob =
    lazy
        (let rec strip =
            function
            | "-denorm" :: _ :: rest -> strip rest
            | x :: rest -> x :: strip rest
            | [] -> []

         let flags = strip (dxilFlags ())
         let repo = Env.repoRoot ()

         use toolchain =
             JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tools", "bootstrap", "toolchain.json")))

         let exe =
             Path.Combine(Env.dataRoot (), toolchain.RootElement.GetProperty("dxc").GetProperty("exe").GetString())

         if not (File.Exists exe) then
             Error("the pinned DXC is missing: " + exe)
         else
             let out =
                 Path.Combine(Path.GetTempPath(), "lrm-denorm-control-" + string Environment.ProcessId + ".dxil")

             let psi =
                 ProcessStartInfo(
                     exe,
                     WorkingDirectory = Path.Combine(repo, "src", "Lrm.Shaders"),
                     RedirectStandardOutput = true,
                     RedirectStandardError = true,
                     UseShellExecute = false
                 )

             for a in flags @ [ "-I"; "include"; "-Fo"; out; "hlsl/selftest/scale_f32.hlsl" ] do
                 psi.ArgumentList.Add a

             use p = Process.Start psi
             let stdout = p.StandardOutput.ReadToEndAsync()
             let stderr = p.StandardError.ReadToEnd()
             p.WaitForExit()
             stdout.Wait()

             if p.ExitCode <> 0 then
                 Error(sprintf "dxc failed with %d: %s" p.ExitCode stderr)
             else
                 let bytes = File.ReadAllBytes out
                 File.Delete out
                 Ok(String.concat " " flags, bytes))

/// Runs the probe on one scale_f32 DXIL blob; its records carry `blob` (which blob) and `dxil_flags`.
let private probeWith (variant: string) (flags: string) (blob: byte[]) : byte[] =
    let run = Interlocked.Increment &probeRuns

    use _ =
        Telemetry.install (probeRun.Value.Telemetry.Child [ Attr.str "blob" variant; Attr.str "dxil_flags" flags ])

    let shaders = Lrm.Shaders.Shaders.embedded ()

    let inputs = Lrm.Gpu.Core.Fp32Denormals.probeInputs
    let n = inputs.Length
    let tableBytes = int Lrm.Gpu.Core.Heap.TableBytes
    let pageBytes = 65536
    let yOffset = 256

    // The tensor table: token 1 is x (slot 1, offset 0), token 2 is y (slot 1, offset 256), both f32 [7].
    let table = Array.zeroCreate<byte> tableBytes

    let row (token: int) (offset: int64) (flags: Lrm.Gpu.Core.TensorFlags) (name: string) =
        let r =
            Lrm.Gpu.Core.TensorRow.create 1 offset DType.F32 [ int64 n ] flags (Lrm.Gpu.Core.TensorRow.debugIdOf name)

        r.WriteTo(Span(table, token * Lrm.Gpu.Core.Heap.RowBytes, Lrm.Gpu.Core.Heap.RowBytes))

    row 1 0L Lrm.Gpu.Core.TensorFlags.ReadOnly "probe.x"
    row 2 (int64 yOffset) Lrm.Gpu.Core.TensorFlags.None "probe.y"

    // The page: x, then y poisoned.
    let page = Array.zeroCreate<byte> pageBytes

    for i in 0 .. n - 1 do
        BitConverter.TryWriteBytes(Span(page, 4 * i, 4), inputs[i]) |> ignore

        BitConverter.TryWriteBytes(Span(page, yOffset + 4 * i, 4), poisonWord run i)
        |> ignore

    let args = Array.zeroCreate<uint32> Lrm.Gpu.Core.ArgBlock.WordCount
    args[0] <- 1u // lrm_groups_x: ceildiv(7, 256) = 1 group
    args[1] <- 1u // x
    args[2] <- 2u // y
    args[3] <- uint32 n // n
    args[4] <- Lrm.Gpu.Core.ArgBlock.f32Bits Lrm.Gpu.Core.Fp32Denormals.ProbeScale // a
    let block = Lrm.Gpu.Core.ArgBlock.OfWords args

    use dev = createDevice ()
    use ctx = new Context(dev)

    use upload =
        ctx.Buffer(D3D12_HEAP_TYPE.UPLOAD, uint64 (tableBytes + pageBytes), D3D12_RESOURCE_STATES.GENERIC_READ)

    use tableBuf =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 tableBytes, D3D12_RESOURCE_STATES.COMMON)

    use pageBuf =
        ctx.Buffer(D3D12_HEAP_TYPE.DEFAULT, uint64 pageBytes, D3D12_RESOURCE_STATES.COMMON)

    use readback =
        ctx.Buffer(D3D12_HEAP_TYPE.READBACK, uint64 (4 * n), D3D12_RESOURCE_STATES.COPY_DEST)

    use stamps =
        ctx.Buffer(D3D12_HEAP_TYPE.READBACK, 16UL, D3D12_RESOURCE_STATES.COPY_DEST)

    write upload (Array.append table page)

    // The canonical root signature (rootsig.bin) and the scale_f32 pipeline, as D1 builds every PSO.
    use rootSig: ComPtr<ID3D12RootSignature> =
        dev.CreateRootSignature(0u, ReadOnlySpan shaders.RootSignature)

    use pin = fixed blob

    let psoDesc =
        Desc.computePipeline rootSig (NativePtr.toNativeInt pin) (unativeint blob.Length)

    use pso: ComPtr<ID3D12PipelineState> = dev.CreateComputePipelineState(&psoDesc)

    // LRM's heap: slot 0 the table, slot 1 the page, as raw UAVs at descriptor indices 0 and 1.
    let heapDesc =
        Desc.descriptorHeap D3D12_DESCRIPTOR_HEAP_TYPE.CBV_SRV_UAV (uint32 Lrm.Gpu.Core.Heap.SlotCount) true

    use heap: ComPtr<ID3D12DescriptorHeap> = dev.CreateDescriptorHeap(&heapDesc)

    let inc =
        dev.GetDescriptorHandleIncrementSize D3D12_DESCRIPTOR_HEAP_TYPE.CBV_SRV_UAV

    let start = heap.GetCPUDescriptorHandleForHeapStart()

    for (slot, r, bytes) in [ 0, tableBuf, tableBytes; 1, pageBuf, pageBytes ] do
        let mutable uav = Desc.rawBufferUav 0UL (uint32 (bytes / 4))
        let at = D3D12_CPU_DESCRIPTOR_HANDLE(ptr = start.ptr + unativeint (slot * int inc))
        dev.CreateUnorderedAccessView(r, ComPtr<ID3D12Resource>.Null, NativePtr.toNativeInt &&uav, at)

    let qdesc = Desc.queryHeap D3D12_QUERY_HEAP_TYPE.TIMESTAMP 2u
    use queries: ComPtr<ID3D12QueryHeap> = dev.CreateQueryHeap(&qdesc)

    let l = ctx.List
    ctx.Barrier(tableBuf, D3D12_RESOURCE_STATES.COMMON, D3D12_RESOURCE_STATES.COPY_DEST)
    ctx.Barrier(pageBuf, D3D12_RESOURCE_STATES.COMMON, D3D12_RESOURCE_STATES.COPY_DEST)
    l.CopyBufferRegion(tableBuf, 0UL, upload, 0UL, uint64 tableBytes)
    l.CopyBufferRegion(pageBuf, 0UL, upload, uint64 tableBytes, uint64 pageBytes)
    ctx.Barrier(tableBuf, D3D12_RESOURCE_STATES.COPY_DEST, D3D12_RESOURCE_STATES.UNORDERED_ACCESS)
    ctx.Barrier(pageBuf, D3D12_RESOURCE_STATES.COPY_DEST, D3D12_RESOURCE_STATES.UNORDERED_ACCESS)
    l.SetDescriptorHeaps heap
    l.SetComputeRootSignature rootSig
    l.SetPipelineState pso
    l.SetComputeRoot32BitConstants(0u, ReadOnlySpan(block.ToWords()), 0u)
    l.EndQuery(queries, D3D12_QUERY_TYPE.TIMESTAMP, 0u)
    l.Dispatch(1u, 1u, 1u)
    l.EndQuery(queries, D3D12_QUERY_TYPE.TIMESTAMP, 1u)
    l.ResolveQueryData(queries, D3D12_QUERY_TYPE.TIMESTAMP, 0u, 2u, stamps, 0UL)
    ctx.Barrier(pageBuf, D3D12_RESOURCE_STATES.UNORDERED_ACCESS, D3D12_RESOURCE_STATES.COPY_SOURCE)
    l.CopyBufferRegion(readback, 0UL, pageBuf, uint64 yOffset, uint64 (4 * n))
    ctx.SubmitAndWait()

    let out = read readback (4 * n)
    let results = MemoryMarshal.Cast<byte, uint32>(ReadOnlySpan out).ToArray()

    for i in 0 .. n - 1 do
        if results[i] = poisonWord run i then
            failtestf "probe word %d still holds its poison: scale_f32 wrote nothing" i

    // Raises GpuContractException when the normal controls are wrong (scale_f32 itself failed). The record carries the
    // words, so the report shows the bit table.
    let mode = Lrm.Gpu.Core.Fp32Denormals.recordProbe ExecutionBackend.D3D12 results

    let hex (xs: uint32[]) =
        xs |> Array.map (sprintf "%08x") |> String.concat " "

    printfn
        "fp32 denormal probe (d3d12, scale_f32, %s): %s\n  inputs   %s\n  results  %s\n  preserve %s\n  flush    %s"
        variant
        (Lrm.Gpu.Core.Fp32Denormals.name mode)
        (hex inputs)
        (hex results)
        (hex (Lrm.Gpu.Core.Fp32Denormals.expected Lrm.Gpu.Core.Fp32DenormalMode.Preserve))
        (hex (Lrm.Gpu.Core.Fp32Denormals.expected Lrm.Gpu.Core.Fp32DenormalMode.Flush))

    let ticks = MemoryMarshal.Cast<byte, uint64>(ReadOnlySpan(read stamps 16)).ToArray()
    let freq = ctx.Queue.GetTimestampFrequency()
    let toNs (t: uint64) = int64 (float t * 1e9 / float freq)

    Telemetry.gpuInterval
        "gpu.dispatch"
        (toNs ticks[0])
        (toNs (ticks[1] - ticks[0]))
        [
            Attr.str TelemetryKeys.Backend "d3d12"
            Attr.str TelemetryKeys.Kernel "scale_f32"
            Attr.str "case" "fp32_denormal_probe"
            Attr.int "groups" 1
        ]

    out

/// The probe on the embedded shader set's scale_f32 (built with -denorm preserve).
let private denormalProbe () : byte[] =
    let blob =
        match
            Lrm.Gpu.Core.ShaderSet.tryBlob "scale_f32" Lrm.Gpu.Core.ShaderTarget.Dxil (Lrm.Shaders.Shaders.embedded ())
        with
        | Some b -> b.Bytes
        | None -> failtest "the embedded shader set has no dxil/scale_f32"

    probeWith "shader set" (String.concat " " (dxilFlags ())) blob

/// The control: the same probe on scale_f32 built without -denorm preserve.
let private denormalControl () : byte[] =
    match controlBlob.Value with
    | Error why -> skiptest why
    | Ok(flags, blob) -> probeWith "control, no -denorm" flags blob

/// Sequenced: one GPU workload at a time, and the leak test compares the process-wide live-reference count.
let tests =
    testSequenced
    <| testList
        "d3d12.interop.gpu"
        [
            testCase "the device check accepts the GPU (SM 6.6+, binding tier 3)"
            <| fun () ->
                record ()
                let r = accepted ()

                match r.Capabilities with
                | Ok c ->
                    Expect.isGreaterThanOrEqual c.HighestShaderModel DeviceCheck.RequiredShaderModel "SM"
                    Expect.isGreaterThanOrEqual c.ResourceBindingTier DeviceCheck.RequiredBindingTier "tier"
                | Error e -> failtest e

                for x in discovered.Value do
                    if x.Adapter.Description.Contains "RTX 4080" then
                        Expect.equal x.Verdict Accepted "the RTX 4080 is ACCEPTED"

                if OperatingSystem.IsLinux() then
                    use dev = createDevice ()

                    Expect.equal
                        (dev.GetDeviceRemovedReason())
                        HResult.S_OK
                        "native creation succeeded on the selected adapter"
                else
                    use factory = DxgiApi.createFactory<IDXGIFactory6> 0u

                    use adapter: ComPtr<IDXGIAdapter1> =
                        factory.EnumAdapterByGpuPreference(uint32 r.Adapter.Index, DXGI_GPU_PREFERENCE.HIGH_PERFORMANCE)

                    Expect.isTrue
                        (D3D12Api.canCreateDevice adapter DeviceCheck.CreationFeatureLevel)
                        "D3D12CreateDevice with a null out pointer reports success"

            testCase "two Linux adapters retain distinct live devices and LUIDs"
            <| fun () ->
                if not (OperatingSystem.IsLinux()) then
                    let reason = "native layer multi-adapter identity test requires Linux"
                    printfn "SKIP: %s" reason
                    skiptest reason

                let adapters = discovered.Value |> List.filter (fun r -> r.Verdict = Accepted)

                match adapters with
                | first :: second :: _ ->
                    let create r =
                        LinuxAdapters.createDevice<ID3D12Device>
                            r.Adapter.Index
                            (DeviceCheck.deviceUuid r.Adapter).Value
                            r.Adapter.Luid
                            DeviceCheck.CreationFeatureLevel

                    use one = create first
                    use two = create second

                    let luid (dev: ComPtr<ID3D12Device>) =
                        let value = dev.GetAdapterLuid()
                        (int64 value.HighPart <<< 32) ||| int64 value.LowPart

                    Expect.notEqual one.Ptr two.Ptr "simultaneously live devices differ"
                    Expect.equal (luid one) first.Adapter.Luid "first selected adapter"
                    Expect.equal (luid two) second.Adapter.Luid "second selected adapter"
                    Expect.notEqual (luid one) (luid two) "distinct physical adapter identities"

                    Expect.notEqual
                        (DeviceCheck.deviceUuid first.Adapter)
                        (DeviceCheck.deviceUuid second.Adapter)
                        "distinct Vulkan UUIDs"

                    printfn
                        "two live adapters: UUID %s LUID %016x; UUID %s LUID %016x; distinct device pointers and matching GetAdapterLuid"
                        (DeviceCheck.deviceUuid first.Adapter).Value
                        (luid one)
                        (DeviceCheck.deviceUuid second.Adapter).Value
                        (luid two)
                | _ ->
                    let reason =
                        "two accepted adapters are required to check simultaneous native devices"

                    printfn "SKIP: %s" reason
                    skiptest reason

            testCase "refcounts balance: the device's last Release returns 0" leakTest
            testCase "failed calls raise D3D12Exception with the removed reason" failures
            testCase "struct returns come back through RetVal before other parameters" retVal
            testCase "DRED settings and data are reachable" dred
            Determinism.testTwice "a 1 MiB copy round trip is bit-identical twice" copyRoundTrip
            Determinism.testTwice "a ResourceDescriptorHeap dispatch is bit-identical twice" dispatchAdd
            Determinism.testTwice
                "the fp32 denormal probe on the real scale_f32 blob is bit-identical twice"
                denormalProbe
            Determinism.testTwice
                "control: the same probe on scale_f32 built without -denorm is bit-identical twice"
                denormalControl
        ]

/// bench tier: the cost of one COM call through Vtbl.get and a cached delegate (AddRef and Release on a DXGI factory,
/// so the callee is real native code). Informational; reported as a gpu.interop.call histogram in ns.
let bench =
    [
        testCase "a COM call through a cached delegate"
        <| fun () ->
            if OperatingSystem.IsLinux() then
                printfn "SKIP: the IDXGIFactory6 COM benchmark requires Windows DXGI"
                skiptest "the IDXGIFactory6 COM benchmark requires Windows DXGI"

            use f = DxgiApi.createFactory<IDXGIFactory6> 0u

            let pairs (n: int) =
                for _ in 1..n do
                    Com.addRefRaw f.Ptr |> ignore
                    Com.releaseRaw f.Ptr |> ignore

            pairs 200_000
            let n = 5_000_000
            let clock = Stopwatch.StartNew()
            pairs n
            let ns = clock.Elapsed.TotalNanoseconds / float (2 * n)
            printfn "bench: %.1f ns per COM call (Vtbl.get + cached delegate, IUnknown on IDXGIFactory6)" ns

            Telemetry.histogram
                "gpu.interop.call"
                ns
                TelemetryUnits.Nanoseconds
                [ Attr.str TelemetryKeys.Backend "d3d12"; Attr.str "path" "cached_delegate" ]
    ]
