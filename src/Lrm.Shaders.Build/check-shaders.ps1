<#
.SYNOPSIS
    Shader checks for CI and local runs (ShaderBuildAndSourceConvention contract, owner R4).

.DESCRIPTION
    Run after the Release build; it uses the built Lrm.Shaders.Build and the build's shader outputs. It checks what the
    build itself cannot show:
      1. Diff-clean codegen: Kernels.g.fs and lrm_args.g.hlsli equal a fresh generation from catalog.json.
      2. Reproducibility: a clean compile into an empty folder gives a byte-identical shaders.manifest.json, so both
         ShaderSetHash values reproduce.
      3. Incrementality: a no-change rebuild of Lrm.Shaders skips LrmCompileShadersCore, so dxc never starts.
      4. File-set changes: adding a helper file reruns LrmCompileShadersCore, deleting it reruns it again, and the
         manifest then equals a clean compile (sources.stamp). The probe file is removed again in every case.
      5. Incremental equals clean, on a scratch copy of the shader tree: editing a header in an include/ subfolder and
         editing a kernel's helper each give an incremental compile equal to a clean compile; deleting a kernel source
         prunes its blobs; a helper with a wave op or a path include is refused.
      6. Pinning: the compile step refuses a missing DXC and a DXC whose version line differs (the Windows Kits dxc
         when it is installed, otherwise another executable).
      7. The SPIR-V inspector (contracts-v1.1) over the built set: every SPIR-V blob has exactly one resource variable
         at set 0 binding 0, the catalog's push-block offsets, names and types, the catalog's LocalSize, no atomics,
         no 16-bit types, no wave ops and at most 32 KiB of groupshared. The compile step already refuses a blob that
         breaks a rule; this reruns the inspector on the built files and keeps its table for the summary.
      8. The inspector's negative fixtures (src/Lrm.Shaders.Build/fixtures/spirv), compiled by the pinned DXC: each
         must break exactly the rules its header names, and the positive control none.
    Prints a Markdown summary (DXC, ShaderSetHash per target, kernels, missing sources, the SPIR-V inspector table)
    and appends it to $GITHUB_STEP_SUMMARY on CI. Exit code 0 when every check holds, otherwise 1.

.EXAMPLE
    pwsh src/Lrm.Shaders.Build/check-shaders.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$DataRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolchainPath = Join-Path $repo 'tools/bootstrap/toolchain.json'
$toolchain = Get-Content -Raw -LiteralPath $toolchainPath | ConvertFrom-Json
if (-not $DataRoot) { $DataRoot = if ($env:LRM_DATA) { $env:LRM_DATA } else { $toolchain.dataRoot } }
# On Linux, the Linux build of the same DXC (toolchain.json dxc.linux).
$dxcPin = if ($IsLinux) { $toolchain.dxc.linux } else { $toolchain.dxc }
$dxc = Join-Path $DataRoot $dxcPin.exe
$versionLine = $dxcPin.versionOutput
$config = $Configuration.ToLowerInvariant()
$tool = Join-Path $repo "artifacts/bin/Lrm.Shaders.Build/$config/Lrm.Shaders.Build.dll"
$built = Join-Path $repo "artifacts/obj/Lrm.Shaders/$config/shaders"
$manifestPath = Join-Path $built 'shaders.manifest.json'
$project = Join-Path $repo 'src/Lrm.Shaders/Lrm.Shaders.fsproj'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Invoke-Tool([string[]]$arguments) {
    $out = & dotnet $tool @arguments 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = $out.Trim() }
}
function Invoke-Compile([string]$root, [string]$out) {
    Invoke-Tool @('compile', '--repo', $root, '--out', $out, '--dxc', $dxc, '--dxc-version', $versionLine, '--toolchain', $toolchainPath)
}
function Test-SameBytes([string]$a, [string]$b) {
    if (-not (Test-Path -LiteralPath $a) -or -not (Test-Path -LiteralPath $b)) { return $false }
    [byte[]]$x = [IO.File]::ReadAllBytes($a)
    [byte[]]$y = [IO.File]::ReadAllBytes($b)
    [Linq.Enumerable]::SequenceEqual($x, $y)
}
function New-Scratch([string]$name) { Join-Path ([IO.Path]::GetTempPath()) ("lrm-shaders-$name-" + [guid]::NewGuid().ToString('n')) }
function Invoke-Build {
    $log = & dotnet build $project -c $Configuration --no-restore --nologo -v:d 2>&1 | Out-String -Width 4096
    [pscustomobject]@{
        Code    = $LASTEXITCODE
        Skipped = $log -match 'Skipping target "LrmCompileShadersCore" because all output files are up-to-date'
        Ran     = $log -match 'Lrm\.Shaders\.Build: \d+ kernel\(s\)'
    }
}

