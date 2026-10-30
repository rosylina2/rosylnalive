namespace Lrm.Gpu.D3D12

#nowarn "9"
#nowarn "51"

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Threading
open Lrm.Core
open Lrm.Gpu.Core
open Lrm.Gpu.D3D12.Interop
open Microsoft.FSharp.NativeInterop

/// One command allocator and list, with the timestamp query heap and readback buffer of the submission that uses it.
/// A frame is reused only after its fence value has completed.
[<Sealed; AllowNullLiteral>]
type internal Frame(index: int, allocator: ComPtr<ID3D12CommandAllocator>, list: ComPtr<ID3D12GraphicsCommandList>) =
    let mutable allocator = allocator
    let mutable list = list
    let mutable queries = ComPtr<ID3D12QueryHeap>.Null
    let mutable queryCapacity = 0
    let mutable stamps = ComPtr<ID3D12Resource>.Null

    member _.Index = index
    member _.Allocator = allocator
    member _.List = list
    member _.Queries = queries
    member _.Stamps = stamps
    member _.QueryCapacity = queryCapacity

    /// The fence value of the submission that last used the frame (0 before its first use).
    member val FenceValue = 0UL with get, set

    /// Runs when the frame's submission has completed, before the frame is reused (reads its timestamps).
    member val OnRetire: (Frame -> unit) option = None with get, set

    /// Makes room for `count` timestamp queries: a query heap and a READBACK buffer of 8 bytes per query.
    member _.EnsureQueries(device: ComPtr<ID3D12Device>, count: int) =
        if count > queryCapacity then
            ComPtr.release &queries
            ComPtr.release &stamps
            let capacity = max 64 (int (Numerics.BitOperations.RoundUpToPowerOf2(uint32 count)))
            let qdesc = Desc.queryHeap D3D12_QUERY_HEAP_TYPE.TIMESTAMP (uint32 capacity)
            queries <- device.CreateQueryHeap(&qdesc)
            queries.SetName(sprintf "lrm.d3d12.frame%d.timestamps" index)
            let props = Desc.heapProperties D3D12_HEAP_TYPE.READBACK
            let desc = Desc.buffer (uint64 capacity * 8UL) D3D12_RESOURCE_FLAGS.NONE

            stamps <-
                device.CreateCommittedResource(
                    &props,
                    D3D12_HEAP_FLAGS.NONE,
                    &desc,
                    D3D12_RESOURCE_STATES.COPY_DEST,
                    0n
                )

            stamps.SetName(sprintf "lrm.d3d12.frame%d.timestamp_readback" index)
            queryCapacity <- capacity

    /// The first `count` resolved timestamps, in GPU ticks.
    member _.ReadStamps(count: int) : uint64[] =
        if count = 0 then
            [||]
        else
            let mutable range = D3D12_RANGE(Begin = 0un, End = unativeint (8 * count))
            let p = stamps.Map(0u, NativePtr.toNativeInt &&range)
            let out = Array.zeroCreate<uint64> count

            let src =
                ReadOnlySpan<byte>(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> p), 8 * count)

            src.CopyTo(MemoryMarshal.AsBytes(Span out))
            let mutable written = D3D12_RANGE(Begin = 0un, End = 0un)
            stamps.Unmap(0u, NativePtr.toNativeInt &&written)
            out

    member _.Dispose() =
        ComPtr.release &queries
        ComPtr.release &stamps
        ComPtr.release &list
        ComPtr.release &allocator

/// A staging buffer on an UPLOAD or READBACK heap, and the fence value of its last use.
[<Sealed>]
type internal Staging(resource: ComPtr<ID3D12Resource>, bytes: int64, mapped: nativeint) =
    let mutable resource = resource

    member _.Resource = resource
    member _.Bytes = bytes
    /// The persistent mapping of an UPLOAD buffer (0 for READBACK buffers, which are mapped per read).
    member _.Mapped = mapped
    member val LastFence = 0UL with get, set

    member _.Dispose() =
        if mapped <> 0n then
            let mutable written = D3D12_RANGE(Begin = 0un, End = unativeint bytes)
            resource.Unmap(0u, NativePtr.toNativeInt &&written)

        ComPtr.release &resource

/// gpu.buffer_memory: a Gauge in bytes of the device memory the backend holds outside the heap's arenas (mem.arena),
/// per `buffer`: `upload_staging` and `readback_staging` (two buffers each), `copy_scratch`, and `probe_heap` (the
/// denormal probe's transient heap, back to 0 once it is released). Emitted whenever that amount changes.
[<RequireQualifiedAccess>]
module internal BufferMemory =
    [<Literal>]
    let Name = "gpu.buffer_memory"

    let report (buffer: string) (bytes: int64) =
        Telemetry.gauge
            Name
            (float bytes)
            TelemetryUnits.Bytes
            [
                Attr.str TelemetryKeys.Backend (ExecutionBackend.name ExecutionBackend.D3D12)
                Attr.str "buffer" buffer
            ]

