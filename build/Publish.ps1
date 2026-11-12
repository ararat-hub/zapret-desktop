param(
    [string]$EngineZip,
    [string]$LicenseDirectory,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'flowseal.json') -Raw | ConvertFrom-Json
$cache = New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'artifacts/downloads')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/win-x64' }

function Get-VerifiedFile([string]$path, [string]$sha256, [string]$url = '') {
    if (-not (Test-Path -LiteralPath $path) -and $url) {
        Invoke-WebRequest -Uri $url -OutFile $path -TimeoutSec 45
    }
    $file = Get-Item -LiteralPath $path
    if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $sha256) {
        throw "Unexpected SHA-256 for $($file.Name). Remove the cached copy and retry."
    }
    return $file
}

$asset = "zapret-discord-youtube-$($pin.tag).zip"
$engineUrl = ''
if (-not $EngineZip) {
    $EngineZip = Join-Path $cache.FullName $asset
    $engineUrl = "https://github.com/Flowseal/zapret-discord-youtube/releases/download/$($pin.tag)/$asset"
}
$engine = Get-VerifiedFile $EngineZip $pin.sha256 $engineUrl
if ($engine.Length -ne $pin.size) { throw 'Unexpected Flowseal archive size.' }

$licenseFiles = @(
    @('Flowseal.txt', 'FE3983A1E91206AD1A530BCFAE01FAD207020CB61882EDD62C1E3CB5F8D5D430', "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/$($pin.tag)/LICENSE.txt"),
    @('WinDivert.txt', '14A0CB5214D536E4FDAE6AA3F5696F981EEDA106CD026E9794BBA489EE79D628', 'https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/LICENSE'),
    @('LGPL-3.0.txt', 'E3A994D82E644B03A792A930F574002658412F62407F5FEE083F2555C5F23118', 'https://www.gnu.org/licenses/lgpl-3.0.txt'),
    @('GPL-3.0.txt', '3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986', 'https://www.gnu.org/licenses/gpl-3.0.txt')
)
$notices = @(
    (Get-Content -LiteralPath (Join-Path $projectRoot 'LICENSE') -Raw -Encoding utf8)
    (Get-Content -LiteralPath (Join-Path $projectRoot 'docs/third-party.md') -Raw -Encoding utf8)
)
foreach ($license in $licenseFiles) {
    $file = if ($LicenseDirectory) {
        Get-VerifiedFile (Join-Path $LicenseDirectory $license[0]) $license[1]
    } else {
        Get-VerifiedFile (Join-Path $cache.FullName $license[0]) $license[1] $license[2]
    }
    $notices += Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
}
$noticesPath = Join-Path $cache.FullName 'ThirdPartyNotices.txt'
[IO.File]::WriteAllText($noticesPath, ($notices -join [Environment]::NewLine))
$startupLauncher = Join-Path $cache.FullName 'ZapretDesktopStartup.exe'
& (Join-Path $PSScriptRoot 'BuildStartupLauncher.ps1') -OutputPath $startupLauncher

$project = Join-Path $projectRoot 'src/ZapretDesktop/ZapretDesktop.csproj'
$publishArgs = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-p:EnableSingleFileAnalyzer=false', '-p:DebugType=None',
    "-p:FlowsealArchive=$($engine.FullName)", "-p:ThirdPartyNotices=$noticesPath",
    "-p:StartupLauncher=$startupLauncher", '-o', $OutputDirectory
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
