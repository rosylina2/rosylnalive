/// cpu tier: the command encoder's state tracking, the adapter mapping, DRED parsing on fake runtime memory, the
/// selftest expectations against KernelBounds and CpuRef, transfer chunking, the hello arguments, and the tail-call
/// scan of the backend and this exe. No GPU is touched.
module Lrm.Gpu.D3D12.Tests.CpuTests

#nowarn "9"

open System
open System.Reflection
open System.Runtime.InteropServices
open Expecto
open Lrm.Core
open Lrm.Gpu.Core
open Lrm.Gpu.D3D12
open Lrm.Gpu.D3D12.Interop
open Lrm.Gpu.D3D12.Tests.Common
open Lrm.Tensor
open Lrm.Testing
open Microsoft.FSharp.NativeInterop

/// A recording backend with the suite's heap and table, so dispatches are prepared by the shared rules.
let private recording () =
    let b = new RecordingBackend()
    let ib = b :> IGpuBackend
    let layout, table = SelfTests.Suite.Plan()
    let heap = ib.CreateHeap layout
    heap.WriteTable table
    ib.LoadKernels(shaders ()) |> ignore
    b, ib, table

let private tok (table: SealedTable) name =
    match table.TryFind name with
    | Some t -> t
    | None -> failtestf "no tensor %s" name

/// Commands as D1 records them, from prepared dispatches and the other commands.
let private prepared (table: SealedTable) (kernel: KernelId) (args: ArgBlock) =
    Command.Dispatch(Dispatch.prepare table kernel args)

let private slotsOf (cmds: Command list) =
    cmds
    |> List.choose (function
        | Command.Dispatch d -> Some(d.Tokens |> List.map _.Slot)
        | _ -> None)

let private verifyOk (cmds: Command list) (enc: Encoded) =
    match Plan.verify (slotsOf cmds) enc.Ops with
    | Ok() -> ()
    | Error e -> failtestf "the encoding breaks a state rule: %s\n%A" e enc.Ops

