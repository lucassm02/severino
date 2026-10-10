# Severino from PowerShell. Every command talks to the open app through a pipe of this Windows
# account, so routes and DNS entries change exactly as they would on the app's screens.

function Invoke-SeverinoControl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Command,
        [hashtable] $Arguments = @{}
    )

    $name = if ($env:SEVERINO_CONTROL_PIPE) { $env:SEVERINO_CONTROL_PIPE }
            else { 'Severino.Control.' + [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $name, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        try {
            $pipe.Connect(3000)
        }
        catch {
            throw 'O Severino não está aberto. Abra o app e tente de novo.'
        }
        $json = @{ command = $Command; args = $Arguments } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $reader = New-Object System.IO.StreamReader($pipe, [System.Text.Encoding]::UTF8)
        $line = $reader.ReadLine()
    }
    finally {
        $pipe.Dispose()
    }

    $response = $line | ConvertFrom-Json
    if (-not $response.ok) {
        throw $response.error
    }
    $response.data
}

function Get-SeverinoRoute {
    <#
    .SYNOPSIS
    Lists the web routes, optionally only those of one domain.
    .EXAMPLE
    Get-SeverinoRoute meuapp.sev
    #>
    [CmdletBinding()]
    param([Parameter(Position = 0)] [string] $Domain)

    $routes = Invoke-SeverinoControl -Command 'routes.list'
    if ($Domain) { $routes = $routes | Where-Object domain -eq $Domain.ToLowerInvariant().TrimEnd('.') }
    $routes
}

function New-SeverinoRoute {
    <#
    .SYNOPSIS
    Creates a route, like meuapp.sev → http://localhost:3000.
    .EXAMPLE
    New-SeverinoRoute meuapp.sev http://localhost:3000 -Https -Group meuapp
    .EXAMPLE
    New-SeverinoRoute meuapp.sev http://localhost:8080 -Path /api
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)] [string] $Domain,
        [Parameter(Mandatory, Position = 1)] [string] $Target,
        [string] $Path = '',
        [switch] $Https,
        [switch] $NoRedirect,
        [string] $Group = '',
        [string] $Notes = ''
    )

    Invoke-SeverinoControl -Command 'routes.add' -Arguments @{
        domain = $Domain; target = $Target; path = $Path; https = [bool]$Https
        redirect = -not $NoRedirect; group = $Group; notes = $Notes
    }
}

function Remove-SeverinoRoute {
    <#
    .SYNOPSIS
    Removes the route of a domain (and path).
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory, Position = 0, ValueFromPipelineByPropertyName)] [string] $Domain,
        [Parameter(ValueFromPipelineByPropertyName)] [string] $Path = ''
    )
    process {
        if ($PSCmdlet.ShouldProcess("$Domain$Path", 'Remover rota')) {
            Invoke-SeverinoControl -Command 'routes.remove' -Arguments @{ domain = $Domain; path = $Path }
        }
    }
}

function Enable-SeverinoRoute {
    <#
    .SYNOPSIS
    Switches a route, or every route of a group, on.
    .EXAMPLE
    Enable-SeverinoRoute -Group meuapp
    #>
    [CmdletBinding(DefaultParameterSetName = 'Route')]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'Route', ValueFromPipelineByPropertyName)] [string] $Domain,
        [Parameter(ParameterSetName = 'Route', ValueFromPipelineByPropertyName)] [string] $Path = '',
        [Parameter(Mandatory, ParameterSetName = 'Group')] [string] $Group
    )
    process { Set-SeverinoRouteState -Enabled $true -Domain $Domain -Path $Path -Group $Group }
}

function Disable-SeverinoRoute {
    <#
    .SYNOPSIS
    Switches a route, or every route of a group, off.
    #>
    [CmdletBinding(DefaultParameterSetName = 'Route')]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'Route', ValueFromPipelineByPropertyName)] [string] $Domain,
        [Parameter(ParameterSetName = 'Route', ValueFromPipelineByPropertyName)] [string] $Path = '',
        [Parameter(Mandatory, ParameterSetName = 'Group')] [string] $Group
    )
    process { Set-SeverinoRouteState -Enabled $false -Domain $Domain -Path $Path -Group $Group }
}

function Set-SeverinoRouteState([bool] $Enabled, [string] $Domain, [string] $Path, [string] $Group) {
    $arguments = @{ enabled = $Enabled }
    if ($Group) { $arguments.group = $Group } else { $arguments.domain = $Domain; $arguments.path = $Path }
    Invoke-SeverinoControl -Command 'routes.set-enabled' -Arguments $arguments
}

function Get-SeverinoDns {
    <#
    .SYNOPSIS
    Lists Severino's DNS entries, or with -Outside the hosts lines that are not Severino's.
    #>
    [CmdletBinding()]
    param([switch] $Outside)

    $list = Invoke-SeverinoControl -Command 'dns.list'
    if ($Outside) { $list.outside } else { $list.entries }
}

function Set-SeverinoDns {
    <#
    .SYNOPSIS
    Creates a DNS entry, or updates the one that already has the first name.
    .EXAMPLE
    Set-SeverinoDns sql.interno, sql 10.0.0.8
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)] [string[]] $Name,
        [Parameter(Mandatory, Position = 1)] [string] $Address,
        [switch] $Disabled,
        [string] $Notes
    )

    $arguments = @{ names = @($Name); address = $Address; enabled = -not $Disabled }
    if ($PSBoundParameters.ContainsKey('Notes')) { $arguments.notes = $Notes }
    Invoke-SeverinoControl -Command 'dns.set' -Arguments $arguments
}

function Remove-SeverinoDns {
    <#
    .SYNOPSIS
    Removes the DNS entry of Severino's that has this name.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory, Position = 0)] [string] $Name)

    if ($PSCmdlet.ShouldProcess($Name, 'Remover entrada DNS')) {
        Invoke-SeverinoControl -Command 'dns.remove' -Arguments @{ name = $Name }
    }
}

function Get-SeverinoService {
    <#
    .SYNOPSIS
    Lists the service routes: names, address, ports and where they came from.
    #>
    [CmdletBinding()]
    param()

    Invoke-SeverinoControl -Command 'services.list'
}

Export-ModuleMember -Function Get-SeverinoRoute, New-SeverinoRoute, Remove-SeverinoRoute, Enable-SeverinoRoute, Disable-SeverinoRoute,
    Get-SeverinoDns, Set-SeverinoDns, Remove-SeverinoDns, Get-SeverinoService
