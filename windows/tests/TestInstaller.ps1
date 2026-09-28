param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$target = [IO.Path]::GetFullPath((Join-Path $artifacts 'installer-smoke-v1.3.1'))
$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\VibeGauge'
$appKey = 'HKCU:\Software\VibeGauge'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeGauge'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (!$target.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid smoke-test path' }
if ((Test-Path $target) -or (Test-Path $menu) -or (Test-Path $uninstallKey) -or (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\VibeGauge'))) {
    throw 'Installer smoke test refuses to touch an existing installation or test directory'
}
$keyExisted = Test-Path $appKey
if ($keyExisted -and (Get-Item $appKey).GetValueNames().Count -gt 0) { throw 'Installer smoke test refuses to change existing application settings' }
if ((Test-Path $runKey) -and (Get-Item $runKey).GetValue('VibeGauge')) { throw 'Installer smoke test refuses to change an existing startup entry' }

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(90000)) { throw "Smoke test timed out: $File (PID $($process.Id))" }
    if ($process.ExitCode -ne 0) { throw "Smoke test failed: $File ($($process.ExitCode))" }
}

try {
    Invoke-Checked ([IO.Path]::GetFullPath($Installer)) @('/S',"/D=$target")
    $gui = Join-Path $target 'VibeGauge.exe'
    $proxy = Join-Path $target 'VibeGauge.Proxy.exe'
    if (!(Test-Path $gui) -or !(Test-Path $proxy)) { throw 'Installer omitted an executable' }
    Invoke-Checked $gui @('--selftest')
    Invoke-Checked $proxy @('--selftest')
    $fixture = Join-Path $artifacts 'upstream-qa\home'
    Invoke-Checked $gui @('--capture-ui','--tray-smoketest',"--capture-home=$fixture")
    Write-Output 'Installer extraction, GUI self-test, proxy self-test, and tray smoke test passed'
}
finally {
    $uninstaller = Join-Path $target 'Uninstall.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        # NSIS _?= runs the uninstaller in-place so WaitForExit covers actual cleanup.
        Invoke-Checked $uninstaller @('/S',"_?=$target")
        Start-Sleep -Milliseconds 300
        if (Test-Path -LiteralPath $uninstaller) { Remove-Item -LiteralPath $uninstaller -Force }
        if ((Test-Path -LiteralPath $target) -and !(Get-ChildItem -LiteralPath $target -Force)) { Remove-Item -LiteralPath $target }
    }
    if ($keyExisted -and !(Test-Path $appKey)) { New-Item -Path $appKey -Force | Out-Null }
}
if ((Test-Path $target) -or (Test-Path $uninstallKey) -or (Test-Path $menu)) { throw 'Installer smoke test left installation files or entries behind' }
Write-Output 'Uninstall cleanup verified; original empty application registry key preserved'