let private planTests =
    let _, _, table = recording ()
    let slotX = (table.Row(tok table "scale.x")).Slot
    let slotOut = (table.Row(tok table "args_echo.y")).Slot

    let scale =
        prepared
            table
            Kernels.ScaleF32.Id
            (Kernels.ScaleF32.encode
                {
                    X = tok table "scale.x"
                    Y = tok table "scale.y"
                    N = 7u
                    A = 2.0f
                })

    let groups =
        prepared
            table
            Kernels.GroupsEcho.Id
            (Kernels.GroupsEcho.encode
                {
                    Y = tok table "groups_echo.y.folded"
                    Groups = uint32 SelfTests.FoldedGroups
                })

    let range slot offset bytes : BufferRange =
        {
            Slot = slot
            Offset = offset
            Bytes = bytes
        }

    testList
        "plan"
        [
            testCase "a dispatch moves the table and its pages to UNORDERED_ACCESS and back to COMMON"
            <| fun () ->
                let enc = Plan.encode false [ scale ]
                verifyOk [ scale ] enc

                let expected =
                    [
                        NativeOp.Barriers
                            [
                                BarrierOp.Transition(Target.Page 0, PageState.Common, PageState.UnorderedAccess)
                                BarrierOp.Transition(Target.Page slotX, PageState.Common, PageState.UnorderedAccess)
                            ]
                        NativeOp.Pipeline Kernels.ScaleF32.Id
                        NativeOp.Constants(
                            match scale with
                            | Command.Dispatch d -> d.Args.ToWords()
                            | _ -> [||]
                        )
                        NativeOp.Dispatch { X = 1; Y = 1; Z = 1 }
                        NativeOp.Barriers
                            [
                                BarrierOp.Transition(Target.Page 0, PageState.UnorderedAccess, PageState.Common)
                                BarrierOp.Transition(Target.Page slotX, PageState.UnorderedAccess, PageState.Common)
                            ]
                    ]

                Expect.equal enc.Ops expected "ops"
                Expect.equal enc.Queries 0 "no queries"
                Expect.equal enc.Dispatches 1 "one dispatch"

            testCase "word 0 is the physical x: a folded grid of 131,077 groups is 43,693 x 3"
            <| fun () ->
                let enc = Plan.encode false [ groups ]

                let dispatched =
                    enc.Ops
                    |> List.choose (function
                        | NativeOp.Dispatch g -> Some g
                        | _ -> None)

                Expect.equal dispatched [ { X = 43693; Y = 3; Z = 1 } ] "folded grid"

                let constants =
                    enc.Ops
                    |> List.pick (function
                        | NativeOp.Constants w -> Some w
                        | _ -> None)

                Expect.equal constants[0] 43693u "lrm_groups_x"

            testCase "the pipeline is set once for consecutive dispatches of one kernel"
            <| fun () ->
                let enc = Plan.encode false [ scale; Command.Barrier; scale; groups; scale ]
                verifyOk [ scale; scale; groups; scale ] enc

                let pipelines =
                    enc.Ops
                    |> List.choose (function
                        | NativeOp.Pipeline k -> Some k
                        | _ -> None)

                Expect.equal pipelines [ Kernels.ScaleF32.Id; Kernels.GroupsEcho.Id; Kernels.ScaleF32.Id ] "pipelines"
                Expect.contains enc.Ops (NativeOp.Barriers [ BarrierOp.AllUav ]) "Barrier is a UAV barrier on all"

            testCase "a copy between pages is a full barrier with UAV<->COPY transitions on both pages"
            <| fun () ->
                let cmds = [ scale; Command.Copy(range slotX 0L 64L, range slotOut 0L 64L); scale ]
                let enc = Plan.encode false cmds
                verifyOk cmds enc

                let i =
                    enc.Ops
                    |> List.findIndex (function
                        | NativeOp.Copy _ -> true
                        | _ -> false)

                Expect.equal
                    enc.Ops[i - 1]
                    (NativeOp.Barriers
                        [
                            BarrierOp.AllUav
                            BarrierOp.Transition(Target.Page slotX, PageState.UnorderedAccess, PageState.CopySource)
                            BarrierOp.Transition(Target.Page slotOut, PageState.Common, PageState.CopyDest)
                        ])
                    "before the copy"

                Expect.equal enc.Ops[i] (NativeOp.Copy(Target.Page slotOut, 0L, Target.Page slotX, 0L, 64L)) "copy"

                Expect.equal
                    enc.Ops[i + 1]
                    (NativeOp.Barriers
                        [
                            BarrierOp.Transition(Target.Page slotX, PageState.CopySource, PageState.UnorderedAccess)
                            BarrierOp.Transition(Target.Page slotOut, PageState.CopyDest, PageState.UnorderedAccess)
                        ])
                    "after the copy"

                Expect.equal enc.Copies [ 64L ] "copied bytes"

            testCase "a copy within one page goes through the scratch buffer, in chunks of at most 64 MiB"
            <| fun () ->
                let big = Plan.ScratchBytes + 4096L
                let cmds = [ Command.Copy(range 1 0L big, range 1 big big) ]
                let enc = Plan.encode false cmds
                verifyOk cmds enc

                let copies =
                    enc.Ops
                    |> List.choose (function
                        | NativeOp.Copy(d, dOff, s, sOff, n) -> Some(d, dOff, s, sOff, n)
                        | _ -> None)

                Expect.equal
                    copies
                    [
                        Target.Scratch, 0L, Target.Page 1, 0L, Plan.ScratchBytes
                        Target.Page 1, big, Target.Scratch, 0L, Plan.ScratchBytes
                        Target.Scratch, 0L, Target.Page 1, Plan.ScratchBytes, 4096L
                        Target.Page 1, big + Plan.ScratchBytes, Target.Scratch, 0L, 4096L
                    ]
                    "two chunks, each through the scratch buffer"

            testCase "timers and timed dispatches are bracketed by queries and resolved at the end"
            <| fun () ->
                let cmds =
                    [
                        Command.BeginTimer(0, "gpu.step", [ Attr.int "step" 3 ])
                        scale
                        Command.BeginTimer(1, "gpu.inner", [])
                        groups
                        Command.EndTimer 1
                        Command.EndTimer 0
                    ]

                let enc = Plan.encode true cmds
                verifyOk cmds enc
                Expect.equal enc.Queries 8 "two per timer, two per dispatch"

                Expect.equal
                    (enc.Timers |> List.map (fun t -> t.Name, t.Begin, t.End))
                    [ "gpu.step", 0, 7; "gpu.inner", 3, 6 ]
                    "timers in opening order"

                Expect.equal
                    (enc.DispatchTimers |> List.map (fun t -> t.Name, t.Attrs, t.Begin, t.End))
                    [
                        "gpu.dispatch", [ Attr.str TelemetryKeys.Kernel "scale_f32" ], 1, 2
                        "gpu.dispatch", [ Attr.str TelemetryKeys.Kernel "groups_echo" ], 4, 5
                    ]
                    "dispatch timers"

                Expect.equal (List.last enc.Ops) (NativeOp.ResolveTimestamps 8) "resolved last"

                // The backend keeps a timed dispatch's attributes until the backend is disposed (every completion stays
                // waitable), so dispatches of one kernel share one list, in every encoding.
                let again = Plan.encode true [ scale; scale ]

                Expect.isTrue
                    (again.DispatchTimers
                     |> List.forall (fun t -> obj.ReferenceEquals(t.Attrs, enc.DispatchTimers[0].Attrs)))
                    "one attribute list per kernel"

                // A timed dispatch runs alone: a UAV barrier before each of its timestamps.
                let i =
                    enc.Ops
                    |> List.findIndex (function
                        | NativeOp.Timestamp 1 -> true
                        | _ -> false)

                Expect.equal
                    enc.Ops[i - 1 .. i + 3]
                    [
                        NativeOp.Barriers [ BarrierOp.AllUav ]
                        NativeOp.Timestamp 1
                        NativeOp.Dispatch { X = 1; Y = 1; Z = 1 }
                        NativeOp.Barriers [ BarrierOp.AllUav ]
                        NativeOp.Timestamp 2
                    ]
                    "barrier, timestamp, dispatch, barrier, timestamp"

            testCase "an empty list encodes to nothing"
            <| fun () ->
                let enc = Plan.encode true []
                Expect.isEmpty enc.Ops "no ops"
                verifyOk [] enc

            testCase "uploads and downloads move one page COMMON -> COPY and back"
            <| fun () ->
                for ops in [ Plan.upload 3 256L 1 1024L; Plan.download 0 0L 0 Heap.TableBytes ] do
                    match Plan.verify [] ops with
                    | Ok() -> ()
                    | Error e -> failtest e

                Expect.equal
                    (Plan.upload 3 256L 1 1024L)
                    [
                        NativeOp.Barriers [ BarrierOp.Transition(Target.Page 3, PageState.Common, PageState.CopyDest) ]
                        NativeOp.Copy(Target.Page 3, 256L, Target.Upload 1, 0L, 1024L)
                        NativeOp.Barriers [ BarrierOp.Transition(Target.Page 3, PageState.CopyDest, PageState.Common) ]
                    ]
                    "upload"

            testCase "verify catches broken encodings"
            <| fun () ->
                let transition t a b =
                    NativeOp.Barriers [ BarrierOp.Transition(t, a, b) ]

                let bad =
                    [
                        [ transition (Target.Page 1) PageState.UnorderedAccess PageState.Common ], [], "is Common"
                        [ transition (Target.Page 1) PageState.Common PageState.CopyDest ], [], "ends with"
                        [ NativeOp.Copy(Target.Page 2, 0L, Target.Page 1, 0L, 4L) ], [], "copy source"
                        [ NativeOp.Dispatch { X = 1; Y = 1; Z = 1 } ], [ [] ], "dispatch with page 0"
                        [ transition (Target.Upload 0) PageState.Common PageState.CopyDest ], [], "never transitions"
                        [ NativeOp.Dispatch { X = 1; Y = 1; Z = 1 } ], [], "more dispatches"
                    ]

                for ops, slots, text in bad do
                    match Plan.verify slots ops with
                    | Ok() -> failtestf "accepted %A" ops
                    | Error e -> Expect.stringContains e text "the broken rule"

            Props.testProperty "random command lists keep every state rule" (fun (picks: (byte * byte * byte) list) ->
                let cmds =
                    picks
                    |> List.truncate 40
                    |> List.map (fun (a, b, c) ->
                        match int a % 5 with
                        | 0 -> scale
                        | 1 -> groups
                        | 2 -> Command.Barrier
                        | 3 ->
                            let s = 1 + int b % 3
                            let d = 1 + int c % 3
                            Command.Copy(range s 0L 256L, range d 1024L 256L)
                        | _ -> Command.Copy(range 2 0L 512L, range 3 4096L 512L))

                let enc = Plan.encode (int (List.length picks) % 2 = 0) cmds

                match Plan.verify (slotsOf cmds) enc.Ops with
                | Ok() -> true
                | Error e -> failwith e)
        ]

