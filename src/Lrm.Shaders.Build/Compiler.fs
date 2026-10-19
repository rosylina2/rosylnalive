/// The shader compile step of the LrmCompileShaders target (ShaderBuildAndSourceConvention contract).
module Lrm.Shaders.Build.Compiler

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Lrm.Core
open Lrm.Gpu.Core

exception BuildError of string

let fail fmt =
    Printf.kprintf (fun s -> raise (BuildError s)) fmt

let utf8 = UTF8Encoding(false)

/// The DXC arguments per target, without include, output and input paths (recorded in the manifest and hashed).
let flags =
    let common = [ "-T"; "cs_6_6"; "-E"; "main"; "-HV"; "2021"; "-O3"; "-WX" ]

    [
        // -denorm preserve: DXIL declares fp32-denorm-mode=preserve (shader model 6.2 and later), as on the CPU
        // reference; the denormal probe confirms it on the device (GpuBackendInterface).
        ShaderTarget.Dxil, common @ [ "-denorm"; "preserve" ]
        // No -denorm for SPIR-V: it would declare DenormPreserve 32, which needs shaderDenormPreserveFloat32, and the
        // RTX 4080 (driver 616.92) reports false. fp32 denormals on Vulkan are the device's behaviour, fixed within one
        // ExecutionClass; the denormal probe measures and records it.
        ShaderTarget.Spirv, common @ [ "-spirv"; "-fspv-target-env=vulkan1.3" ]
    ]

let flagsOf (t: ShaderTarget) =
    flags |> List.find (fst >> (=) t) |> snd

/// The arguments of the root signature compile.
let rootSignatureFlags = [ "-T"; "rootsig_1_1"; "-E"; "LRM_ROOTSIG" ]

// ---- processes ------------------------------------------------------------------------------------------------

type ProcessResult = { Code: int; Output: string }

let run (exe: string) (workDir: string) (args: string list) : ProcessResult =
    let psi = ProcessStartInfo(exe)

    for a in args do
        psi.ArgumentList.Add a

    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()

    {
        Code = p.ExitCode
        Output = (out.Result + err.Result).Trim()
    }

// ---- the pinned DXC -------------------------------------------------------------------------------------------

/// Verifies that `dxc` is the pinned compiler: the file exists, `dxc --version` prints exactly `expectedVersion`, and,
/// when toolchain.json is given, dxc sits at its pinned path and every pinned file has its pinned SHA-256. Returns the
/// version line.
let verifyDxc (dxc: string) (expectedVersion: string) (toolchain: string option) =
    if String.IsNullOrWhiteSpace dxc || not (File.Exists dxc) then
        fail "the pinned DXC is missing at '%s'; run: pwsh tools/bootstrap/bootstrap.ps1" dxc

    let v = run dxc (Path.GetDirectoryName(Path.GetFullPath dxc)) [ "--version" ]

    if v.Code <> 0 || v.Output <> expectedVersion then
        fail
            "'%s' is not the pinned DXC: --version printed '%s', expected '%s' (never use the Windows Kits dxc)"
            dxc
            v.Output
            expectedVersion

    match toolchain with
    | None -> ()
    | Some path ->
        use doc = JsonDocument.Parse(File.ReadAllText path)
        let entry = doc.RootElement.GetProperty "dxc"

        // A Linux host runs the entry's Linux build (dxc.linux), which pins its binaries in files (its sha256 is the
        // archive's).
        let d, pins =
            if OperatingSystem.IsLinux() then
                entry.GetProperty "linux", "files"
            else
                entry, "sha256"

        let exeRel = d.GetProperty("exe").GetString()
        let installRel = d.GetProperty("install").GetString()

        if not (exeRel.StartsWith(installRel + "/", StringComparison.Ordinal)) then
            fail "toolchain.json: dxc.exe is not under dxc.install"

        let full = Path.GetFullPath(dxc).Replace('\\', '/')
        let inInstall = exeRel.Substring(installRel.Length + 1)

        if not (full.EndsWith("/" + inInstall, StringComparison.OrdinalIgnoreCase)) then
            fail "'%s' is not at the pinned location .../%s" dxc exeRel

        let installDir = full.Substring(0, full.Length - inInstall.Length)

        for p in d.GetProperty(pins).EnumerateObject() do
            let file = Path.Combine(installDir, p.Name)

            if not (File.Exists file) then
                fail "the pinned DXC file %s is missing; run: pwsh tools/bootstrap/bootstrap.ps1" file

            let actual = (Hash.sha256File file).Hex

            if actual <> p.Value.GetString() then
                fail "%s has SHA-256 %s, but toolchain.json pins %s" file actual (p.Value.GetString())

    v.Output

