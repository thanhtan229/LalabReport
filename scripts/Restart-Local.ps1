param([switch]$StopOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$exe = [IO.Path]::GetFullPath((Join-Path $projectRoot 'src\LalabAutoReport.UI\bin\Debug\net10.0-windows\LalabAutoReport.UI.exe'))
$owned = @(Get-CimInstance Win32_Process -Filter "Name='LalabAutoReport.UI.exe'" | Where-Object { $_.ExecutablePath -eq $exe })
foreach ($process in $owned) {
    Write-Output "Stopping owned application PID $($process.ProcessId): $($process.ExecutablePath)"
    Stop-Process -Id $process.ProcessId -Force
    Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
}
if ($StopOnly) { exit 0 }
if (!(Test-Path -LiteralPath $exe)) { throw 'Build Debug first: application executable missing.' }
$dll = Get-Item -LiteralPath (Join-Path (Split-Path $exe) 'LalabAutoReport.UI.dll')
$started = Start-Process -FilePath $exe -WorkingDirectory $projectRoot -ArgumentList '--minimized' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 4
$current = Get-CimInstance Win32_Process -Filter "ProcessId=$($started.Id)"
if (!$current -or $current.ExecutablePath -ne $exe) { throw 'Owned application did not remain running.' }
if ($started.StartTime.ToUniversalTime() -lt $dll.LastWriteTimeUtc) { throw 'Running process predates current build.' }
Write-Output "Verified PID $($started.Id), start $($started.StartTime.ToString('o')), DLL $($dll.LastWriteTimeUtc.ToString('o'))"
$health = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5050/api/status' -TimeoutSec 2
        if ($health.appName -eq 'Lalab Auto Report') { break }
    } catch { }
}
try {
    if (!$health -or $health.appName -ne 'Lalab Auto Report') { throw 'Unexpected or missing health response.' }
    $listener = Get-NetTCPConnection -LocalPort 5050 -State Listen -ErrorAction Stop
    if (!($listener.OwningProcess -contains $started.Id)) { throw 'Health listener belongs to another process.' }
    Write-Output 'Owned LAN health check: OK'
} catch {
    throw "Owned LAN health verification failed: $($_.Exception.Message)"
}