foreach ($p in @($tool, $manifestPath, $dxc)) {
    if (-not (Test-Path -LiteralPath $p)) {
        Write-Host "check-shaders: $p is missing (bootstrap, then build Lrm.slnx -c $Configuration first)" -ForegroundColor Red
        exit 1
    }
}
Write-Host "check-shaders: $tool"

# 1. Diff-clean codegen.
$r = Invoke-Tool @('gen-catalog', '--repo', $repo, '--check')
if ($r.Output) { Write-Host $r.Output }
Check ($r.Code -eq 0) 'generated catalog files are current (gen-catalog --check)'

# 2. A clean compile reproduces the manifest byte for byte.
$fresh = New-Scratch 'clean'
try {
    $r = Invoke-Compile $repo $fresh
    if ($r.Output) { Write-Host $r.Output }
    Check ($r.Code -eq 0) 'a clean compile into an empty folder succeeds'
    if ($r.Code -eq 0) {
        Check (Test-SameBytes $manifestPath (Join-Path $fresh 'shaders.manifest.json')) 'the clean manifest equals the build manifest byte for byte (ShaderSetHash reproduces)'
    }
}
finally {
    Remove-Item -LiteralPath $fresh -Recurse -Force -ErrorAction SilentlyContinue
}

# 3. A no-change rebuild skips the shader target. The first build settles the tree (a new commit changes the source
#    revision stamped into every assembly, so the tool is rebuilt once); the second changes nothing and must skip.
& dotnet build $project -c $Configuration --no-restore --nologo -v:q | Out-Null
Check ($LASTEXITCODE -eq 0) 'the settling build succeeds'
$b = Invoke-Build
Check ($b.Code -eq 0) 'the no-change rebuild succeeds'
Check $b.Skipped 'a no-change rebuild skips LrmCompileShadersCore (dxc never starts)'

