Remove-Item "$PSScriptRoot\replace_log.txt" -ErrorAction SilentlyContinue
try {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',"$PSScriptRoot\replace_elevated.ps1"
    Write-Output "elevated replace launch requested"
} catch {
    Write-Output ("LAUNCH FAILED: " + $_.Exception.Message)
}
