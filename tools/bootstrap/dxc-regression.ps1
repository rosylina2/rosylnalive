<#
.SYNOPSIS
    DXC regression step: the pinned DXC still compiles our bindless form the way the plan relies on.

.DESCRIPTION
    Uses only the DXC pinned in tools/bootstrap/toolchain.json (run bootstrap first; on Linux, its Linux build, dxc.linux)
    and checks:
      1. `dxc --version` prints the pinned version line.
      2. dxc-regression/bindless.hlsl compiles to validated DXIL (cs_6_6, HLSL 2021, warnings are errors).
      3. The same source compiles to SPIR-V (vulkan1.3) with exactly one descriptor variable, at set 0 binding 0,
         and the arguments in a push-constant block.
      4. dxc-regression/rootsig.hlsl compiles with -T rootsig_1_1 to the 92-byte global root signature.
    Outputs go to artifacts/dxc-regression/. Exit code 0 when every check holds, otherwise 1.
#>
[CmdletBinding()]
param(
    [string]$DataRoot,
    [string]$OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$toolchain = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') | ConvertFrom-Json
if (-not $DataRoot) { $DataRoot = if ($env:LRM_DATA) { $env:LRM_DATA } else { $toolchain.dataRoot } }
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts/dxc-regression' }
# On Linux, the Linux build of the same DXC (toolchain.json dxc.linux).
$dxcPin = if ($IsLinux) { $toolchain.dxc.linux } else { $toolchain.dxc }
$dxc = Join-Path $DataRoot $dxcPin.exe
$src = Join-Path $PSScriptRoot 'dxc-regression'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Invoke-Dxc([string[]]$arguments) {
    $out = & $dxc @arguments 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = $out.Trim() }
}

if (-not (Test-Path -LiteralPath $dxc)) {
    Write-Host "dxc-regression: $dxc is missing; run pwsh tools/bootstrap/bootstrap.ps1 first" -ForegroundColor Red
    exit 1
}
Write-Host "dxc-regression: $dxc"

$v = Invoke-Dxc @('--version')
Check ($v.Code -eq 0 -and $v.Output -eq $dxcPin.versionOutput) "version is '$($dxcPin.versionOutput)' (got '$($v.Output)')"

$hlsl = Join-Path $src 'bindless.hlsl'
$dxil = Join-Path $OutDir 'bindless.dxil'
$r = Invoke-Dxc @('-T', 'cs_6_6', '-E', 'main', '-HV', '2021', '-WX', $hlsl, '-Fo', $dxil)
if ($r.Output) { Write-Host $r.Output }
$dxilOk = $r.Code -eq 0 -and (Test-Path $dxil)
Check $dxilOk 'bindless.hlsl compiles to DXIL cs_6_6'
if ($dxilOk) {
    $bytes = [IO.File]::ReadAllBytes($dxil)
    Check ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -eq 'DXBC') 'DXIL output is a DXBC container'
    # A validated container carries a non-zero hash right after the magic.
    Check (($bytes[4..19] | Where-Object { $_ -ne 0 }).Count -gt 0) 'DXIL container is validated (hash present)'
}

$spv = Join-Path $OutDir 'bindless.spv'
$asm = Join-Path $OutDir 'bindless.spvasm'
$r = Invoke-Dxc @('-T', 'cs_6_6', '-E', 'main', '-HV', '2021', '-WX', '-spirv', '-fspv-target-env=vulkan1.3', $hlsl, '-Fo', $spv, '-Fc', $asm)
if ($r.Output) { Write-Host $r.Output }
$spvOk = $r.Code -eq 0 -and (Test-Path $spv) -and (Test-Path $asm)
Check $spvOk 'bindless.hlsl compiles to SPIR-V (vulkan1.3)'
if ($spvOk) {
    $bytes = [IO.File]::ReadAllBytes($spv)
    Check ([BitConverter]::ToUInt32($bytes, 0) -eq 0x07230203) 'SPIR-V magic number'
    $text = Get-Content -LiteralPath $asm
    $sets = @($text | Select-String -Pattern 'OpDecorate %(\S+) DescriptorSet (\d+)')
    $bindings = @($text | Select-String -Pattern 'OpDecorate %(\S+) Binding (\d+)')
    Check ($sets.Count -eq 1) "exactly one descriptor variable (found $($sets.Count))"
    if ($sets.Count -eq 1 -and $bindings.Count -eq 1) {
        $set = $sets[0].Matches[0].Groups
        $bind = $bindings[0].Matches[0].Groups
        Check ($set[1].Value -eq 'g_buf' -and $set[2].Value -eq '0' -and $bind[1].Value -eq 'g_buf' -and $bind[2].Value -eq '0') 'g_buf is at set 0, binding 0'
    }
    else { Check $false "exactly one binding decoration (found $($bindings.Count))" }
    Check (@($text | Select-String -SimpleMatch 'OpVariable %_ptr_PushConstant_').Count -eq 1) 'arguments are one push-constant block'
}

$rs = Join-Path $OutDir 'rootsig.bin'
$r = Invoke-Dxc @('-T', 'rootsig_1_1', '-E', 'LRM_ROOTSIG', (Join-Path $src 'rootsig.hlsl'), '-Fo', $rs)
if ($r.Output) { Write-Host $r.Output }
$rsOk = $r.Code -eq 0 -and (Test-Path $rs)
Check $rsOk 'rootsig.hlsl compiles with -T rootsig_1_1'
if ($rsOk) { Check ((Get-Item $rs).Length -eq 92) "root signature is 92 bytes (got $((Get-Item $rs).Length))" }

if ($failures.Count -gt 0) { Write-Host "dxc-regression: FAILED ($($failures.Count) check(s))" -ForegroundColor Red; exit 1 }
Write-Host 'dxc-regression: OK'
