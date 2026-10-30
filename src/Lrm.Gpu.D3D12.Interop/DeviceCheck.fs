namespace Lrm.Gpu.D3D12.Interop

#nowarn "9"
#nowarn "51"

open System
open System.Runtime.InteropServices
open Lrm.Core
open Microsoft.FSharp.NativeInterop

/// One DXGI adapter, as IDXGIAdapter1::GetDesc1 and CheckInterfaceSupport report it.
type AdapterIdentity =
    {
        /// The position in IDXGIFactory6::EnumAdapterByGpuPreference(HIGH_PERFORMANCE) order.
        Index: int
        Description: string
        VendorId: uint32
        DeviceId: uint32
        SubSysId: uint32
        Revision: uint32
        DedicatedVideoMemory: uint64
        DedicatedSystemMemory: uint64
        SharedSystemMemory: uint64
        /// The adapter LUID, HighPart in the top 32 bits.
        Luid: int64
        /// DXGI_ADAPTER_FLAG_SOFTWARE: WARP or another software rasterizer.
        Software: bool
        /// The user-mode driver version, for example 32.0.16.1692, when the adapter reports it.
        UmdVersion: string option
        /// The vendor's version name: 616.92 for NVIDIA UMD 32.0.16.1692, else the UMD version, else "unknown".
        DriverVersion: string
    }

/// What the D3D12 device on an adapter supports, from ID3D12Device::CheckFeatureSupport.
type Capabilities =
    {
        MaxFeatureLevel: D3D_FEATURE_LEVEL
        HighestShaderModel: D3D_SHADER_MODEL
        ResourceBindingTier: D3D12_RESOURCE_BINDING_TIER
        ResourceHeapTier: D3D12_RESOURCE_HEAP_TIER
        HighestRootSignature: D3D_ROOT_SIGNATURE_VERSION
        DoublePrecisionShaderOps: bool
        WaveOps: bool
        WaveLaneCountMin: uint32
        WaveLaneCountMax: uint32
        Int64ShaderOps: bool
        Native16BitShaderOps: bool
        AtomicInt64OnTypedResource: bool
        AtomicInt64OnGroupShared: bool
        AtomicInt64OnDescriptorHeapResource: bool
        EnhancedBarriers: bool
        Uma: bool
        CacheCoherentUma: bool
        MaxGpuVirtualAddressBitsPerResource: uint32
        MaxGpuVirtualAddressBitsPerProcess: uint32
        NodeCount: uint32
        /// D3D12_SDK_VERSION of the D3D12 runtime in use (ID3D12DeviceConfiguration), when it reports one.
        RuntimeSdkVersion: uint32 option
    }

/// The device-check verdict for the d3d12 backend.
type Verdict =
    | Accepted
    | Rejected of reasons: string list

type AdapterReport =
    {
        Adapter: AdapterIdentity
        /// Error when no D3D12 device could be created on the adapter.
        Capabilities: Result<Capabilities, string>
        Verdict: Verdict
    }

