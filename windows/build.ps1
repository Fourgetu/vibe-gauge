[CmdletBinding()]
param(
    [switch]$RequireInstaller,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$windows = $PSScriptRoot
$artifacts = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $windows 'artifacts' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $windows 'artifacts'))
if ($artifacts -ne $artifactRoot -and !$artifacts.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be windows/artifacts or one of its subdirectories'
}
if ((Test-Path -LiteralPath $artifacts) -and (Get-Item -LiteralPath $artifacts).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing to clean a linked artifact directory'
}
$running = Get-Process VibeGauge,VibeGauge.Proxy -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw 'An application is running from this output directory. Use a separate OutputDirectory or exit it first.' }
$publish = Join-Path $artifacts 'publish'
$proxyPublish = Join-Path $artifacts 'proxy-publish'
$dotnet = Join-Path $HOME '.dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }

[xml]$plist = Get-Content (Join-Path $root 'Resources\Info.plist')
$nodes = $plist.plist.dict.ChildNodes
for ($i = 0; $i -lt $nodes.Count; $i++) {
    if ($nodes[$i].Name -eq 'key' -and $nodes[$i].InnerText -eq 'CFBundleShortVersionString') {
        $version = $nodes[$i + 1].InnerText
        break
    }
}
if (!$version) { throw 'Cannot read CFBundleShortVersionString from Resources/Info.plist' }

if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
New-Item $publish -ItemType Directory -Force | Out-Null
New-Item $proxyPublish -ItemType Directory -Force | Out-Null

& $dotnet test (Join-Path $windows 'VibeGauge.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed: $LASTEXITCODE" }

& $dotnet publish (Join-Path $windows 'src\VibeGauge.Windows\VibeGauge.Windows.csproj') `
    -c Release -r win-x64 --self-contained true -o $publish `
    -p:Version=$version -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

& $dotnet publish (Join-Path $windows 'src\VibeGauge.Proxy\VibeGauge.Proxy.csproj') `
    -c Release -r win-x64 --self-contained true -o $proxyPublish `
    -p:Version=$version -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "proxy publish failed: $LASTEXITCODE" }
Copy-Item (Join-Path $proxyPublish 'VibeGauge.Proxy.exe') $publish

Copy-Item (Join-Path $root 'LICENSE') $publish
Copy-Item (Join-Path $root 'README_zh.md') (Join-Path $publish 'README.md')

$exe = Join-Path $publish 'VibeGauge.exe'
$selfTest = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
if ($selfTest.ExitCode -ne 0) { throw "published self-test failed: $($selfTest.ExitCode)" }
$proxyExe = Join-Path $publish 'VibeGauge.Proxy.exe'
$proxySelfTest = Start-Process -FilePath $proxyExe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
if ($proxySelfTest.ExitCode -ne 0) { throw "published proxy self-test failed: $($proxySelfTest.ExitCode)" }

$zip = Join-Path $artifacts "VibeGauge-Windows-x64-v$version.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $(Split-Path $zip -Leaf)" -Encoding ascii

$makensis = @(
    (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe'),
    (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
    (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($makensis) {
    & $makensis "/DAPP_VERSION=$version" "/DPUBLISH_DIR=$publish" "/DOUTPUT_DIR=$artifacts" (Join-Path $windows 'installer\VibeGauge.nsi')
    if ($LASTEXITCODE -ne 0) { throw "NSIS failed: $LASTEXITCODE" }
    $installer = Join-Path $artifacts "VibeGauge-Setup-v$version.exe"
    $installerHash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$installer.sha256" -Value "$installerHash  $(Split-Path $installer -Leaf)" -Encoding ascii
} elseif ($RequireInstaller) {
    throw 'NSIS was not found'
} else {
    Write-Warning 'NSIS was not found; portable ZIP was built without an installer.'
}

Write-Host "Windows artifacts: $artifacts"
