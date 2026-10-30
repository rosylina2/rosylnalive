namespace Lrm.Gpu.D3D12

open System
open Lrm.Core
open Lrm.Gpu.Core
open Lrm.Gpu.D3D12.Interop

/// Device discovery for the d3d12 backend: R3's DeviceCheck reports, as the DeviceInfo and DeviceCandidate records of
/// GpuBackendInterface.
[<RequireQualifiedAccess>]
module Adapters =
    /// The UMD version text "a.b.c.d" (DeviceCheck: four decimal 16-bit parts, high first) back as the LARGE_INTEGER
    /// IDXGIAdapter::CheckInterfaceSupport returned. None when the text is not four 16-bit parts.
    let tryParseUmd (text: string) : int64 option =
        if String.IsNullOrEmpty text then
            None
        else
            let parts = text.Split '.'

            let values =
                parts
                |> Array.map (fun p ->
                    match UInt16.TryParse(p, Globalization.NumberStyles.None, Invariant.culture) with
                    | true, v -> Some v
                    | _ -> None)

            if parts.Length <> 4 || values |> Array.exists Option.isNone then
                None
            else
                Some(
                    values
                    |> Array.fold (fun (acc: uint64) v -> (acc <<< 16) ||| uint64 v.Value) 0UL
                    |> int64
                )

    /// The Driver field of the GPU fill rule (GpuClass.d3d12Driver over the UMD version), or "unknown" when the
    /// adapter reported none.
    let driverOf (a: AdapterIdentity) =
        if
            OperatingSystem.IsLinux()
            && a.DriverVersion.StartsWith("vkd3d-proton ", StringComparison.Ordinal)
        then
            a.DriverVersion
        else
            match a.UmdVersion |> Option.bind tryParseUmd with
            | Some umd -> GpuClass.d3d12Driver umd
            | None -> "unknown"

    let private yesNo (b: bool) = if b then "true" else "false"

    /// The LUID as DeviceInfo.Details carries it: 16 lowercase hex digits, HighPart first.
    let luidText (luid: int64) = sprintf "%016x" luid

    /// The DeviceInfo of an adapter report. Details, in this order: luid, umd_version, driver_version, software,
    /// dedicated_system_memory, shared_system_memory, then for a device feature_level, shader_model,
    /// resource_binding_tier, resource_heap_tier, root_signature, wave_lanes, enhanced_barriers and runtime_sdk.
    let info (r: AdapterReport) : DeviceInfo =
        let a = r.Adapter

        let caps =
            match r.Capabilities with
            | Ok c ->
                [
                    "feature_level", DeviceCheck.formatFeatureLevel c.MaxFeatureLevel
                    "shader_model", DeviceCheck.formatShaderModel c.HighestShaderModel
                    "resource_binding_tier", Invariant.i32 (int c.ResourceBindingTier)
                    "resource_heap_tier", Invariant.i32 (int c.ResourceHeapTier)
                    "root_signature", sprintf "1.%d" (int c.HighestRootSignature - 1)
                    "wave_lanes", sprintf "%d..%d" c.WaveLaneCountMin c.WaveLaneCountMax
                    "enhanced_barriers", yesNo c.EnhancedBarriers
                    "runtime_sdk",
                    (match c.RuntimeSdkVersion with
                     | Some v -> Invariant.u32 v
                     | None -> "unknown")
                ]
            | Error e -> [ "device_error", e ]

        {
            Backend = ExecutionBackend.D3D12
            Index = a.Index
            Name = a.Description
            VendorId = a.VendorId
            PciDeviceId = a.DeviceId
            Driver = driverOf a
            MemoryBytes = int64 a.DedicatedVideoMemory
            Details =
                [
                    "luid", luidText a.Luid
                    "umd_version", defaultArg a.UmdVersion "unknown"
                    "driver_version", a.DriverVersion
                    "software", yesNo a.Software
                    "dedicated_system_memory", Invariant.u64 a.DedicatedSystemMemory
                    "shared_system_memory", Invariant.u64 a.SharedSystemMemory
                ]
                @ caps
                @ DeviceCheck.detailsOf a
        }

    /// The Vulkan UUID in discovery details, used for adapter selection only.
    let deviceUuid (info: DeviceInfo) =
        info.Details
        |> List.tryPick (fun (key, value) -> if key = "device_uuid" then Some value else None)

    /// The rejections of an adapter report, with the Requirements texts for the two device requirements
    /// (GpuBackendInterface: shader model 6.6 and resource binding tier 3); a software adapter and an adapter without
    /// a device keep DeviceCheck's reason.
    let rejections (r: AdapterReport) : string list =
        [
            if r.Adapter.Software then
                "software adapter (WARP): the d3d12 backend needs hardware; d3d12-warp is a separate class"
            match r.Capabilities with
            | Error e -> "no D3D12 device: " + e
            | Ok c ->
                if c.HighestShaderModel < DeviceCheck.RequiredShaderModel then
                    Requirements.D3D12ShaderModel66

                if c.ResourceBindingTier < DeviceCheck.RequiredBindingTier then
                    Requirements.D3D12BindingTier3
        ]

    /// The candidate of an adapter report. It is accepted exactly when DeviceCheck accepts the adapter.
    let candidate (r: AdapterReport) : DeviceCandidate =
        let rejections =
            match r.Verdict, rejections r with
            | Accepted, _ -> []
            | Rejected reasons, [] -> reasons
            | Rejected _, xs -> xs

        {
            Info = info r
            Rejections = rejections
        }

    /// Every adapter, in IDXGIFactory6 high-performance order, as candidates. DeviceCheck reports each verdict as a
    /// gpu.device.check record and its memory as a gpu.device.memory gauge, inside a gpu.device.discover span.
    let enumerate () : DeviceCandidate list =
        DeviceCheck.enumerate () |> List.map candidate

    /// The LUID that DeviceInfo.Details names, if any.
    let luidOf (info: DeviceInfo) =
        info.Details
        |> List.tryFind (fun (k, _) -> k = "luid")
        |> Option.bind (fun (_, v) ->
            match Int64.TryParse(v, Globalization.NumberStyles.HexNumber, Invariant.culture) with
            | true, x -> Some x
            | _ -> None)
