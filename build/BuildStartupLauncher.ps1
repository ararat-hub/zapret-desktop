param([Parameter(Mandatory)][string]$OutputPath)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.'
}
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio x64 C++ build tools were not found.' }
$vc = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/Hostx64/x64/cl.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $vc) { throw 'The x64 MSVC compiler was not found.' }
$kits = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdk = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um/Windows.h') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'The Windows SDK was not found.' }

$sdkLib = Join-Path $kits ('Lib/' + $sdk.Name)
$source = Join-Path $PSScriptRoot 'StartupLauncher.c'
$object = [IO.Path]::ChangeExtension($OutputPath, '.obj')
$options = @(
    '/nologo', '/W4', '/WX', '/O1', '/MT', '/DUNICODE', '/D_UNICODE',
    ('/I' + (Join-Path $vc 'include')), ('/I' + (Join-Path $sdk.FullName 'ucrt')),
    ('/I' + (Join-Path $sdk.FullName 'shared')), ('/I' + (Join-Path $sdk.FullName 'um')),
    ('/Fo' + $object), ('/Fe' + $OutputPath), $source, '/link', '/SUBSYSTEM:WINDOWS',
    ('/LIBPATH:' + (Join-Path $vc 'lib/x64')), ('/LIBPATH:' + (Join-Path $sdkLib 'ucrt/x64')),
    ('/LIBPATH:' + (Join-Path $sdkLib 'um/x64')), 'kernel32.lib'
)
& (Join-Path $vc 'bin/Hostx64/x64/cl.exe') @options
if ($LASTEXITCODE -ne 0) { throw "Startup launcher build failed ($LASTEXITCODE)." }
