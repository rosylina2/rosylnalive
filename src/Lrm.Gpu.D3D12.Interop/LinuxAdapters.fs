namespace Lrm.Gpu.D3D12.Interop

#nowarn "9"
#nowarn "51"

open System
open System.Collections.Concurrent
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop

/// Vulkan identity used by the native vkd3d-proton route. Handles never escape their owning instance.
type LinuxAdapter =
    {
        Index: int
        Name: string
        VendorId: uint32
        DeviceId: uint32
        Uuid: string
        Luid: int64
        Software: bool
        DedicatedMemory: uint64
        SharedMemory: uint64
        Driver: string
    }

/// A deliberately private Vulkan surface: the project graph forbids one interop referencing another interop.
/// The layer owns the VkInstance, so enumeration and device creation use the same instance and physical handles.
module private LinuxNative =
    // Like the D3D12 libraries, the loader stays loaded for every device's lifetime.
    let private loader = lazy (NativeLibrary.Load("libvulkan.so.1"))

    [<DllImport("libvulkan.so.1", ExactSpelling = true)>]
    extern nativeint vkGetInstanceProcAddr(nativeint instance, nativeint name)

    [<DllImport("libvkd3d-proton-d3d12core.so", ExactSpelling = true)>]
    extern int vkd3d_create_instance(nativeint info, nativeint result)

    [<DllImport("libvkd3d-proton-d3d12core.so", ExactSpelling = true)>]
    extern nativeint vkd3d_instance_get_vk_instance(nativeint instance)

    [<DllImport("libvkd3d-proton-d3d12core.so", ExactSpelling = true)>]
    extern uint32 vkd3d_instance_decref(nativeint instance)

    [<DllImport("libvkd3d-proton-d3d12core.so", ExactSpelling = true)>]
    extern int vkd3d_create_device(nativeint info, nativeint iid, nativeint result)

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type Enumerate = delegate of nativeint * nativeint * nativeint -> int

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type Properties = delegate of nativeint * nativeint -> unit

    type Buffer(size: int) =
        let ptr =
            NativeMemory.AllocZeroed(unativeint size)
            |> NativePtr.ofVoidPtr<byte>
            |> NativePtr.toNativeInt

        member _.Ptr = ptr

        interface IDisposable with
            member _.Dispose() =
                NativeMemory.Free(NativePtr.toVoidPtr (NativePtr.ofNativeInt<byte> ptr))

    let proc<'T when 'T :> Delegate> instance name =
        let bytes = Encoding.UTF8.GetBytes(name + "\000")
        use p = fixed bytes
        let address = vkGetInstanceProcAddr (instance, NativePtr.toNativeInt p)

        if address = 0n then
            invalidOp ("Vulkan entry point unavailable: " + name)

        Marshal.GetDelegateForFunctionPointer<'T> address

    type Instance() =
        do LinuxLibrary.ensure ()

        let handle =
            // x64 layout checked by tools/abi-oracle/d3d12/linux-probe.c.
            use info = new Buffer(40)
            Marshal.WriteIntPtr(info.Ptr, NativeLibrary.GetExport(loader.Value, "vkGetInstanceProcAddr"))
            let mutable result = 0n
            Com.check "vkd3d_create_instance" (vkd3d_create_instance (info.Ptr, NativePtr.toNativeInt &&result)) 0n
            result

        member _.Handle = handle
        member _.Vulkan = vkd3d_instance_get_vk_instance handle

        interface IDisposable with
            member _.Dispose() = vkd3d_instance_decref handle |> ignore

    let physicalDevices (instance: Instance) =
        let enumerate = proc<Enumerate> instance.Vulkan "vkEnumeratePhysicalDevices"
        let mutable count = 0u

        let check result =
            if result <> 0 then
                invalidOp (sprintf "vkEnumeratePhysicalDevices: VkResult %d" result)

        check (enumerate.Invoke(instance.Vulkan, NativePtr.toNativeInt &&count, 0n))
        let devices = Array.zeroCreate<nativeint>(int count)
        use p = fixed devices
        check (enumerate.Invoke(instance.Vulkan, NativePtr.toNativeInt &&count, NativePtr.toNativeInt p))
        devices |> Array.take (int count)

    let identity (instance: Instance) index physical =
        // Vulkan x64 layouts (840, 64, 536 and 520 bytes) are measured by the Linux ABI oracle.
        use properties = new Buffer(840)
        use ids = new Buffer(64)
        use driver = new Buffer(536)
        use memory = new Buffer(520)
        Marshal.WriteInt32(properties.Ptr, 1000059001)
        Marshal.WriteIntPtr(properties.Ptr, 8, ids.Ptr)
        Marshal.WriteInt32(ids.Ptr, 1000071004)
        Marshal.WriteIntPtr(ids.Ptr, 8, driver.Ptr)
        Marshal.WriteInt32(driver.Ptr, 1000196000)
        (proc<Properties> instance.Vulkan "vkGetPhysicalDeviceProperties2").Invoke(physical, properties.Ptr)
        (proc<Properties> instance.Vulkan "vkGetPhysicalDeviceMemoryProperties").Invoke(physical, memory.Ptr)
        let u32 ptr offset = uint32 (Marshal.ReadInt32(ptr, offset))
        let api = u32 properties.Ptr 16
        let version = u32 properties.Ptr 20
        let vendor = u32 properties.Ptr 24
        let name = Marshal.PtrToStringUTF8(properties.Ptr + 36n)
        let driverName = Marshal.PtrToStringUTF8(driver.Ptr + 20n)
        let driverInfo = Marshal.PtrToStringUTF8(driver.Ptr + 276n)

        let driverVersion =
            if vendor = 0x10deu then
                sprintf "%d.%d.%02d" (version >>> 22) ((version >>> 14) &&& 255u) ((version >>> 6) &&& 255u)
            else
                driverInfo

        let uuidBytes = Array.zeroCreate<byte> 16
        Marshal.Copy(ids.Ptr + 16n, uuidBytes, 0, 16)
        let hex = Convert.ToHexString(uuidBytes).ToLowerInvariant()

        let uuid =
            sprintf "%s-%s-%s-%s-%s" hex[..7] hex[8..11] hex[12..15] hex[16..19] hex[20..]

        let mutable dedicated = 0UL
        let mutable shared = 0UL

        for heap in 0 .. int (u32 memory.Ptr 260) - 1 do
            let offset = 264 + heap * 16
            let size = uint64 (Marshal.ReadInt64(memory.Ptr, offset))

            if u32 memory.Ptr (offset + 8) &&& 1u <> 0u then
                dedicated <- max dedicated size
            else
                shared <- shared + size

        {
            Index = index
            Name = name
            VendorId = vendor
            DeviceId = u32 properties.Ptr 28
            Uuid = uuid
            Luid =
                if u32 ids.Ptr 60 <> 0u then
                    Marshal.ReadInt64(ids.Ptr, 48)
                else
                    // Preserve Vulkan byte order; force the high part non-zero for an unavailable native LUID.
                    int64 (
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(uuidBytes.AsSpan(0, 8))
                        ||| 0x0000000100000000UL
                    )
            Software = u32 properties.Ptr 32 = 4u
            DedicatedMemory = dedicated
            SharedMemory = shared
            Driver =
                sprintf
                    "%s / %s %s Vulkan %d.%d.%d"
                    (LinuxLibrary.version ())
                    (if vendor = 0x10deu then "NVIDIA" else driverName)
                    driverVersion
                    (api >>> 22)
                    ((api >>> 12) &&& 1023u)
                    (api &&& 4095u)
        }

