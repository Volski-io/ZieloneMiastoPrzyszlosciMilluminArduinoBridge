$ErrorActionPreference = "Stop"

$isAdministrator = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    throw "Uruchom ten skrypt w PowerShellu jako Administrator."
}

$rules = @(
    @{
        DisplayName = "Millumin Bridge - panel TCP 8080"
        Protocol = "TCP"
        LocalPort = 8080
    },
    @{
        DisplayName = "Millumin Bridge - OSC feedback UDP 5001"
        Protocol = "UDP"
        LocalPort = 5001
    }
)

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.DisplayName -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-NetFirewallRule `
            -DisplayName $rule.DisplayName `
            -Description "Millumin UART Bridge - dostęp wyłącznie z lokalnej podsieci" `
            -Direction Inbound `
            -Action Allow `
            -Protocol $rule.Protocol `
            -LocalPort $rule.LocalPort `
            -RemoteAddress LocalSubnet `
            -Profile Any | Out-Null

        Write-Host "Dodano: $($rule.DisplayName)"
    }
    else {
        Enable-NetFirewallRule -DisplayName $rule.DisplayName
        Write-Host "Reguła już istnieje i została włączona: $($rule.DisplayName)"
    }
}

Write-Host "Gotowe. Panel: TCP 8080; feedback OSC: UDP 5001; źródło: LocalSubnet."
