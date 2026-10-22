$dst = 'C:\Program Files\behnamapps\Rtl Terminal\RtlTerminal.exe'
$src = Join-Path $PSScriptRoot 'publish\win-x64\RtlTerminal.exe'
$log = Join-Path $PSScriptRoot 'replace_log.txt'

function Note([string]$m) {
    Add-Content -Path $log -Value ("$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  " + $m)
}

Note "replace_elevated started"
$oldVer = try { (Get-Item $dst).VersionInfo.FileVersion } catch { 'old' }
$bakName = "RtlTerminal.exe.bak_$oldVer"
try {
    Rename-Item -Path $dst -NewName $bakName -Force -ErrorAction Stop
    Note "RENAME_OK -> $bakName"
} catch {
    Note ("RENAME_FAILED: " + $_.Exception.Message)
    exit 1
}
try {
    Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
    Note 'COPY_OK'
    $vi = (Get-Item $dst).VersionInfo
    Note ("NEW ProductVersion=" + $vi.ProductVersion)
} catch {
    Note ("COPY_FAILED: " + $_.Exception.Message)
    # rollback: put old file back
    Rename-Item -Path (Join-Path (Split-Path $dst) $bakName) -NewName 'RtlTerminal.exe' -Force -ErrorAction SilentlyContinue
    Note 'ROLLED_BACK'
}
