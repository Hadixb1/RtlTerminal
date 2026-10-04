Remove-Item 'C:\Users\hadixb\RtlTerminal_repo\replace_done.txt' -ErrorAction SilentlyContinue
try {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File','C:\Users\hadixb\RtlTerminal_repo\replace_installed.ps1'
    Write-Output "elevated watcher launch requested"
} catch {
    Write-Output ("LAUNCH FAILED: " + $_.Exception.Message)
}