# 4. Adding or deleting a file reruns the target (sources.stamp). The probe is a lint-clean helper that no kernel
#    includes, so no blob changes; it sits at hlsl/ itself and is never committed.
$probe = Join-Path $repo 'src/Lrm.Shaders/hlsl/check-shaders.probe.hlsli'
$fresh = New-Scratch 'clean'
try {
    [IO.File]::WriteAllText($probe, "// check-shaders.ps1 probe; deleted when the check ends.`nfloat check_shaders_probe(float v) { return v; }`n")
    $b = Invoke-Build
    Check ($b.Code -eq 0 -and $b.Ran -and -not $b.Skipped) 'adding a file reruns LrmCompileShadersCore'
    Remove-Item -LiteralPath $probe -Force
    $b = Invoke-Build
    Check ($b.Code -eq 0 -and $b.Ran -and -not $b.Skipped) 'deleting a file reruns LrmCompileShadersCore'
    $r = Invoke-Compile $repo $fresh
    Check ($r.Code -eq 0 -and (Test-SameBytes $manifestPath (Join-Path $fresh 'shaders.manifest.json'))) 'after the add and delete, the build manifest equals a clean compile'
}
finally {
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fresh -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. Incremental equals clean, on a scratch copy of the shader tree (the tool needs src/Lrm.Shaders and the catalog).
$copy = New-Scratch 'tree'
$inc = New-Scratch 'inc'
$cleanRoots = [System.Collections.Generic.List[string]]::new()
try {
    $shaders = Join-Path $copy 'src/Lrm.Shaders'
    New-Item -ItemType Directory -Force -Path $shaders, (Join-Path $copy 'src/Lrm.Gpu.Core/Catalog') | Out-Null
    foreach ($d in @('include', 'hlsl', 'rootsig')) { Copy-Item -Recurse -LiteralPath (Join-Path $repo "src/Lrm.Shaders/$d") -Destination $shaders }
    Copy-Item -LiteralPath (Join-Path $repo 'src/Lrm.Gpu.Core/Catalog/catalog.json') -Destination (Join-Path $copy 'src/Lrm.Gpu.Core/Catalog')
    Copy-Item -LiteralPath (Join-Path $repo 'src/Lrm.Gpu.Core/Kernels.g.fs') -Destination (Join-Path $copy 'src/Lrm.Gpu.Core')

    function Set-Text([string]$rel, [string]$text) { [IO.File]::WriteAllText((Join-Path $shaders $rel), $text) }
    function Edit-Text([string]$rel, [string]$from, [string]$to) {
        $path = Join-Path $shaders $rel
        $text = [IO.File]::ReadAllText($path)
        if (-not $text.Contains($from)) { throw "check-shaders: '$from' not found in $rel" }
        [IO.File]::WriteAllText($path, $text.Replace($from, $to))
    }
    # Incremental compile into $inc, then a clean compile into a new folder; returns whether both manifests are equal.
    function Test-IncrementalEqualsClean {
        $ri = Invoke-Compile $copy $inc
        $clean = New-Scratch 'clean'
        $cleanRoots.Add($clean)
        $rc = Invoke-Compile $copy $clean
        if ($ri.Code -ne 0 -or $rc.Code -ne 0) { Write-Host $ri.Output; Write-Host $rc.Output; return $false }
        Test-SameBytes (Join-Path $inc 'shaders.manifest.json') (Join-Path $clean 'shaders.manifest.json')
    }
    function Get-Manifest { [IO.File]::ReadAllText((Join-Path $inc 'shaders.manifest.json')) }

    # A header in an include/ subfolder (reached through lrm.hlsli) and a helper beside scale_f32, both on its path.
    New-Item -ItemType Directory -Force -Path (Join-Path $shaders 'include/sub') | Out-Null
    Set-Text 'include/sub/probe.hlsli' "float check_probe(float v) { return v; }`n"
    Edit-Text 'include/lrm.hlsli' '#include "lrm_args.g.hlsli"' "#include `"lrm_args.g.hlsli`"`n#include `"sub/probe.hlsli`""
    Set-Text 'hlsl/selftest/probe_helper.hlsli' "float check_helper(float v) { return v; }`n"
    Edit-Text 'hlsl/selftest/scale_f32.hlsl' '#include "lrm.hlsli"' "#include `"lrm.hlsli`"`n#include `"probe_helper.hlsli`""
    Edit-Text 'hlsl/selftest/scale_f32.hlsl' 'lrm_load_f32(x, i) * lrm_args.a' 'check_helper(check_probe(lrm_load_f32(x, i))) * lrm_args.a'
    $r = Invoke-Compile $copy $inc
    if ($r.Code -ne 0) { Write-Host $r.Output }
    Check ($r.Code -eq 0) 'scratch tree: a kernel with a helper and a nested include/ header compiles'
    $m0 = Get-Manifest

    Edit-Text 'include/sub/probe.hlsli' 'return v;' 'return v + 1.0;'
    Check (Test-IncrementalEqualsClean) 'scratch tree: after editing include/sub/probe.hlsli, incremental equals clean'
    $m1 = Get-Manifest
    Check ($m1 -ne $m0) 'scratch tree: the include/ subfolder edit changed the set'

    Edit-Text 'hlsl/selftest/probe_helper.hlsli' 'return v;' 'return v * 3.0;'
    Check (Test-IncrementalEqualsClean) 'scratch tree: after editing a helper, incremental equals clean'
    Check ((Get-Manifest) -ne $m1) 'scratch tree: the helper edit changed the set'

    Remove-Item -LiteralPath (Join-Path $shaders 'hlsl/selftest/groups_echo.hlsl') -Force
    Check (Test-IncrementalEqualsClean) 'scratch tree: after deleting groups_echo.hlsl, incremental equals clean'
    $gone = -not (Test-Path (Join-Path $inc 'dxil/groups_echo.dxil')) -and -not (Test-Path (Join-Path $inc 'spirv/groups_echo.spv')) -and -not (Test-Path (Join-Path $inc 'dxil/groups_echo.dxil.key'))
    Check ($gone -and @((Get-Manifest | ConvertFrom-Json).missing) -contains 'groups_echo') 'scratch tree: the deleted kernel is missing and its blobs are pruned'

    $helper = 'hlsl/selftest/probe_helper.hlsli'
    Set-Text $helper "uint check_helper_sum(uint v) { return WaveActiveSum(v); }`nfloat check_helper(float v) { return v; }`n"
    $r = Invoke-Compile $copy $inc
    Check ($r.Code -ne 0 -and $r.Output -match 'WaveActiveSum') 'scratch tree: a helper with a wave op is refused'
    Set-Text $helper "#include `"../shared/deep.hlsli`"`nfloat check_helper(float v) { return v; }`n"
    $r = Invoke-Compile $copy $inc
    Check ($r.Code -ne 0 -and $r.Output -match '#include is not allowed in a helper') 'scratch tree: a helper that includes a path is refused'
}
finally {
    foreach ($d in @($copy, $inc) + $cleanRoots) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue }
}

# 6. Only the pinned DXC.
$scratch = New-Scratch 'neg'
try {
    $r = Invoke-Tool @('compile', '--repo', $repo, '--out', $scratch, '--dxc', (Join-Path $scratch 'no-such-dxc.exe'), '--dxc-version', $versionLine)
    Check ($r.Code -ne 0 -and $r.Output -match 'pinned DXC is missing') 'a missing DXC fails'

    $kits = @(Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\dxc.exe" -ErrorAction SilentlyContinue | Select-Object -First 1)
    $other = if ($kits) { $kits[0].FullName } else { (Get-Command dotnet).Source }
    $r = Invoke-Tool @('compile', '--repo', $repo, '--out', $scratch, '--dxc', $other, '--dxc-version', $versionLine)
    Check ($r.Code -ne 0 -and $r.Output -match 'is not the pinned DXC') "a DXC with another version line fails ($other)"
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

# 7. The SPIR-V inspector over the built set.
$spirvTable = New-Scratch 'spirv'
$spirvTable = "$spirvTable.md"
$spirvMd = ''
try {
    $r = Invoke-Tool @('inspect-spirv', '--repo', $repo, '--dir', $built, '--markdown', $spirvTable)
    if ($r.Output) { Write-Host $r.Output }
    Check ($r.Code -eq 0) 'the SPIR-V inspector passes every built SPIR-V blob (inspect-spirv)'
    if (Test-Path -LiteralPath $spirvTable) { $spirvMd = [IO.File]::ReadAllText($spirvTable) }
}
finally {
    Remove-Item -LiteralPath $spirvTable -Force -ErrorAction SilentlyContinue
}

# 8. The negative fixtures, compiled by the pinned DXC.
$r = Invoke-Tool @('spirv-fixtures', '--repo', $repo, '--dxc', $dxc, '--dxc-version', $versionLine, '--toolchain', $toolchainPath)
if ($r.Output) { Write-Host $r.Output }
Check ($r.Code -eq 0) 'every SPIR-V fixture breaks exactly the rules it expects (spirv-fixtures)'
$fixtureLine = @($r.Output -split "`n" | Where-Object { $_ -match '^spirv-fixtures:' }) | Select-Object -Last 1

# Summary.
$m = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine('### Shaders (LrmCompileShaders)')
[void]$md.AppendLine('')
[void]$md.AppendLine("DXC ``$($m.dxcVersion)``; catalog ``$($m.catalogHash)``.")
[void]$md.AppendLine('')
[void]$md.AppendLine('| Target | ShaderSetHash | Kernels |')
[void]$md.AppendLine('| --- | --- | ---: |')
foreach ($t in @('dxil', 'spirv')) {
    $e = $m.targets.$t
    [void]$md.AppendLine("| $t | ``$($e.shaderSetHash)`` | $(@($e.kernels).Count) |")
}
[void]$md.AppendLine('')
$missing = @($m.missing)
[void]$md.AppendLine("Missing sources ($($missing.Count)): $(if ($missing) { $missing -join ', ' } else { 'none' }).")
[void]$md.AppendLine('')
[void]$md.AppendLine('#### SPIR-V inspector')
[void]$md.AppendLine('')
if ($spirvMd) { [void]$md.Append($spirvMd.Replace("`r`n", "`n")) } else { [void]$md.AppendLine('(no table)') }
[void]$md.AppendLine('')
[void]$md.AppendLine("Fixtures: $(if ($fixtureLine) { $fixtureLine.Trim() } else { 'not run' })")
$verdict = if ($failures.Count -gt 0) { "FAILED ($($failures.Count))" } else { 'PASSED' }
[void]$md.AppendLine('')
[void]$md.AppendLine("Checks: **$verdict**")
$text = $md.ToString().Replace("`r`n", "`n")
Write-Host ''
Write-Host $text
if ($env:GITHUB_STEP_SUMMARY) { $text | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }

if ($failures.Count -gt 0) { Write-Host "check-shaders: FAILED ($($failures.Count) check(s))" -ForegroundColor Red; exit 1 }
Write-Host 'check-shaders: OK'
# The negative checks above run a failing dxc on purpose; GitHub's pwsh wrapper exits with $LASTEXITCODE, so
# end with an explicit success code.
exit 0