/// Fake runtime memory: a breadcrumb node list and allocation nodes, laid out as d3d12.h declares them.
let private dredTests =
    testList
        "dred"
        [
            testCase "breadcrumb nodes, contexts and allocation lists are read from runtime memory"
            <| fun () ->
                let alloc (bytes: int) = Marshal.AllocHGlobal bytes
                let held = ResizeArray<nativeint>()

                let keep p =
                    held.Add p
                    p

                try
                    let listName = keep (Marshal.StringToHGlobalUni "lrm.d3d12.frame0.list")
                    let queueName = keep (Marshal.StringToHGlobalUni "lrm.d3d12.queue")
                    let history = keep (alloc 16)

                    for i, op in
                        List.indexed
                            [
                                D3D12_AUTO_BREADCRUMB_OP.RESOURCEBARRIER
                                D3D12_AUTO_BREADCRUMB_OP.DISPATCH
                                D3D12_AUTO_BREADCRUMB_OP.COPYBUFFERREGION
                                D3D12_AUTO_BREADCRUMB_OP.RESOLVEQUERYDATA
                            ] do
                        Marshal.WriteInt32(history, 4 * i, int op)

                    let last = keep (alloc 4)
                    Marshal.WriteInt32(last, 2)
                    let ctxText = keep (Marshal.StringToHGlobalUni "scale_f32")
                    let contexts = keep (alloc 16)
                    Marshal.WriteInt32(contexts, 0, 1)
                    Marshal.WriteIntPtr(contexts, 8, ctxText)
                    let node = keep (alloc 96)

                    let mutable n = D3D12_AUTO_BREADCRUMB_NODE1()
                    n.pCommandListDebugNameW <- listName
                    n.pCommandQueueDebugNameW <- queueName
                    n.BreadcrumbCount <- 4u
                    n.pLastBreadcrumbValue <- last
                    n.pCommandHistory <- history
                    n.BreadcrumbContextsCount <- 1u
                    n.pBreadcrumbContexts <- contexts
                    NativePtr.write (NativePtr.ofNativeInt<D3D12_AUTO_BREADCRUMB_NODE1> node) n

                    let crumbs = Dred.readBreadcrumbs node
                    Expect.equal crumbs.Length 1 "one node"
                    let b = crumbs.Head
                    Expect.equal b.CommandList "lrm.d3d12.frame0.list" "list name"
                    Expect.equal b.CommandQueue "lrm.d3d12.queue" "queue name"
                    Expect.equal b.Completed (Some 2u) "completed"
                    Expect.equal b.Ops.Length 4 "ops"
                    Expect.equal b.Contexts [ 1u, "scale_f32" ] "contexts"

                    let allocName = keep (Marshal.StringToHGlobalUni "lrm.d3d12.heap.page1.weights0")
                    let anode = keep (alloc 40)
                    let mutable a = D3D12_DRED_ALLOCATION_NODE1()
                    a.ObjectNameW <- allocName
                    a.AllocationType <- D3D12_DRED_ALLOCATION_TYPE.RESOURCE
                    NativePtr.write (NativePtr.ofNativeInt<D3D12_DRED_ALLOCATION_NODE1> anode) a
                    let allocs = Dred.readAllocations anode

                    Expect.equal
                        allocs
                        [
                            {
                                Name = "lrm.d3d12.heap.page1.weights0"
                                Kind = D3D12_DRED_ALLOCATION_TYPE.RESOURCE
                            }
                        ]
                        "allocations"

                    let text =
                        Dred.format
                            {
                                Enabled = true
                                Breadcrumbs = crumbs
                                PageFault =
                                    Some
                                        {
                                            Address = 0x1234_5678UL
                                            Existing = allocs
                                            RecentlyFreed = []
                                        }
                                Notes = []
                            }

                    Expect.equal
                        text
                        (String.concat
                            "\n"
                            [
                                "DRED breadcrumbs: list 'lrm.d3d12.frame0.list' on queue 'lrm.d3d12.queue': 4 ops, 2 completed"
                                "    0: RESOURCEBARRIER"
                                "    1: DISPATCH \"scale_f32\""
                                "  > 2: COPYBUFFERREGION"
                                "    3: RESOLVEQUERYDATA"
                                "DRED page fault at GPU VA 0x0000000012345678"
                                "  existing: 'lrm.d3d12.heap.page1.weights0' (RESOURCE)"
                            ])
                        "format"
                finally
                    for p in held do
                        Marshal.FreeHGlobal p

            testCase "an empty report says DRED was not forced on"
            <| fun () ->
                Expect.equal
                    (Dred.format
                        {
                            Enabled = false
                            Breadcrumbs = []
                            PageFault = None
                            Notes = [ "x" ]
                        })
                    "DRED: not forced on for this device\nDRED: x"
                    "text"

                Expect.isEmpty (Dred.readBreadcrumbs 0n) "a null head is an empty list"
        ]

