module Lrm.Core.Tests.ExecutionClassTests

open System
open Expecto
open Lrm.Core
open Lrm.Core.Tests.Common

/// KAT 1 of docs/contracts/execution-class.md: a CPU class.
let cpuKat =
    {
        Backend = ExecutionBackend.CpuF32
        DeviceId = "cpu"
        Driver = ".NET 10.0.0"
        ShaderSetHash = None
        Arch = "win-x64+avx2+fma"
    }

let cpuKatPayload =
    "07000000"
    + "6370752d663332" // "cpu-f32"
    + "03000000"
    + "637075" // "cpu"
    + "0b000000"
    + "2e4e45542031302e302e30" // ".NET 10.0.0"
    + "00" // ShaderSetHash None
    + "10000000"
    + "77696e2d7836342b617678322b666d61" // "win-x64+avx2+fma"

/// SHA-256("lrm.xclass/1" || 0x00 || cpuKatPayload), computed independently with sha256sum.
let cpuKatId = "2143ca35d54af553475a125be6abf185e914ecbe41eb400a80093b060c057eb0"

/// KAT 2: a GPU class with illustrative values (the real fill rule is R4a's).
let gpuKat =
    {
        Backend = ExecutionBackend.Vulkan
        DeviceId = "pci:10de:2704"
        Driver = "616.92"
        ShaderSetHash = Some(Hash.sha256Utf8 "abc")
        Arch = "win-x64"
    }

let gpuKatPayload =
    "06000000"
    + "76756c6b616e" // "vulkan"
    + "0d000000"
    + "7063693a313064653a32373034" // "pci:10de:2704"
    + "06000000"
    + "3631362e3932" // "616.92"
    + "01"
    + "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" // Some SHA-256("abc")
    + "07000000"
    + "77696e2d783634" // "win-x64"

let gpuKatId = "d79c93db855714890ff41356c0fd4dcb61e048b1fa346c87b7ca140506a3b057"

let tests =
    testList
        "execution-class"
        [
            testCase "backend names are frozen"
            <| fun () ->
                Expect.equal
                    (ExecutionBackend.all |> List.map ExecutionBackend.name)
                    [ "cpu-f32"; "cpu-f64"; "cpu-device"; "vulkan"; "d3d12"; "d3d12-warp" ]
                    "names"

                for b in ExecutionBackend.all do
                    Expect.equal (ExecutionBackend.tryParse (ExecutionBackend.name b)) (Some b) "parse"

                Expect.equal (ExecutionBackend.tryParse "Vulkan") None "ordinal"

            testCase "CPU class known answer"
            <| fun () ->
                Expect.equal (hex (Canonical.encode (fun w -> ExecutionClass.encode w cpuKat))) cpuKatPayload "payload"
                Expect.equal (ExecutionClass.id cpuKat).Value.Hex cpuKatId "id"
                Expect.equal (string (ExecutionClass.id cpuKat)) cpuKatId "ToString"

            testCase "GPU class known answer"
            <| fun () ->
                Expect.equal (hex (Canonical.encode (fun w -> ExecutionClass.encode w gpuKat))) gpuKatPayload "payload"
                Expect.equal (ExecutionClass.id gpuKat).Value.Hex gpuKatId "id"

            testCase "every field changes the id"
            <| fun () ->
                let variants =
                    [
                        { cpuKat with
                            Backend = ExecutionBackend.CpuF64
                        }
                        { cpuKat with DeviceId = "cpu2" }
                        { cpuKat with Driver = ".NET 10.0.1" }
                        { cpuKat with
                            ShaderSetHash = Some ContentId.zero
                        }
                        { cpuKat with Arch = "win-x64+avx2" }
                    ]

                let ids = cpuKat :: variants |> List.map ExecutionClass.id
                Expect.equal (List.distinct ids).Length ids.Length "distinct"

                Expect.isTrue
                    (ExecutionClass.same
                        cpuKat
                        { cpuKat with
                            Arch = "win-x64+avx2+fma"
                        })
                    "same"

            testCase "validation"
            <| fun () ->
                let bad =
                    [
                        { gpuKat with ShaderSetHash = None }, "GPU without a shader set hash"
                        { cpuKat with DeviceId = "" }, "empty DeviceId"
                        { cpuKat with Driver = " 1.0" }, "white space"
                        { cpuKat with Arch = "x64\n" }, "control character"
                        { cpuKat with DeviceId = null }, "null"
                        { cpuKat with Arch = String('a', 257) }, "too long"
                    ]

                for c, what in bad do
                    Expect.isError (ExecutionClass.validate c) what
                    Expect.throwsT<ArgumentException> (fun () -> ExecutionClass.id c |> ignore) what

                Expect.isOk (ExecutionClass.validate cpuKat) "cpu"
                Expect.isOk (ExecutionClass.validate gpuKat) "gpu"

            testCase "forCpu describes this host"
            <| fun () ->
                let c = ExecutionClass.forCpu ExecutionBackend.CpuF32
                Expect.equal c.DeviceId "cpu" "device"
                Expect.stringStarts c.Driver ".NET " "driver"
                let hostX64 = if OperatingSystem.IsLinux() then "linux-x64" else "win-x64"
                Expect.stringStarts c.Arch hostX64 "arch (Windows x64 and Linux x64 are the supported hosts)"
                Expect.isNone c.ShaderSetHash "no shaders"
                Expect.isOk (ExecutionClass.validate c) "valid"

                Expect.notEqual
                    (ExecutionClass.id c)
                    (ExecutionClass.id (ExecutionClass.forCpu ExecutionBackend.CpuF64))
                    "f32 vs f64"

                Expect.throwsT<ArgumentException>
                    (fun () -> ExecutionClass.forCpu ExecutionBackend.Vulkan |> ignore)
                    "GPU backends are filled by R4a's rule"

                printfn "host class: %s (%s)" (ExecutionClass.describe c) (ExecutionClass.id c).Value.Short
        ]
