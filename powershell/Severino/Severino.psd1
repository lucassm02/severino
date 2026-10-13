@{
    RootModule        = 'Severino.psm1'
    ModuleVersion     = '0.4.0'
    GUID              = '0b8f6f1e-6d7a-4f3e-9a0e-5c3f4b8a2d61'
    Author            = 'Severino'
    Description       = 'Severino routes, DNS entries and services from PowerShell. Talks to the running app.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @(
        'Get-SeverinoRoute', 'New-SeverinoRoute', 'Remove-SeverinoRoute', 'Enable-SeverinoRoute', 'Disable-SeverinoRoute',
        'Get-SeverinoDns', 'Set-SeverinoDns', 'Remove-SeverinoDns', 'Get-SeverinoService', 'Get-SeverinoEnvironment', 'Set-SeverinoEnvironment'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