let private report (verdict: Verdict) (caps: Result<Capabilities, string>) (software: bool) : AdapterReport =
    {
        Adapter =
            {
                Index = 1
                Description = "NVIDIA GeForce RTX 4080"
                VendorId = 0x10DEu
                DeviceId = 0x2704u
                SubSysId = 0u
                Revision = 161u
                DedicatedVideoMemory = 16UL <<< 30
                DedicatedSystemMemory = 0UL
                SharedSystemMemory = 32UL <<< 30
                Luid = 0x0000000100002345L
                Software = software
                UmdVersion = Some "32.0.16.1692"
                DriverVersion = "616.92"
            }
        Capabilities = caps
        Verdict = verdict
    }

let private caps sm tier : Capabilities =
    {
        MaxFeatureLevel = D3D_FEATURE_LEVEL.LEVEL_12_2
        HighestShaderModel = sm
        ResourceBindingTier = tier
        ResourceHeapTier = D3D12_RESOURCE_HEAP_TIER.TIER_2
        HighestRootSignature = D3D_ROOT_SIGNATURE_VERSION.VERSION_1_2
        DoublePrecisionShaderOps = true
        WaveOps = true
        WaveLaneCountMin = 32u
        WaveLaneCountMax = 32u
        Int64ShaderOps = true
        Native16BitShaderOps = true
        AtomicInt64OnTypedResource = true
        AtomicInt64OnGroupShared = true
        AtomicInt64OnDescriptorHeapResource = true
        EnhancedBarriers = true
        Uma = false
        CacheCoherentUma = false
        MaxGpuVirtualAddressBitsPerResource = 40u
        MaxGpuVirtualAddressBitsPerProcess = 47u
        NodeCount = 1u
        RuntimeSdkVersion = Some 616u
    }