// ---- compiling ------------------------------------------------------------------------------------------------

type Job =
    {
        Kernel: KernelSpec
        Target: ShaderTarget
        /// Relative to src/Lrm.Shaders.
        Source: string
        /// The helper headers the source includes, relative to src/Lrm.Shaders: files beside the source, which the
        /// source convention keeps free of includes.
        Helpers: string list
    }

let relativeSlash (root: string) (path: string) =
    Path.GetRelativePath(root, path).Replace('\\', '/')

let private ordinalSort (xs: string seq) =
    xs
    |> Seq.distinct
    |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofSeq

/// Every file under include/ (the -I directory, subfolders included), relative to src/Lrm.Shaders, sorted (ordinal).
let includeFiles (shadersDir: string) =
    Directory.GetFiles(Path.Combine(shadersDir, "include"), "*", SearchOption.AllDirectories)
    |> Seq.map (relativeSlash shadersDir)
    |> ordinalSort

/// The cache key of one compile: the DXC version, the target and its flags, and the path and bytes of every file the
/// source can reach: the source, the helpers it includes and every file under include/ (`headers`). The source
/// convention makes this the whole include closure: a kernel includes only lrm.hlsli and helpers beside it, and
/// helpers include nothing.
let cacheKey (shadersDir: string) (dxcVersion: string) (headers: string list) (job: Job) =
    Canonical.hash "lrm.shader-cache/2" (fun w ->
        w.String dxcVersion
        w.String(ShaderTarget.name job.Target)
        w.Seq(flagsOf job.Target, fun f -> w.String f)

        w.Seq(
            job.Source :: ordinalSort job.Helpers @ headers,
            fun rel ->
                w.String rel
                w.Bytes(File.ReadAllBytes(Path.Combine(shadersDir, rel)))
        ))

/// Checks a compiled blob and returns its numthreads: DXIL is a signed DXBC container with a DXIL part and numthreads
/// in PSV0; SPIR-V passes every rule of the SPIR-V inspector (one resource variable at set 0 binding 0, the catalog's
/// push-block offsets and LocalSize, no float atomics, no 16-bit types, ...), and its report is emitted as telemetry.
/// Either must have the catalog's threads.
let checkBlob (job: Job) (bytes: byte[]) : int * int * int =
    let where = sprintf "%s (%s)" job.Kernel.Name (ShaderTarget.name job.Target)

    let threads =
        match job.Target with
        | ShaderTarget.Dxil ->
            match Dxbc.parts bytes with
            | Error e -> fail "%s: not a DXBC container: %s" where e
            | Ok ps ->
                if not (ps |> List.exists (fun p -> p.FourCC = "DXIL")) then
                    fail "%s: the container has no DXIL part" where

                if not (Dxbc.isSigned bytes) then
                    fail "%s: the container is not signed (is dxil.dll beside dxc.exe?)" where

                match Dxbc.numThreads bytes with
                | Some t -> t
                | None -> fail "%s: PSV0 has no numthreads" where
        | ShaderTarget.Spirv ->
            let report = SpirvInspector.inspect job.Kernel bytes
            SpirvInspector.record report

            if not report.Passed then
                fail
                    "%s: the SPIR-V inspector found %d problem(s):\n  %s"
                    where
                    report.Findings.Length
                    (report.Findings |> List.map SpirvInspector.describe |> String.concat "\n  ")

            match report.Facts |> Option.bind _.LocalSize with
            | Some t -> t
            | None -> fail "%s: no single LocalSize execution mode" where

    if threads <> job.Kernel.Threads then
        fail "%s: numthreads %A differ from the catalog's %A" where threads job.Kernel.Threads

    threads

type CompileOutcome = { Blob: ShaderBlob; Cached: bool }