/// Linux discovery and creation through the pinned native layer; no DXGI object is involved.
[<RequireQualifiedAccess>]
module LinuxAdapters =
    let private metadata =
        ConcurrentDictionary<struct (int * int64), (string * string) list>()

    /// Supplemental discovery data, carried into DeviceInfo.Details without extending AdapterIdentity.
    let details index luid =
        match metadata.TryGetValue(struct (index, luid)) with
        | true, values -> values
        | _ -> []

    let private environmentDetails () =
        Environment.GetEnvironmentVariables()
        |> Seq.cast<System.Collections.DictionaryEntry>
        |> Seq.choose (fun e ->
            let name = string e.Key

            if
                name = "VKD3D_CONFIG"
                || name.StartsWith("VKD3D_SHADER_", StringComparison.Ordinal)
            then
                Some(name, string e.Value)
            else
                None)
        |> Seq.sortBy fst
        |> Seq.toList

    // Keep one native instance for the process, like the Vulkan loader and pinned D3D12 libraries.
    // Reuse avoids cycling loader/ICD state for every device while keeping enumeration handles in one instance.
    // Devices retain their own native reference; this releases only the discovery owner's reference at exit.
    let private instance =
        lazy
            (if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "VKD3D_VULKAN_DEVICE")) then
                 invalidOp
                     "VKD3D_VULKAN_DEVICE overrides the selected physical device; unset it and use the backend adapter index"

             let value = new LinuxNative.Instance()
             AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> (value :> IDisposable).Dispose())
             value)

    let enumerate () =
        if not (OperatingSystem.IsLinux()) then
            invalidOp "native vkd3d-proton requires Linux"

        let instance = instance.Value

        LinuxNative.physicalDevices instance
        |> Array.mapi (LinuxNative.identity instance)
        |> Array.map (fun a ->
            metadata[struct (a.Index, a.Luid)] <- ("device_uuid", a.Uuid) :: environmentDetails ()
            a)
        |> Array.toList

    /// Re-enumerate and match UUID and LUID before creating a device on the selected VkPhysicalDevice.
    let createDevice<'T when 'T :> IUnknown> index uuid luid (minimumFeatureLevel: D3D_FEATURE_LEVEL) : ComPtr<'T> =
        if not (OperatingSystem.IsLinux()) then
            invalidOp "native vkd3d-proton requires Linux"

        let instance = instance.Value
        let devices = LinuxNative.physicalDevices instance

        if index < 0 || index >= devices.Length then
            invalidOp "Vulkan adapter disappeared; enumerate again"

        let physical = devices[index]
        let adapter = LinuxNative.identity instance index physical

        if adapter.Uuid <> uuid || adapter.Luid <> luid then
            invalidOp "Vulkan adapter identity changed; enumerate again"

        use info = new LinuxNative.Buffer(88)
        Marshal.WriteInt32(info.Ptr, int minimumFeatureLevel)
        Marshal.WriteIntPtr(info.Ptr, 8, instance.Handle)
        Marshal.WriteIntPtr(info.Ptr, 24, physical)
        Marshal.WriteInt64(info.Ptr, 72, luid)
        // The layer honours independent unless ENABLE_VULKAN_RENDERDOC_CAPTURE=1; its INFO line is unconditional.
        // UUID-derived LUIDs also distinguish adapters if that environment setting forces the singleton map.
        Marshal.WriteByte(info.Ptr, 84, 1uy)
        let mutable iid = IidOf<'T>.Value
        let mutable result = 0n

        let hr =
            LinuxNative.vkd3d_create_device (info.Ptr, NativePtr.toNativeInt &&iid, NativePtr.toNativeInt &&result)

        Com.check "vkd3d_create_device" hr 0n
        let device = ComPtr<'T>.Attach result

        try
            use baseDevice = device.QueryInterface<ID3D12Device>()
            let actual = baseDevice.GetAdapterLuid()
            let actualLuid = (int64 actual.HighPart <<< 32) ||| int64 actual.LowPart

            if actualLuid <> luid then
                invalidOp (sprintf "vkd3d_create_device returned adapter LUID %016x, expected %016x" actualLuid luid)

            device
        with _ ->
            device.Release() |> ignore
            reraise ()