let private adapterTests =
    testList
        "adapters"
        [
            testCase "the UMD text parses back to the LARGE_INTEGER, and the Driver is GpuClass.d3d12Driver"
            <| fun () ->
                Expect.equal
                    (Adapters.tryParseUmd "32.0.16.1692")
                    (Some 0x002000000010069CL)
                    "GpuBackendInterface vector"

                Expect.equal (GpuClass.d3d12Driver 0x002000000010069CL) "32.0.16.1692" "round trip"
                Expect.isNone (Adapters.tryParseUmd "32.0.16") "three parts"
                Expect.isNone (Adapters.tryParseUmd "32.0.16.70000") "a part above 65535"
                Expect.isNone (Adapters.tryParseUmd "") "empty"

            testCase "an accepted RTX 4080 report becomes the d3d12 DeviceInfo of the fill rule"
            <| fun () ->
                let r =
                    report
                        Accepted
                        (Ok(caps D3D_SHADER_MODEL.SHADER_MODEL_6_8 D3D12_RESOURCE_BINDING_TIER.TIER_3))
                        false

                let c = Adapters.candidate r
                Expect.isTrue c.Accepted "accepted"
                Expect.equal c.Info.Backend ExecutionBackend.D3D12 "backend"
                Expect.equal c.Info.Driver "32.0.16.1692" "driver"
                Expect.equal (GpuClass.deviceId c.Info.VendorId c.Info.PciDeviceId) "pci:10de:2704" "device id"
                Expect.equal (Adapters.luidOf c.Info) (Some 0x0000000100002345L) "luid round trip"

                Expect.equal
                    (c.Info.Details |> List.map fst)
                    [
                        "luid"
                        "umd_version"
                        "driver_version"
                        "software"
                        "dedicated_system_memory"
                        "shared_system_memory"
                        "feature_level"
                        "shader_model"
                        "resource_binding_tier"
                        "resource_heap_tier"
                        "root_signature"
                        "wave_lanes"
                        "enhanced_barriers"
                        "runtime_sdk"
                    ]
                    "details in a stable order"

                Expect.equal (c.Info.Details |> List.find (fun (k, _) -> k = "shader_model") |> snd) "6.8" "SM"

            testCase "Linux layer classes preserve PCI identity and differ from native D3D12"
            <| fun () ->
                if OperatingSystem.IsLinux() then
                    let r =
                        report
                            Accepted
                            (Ok(caps D3D_SHADER_MODEL.SHADER_MODEL_6_8 D3D12_RESOURCE_BINDING_TIER.TIER_3))
                            false

                    let uuid = "321ac892-09a0-1578-b218-a2231e8f1961"
                    let driver = "vkd3d-proton 3.0.1 (fixture) / NVIDIA 610.57.04 Vulkan 1.4.341"

                    let layered =
                        { r with
                            Adapter =
                                { r.Adapter with
                                    UmdVersion = None
                                    DriverVersion = driver
                                }
                        }

                    let shaders = Lrm.Shaders.Shaders.embedded ()
                    let info = Adapters.info layered

                    let info =
                        { info with
                            Details = info.Details @ [ "device_uuid", uuid ]
                        }

                    Expect.equal (Adapters.deviceUuid info) (Some uuid) "adapter UUID accessor"
                    let klass = GpuClass.fill info shaders
                    Expect.equal klass.DeviceId "pci:10de:2704" "PCI model identity"

                    let other =
                        { info with
                            Details = [ "device_uuid", "11e1c1c9-0b71-f58b-4d30-afb9ecebaf19" ]
                        }

                    Expect.isTrue
                        (ExecutionClass.same klass (GpuClass.fill other shaders))
                        "identical GPUs share a class"

                    Expect.equal klass.Driver driver "layer and Vulkan driver identity"
                    Expect.equal klass.Arch "linux-x64" "GPU host rule"
                    let native = GpuClass.fill (Adapters.info r) shaders
                    Expect.isFalse (ExecutionClass.same klass native) "no bit-identity claim crosses the layer boundary"
                else
                    let reason = "Linux layer execution-class metadata requires a Linux host"
                    printfn "SKIP: %s" reason
                    skiptest reason

            testCase "rejections use the Requirements texts"
            <| fun () ->
                let r =
                    report
                        (Rejected [ "x" ])
                        (Ok(caps D3D_SHADER_MODEL.SHADER_MODEL_6_5 D3D12_RESOURCE_BINDING_TIER.TIER_2))
                        false

                Expect.equal
                    (Adapters.candidate r).Rejections
                    [ Requirements.D3D12ShaderModel66; Requirements.D3D12BindingTier3 ]
                    "SM and tier"

                let warp =
                    report
                        (Rejected [ "software" ])
                        (Ok(caps D3D_SHADER_MODEL.SHADER_MODEL_6_8 D3D12_RESOURCE_BINDING_TIER.TIER_3))
                        true

                Expect.stringContains (Adapters.candidate warp).Rejections.Head "WARP" "WARP keeps its reason"
                let none = report (Rejected [ "no device" ]) (Error "E_FAIL") false
                Expect.equal (Adapters.candidate none).Rejections [ "no D3D12 device: E_FAIL" ] "no device"
        ]