/// Compiles one job unless its cached blob has the same key, then checks the blob.
let compileOne
    (dxc: string)
    (dxcVersion: string)
    (shadersDir: string)
    (headers: string list)
    (outDir: string)
    (job: Job)
    : CompileOutcome =
    let blobPath =
        Path.Combine(outDir, ShaderTarget.name job.Target, job.Kernel.Name + ShaderTarget.extension job.Target)

    let keyPath = blobPath + ".key"
    let key = (cacheKey shadersDir dxcVersion headers job).Hex

    let cached =
        File.Exists blobPath
        && File.Exists keyPath
        && File.ReadAllText(keyPath).Trim() = key

    let attrs =
        [
            Attr.str TelemetryKeys.Kernel job.Kernel.Name
            Attr.str "target" (ShaderTarget.name job.Target)
        ]

    if not cached then
        use _ = Telemetry.span GpuTelemetry.ShaderCompile attrs
        let tmp = blobPath + ".tmp"

        let r =
            run dxc shadersDir (flagsOf job.Target @ [ "-I"; "include"; "-Fo"; tmp; job.Source ])

        if r.Code <> 0 then
            fail "%s (%s) failed to compile:\n%s" job.Source (ShaderTarget.name job.Target) r.Output

        File.Move(tmp, blobPath, true)
        File.WriteAllText(keyPath, key + "\n")

    let bytes = File.ReadAllBytes blobPath
    let threads = checkBlob job bytes
    Telemetry.gauge GpuTelemetry.ShaderBytes (float bytes.Length) TelemetryUnits.Bytes attrs

    {
        Blob =
            {
                Kernel = job.Kernel.Name
                Target = job.Target
                Bytes = bytes
                Sha256 = Hash.sha256 bytes
                Threads = threads
            }
        Cached = cached
    }

/// The catalog at `repo`, refusing stale generated files (the header the kernels include would be out of date).
let loadCatalog (repo: string) =
    match CatalogGen.stale repo with
    | Error e -> fail "%s" e
    | Ok(_ :: _ as files) ->
        fail
            "generated files are stale: %s; run: dotnet artifacts/bin/Lrm.Shaders.Build/release/Lrm.Shaders.Build.dll gen-catalog"
            (String.concat ", " files)
    | Ok [] ->
        match CatalogGen.parse (File.ReadAllText(Path.Combine(repo, CatalogGen.CatalogPath), utf8)) with
        | Ok c -> c
        | Error e -> fail "%s" e

