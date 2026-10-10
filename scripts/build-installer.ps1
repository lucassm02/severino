<#
.SYNOPSIS
    Builds the Severino installer: publishes the app and the Helper, then compiles installer/severino.iss.

.DESCRIPTION
    Both are published self-contained for win-x64, so the target machine needs no .NET runtime.
    The result is artifacts\installer\Severino-Setup-<version>.exe, the version coming from
    Directory.Build.props.

    Needs Inno Setup 6: winget install JRSoftware.InnoSetup

.EXAMPLE
    ./scripts/build-installer.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$app = Join-Path $root 'src\Severino.App\Severino.App.csproj'
$helper = Join-Path $root 'src\Severino.Helper\Severino.Helper.csproj'

$onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = @(
    $(if ($onPath) { $onPath.Source })
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup'
}

# A clean publish folder: leftovers from an older build would end up in the installer.
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

foreach ($project in @(@{ Path = $app; Out = 'app' }, @{ Path = $helper; Out = 'helper' })) {
    dotnet publish $project.Path -c $Configuration -r win-x64 --self-contained -o (Join-Path $publish $project.Out) --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou: $($project.Path)" }
}

$version = (dotnet msbuild $app -getProperty:Version).Trim()

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publish" "/O$(Join-Path $artifacts 'installer')" (Join-Path $root 'installer\severino.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC falhou.' }

$setup = Join-Path $artifacts "installer\Severino-Setup-$version.exe"
Write-Host "Instalador: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
