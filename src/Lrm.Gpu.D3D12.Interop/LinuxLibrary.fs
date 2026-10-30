namespace Lrm.Gpu.D3D12.Interop

#nowarn "9"
#nowarn "51"

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Text
open Lrm.Core
open Microsoft.FSharp.NativeInterop

/// The Linux native development build is resolved only from the embedded toolchain pin and LRM_DATA.
module internal LinuxLibrary =
    [<DllImport("libc.so.6", ExactSpelling = true)>]
    extern int setenv(nativeint name, nativeint value, int overwrite)

    // .NET's managed environment table does not update libc's getenv on this Linux runtime.
    // Publish the cache default to both native code and any managed child-process launch.
    let private setNativeEnvironment name value =
        let nameBytes = Encoding.UTF8.GetBytes(name + "\000")
        let valueBytes = Encoding.UTF8.GetBytes(value + "\000")
        use n = fixed nameBytes
        use v = fixed valueBytes

        if setenv (NativePtr.toNativeInt n, NativePtr.toNativeInt v, 1) <> 0 then
            invalidOp ("could not set native environment variable " + name)

        Environment.SetEnvironmentVariable(name, value)

    let private pin =
        lazy
            (use stream =
                typeof<ID3D12Device>.Assembly.GetManifestResourceStream("Lrm.D3D12.Toolchain.json")

             use document = JsonDocument.Parse stream
             document.RootElement.GetProperty("vkd3dProton").Clone())

    let field (name: string) = pin.Value.GetProperty(name).GetString()

    let private libraryPath name =
        Path.Combine(LrmPaths.dataRoot (), field "install", field name)

    let private unavailable path (cause: Exception) =
        DllNotFoundException(
            sprintf
                "vkd3dProton %s: missing native layer at %s; run tools/bootstrap/build-vkd3d.sh, install its prefix under LRM_DATA, then run tools/bootstrap/bootstrap.ps1"
                (field "version")
                path,
            cause
        )

    let private verifyReceipt () =
        let path = Path.Combine(LrmPaths.dataRoot (), field "install", ".lrm-native.json")

        if not (File.Exists path) then
            raise (unavailable path null)

        use document = JsonDocument.Parse(File.ReadAllText path)
        let receipt = document.RootElement

        for key in [ "version"; "commit"; "patchSha256" ] do
            let mutable value = Unchecked.defaultof<JsonElement>

            if not (receipt.TryGetProperty(key, &value)) || value.GetString() <> field key then
                raise (
                    DllNotFoundException(
                        sprintf
                            "vkd3dProton %s: %s mismatch in %s; rerun the pinned build-vkd3d.sh recipe and bootstrap"
                            (field "version")
                            key
                            path
                    )
                )

    let private load name =
        let path = libraryPath name

        try
            NativeLibrary.Load path
        with :? DllNotFoundException as e ->
            raise (unavailable path e)

    // Load the core by its pinned absolute path first. Its SONAME lets the front library's dlopen find this
    // exact instance even though upstream removes the build-tree RUNPATH when installing the libraries.
    let private loaded =
        lazy
            (if not (OperatingSystem.IsLinux()) then
                 invalidOp "the vkd3d-proton route requires Linux"

             if String.IsNullOrEmpty(Environment.GetEnvironmentVariable "VKD3D_SHADER_CACHE_PATH") then
                 let cache =
                     Path.Combine(LrmPaths.dataRoot (), "cache", "vkd3d-proton", field "version")

                 Directory.CreateDirectory cache |> ignore
                 setNativeEnvironment "VKD3D_SHADER_CACHE_PATH" cache

             verifyReceipt ()
             let core = load "coreLibrary"
             let front = load "library"

             NativeLibrary.SetDllImportResolver(
                 typeof<ID3D12Device>.Assembly,
                 DllImportResolver(fun name _ _ -> if name = "d3d12.dll" then front else 0n)
             )

             core)

    let ensure () =
        if OperatingSystem.IsLinux() then
            loaded.Value |> ignore

    let version () =
        sprintf "vkd3d-proton %s (%s)" (field "version") (field "commit")