/// The whole compile step: verify DXC, load the catalog, lint, compile both targets, check the root signature, prune
/// stale blobs and write shaders.manifest.json into `outDir`.
let compile (repo: string) (outDir: string) (dxc: string) (expectedVersion: string) (toolchain: string option) =
    let clock = Stopwatch.StartNew()

    match SpirvBinding.spirvFlagProblems (flagsOf ShaderTarget.Spirv) with
    | [] -> ()
    | ps -> fail "%s" (String.concat "\n" ps)

    let dxcVersion = verifyDxc dxc expectedVersion toolchain
    let shadersDir = Path.Combine(repo, "src", "Lrm.Shaders")
    let catalog = loadCatalog repo

    // Every .hlsl under hlsl/ must be a catalog kernel at its catalog path.
    let sources =
        Directory.GetFiles(Path.Combine(shadersDir, "hlsl"), "*.hlsl", SearchOption.AllDirectories)
        |> Array.map (relativeSlash shadersDir)
        |> Set.ofArray

    let known = catalog.Kernels |> List.map _.Source |> Set.ofList

    for s in Set.difference sources known do
        fail "%s is not a catalog kernel at its catalog path; add it to %s first" s CatalogGen.CatalogPath

    let present, missing =
        catalog.Kernels |> List.partition (fun k -> sources.Contains k.Source)

    let headers = includeFiles shadersDir

    let headerNames =
        headers
        |> List.map Path.GetFileName
        |> Set.ofList
        |> Set.map (fun n -> n.ToLowerInvariant())

    // Every helper header under hlsl/ follows the convention and cannot shadow a file of include/.
    let helperProblems =
        Directory.GetFiles(Path.Combine(shadersDir, "hlsl"), "*.hlsli", SearchOption.AllDirectories)
        |> Seq.map (relativeSlash shadersDir)
        |> ordinalSort
        |> List.collect (fun rel ->
            let fileName = Path.GetFileName rel

            [
                yield! SourceConvention.lintHelper fileName (File.ReadAllText(Path.Combine(shadersDir, rel), utf8))

                if headerNames.Contains(fileName.ToLowerInvariant()) then
                    yield sprintf "the helper name %s is also a file of include/" fileName
            ]
            |> List.map (fun p -> sprintf "%s: %s" rel p))

    let kernelSources =
        present
        |> List.map (fun k -> k, File.ReadAllText(Path.Combine(shadersDir, k.Source), utf8))

    // The helpers a kernel includes, relative to src/Lrm.Shaders (names the lint accepts, beside the kernel).
    let helpersOf (k: KernelSpec) (text: string) =
        let dir = k.Source.Substring(0, k.Source.LastIndexOf '/' + 1)

        SourceConvention.helpers text
        |> List.filter SourceConvention.isHelperName
        |> List.map (fun h -> dir + h)

    // Every kernel follows the convention, and each helper it includes exists beside it.
    let kernelProblems =
        kernelSources
        |> List.collect (fun (k, text) ->
            [
                yield! SourceConvention.lint k text

                for h in helpersOf k text do
                    if not (File.Exists(Path.Combine(shadersDir, h))) then
                        yield sprintf "includes \"%s\", but %s does not exist" (Path.GetFileName h) h
            ]
            |> List.map (fun p -> sprintf "%s: %s" k.Source p))

    let lintProblems = kernelProblems @ helperProblems

    if not lintProblems.IsEmpty then
        fail "source convention violations:\n  %s" (String.concat "\n  " lintProblems)

    for t in ShaderTarget.all do
        Directory.CreateDirectory(Path.Combine(outDir, ShaderTarget.name t)) |> ignore

    let jobs =
        [|
            for k, text in kernelSources do
                for t in ShaderTarget.all do
                    {
                        Kernel = k
                        Target = t
                        Source = k.Source
                        Helpers = helpersOf k text
                    }
        |]

    let outcomes = Array.zeroCreate<CompileOutcome> jobs.Length
    let errors = Collections.Concurrent.ConcurrentQueue<string>()

    Parallel.For(
        0,
        jobs.Length,
        ParallelOptions(MaxDegreeOfParallelism = min 8 Environment.ProcessorCount),
        fun i ->
            try
                outcomes[i] <- compileOne dxc dxcVersion shadersDir headers outDir jobs[i]
            with
            | BuildError e -> errors.Enqueue e
            | e -> errors.Enqueue(sprintf "%s: %s" jobs[i].Source e.Message)
    )
    |> ignore

    if not errors.IsEmpty then
        fail "%s" (errors |> Seq.sort |> String.concat "\n")

    // The canonical root signature.
    let rsPath = Path.Combine(outDir, ShaderSet.RootSignatureResource)

    let r =
        run dxc shadersDir (rootSignatureFlags @ [ "-Fo"; rsPath; "rootsig/rootsig.hlsl" ])

    if r.Code <> 0 then
        fail "rootsig/rootsig.hlsl failed to compile:\n%s" r.Output

    let rootSig = File.ReadAllBytes rsPath

    if not (RootSignature.isCanonical rootSig) then
        fail
            "rootsig.bin (%d bytes, %s) is not the canonical root signature %s"
            rootSig.Length
            (Convert.ToHexStringLower rootSig)
            RootSignature.CanonicalHex

    // Remove the blobs and keys of kernels that are gone.
    let keep =
        jobs
        |> Array.map (fun j -> ShaderSet.resourceName j.Target j.Kernel.Name)
        |> Set.ofArray

    for t in ShaderTarget.all do
        for f in Directory.GetFiles(Path.Combine(outDir, ShaderTarget.name t)) do
            let rel = ShaderTarget.name t + "/" + Path.GetFileName f

            let blob =
                if rel.EndsWith ".key" then
                    rel.Substring(0, rel.Length - 4)
                else
                    rel

            if not (keep.Contains blob) then
                File.Delete f

    let set =
        {
            DxcVersion = dxcVersion
            CatalogHash = Catalog.hash catalog
            Flags = flags
            RootSignature = rootSig
            Blobs =
                outcomes
                |> Array.map _.Blob
                |> Array.sortWith (fun a b ->
                    match String.CompareOrdinal(ShaderTarget.name a.Target, ShaderTarget.name b.Target) with
                    | 0 -> String.CompareOrdinal(a.Kernel, b.Kernel)
                    | c -> c)
                |> List.ofArray
            Missing = missing |> List.map _.Name
        }

    File.WriteAllText(Path.Combine(outDir, ShaderSet.ManifestResource), ShaderSet.manifestJson set, utf8)
    let compiled = outcomes |> Array.filter (fun o -> not o.Cached) |> Array.length

    printfn
        "Lrm.Shaders.Build: %d kernel(s) x 2 targets: %d compiled, %d cached, %.0f ms. Missing sources: %s"
        present.Length
        compiled
        (outcomes.Length - compiled)
        clock.Elapsed.TotalMilliseconds
        (if missing.IsEmpty then
             "none"
         else
             missing |> List.map _.Name |> String.concat ", ")

    for t in ShaderTarget.all do
        printfn "Lrm.Shaders.Build: ShaderSetHash %s %s" (ShaderTarget.name t) (ShaderSet.setHash t set).Hex

    printfn
        "Lrm.Shaders.Build: SPIR-V inspector: %d blob(s) pass all %d rules"
        (ShaderSet.blobsFor ShaderTarget.Spirv set).Length
        SpirvRule.all.Length

    set