/// What a native command list binds and how the encoder's targets resolve to resources.
type internal Bindings =
    {
        /// The shader-visible CBV/SRV/UAV heap of 64 descriptors, bound before the root signature.
        DescriptorHeap: ComPtr<ID3D12DescriptorHeap>
        RootSignature: ComPtr<ID3D12RootSignature>
        Pipeline: KernelId -> ComPtr<ID3D12PipelineState>
        Resource: Target -> ComPtr<ID3D12Resource>
    }

/// The device, its COMPUTE queue and fence, the pool of frames, the staging buffers and the scratch buffer. Every
/// submission is checked with GetDeviceRemovedReason (dev plan: after every submit), and every wait is bounded.
[<Sealed>]
type internal DeviceContext(device: ComPtr<ID3D12Device>, dredEnabled: bool, waitTimeout: TimeSpan) =
    let mutable device = device
    let qdesc = Desc.commandQueue D3D12_COMMAND_LIST_TYPE.COMPUTE
    let mutable queue: ComPtr<ID3D12CommandQueue> = device.CreateCommandQueue(&qdesc)

    let mutable fence: ComPtr<ID3D12Fence> =
        device.CreateFence(0UL, D3D12_FENCE_FLAGS.NONE)

    let event = new AutoResetEvent(false)
    let frequency = queue.GetTimestampFrequency()
    let mutable nextFence = 0UL
    let free = Stack<Frame>()
    let inFlight = Queue<Frame>()
    let mutable framesCreated = 0
    let uploads = ResizeArray<Staging>()
    let readbacks = ResizeArray<Staging>()
    let mutable scratch = ComPtr<ID3D12Resource>.Null
    let mutable disposed = false

    do
        queue.SetName "lrm.d3d12.queue"
        fence.SetName "lrm.d3d12.fence"

    member _.Device = device
    member _.Queue = queue
    member _.DredEnabled = dredEnabled

    /// The queue's timestamp frequency in ticks per second.
    member _.TimestampFrequency = frequency

    /// Frames created so far: the pool's size, which stays flat once the in-flight depth stops growing.
    member _.FramesCreated = framesCreated

    /// The last fence value signalled.
    member _.LastFence = nextFence

    /// GPU ticks to ns, exactly in integers.
    member _.TicksToNs(ticks: uint64) : int64 =
        let f = frequency
        int64 ((ticks / f) * 1_000_000_000UL + (ticks % f) * 1_000_000_000UL / f)

    /// Raises D3D12DeviceLostException, with DRED's report, when the device has been removed.
    member _.CheckRemoved(call: string) =
        let reason = device.GetDeviceRemovedReason()

        if reason <> HResult.S_OK then
            raise (D3D12DeviceLostException(call, reason, Dred.format (Dred.report dredEnabled device)))

    member x.Completed = fence.GetCompletedValue()

    /// Retires every frame whose submission has completed (running its OnRetire), so it can be reused.
    member x.Recycle() =
        let completed = fence.GetCompletedValue()

        while inFlight.Count > 0 && inFlight.Peek().FenceValue <= completed do
            let f = inFlight.Dequeue()

            match f.OnRetire with
            | Some retire ->
                f.OnRetire <- None
                retire f
            | None -> ()

            free.Push f

    /// Blocks until fence `value` has completed, at most the wait timeout, then retires completed frames. Raises
    /// D3D12DeviceLostException, with DRED's report, when the device was removed or the wait timed out.
    member x.WaitFor(value: uint64) =
        if value > nextFence then
            invalidArg "value" (sprintf "fence value %d was never signalled (last %d)" value nextFence)

        let clock = Diagnostics.Stopwatch.StartNew()

        while fence.GetCompletedValue() < value do
            if not (OperatingSystem.IsLinux()) then
                fence.SetEventOnCompletion(value, event.SafeWaitHandle.DangerousGetHandle())

            if fence.GetCompletedValue() < value then
                let remaining = waitTimeout - clock.Elapsed

                let timedOut =
                    if remaining <= TimeSpan.Zero then
                        true
                    elif OperatingSystem.IsLinux() then
                        // A null native event blocks without a timeout; a CLR handle is not a native event.
                        Thread.Yield() |> ignore
                        Thread.Sleep 1
                        clock.Elapsed >= waitTimeout
                    else
                        // A stale signal can wake the wait early; the loop then checks the fence again.
                        not (event.WaitOne remaining)

                if timedOut then
                    if fence.GetCompletedValue() < value then
                        let reason = device.GetDeviceRemovedReason()

                        raise (
                            D3D12DeviceLostException(
                                sprintf
                                    "fence wait for %d (completed %d after %O)"
                                    value
                                    (fence.GetCompletedValue())
                                    waitTimeout,
                                reason,
                                Dred.format (Dred.report dredEnabled device)
                            )
                        )

        x.CheckRemoved "fence wait"
        x.Recycle()

    /// Waits for everything submitted so far.
    member x.WaitIdle() =
        if nextFence > 0UL then
            x.WaitFor nextFence

    member private x.AcquireFrame() : Frame =
        x.Recycle()

        if free.Count > 0 then
            free.Pop()
        else
            let index = framesCreated
            framesCreated <- framesCreated + 1

            let allocator: ComPtr<ID3D12CommandAllocator> =
                device.CreateCommandAllocator D3D12_COMMAND_LIST_TYPE.COMPUTE

            let list: ComPtr<ID3D12GraphicsCommandList> =
                device.CreateCommandList(
                    0u,
                    D3D12_COMMAND_LIST_TYPE.COMPUTE,
                    allocator,
                    ComPtr<ID3D12PipelineState>.Null
                )

            allocator.SetName(sprintf "lrm.d3d12.frame%d.allocator" index)
            list.SetName(sprintf "lrm.d3d12.frame%d.list" index)
            list.Close()
            new Frame(index, allocator, list)

    /// Records `ops` into a frame's list (binding the heap and root signature first when the list dispatches),
    /// executes it, signals the fence and checks the removed reason. Returns the frame, whose FenceValue is the
    /// submission's, with `onRetire` to run once it has completed.
    member x.Execute
        (bindings: Bindings option)
        (queries: int)
        (ops: NativeOp list)
        (onRetire: (Frame -> unit) option)
        : Frame =
        if disposed then
            raise (ObjectDisposedException "D3D12Backend")

        let frame = x.AcquireFrame()

        try
            x.Record(frame, bindings, queries, ops)
        with _ ->
            // The frame was never submitted: close its list (Reset needs a closed list) and return it to the pool.
            try
                let list = frame.List
                list.Close()
            with _ ->
                ()

            free.Push frame
            reraise ()

        let list = frame.List
        queue.ExecuteCommandLists list
        nextFence <- nextFence + 1UL
        queue.Signal(fence, nextFence)
        frame.FenceValue <- nextFence
        frame.OnRetire <- onRetire
        inFlight.Enqueue frame
        x.CheckRemoved "ExecuteCommandLists"
        frame

    /// Resets a frame's allocator and list and records `ops` into it, then closes the list.
    member private _.Record(frame: Frame, bindings: Bindings option, queries: int, ops: NativeOp list) =
        let allocator = frame.Allocator
        let list = frame.List
        allocator.Reset()
        list.Reset(allocator, ComPtr<ID3D12PipelineState>.Null)

        if queries > 0 then
            frame.EnsureQueries(device, queries)

        let dispatching =
            ops
            |> List.exists (function
                | NativeOp.Dispatch _ -> true
                | _ -> false)

        let resource (t: Target) =
            match t with
            | Target.Scratch -> scratch
            | Target.Upload i -> uploads[i].Resource
            | Target.Readback i -> readbacks[i].Resource
            | Target.Page _ ->
                match bindings with
                | Some b -> b.Resource t
                | None -> invalidOp "a page op needs heap bindings"

        match bindings with
        | Some b when dispatching ->
            list.SetDescriptorHeaps b.DescriptorHeap
            list.SetComputeRootSignature b.RootSignature
        | _ -> ()

        let queryHeap = frame.Queries

        for op in ops do
            match op with
            | NativeOp.Barriers bs ->
                let arr =
                    bs
                    |> List.map (function
                        | BarrierOp.AllUav -> Desc.uavBarrier ComPtr<ID3D12Resource>.Null
                        | BarrierOp.Transition(t, before, after) ->
                            let state =
                                function
                                | PageState.Common -> D3D12_RESOURCE_STATES.COMMON
                                | PageState.UnorderedAccess -> D3D12_RESOURCE_STATES.UNORDERED_ACCESS
                                | PageState.CopySource -> D3D12_RESOURCE_STATES.COPY_SOURCE
                                | PageState.CopyDest -> D3D12_RESOURCE_STATES.COPY_DEST

                            Desc.transition (resource t) (state before) (state after))
                    |> Array.ofList

                list.ResourceBarrier(ReadOnlySpan arr)
            | NativeOp.Pipeline k ->
                match bindings with
                | Some b -> list.SetPipelineState(b.Pipeline k)
                | None -> invalidOp "a dispatch needs heap bindings"
            | NativeOp.Constants words -> list.SetComputeRoot32BitConstants(0u, ReadOnlySpan words, 0u)
            | NativeOp.Dispatch g -> list.Dispatch(uint32 g.X, uint32 g.Y, uint32 g.Z)
            | NativeOp.Copy(d, dOff, s, sOff, n) ->
                list.CopyBufferRegion(resource d, uint64 dOff, resource s, uint64 sOff, uint64 n)
            | NativeOp.Timestamp q -> list.EndQuery(queryHeap, D3D12_QUERY_TYPE.TIMESTAMP, uint32 q)
            | NativeOp.ResolveTimestamps n ->
                list.ResolveQueryData(queryHeap, D3D12_QUERY_TYPE.TIMESTAMP, 0u, uint32 n, frame.Stamps, 0UL)

        list.Close()

    /// A committed buffer of `bytes` on a heap of `kind`, named for DRED and debuggers. DEFAULT buffers allow
    /// unordered access and start in COMMON; UPLOAD buffers are GENERIC_READ and READBACK buffers COPY_DEST for life.
    member _.CreateBuffer(kind: D3D12_HEAP_TYPE, bytes: int64, name: string) : ComPtr<ID3D12Resource> =
        let props = Desc.heapProperties kind

        let flags, state =
            match kind with
            | D3D12_HEAP_TYPE.UPLOAD -> D3D12_RESOURCE_FLAGS.NONE, D3D12_RESOURCE_STATES.GENERIC_READ
            | D3D12_HEAP_TYPE.READBACK -> D3D12_RESOURCE_FLAGS.NONE, D3D12_RESOURCE_STATES.COPY_DEST
            | _ -> D3D12_RESOURCE_FLAGS.ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATES.COMMON

        let desc = Desc.buffer (uint64 bytes) flags

        let r: ComPtr<ID3D12Resource> =
            device.CreateCommittedResource(&props, D3D12_HEAP_FLAGS.NONE, &desc, state, 0n)

        r.SetName name
        r

    /// The two upload staging buffers, each at least `bytes` (at most Transfer.ChunkBytes). Only called with no
    /// transfer in flight (transfers wait for all submitted work first).
    member x.UploadStaging(bytes: int64) : Staging list =
        if uploads.Count = 0 || uploads[0].Bytes < bytes then
            for s in uploads do
                s.Dispose()

            uploads.Clear()

            for i in 0..1 do
                let r =
                    x.CreateBuffer(D3D12_HEAP_TYPE.UPLOAD, bytes, sprintf "lrm.d3d12.upload%d" i)

                let mutable none = D3D12_RANGE(Begin = 0un, End = 0un)
                let p = r.Map(0u, NativePtr.toNativeInt &&none)
                uploads.Add(new Staging(r, bytes, p))

            BufferMemory.report "upload_staging" (2L * bytes)

        List.ofSeq uploads

    /// The two readback staging buffers, each at least `bytes`.
    member x.ReadbackStaging(bytes: int64) : Staging list =
        if readbacks.Count = 0 || readbacks[0].Bytes < bytes then
            for s in readbacks do
                s.Dispose()

            readbacks.Clear()

            for i in 0..1 do
                let r =
                    x.CreateBuffer(D3D12_HEAP_TYPE.READBACK, bytes, sprintf "lrm.d3d12.readback%d" i)

                readbacks.Add(new Staging(r, bytes, 0n))

            BufferMemory.report "readback_staging" (2L * bytes)

        List.ofSeq readbacks

    /// Creates the scratch buffer for copies within one page, once.
    member x.EnsureScratch() =
        if scratch.IsNull then
            scratch <- x.CreateBuffer(D3D12_HEAP_TYPE.DEFAULT, Plan.ScratchBytes, "lrm.d3d12.scratch")
            BufferMemory.report "copy_scratch" Plan.ScratchBytes

    member _.HasScratch = not scratch.IsNull

    member x.Dispose() =
        if not disposed then
            try
                try
                    x.WaitIdle()
                with _ ->
                    ()
            finally
                disposed <- true

                while inFlight.Count > 0 do
                    free.Push(inFlight.Dequeue())

                for f in free do
                    f.Dispose()

                free.Clear()

                for s in uploads do
                    s.Dispose()

                for s in readbacks do
                    s.Dispose()

                uploads.Clear()
                readbacks.Clear()
                ComPtr.release &scratch
                event.Dispose()
                ComPtr.release &fence
                ComPtr.release &queue
                ComPtr.release &device