let private selftestTests =
    testList
        "selftests"
        [
            testCase "the suite's layout fits one page per arena and its table seals"
            <| fun () ->
                let layout, table = SelfTests.Suite.Plan()

                Expect.equal
                    (layout.Pages |> List.map _.Arena)
                    [ ArenaKind.Weights; ArenaKind.Activations; ArenaKind.Scratch ]
                    "three arenas"

                Expect.equal table.Count 12 "twelve tensors"

            testCase "scale_f32's expectations are CpuRef's multiply, checked by KernelBounds in each denormal mode"
            <| fun () ->
                for mode in [ Fp32DenormalMode.Preserve; Fp32DenormalMode.Flush ] do
                    let expected =
                        SelfTests.scaleInputs
                        |> Array.map (SelfTests.scaleExpected mode SelfTests.ScaleA)

                    // CpuRef semantics in the mode: an IEEE float32 multiply; flush zeroes denormal inputs and results.
                    let reference =
                        SelfTests.scaleInputs
                        |> Array.map (fun x ->
                            let f (w: uint32) =
                                if mode = Fp32DenormalMode.Flush then
                                    Fp32Denormals.flushBits w
                                else
                                    w

                            f (
                                BitConverter.SingleToUInt32Bits(
                                    BitConverter.UInt32BitsToSingle(f x) * SelfTests.ScaleA
                                )
                            ))

                    let c =
                        KernelBounds.checkExact
                            "scale_f32"
                            4
                            (ReadOnlySpan(bytesOf expected))
                            (ReadOnlySpan(bytesOf reference))

                    Expect.isTrue c.Passed (sprintf "%A: %g differ" mode c.Measured)

                // Preserve and flush differ exactly on the denormal elements.
                let p =
                    SelfTests.scaleInputs
                    |> Array.map (SelfTests.scaleExpected Fp32DenormalMode.Preserve 0.75f)

                let f =
                    SelfTests.scaleInputs
                    |> Array.map (SelfTests.scaleExpected Fp32DenormalMode.Flush 0.75f)

                let differ = Array.map2 (<>) p f |> Array.filter id |> Array.length
                Expect.isGreaterThan differ 5 "the probe's denormal words and the random ones differ between modes"

            testCase "a GPU NaN (0x7FFFFFFF) matches CpuRef's NaN under the class rule, and only there"
            <| fun () ->
                let x = [| 0x7FC00000u; 0xFFC00001u; 0x3F800000u |]

                let expected =
                    x |> Array.map (SelfTests.scaleExpected Fp32DenormalMode.Preserve 0.75f)

                let gpu = [| 0x7FFFFFFFu; 0x7FFFFFFFu; expected[2] |]
                let none = Array.zeroCreate<uint32> 0

                let c =
                    SelfTests.judge "scale_f32" "nan" true expected [| 1u; 1u; 1u |] none (fun _ -> false) gpu

                Expect.isTrue c.Passed "NaN matches NaN"
                Expect.equal c.NanWords [ 0x7FFFFFFFu ] "the GPU NaN word is recorded"

                let k =
                    KernelBounds.checkExact "scale_f32" 4 (ReadOnlySpan(bytesOf gpu)) (ReadOnlySpan(bytesOf expected))

                Expect.isTrue k.Passed "KernelBounds agrees"

                let b =
                    SelfTests.judge "bf16_unpack" "nan" false expected [| 1u; 1u; 1u |] none (fun _ -> false) gpu

                Expect.equal b.Differing 2 "bit-moving kernels compare NaN payloads"

            testCase "bf16_unpack's expectation is CpuRef.widen over every pattern"
            <| fun () ->
                let input = HostTensor.bf16 (Shape.vector SelfTests.Bf16N) SelfTests.bf16Inputs
                let widened: HostTensor<float32> = Lrm.Tensor.CpuReference.CpuRef.widen input

                let reference = widened.Data |> Array.map BitConverter.SingleToUInt32Bits
                let expected = SelfTests.bf16Inputs |> Array.map Bf16.widenBits

                let k =
                    KernelBounds.checkExact
                        "bf16_unpack"
                        4
                        (ReadOnlySpan(bytesOf expected))
                        (ReadOnlySpan(bytesOf reference))

                Expect.isTrue k.Passed (sprintf "%g of 65536 differ" k.Measured)

            testCase "poison never equals the expected word, and never matches it under the NaN rule"
            <| fun () ->
                for case in 0..8 do
                    for run in 1..3 do
                        for i in 0..4095 do
                            let e = SelfTests.scaleInputs[i % SelfTests.scaleInputs.Length]
                            let p = SelfTests.poison true case run i (ValueSome e)
                            Expect.notEqual p e "differs"

                            let nan (w: uint32) =
                                w &&& 0x7F800000u = 0x7F800000u && w &&& 0x007FFFFFu <> 0u

                            Expect.isFalse (nan e && nan p) "a NaN expectation gets a non-NaN poison"

                Expect.notEqual
                    (SelfTests.poison false 0 1 5 (ValueSome 7u))
                    (SelfTests.poison false 0 2 5 (ValueSome 7u))
                    "poison differs per run"

            testCase "judge counts unwritten, differing and clobbered words"
            <| fun () ->
                let expected = [| 1u; 2u; 3u; 4u |]
                let unwritten = [| 9u; 9u; 9u; 9u |]
                let spare = [| 7u; 7u |]
                let out = [| 1u; 9u; 5u; 4u; 7u; 8u |]

                let c =
                    SelfTests.judge "args_echo" "x" false expected unwritten spare (fun _ -> false) out

                Expect.equal (c.NotWritten, c.Differing, c.Clobbered) (1, 1, 1) "one of each"
                Expect.isFalse c.Passed "fails"
                Expect.stringContains c.Detail "still unwritten" "detail"

            testCase "a mixed class compares denormal elements within the class only"
            <| fun () ->
                Expect.isTrue (SelfTests.classOnly Fp32DenormalMode.Mixed 0.75f 0x00000002u) "denormal input"
                Expect.isTrue (SelfTests.classOnly Fp32DenormalMode.Mixed 0.5f 0x00800000u) "denormal result"
                Expect.isFalse (SelfTests.classOnly Fp32DenormalMode.Mixed 0.75f 0x3F800000u) "normal"
                Expect.isFalse (SelfTests.classOnly Fp32DenormalMode.Preserve 0.75f 0x00000002u) "only in mixed"
        ]

