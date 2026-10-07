$src = 'C:\Users\hadixb\RtlTerminal_repo\publish\win-x64\RtlTerminal.exe'
$dst = 'C:\Program Files\Hadi\Rtl Terminal\RtlTerminal.exe'
$marker = 'C:\Users\hadixb\RtlTerminal_repo\replace_done.txt'

function Note([string]$m) {
    Add-Content -Path $marker -Value ("$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  " + $m)
}

Note "watcher started elevated - waiting for the INSTALLED RtlTerminal to close"

$deadline = (Get-Date).AddHours(12)
while ((Get-Date) -lt $deadline) {
    $busy = @(Get-Process RtlTerminal -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -eq $dst } catch { $false }
    }).Count
    if ($busy -eq 0) { break }
    Start-Sleep -Seconds 2
}

Note "installed instance closed - copying new build"

$ok = $false
$err = ''
for ($i = 0; $i -lt 45; $i++) {
    try {
        Copy-Item -Path $src -Destination $dst -Force -ErrorAction Stop
        $ok = $true
        break
    } catch {
        $err = $_.Exception.Message
        Start-Sleep -Seconds 2
    }
}

if ($ok) {
    $vi = (Get-Item $dst).VersionInfo
    Note ("REPLACED OK -> FileVersion=" + $vi.FileVersion + " Product=" + $vi.ProductVersion)
    Start-Process -FilePath $dst
    Note "relaunched installed terminal"
} else {
    Note ("FAILED: " + $err)
}
