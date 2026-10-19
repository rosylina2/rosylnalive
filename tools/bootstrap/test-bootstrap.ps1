<#
.SYNOPSIS
    Self-test of bootstrap.ps1's file entries against a loopback HTTP server that counts requests.

.DESCRIPTION
    Runs bootstrap.ps1 with a fixture toolchain.json and a temporary LRM_DATA, in child processes, and checks:
      - an approved entry downloads once, and a second run downloads nothing (the request count stays 1);
      - a pending entry is never downloaded, even with -AllowDownload; its pinned local copy is verified, a corrupted
        copy fails the run, and a missing one is skipped;
      - a pending entry without pins is skipped even when a copy exists;
      - a missing approved file without -AllowDownload fails, and with it downloads again;
      - a download that does not match its pin fails and leaves neither the file nor a .partial behind;
      - a linux object that sets its own approval never approves its pending entry: a Linux host refuses the entry,
        every other host reads it as the pending entry it is, and neither fetches anything;
      - an installed tool's --version output must equal its pinned line exactly: a line that differs only in case
        fails the run.
    Nothing leaves the machine: the server listens on 127.0.0.1 only and serves a generated payload.

    Exit code: 0 when every check holds, otherwise 1.

.EXAMPLE
    pwsh tools/bootstrap/test-bootstrap.ps1
#>
[CmdletBinding()]
param([switch]$Keep)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$boot = Join-Path $PSScriptRoot 'bootstrap.ps1'
$pwsh = (Get-Process -Id $PID).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ("lrm-bootstrap-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Force -Path $data | Out-Null

$payload = [Text.Encoding]::UTF8.GetBytes(('LRM bootstrap fixture payload' + "`n") * 64)
$sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($payload)).ToLowerInvariant()

# A loopback HTTP/1.1 server that answers every GET with the payload and counts the requests.
$state = [hashtable]::Synchronized(@{ Requests = 0; Stop = $false })
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$server = Start-ThreadJob -ArgumentList $listener, $state, $payload -ScriptBlock {
    param($listener, $state, $payload)
    while (-not $state.Stop) {
        $accept = $listener.AcceptTcpClientAsync()
        while (-not $accept.Wait(50)) { if ($state.Stop) { return } }
        $client = $accept.Result
        try {
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
            while ($true) { $line = $reader.ReadLine(); if ($null -eq $line -or $line -eq '') { break } }
            $state.Requests++
            $head = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: application/octet-stream`r`nContent-Length: $($payload.Length)`r`nConnection: close`r`n`r`n")
            $stream.Write($head, 0, $head.Length)
            $stream.Write($payload, 0, $payload.Length)
            $stream.Flush()
        }
        finally { $client.Close() }
    }
}