let private miscTests =
    testList
        "misc"
        [
            testCase "transfers are cut into chunks of at most 64 MiB, staged in 64 KiB steps"
            <| fun () ->
                Expect.equal (Transfer.chunks 0L) [] "nothing"
                Expect.equal (Transfer.chunks 28L) [ 0L, 28L ] "small"

                Expect.equal
                    (Transfer.chunks (Transfer.ChunkBytes * 2L + 8L))
                    [
                        0L, Transfer.ChunkBytes
                        Transfer.ChunkBytes, Transfer.ChunkBytes
                        2L * Transfer.ChunkBytes, 8L
                    ]
                    "two full chunks and a tail"

                Expect.equal (Transfer.stagingBytes 28L) 65536L "a small transfer stages 64 KiB"
                Expect.equal (Transfer.stagingBytes (1L <<< 40)) Transfer.ChunkBytes "a large one 64 MiB"

            testCase "hello-d3d12 arguments"
            <| fun () ->
                Expect.equal (Hello.parseArgs [||]) (Ok None) "none"
                Expect.equal (Hello.parseArgs [| "--index"; "1" |]) (Ok(Some 1)) "index"
                Expect.isError (Hello.parseArgs [| "--index"; "x" |]) "bad index"
                Expect.isError (Hello.parseArgs [| "--json" |]) "unknown"

            testCase "Lrm.Gpu.D3D12 and this exe build without tail calls (ADR R3-0001)"
            <| fun () ->
                let asm = typeof<D3D12Backend>.Assembly

                if TailCalls.builtWithoutOptimization asm then
                    skiptest TailCalls.UnoptimizedBuild

                for a in [ asm; Assembly.GetExecutingAssembly() ] do
                    Expect.isEmpty (TailCalls.scan a.Location) (a.GetName().Name + " has no tail. prefixes")

            testCase "the provider names the d3d12 backend"
            <| fun () -> Expect.equal D3D12.provider.Backend ExecutionBackend.D3D12 "backend"
        ]

let tests =
    testList "d3d12.cpu" [ planTests; dredTests; adapterTests; selftestTests; miscTests ]
