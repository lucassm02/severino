@{
    RootModule        = 'Severino.psm1'
    ModuleVersion     = '0.4.0'
    GUID              = '0b8f6f1e-6d7a-4f3e-9a0e-5c3f4b8a2d61'
    Author            = 'Severino'
    Description       = 'Rotas, entradas DNS e serviços do Severino pelo PowerShell. Fala com o app aberto.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @(
        'Get-SeverinoRoute', 'New-SeverinoRoute', 'Remove-SeverinoRoute', 'Enable-SeverinoRoute', 'Disable-SeverinoRoute',
        'Get-SeverinoDns', 'Set-SeverinoDns', 'Remove-SeverinoDns', 'Get-SeverinoService'
    )
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()
}
