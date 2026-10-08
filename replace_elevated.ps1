$dst = 'C:\Program Files\behnamapps\Rtl Terminal\RtlTerminal.exe'
$src = 'C:\Users\hadixb\RtlTerminal-work\publish\win-x64\RtlTerminal.exe'
try {
    Rename-Item -Path $dst -NewName 'RtlTerminal.exe.old_1023' -Force -ErrorAction Stop
    Write-Output 'RENAME_OK'
} catch {
    Write-Output ("RENAME_FAILED: " + $_.Exception.Message)
    exit 1
}
try {
    Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
    Write-Output 'COPY_OK'
    $vi = (Get-Item $dst).VersionInfo
    Write-Output ("NEW ProductVersion=" + $vi.ProductVersion)
} catch {
    Write-Output ("COPY_FAILED: " + $_.Exception.Message)
    # rollback: put old file back
    Rename-Item -Path (Join-Path (Split-Path $dst) 'RtlTerminal.exe.old_1023') -NewName 'RtlTerminal.exe' -Force -ErrorAction SilentlyContinue
    Write-Output 'ROLLED_BACK'
}
