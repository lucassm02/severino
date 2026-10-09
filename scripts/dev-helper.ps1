<#
.SYNOPSIS
    Installs, removes or runs Severino.Helper for development, before the installer exists.

.DESCRIPTION
    install    Publishes the Helper to "Program Files\Severino\Helper", stores the allowed user SID
               in HKLM and registers and starts the Severino.Helper service. Reinstalling replaces it.
    uninstall  Removes the Severino block from the hosts file, then the service, the folder and the
               registry key.
    run        Runs the Helper from the source tree as a console app, for debugging. The pipe
               accepts the user running this script.

    The binary goes under Program Files on purpose: it runs as SYSTEM, and a folder the user can
    write to would let any program of theirs replace it.

.PARAMETER UserSid
    The account allowed to use the pipe. Defaults to the account running the script; pass it when
    elevating with a different admin account.

.EXAMPLE
    ./scripts/dev-helper.ps1 install
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('install', 'uninstall', 'run')]
    [string] $Action,

    [string] $UserSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
)

$ErrorActionPreference = 'Stop'

$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Severino.Helper\Severino.Helper.csproj'
$installDir = Join-Path $env:ProgramFiles 'Severino\Helper'
$exe = Join-Path $installDir 'Severino.Helper.exe'
$serviceName = 'Severino.Helper'
$registryPath = 'HKLM:\SOFTWARE\Severino\Helper'

function Remove-HelperService {
    $service = Get-Service $serviceName -ErrorAction SilentlyContinue
    if (-not $service) { return }
    if ($service.Status -ne 'Stopped') {
        Stop-Service $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }
    Remove-Service $serviceName
}

switch ($Action) {
    'install' {
        $null = [System.Security.Principal.SecurityIdentifier]::new($UserSid)  # validates the SID

        Remove-HelperService
        dotnet publish $project -c Release -o $installDir --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

        New-Item $registryPath -Force | Out-Null
        Set-ItemProperty $registryPath -Name AllowedUserSid -Value $UserSid

        New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'Severino Helper' `
            -Description 'Mantém o bloco do Severino no arquivo hosts.' -StartupType Automatic | Out-Null
        Start-Service $serviceName
        Write-Host "Severino.Helper instalado em $installDir para o SID $UserSid."
    }

    'uninstall' {
        if (Test-Path $exe) {
            & $exe --clear-hosts
        }
        Remove-HelperService
        Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $registryPath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host 'Severino.Helper removido.'
    }

    'run' {
        dotnet run --project $project
    }
}