function Entry([string]$Approval, [string]$Install, $Sha256 = $sha) {
    [ordered]@{
        managedBy = 'bootstrap'; kind = 'file'; approval = $Approval; version = 'fixture'
        url = "http://127.0.0.1:$port/fixture.bin"; size = $payload.Length; sha256 = $Sha256; install = $Install
    }
}
# A pending entry whose linux object claims an approval of its own.
$conflict = Entry 'pending' 'models/fixture/conflict.bin'
$conflict.linux = [ordered]@{
    approval = 'approved (fixture)'; kind = 'file'; url = $conflict.url; size = $payload.Length; sha256 = $sha
    install = 'toolchain/fixture/conflict-linux.bin'
}
# Approved zip entries already installed (folder and .lrm-install.json in place, so nothing is fetched), whose exe prints
# 'Fixture Version 1'. One pins that line, the other the same line with one letter's case changed.
$toolArchive = '1' * 64
$toolExe = if ($IsWindows) { 'version.cmd' } else { 'version.sh' }
function ToolEntry([string]$VersionOutput) {
    [ordered]@{
        managedBy = 'bootstrap'; kind = 'zip'; approval = 'approved (fixture)'; version = 'fixture'
        url = "http://127.0.0.1:$port/tool.zip"; size = 1; sha256 = $toolArchive; install = 'toolchain/fixture/tool'
        exe = "toolchain/fixture/tool/$toolExe"; versionOutput = $VersionOutput
    }
}
$toolchain = [ordered]@{
    schema          = 'lrm.toolchain/1'
    dataRoot        = $data
    approvedFile    = Entry 'approved (fixture)' 'toolchain/fixture/approved.bin'
    pendingLocal    = Entry 'pending' 'models/fixture/local.bin'
    pendingMissing  = Entry 'pending' 'models/fixture/missing.bin'
    pendingUnpinned = Entry 'pending' 'models/fixture/unpinned.bin' $null
    badDownload     = Entry 'approved (fixture)' 'toolchain/fixture/bad.bin' ('0' * 64)
    conflictLinux   = $conflict
    versionExact    = ToolEntry 'Fixture Version 1'
    versionCase     = ToolEntry 'Fixture version 1'
}
$toolchainPath = Join-Path $root 'toolchain.json'
[IO.File]::WriteAllText($toolchainPath, ($toolchain | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
function Put([string]$Rel, [byte[]]$Bytes) {
    $p = Join-Path $data $Rel
    New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null
    [IO.File]::WriteAllBytes($p, $Bytes)
}
Put 'models/fixture/local.bin' $payload
Put 'models/fixture/unpinned.bin' ([Text.Encoding]::UTF8.GetBytes("anything`n"))
$toolText = if ($IsWindows) { "@echo Fixture Version 1`r`n" } else { "#!/bin/sh`necho 'Fixture Version 1'`n" }
Put "toolchain/fixture/tool/$toolExe" ([Text.Encoding]::ASCII.GetBytes($toolText))
$toolPath = Join-Path $data "toolchain/fixture/tool/$toolExe"
if (-not $IsWindows) { [IO.File]::SetUnixFileMode($toolPath, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute') }
$toolRecord = [ordered]@{
    schema = 'lrm.toolchain-install/1'; entry = 'versionExact'; version = 'fixture'; url = "http://127.0.0.1:$port/tool.zip"
    archiveSha256 = $toolArchive; files = [ordered]@{ $toolExe = (Get-FileHash -LiteralPath $toolPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[IO.File]::WriteAllText((Join-Path $data 'toolchain/fixture/tool/.lrm-install.json'), ($toolRecord | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

$failures = 0
function Invoke-Bootstrap([string]$Name, [string[]]$Arguments, [int]$ExpectCode, [int]$ExpectRequests, [string[]]$Patterns) {
    $out = & $pwsh -NoProfile -NonInteractive -File $boot -ToolchainPath $toolchainPath -DataRoot $data @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $problems = @()
    if ($code -ne $ExpectCode) { $problems += "exit $code, expected $ExpectCode" }
    if ($state.Requests -ne $ExpectRequests) { $problems += "$($state.Requests) request(s) so far, expected $ExpectRequests" }
    foreach ($p in $Patterns) { if ($out -notmatch $p) { $problems += "output does not match /$p/" } }
    if ($problems) {
        $script:failures++
        Write-Host "  FAIL  $Name`: $($problems -join '; ')" -ForegroundColor Red
        Write-Host ($out.Trim() -replace '(?m)^', '        ')
    }
    else { Write-Host "  ok    $Name (exit $code, $($state.Requests) request(s) so far)" }
}

try {
    Write-Host "bootstrap self-test: loopback server on 127.0.0.1:$port, LRM_DATA $data" -ForegroundColor Cyan
    $entries = @('-Only', 'approvedFile,pendingLocal,pendingMissing,pendingUnpinned')
    Invoke-Bootstrap 'first run downloads the approved file once' (@('-AllowDownload') + $entries) 0 1 @(
        'approvedFile: downloading http://127.0.0.1', 'approvedFile fixture: OK', 'pendingLocal fixture: OK .*local copy verified; download not approved',
        'pendingMissing fixture: SKIPPED, the download awaits user approval; nothing fetched; not present',
        'pendingUnpinned fixture: SKIPPED.*a copy exists at .* but is not verified')
    Invoke-Bootstrap 'second run downloads nothing' (@('-AllowDownload') + $entries) 0 1 @('approvedFile fixture: OK', 'bootstrap: OK')
    Put 'models/fixture/local.bin' ($payload + [byte[]]@(10))
    Invoke-Bootstrap 'a corrupted pending copy fails and is not re-downloaded' (@('-AllowDownload') + $entries) 1 1 @("pendingLocal: .* is $($payload.Length + 1) bytes, the pin says $($payload.Length)")
    Put 'models/fixture/local.bin' $payload
    Remove-Item -LiteralPath (Join-Path $data 'toolchain/fixture/approved.bin')
    Invoke-Bootstrap 'a missing approved file without -AllowDownload fails' $entries 1 1 @('Run bootstrap with -AllowDownload')
    Invoke-Bootstrap 'with -AllowDownload it downloads again' (@('-AllowDownload') + $entries) 0 2 @('approvedFile: downloading http://127.0.0.1')
    Invoke-Bootstrap 'a download that misses its pin fails' @('-AllowDownload', '-Only', 'badDownload') 1 3 @('badDownload: .* has SHA-256 [0-9a-f]{64}, the pin says 0{64}')
    $left = @(Get-ChildItem -LiteralPath (Join-Path $data 'toolchain/fixture') -Filter 'bad.bin*' -ErrorAction SilentlyContinue)
    if ($left) { $failures++; Write-Host "  FAIL  the failed download left $($left.Name -join ', ')" -ForegroundColor Red }
    else { Write-Host '  ok    the failed download left neither the file nor a .partial' }
    # Linux refuses the contradicting linux object; every other host reads the pending entry and skips it.
    $conflictCode = if ($IsLinux) { 1 } else { 0 }
    $conflictSays = if ($IsLinux) { "conflictLinux: its linux object sets 'approval'" } else { 'conflictLinux fixture: SKIPPED, the download awaits user approval; nothing fetched' }
    Invoke-Bootstrap 'a linux object cannot approve what its entry does not' @('-AllowDownload', '-Only', 'conflictLinux') $conflictCode 3 @($conflictSays)
    if (Test-Path -LiteralPath (Join-Path $data 'toolchain/fixture/conflict-linux.bin')) { $failures++; Write-Host '  FAIL  the contradicting linux object installed its file' -ForegroundColor Red }
    else { Write-Host '  ok    the contradicting linux object installed nothing' }
    Invoke-Bootstrap 'an installed tool whose version line equals the pin passes' @('-Only', 'versionExact') 0 3 @('versionExact fixture: OK')
    Invoke-Bootstrap 'a version line that differs from the pin only in case fails' @('-Only', 'versionCase') 1 3 @("versionCase: --version printed 'Fixture Version 1', the pin says 'Fixture version 1'")
}
finally {
    $state.Stop = $true
    Wait-Job $server -Timeout 5 | Out-Null
    Remove-Job $server -Force
    $listener.Stop()
    if (-not $Keep) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue } else { Write-Host "kept $root" }
}

if ($failures) { Write-Host "bootstrap self-test: FAILED ($failures)" -ForegroundColor Red; exit 1 }
Write-Host 'bootstrap self-test: OK'
exit 0
