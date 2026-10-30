namespace Lrm.Gpu.D3D12.Interop

#nowarn "9"
#nowarn "51"

open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

/// The DLL exports the interop calls, bound by name from the in-box d3d12.dll and dxgi.dll. Signatures are blittable;
/// the ABI oracle checks each one against the SDK declaration.
module Native =
    /// d3d12.h: HRESULT D3D12CreateDevice(IUnknown* pAdapter, D3D_FEATURE_LEVEL MinimumFeatureLevel, REFIID riid,
    /// void** ppDevice).
    [<DllImport("d3d12.dll", ExactSpelling = true)>]
    extern int D3D12CreateDevice(
        nativeint pAdapter,
        D3D_FEATURE_LEVEL MinimumFeatureLevel,
        nativeint riid,
        nativeint ppDevice
    )

    /// d3d12.h: HRESULT D3D12GetDebugInterface(REFIID riid, void** ppvDebug).
    [<DllImport("d3d12.dll", ExactSpelling = true)>]
    extern int D3D12GetDebugInterface(nativeint riid, nativeint ppvDebug)

    /// dxgi1_3.h: HRESULT CreateDXGIFactory2(UINT Flags, REFIID riid, void** ppFactory).
    [<DllImport("dxgi.dll", ExactSpelling = true)>]
    extern int CreateDXGIFactory2(uint32 Flags, nativeint riid, nativeint ppFactory)

/// Typed forms of the d3d12.dll exports.
[<RequireQualifiedAccess>]
module D3D12Api =
    /// D3D12CreateDevice on `adapter` (ComPtr.Null for the default adapter). Raises D3D12Exception on failure.
    let createDevice<'A, 'T when 'A :> IUnknown and 'T :> IUnknown>
        (adapter: ComPtr<'A>)
        (minimumFeatureLevel: D3D_FEATURE_LEVEL)
        : ComPtr<'T> =
        LinuxLibrary.ensure ()
        let mutable iid = IidOf<'T>.Value
        let mutable out = 0n

        let hr =
            Native.D3D12CreateDevice(
                adapter.Ptr,
                minimumFeatureLevel,
                NativePtr.toNativeInt &&iid,
                NativePtr.toNativeInt &&out
            )

        Com.check "D3D12CreateDevice" hr 0n
        ComPtr<'T>.Attach out

    /// Whether D3D12CreateDevice would succeed, without creating a device (ppDevice = null returns S_FALSE).
    let canCreateDevice<'A when 'A :> IUnknown> (adapter: ComPtr<'A>) (minimumFeatureLevel: D3D_FEATURE_LEVEL) =
        LinuxLibrary.ensure ()
        let mutable iid = Iids.ID3D12Device

        let hr =
            Native.D3D12CreateDevice(adapter.Ptr, minimumFeatureLevel, NativePtr.toNativeInt &&iid, 0n)

        hr >= 0

    /// D3D12GetDebugInterface for 'T (ID3D12Debug, ID3D12Debug1, ID3D12DeviceRemovedExtendedDataSettings, ...).
    /// ValueNone when the interface is unavailable, for example without the Graphics Tools optional feature.
    let tryGetDebugInterface<'T when 'T :> IUnknown> () : ComPtr<'T> voption =
        LinuxLibrary.ensure ()
        let mutable iid = IidOf<'T>.Value
        let mutable out = 0n

        let hr =
            Native.D3D12GetDebugInterface(NativePtr.toNativeInt &&iid, NativePtr.toNativeInt &&out)

        if hr >= 0 && out <> 0n then
            ValueSome(ComPtr<'T>.Attach out)
        else
            ValueNone

/// Typed forms of the dxgi.dll exports.
[<RequireQualifiedAccess>]
module DxgiApi =
    /// DXGI_CREATE_FACTORY_DEBUG (dxgi1_3.h).
    [<Literal>]
    let CreateFactoryDebug = 1u

    /// CreateDXGIFactory2 for 'T (IDXGIFactory6 for adapter enumeration by GPU preference).
    let createFactory<'T when 'T :> IUnknown> (flags: uint32) : ComPtr<'T> =
        let mutable iid = IidOf<'T>.Value
        let mutable out = 0n

        let hr =
            Native.CreateDXGIFactory2(flags, NativePtr.toNativeInt &&iid, NativePtr.toNativeInt &&out)

        Com.check "CreateDXGIFactory2" hr 0n
        ComPtr<'T>.Attach out