/// Device discovery and acceptance for the d3d12 backend (GpuBackendInterface: SM 6.6 and resource binding tier 3).
[<RequireQualifiedAccess>]
module DeviceCheck =
    /// Linux discovery metadata for the existing DeviceInfo.Details map; Windows and synthetic records have none.
    let detailsOf (adapter: AdapterIdentity) =
        if OperatingSystem.IsLinux() then
            LinuxAdapters.details adapter.Index adapter.Luid
        else
            []

    /// UUID identifies the selected adapter, not its execution class.
    let deviceUuid (adapter: AdapterIdentity) =
        detailsOf adapter
        |> List.tryPick (fun (key, value) -> if key = "device_uuid" then Some value else None)

    /// The lowest shader model the backend accepts: SM 6.6 (ResourceDescriptorHeap indexing).
    let RequiredShaderModel = D3D_SHADER_MODEL.SHADER_MODEL_6_6

    /// The lowest resource binding tier the backend accepts.
    let RequiredBindingTier = D3D12_RESOURCE_BINDING_TIER.TIER_3

    /// The feature level devices are created at; capabilities decide acceptance, not the feature level.
    let CreationFeatureLevel = D3D_FEATURE_LEVEL.LEVEL_11_0

    /// The PCI vendor id of NVIDIA.
    [<Literal>]
    let VendorNvidia = 0x10DEu

    let private shaderModelText (sm: D3D_SHADER_MODEL) =
        let v = int sm
        sprintf "%d.%d" (v >>> 4) (v &&& 0xF)

    let private featureLevelText (fl: D3D_FEATURE_LEVEL) =
        let v = int fl
        sprintf "%d_%d" (v >>> 12) ((v >>> 8) &&& 0xF)

    /// "6.6" for D3D_SHADER_MODEL_6_6.
    let formatShaderModel = shaderModelText

    /// "12_2" for D3D_FEATURE_LEVEL_12_2.
    let formatFeatureLevel = featureLevelText

    /// The four 16-bit parts of a UMD version as returned in a LARGE_INTEGER, most significant first.
    let umdParts (umd: int64) =
        [| for shift in [ 48; 32; 16; 0 ] -> uint16 ((umd >>> shift) &&& 0xFFFFL) |]

    /// The vendor's own version name. NVIDIA names drivers by the last five digits of the UMD version
    /// (32.0.16.1692 -> 616.92); other vendors are shown with the UMD version itself.
    let formatDriverVersion (vendorId: uint32) (parts: uint16[]) =
        let umd = parts |> Array.map string |> String.concat "."

        if vendorId = VendorNvidia && parts.Length = 4 then
            let n = (int parts[2] % 10) * 10000 + int parts[3]
            sprintf "%d.%02d" (n / 100) (n % 100)
        else
            umd

    /// The verdict for one adapter. Pure, so the cpu tier checks every rule.
    let verdict (adapter: AdapterIdentity) (caps: Result<Capabilities, string>) : Verdict =
        let reasons =
            [
                if adapter.Software then
                    "software adapter (WARP); the d3d12 backend needs hardware, d3d12-warp is a separate class"
                match caps with
                | Error e -> sprintf "no D3D12 device: %s" e
                | Ok c ->
                    if c.HighestShaderModel < RequiredShaderModel then
                        sprintf
                            "shader model %s is below the required %s"
                            (shaderModelText c.HighestShaderModel)
                            (shaderModelText RequiredShaderModel)

                    if c.ResourceBindingTier < RequiredBindingTier then
                        sprintf
                            "resource binding tier %d is below the required %d"
                            (int c.ResourceBindingTier)
                            (int RequiredBindingTier)
            ]

        if reasons.IsEmpty then Accepted else Rejected reasons

    let private query<'T when 'T: unmanaged> (dev: ComPtr<ID3D12Device>) (feature: D3D12_FEATURE) (init: 'T) =
        let mutable data = init

        let hr =
            dev.CheckFeatureSupportRaw(feature, NativePtr.toNativeInt &&data, uint32 sizeof<'T>)

        if hr >= 0 then ValueSome data else ValueNone

    /// Queries the capabilities of a device.
    let capabilities (dev: ComPtr<ID3D12Device>) : Capabilities =
        let shaderModel =
            // The runtime rejects shader models it does not know with E_INVALIDARG: ask from the newest down.
            let candidates =
                [
                    D3D_SHADER_MODEL.SHADER_MODEL_6_9
                    D3D_SHADER_MODEL.SHADER_MODEL_6_8
                    D3D_SHADER_MODEL.SHADER_MODEL_6_7
                    D3D_SHADER_MODEL.SHADER_MODEL_6_6
                    D3D_SHADER_MODEL.SHADER_MODEL_6_5
                    D3D_SHADER_MODEL.SHADER_MODEL_6_4
                    D3D_SHADER_MODEL.SHADER_MODEL_6_3
                    D3D_SHADER_MODEL.SHADER_MODEL_6_2
                    D3D_SHADER_MODEL.SHADER_MODEL_6_1
                    D3D_SHADER_MODEL.SHADER_MODEL_6_0
                    D3D_SHADER_MODEL.SHADER_MODEL_5_1
                ]

            candidates
            |> List.tryPick (fun sm ->
                match
                    query dev D3D12_FEATURE.SHADER_MODEL (D3D12_FEATURE_DATA_SHADER_MODEL(HighestShaderModel = sm))
                with
                | ValueSome d -> Some d.HighestShaderModel
                | ValueNone -> None)
            |> Option.defaultValue D3D_SHADER_MODEL.SHADER_MODEL_NONE

        let featureLevel =
            let levels =
                [|
                    D3D_FEATURE_LEVEL.LEVEL_12_2
                    D3D_FEATURE_LEVEL.LEVEL_12_1
                    D3D_FEATURE_LEVEL.LEVEL_12_0
                    D3D_FEATURE_LEVEL.LEVEL_11_1
                    D3D_FEATURE_LEVEL.LEVEL_11_0
                |]

            use p = fixed levels

            match
                query
                    dev
                    D3D12_FEATURE.FEATURE_LEVELS
                    (D3D12_FEATURE_DATA_FEATURE_LEVELS(
                        NumFeatureLevels = uint32 levels.Length,
                        pFeatureLevelsRequested = NativePtr.toNativeInt p
                    ))
            with
            | ValueSome d -> d.MaxSupportedFeatureLevel
            | ValueNone -> CreationFeatureLevel

        let o0 =
            query dev D3D12_FEATURE.D3D12_OPTIONS (D3D12_FEATURE_DATA_D3D12_OPTIONS())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS())

        let o1 =
            query dev D3D12_FEATURE.D3D12_OPTIONS1 (D3D12_FEATURE_DATA_D3D12_OPTIONS1())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS1())

        let o4 =
            query dev D3D12_FEATURE.D3D12_OPTIONS4 (D3D12_FEATURE_DATA_D3D12_OPTIONS4())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS4())

        let o9 =
            query dev D3D12_FEATURE.D3D12_OPTIONS9 (D3D12_FEATURE_DATA_D3D12_OPTIONS9())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS9())

        let o11 =
            query dev D3D12_FEATURE.D3D12_OPTIONS11 (D3D12_FEATURE_DATA_D3D12_OPTIONS11())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS11())

        let o12 =
            query dev D3D12_FEATURE.D3D12_OPTIONS12 (D3D12_FEATURE_DATA_D3D12_OPTIONS12())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_D3D12_OPTIONS12())

        let arch =
            query dev D3D12_FEATURE.ARCHITECTURE1 (D3D12_FEATURE_DATA_ARCHITECTURE1(NodeIndex = 0u))
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_ARCHITECTURE1())

        let va =
            query dev D3D12_FEATURE.GPU_VIRTUAL_ADDRESS_SUPPORT (D3D12_FEATURE_DATA_GPU_VIRTUAL_ADDRESS_SUPPORT())
            |> ValueOption.defaultValue (D3D12_FEATURE_DATA_GPU_VIRTUAL_ADDRESS_SUPPORT())

        let rootSignature =
            [
                D3D_ROOT_SIGNATURE_VERSION.VERSION_1_2
                D3D_ROOT_SIGNATURE_VERSION.VERSION_1_1
            ]
            |> List.tryPick (fun v ->
                match
                    query dev D3D12_FEATURE.ROOT_SIGNATURE (D3D12_FEATURE_DATA_ROOT_SIGNATURE(HighestVersion = v))
                with
                | ValueSome d -> Some d.HighestVersion
                | ValueNone -> None)
            |> Option.defaultValue D3D_ROOT_SIGNATURE_VERSION.VERSION_1_0

        let sdkVersion =
            match dev.TryQueryInterface<ID3D12DeviceConfiguration>() with
            | ValueSome cfg ->
                use cfg = cfg
                let d = cfg.GetDesc()
                Some d.SDKVersion
            | ValueNone -> None

        {
            MaxFeatureLevel = featureLevel
            HighestShaderModel = shaderModel
            ResourceBindingTier = o0.ResourceBindingTier
            ResourceHeapTier = o0.ResourceHeapTier
            HighestRootSignature = rootSignature
            DoublePrecisionShaderOps = o0.DoublePrecisionFloatShaderOps <> 0
            WaveOps = o1.WaveOps <> 0
            WaveLaneCountMin = o1.WaveLaneCountMin
            WaveLaneCountMax = o1.WaveLaneCountMax
            Int64ShaderOps = o1.Int64ShaderOps <> 0
            Native16BitShaderOps = o4.Native16BitShaderOpsSupported <> 0
            AtomicInt64OnTypedResource = o9.AtomicInt64OnTypedResourceSupported <> 0
            AtomicInt64OnGroupShared = o9.AtomicInt64OnGroupSharedSupported <> 0
            AtomicInt64OnDescriptorHeapResource = o11.AtomicInt64OnDescriptorHeapResourceSupported <> 0
            EnhancedBarriers = o12.EnhancedBarriersSupported <> 0
            Uma = arch.UMA <> 0
            CacheCoherentUma = arch.CacheCoherentUMA <> 0
            MaxGpuVirtualAddressBitsPerResource = va.MaxGPUVirtualAddressBitsPerResource
            MaxGpuVirtualAddressBitsPerProcess = va.MaxGPUVirtualAddressBitsPerProcess
            NodeCount = dev.GetNodeCount()
            RuntimeSdkVersion = sdkVersion
        }

    let private identity (index: int) (adapter: ComPtr<IDXGIAdapter1>) : AdapterIdentity =
        let mutable d = adapter.GetDesc1()
        let name = MemoryMarshal.CreateReadOnlySpan(&d.Description.e0, 128)
        let len = (let k = name.IndexOf 0us in if k < 0 then name.Length else k)
        let chars = Array.zeroCreate len

        for i in 0 .. len - 1 do
            chars[i] <- char name[i]

        let umd =
            let mutable iid = Iids.IDXGIDevice
            let mutable v = 0L

            let hr =
                adapter.CheckInterfaceSupportRaw(NativePtr.toNativeInt &&iid, NativePtr.toNativeInt &&v)

            if hr >= 0 then Some(umdParts v) else None

        {
            Index = index
            Description = String(chars)
            VendorId = d.VendorId
            DeviceId = d.DeviceId
            SubSysId = d.SubSysId
            Revision = d.Revision
            DedicatedVideoMemory = uint64 d.DedicatedVideoMemory
            DedicatedSystemMemory = uint64 d.DedicatedSystemMemory
            SharedSystemMemory = uint64 d.SharedSystemMemory
            Luid = (int64 d.AdapterLuid.HighPart <<< 32) ||| int64 d.AdapterLuid.LowPart
            Software = (d.Flags &&& uint32 DXGI_ADAPTER_FLAG.SOFTWARE) <> 0u
            UmdVersion = umd |> Option.map (Array.map string >> String.concat ".")
            DriverVersion =
                match umd with
                | Some parts -> formatDriverVersion d.VendorId parts
                | None -> "unknown"
        }

    let private emit (r: AdapterReport) =
        let a = r.Adapter

        let attrs =
            [
                Attr.str TelemetryKeys.Backend "d3d12"
                Attr.str "device" a.Description
                Attr.int64 "vendor_id" (int64 a.VendorId)
                Attr.int64 "device_id" (int64 a.DeviceId)
                Attr.str "driver" a.DriverVersion
                match r.Capabilities with
                | Ok c ->
                    Attr.str "shader_model" (shaderModelText c.HighestShaderModel)
                    Attr.int "binding_tier" (int c.ResourceBindingTier)
                    Attr.str "feature_level" (featureLevelText c.MaxFeatureLevel)
                | Error e -> Attr.str "error" e
                match r.Verdict with
                | Accepted -> ()
                | Rejected reasons -> Attr.str "reason" (String.concat "; " reasons)
            ]

        Telemetry.check "gpu.device.check" (r.Verdict = Accepted) attrs

        Telemetry.gauge
            "gpu.device.memory"
            (float a.DedicatedVideoMemory)
            TelemetryUnits.Bytes
            [
                Attr.str TelemetryKeys.Backend "d3d12"
                Attr.str "device" a.Description
                Attr.str "pool" "dedicated_video"
            ]

    /// Every adapter in high-performance order with its capabilities and verdict. Each check is reported as a
    /// gpu.device.check record and each adapter's dedicated memory as a gpu.device.memory gauge.
    let private enumerateWindows () : AdapterReport list =
        use span =
            Telemetry.span "gpu.device.discover" [ Attr.str TelemetryKeys.Backend "d3d12" ]

        use factory = DxgiApi.createFactory<IDXGIFactory6> 0u

        let reports = ResizeArray<AdapterReport>()
        let mutable go = true

        while go do
            let mutable iid = Iids.IDXGIAdapter1
            let mutable out = 0n

            let hr =
                factory.EnumAdapterByGpuPreferenceRaw(
                    uint32 reports.Count,
                    DXGI_GPU_PREFERENCE.HIGH_PERFORMANCE,
                    NativePtr.toNativeInt &&iid,
                    NativePtr.toNativeInt &&out
                )

            if hr = HResult.DXGI_ERROR_NOT_FOUND then
                go <- false
            else
                Com.check "IDXGIFactory6::EnumAdapterByGpuPreference" hr factory.Ptr
                use adapter = ComPtr<IDXGIAdapter1>.Attach out
                let id = identity reports.Count adapter

                let caps =
                    try
                        use dev =
                            D3D12Api.createDevice<IDXGIAdapter1, ID3D12Device> adapter CreationFeatureLevel

                        Ok(capabilities dev)
                    with
                    | :? D3D12Exception as e -> Error e.Message
                    | :? DllNotFoundException as e -> Error e.Message

                let r =
                    {
                        Adapter = id
                        Capabilities = caps
                        Verdict = verdict id caps
                    }

                emit r
                reports.Add r

        let reports = List.ofSeq reports

        span.Add(Attr.int "adapters" reports.Length)

        span.Add(Attr.int "accepted" (reports |> List.filter (fun r -> r.Verdict = Accepted) |> List.length))

        reports

    /// Discovery uses Vulkan identity and a native vkd3d-proton device on Linux, DXGI on Windows.
    let enumerate () : AdapterReport list =
        if not (OperatingSystem.IsLinux()) then
            enumerateWindows ()
        else
            use span =
                Telemetry.span "gpu.device.discover" [ Attr.str TelemetryKeys.Backend "d3d12" ]

            let discover () =
                LinuxAdapters.enumerate ()
                |> List.map (fun a ->
                    let id =
                        {
                            Index = a.Index
                            Description = a.Name
                            VendorId = a.VendorId
                            DeviceId = a.DeviceId
                            SubSysId = 0u
                            Revision = 0u
                            DedicatedVideoMemory = a.DedicatedMemory
                            DedicatedSystemMemory = 0UL
                            SharedSystemMemory = a.SharedMemory
                            Luid = a.Luid
                            Software = a.Software
                            UmdVersion = None
                            DriverVersion = a.Driver
                        }

                    let caps =
                        try
                            use dev =
                                LinuxAdapters.createDevice<ID3D12Device> a.Index a.Uuid a.Luid CreationFeatureLevel

                            Ok(capabilities dev)
                        with
                        | :? D3D12Exception as e -> Error e.Message
                        | :? DllNotFoundException as e -> Error e.Message

                    let report =
                        {
                            Adapter = id
                            Capabilities = caps
                            Verdict = verdict id caps
                        }

                    emit report
                    report)


            let reports =
                try
                    discover ()
                with :? DllNotFoundException as e ->
                    let id =
                        {
                            Index = -1
                            Description = "vkd3dProton native layer unavailable"
                            VendorId = 0u
                            DeviceId = 0u
                            SubSysId = 0u
                            Revision = 0u
                            DedicatedVideoMemory = 0UL
                            DedicatedSystemMemory = 0UL
                            SharedSystemMemory = 0UL
                            Luid = 0L
                            Software = false
                            UmdVersion = None
                            DriverVersion = LinuxLibrary.version ()
                        }

                    let caps = Error e.Message

                    let report =
                        {
                            Adapter = id
                            Capabilities = caps
                            Verdict = verdict id caps
                        }

                    emit report
                    [ report ]

            span.Add(Attr.int "adapters" reports.Length)
            span.Add(Attr.int "accepted" (reports |> List.filter (fun r -> r.Verdict = Accepted) |> List.length))
            reports

    /// The first accepted adapter, if any (callers that need one device).
    let firstAccepted (reports: AdapterReport list) =
        reports |> List.tryFind (fun r -> r.Verdict = Accepted)
