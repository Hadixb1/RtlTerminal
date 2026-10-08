$dst = 'C:\Program Files\behnamapps\Rtl Terminal\RtlTerminal.exe'
$src = 'C:\Users\hadixb\RtlTerminal-work\publish\win-x64\RtlTerminal.exe'
$log = 'C:\Users\hadixb\RtlTerminal-work\replace_log.txt'

function Note([string]$m) {
    Add-Content -Path $log -Value ("$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  " + $m)
}

Note "replace_elevated started"
try {
    Rename-Item -Path $dst -NewName 'RtlTerminal.exe.bak_1.0.27' -Force -ErrorAction Stop
    Note 'RENAME_OK'
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
    Rename-Item -Path (Join-Path (Split-Path $dst) 'RtlTerminal.exe.bak_1.0.27') -NewName 'RtlTerminal.exe' -Force -ErrorAction SilentlyContinue
    Note 'ROLLED_BACK'
}
