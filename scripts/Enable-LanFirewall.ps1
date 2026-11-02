# Optional, explicit administrator action. Does not enable Public-profile access.
# Run from an elevated PowerShell after reviewing the application path.
param([Parameter(Mandatory=$true)][string]$ProgramPath, [ValidateRange(1024,65535)][int]$TcpPort = 45831)
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path $ProgramPath).Path
if ([IO.Path]::GetFileName($path) -ne 'RemoteDesk.exe') { throw 'Choose the extracted RemoteDesk.exe.' }
foreach ($rule in @(@{Name='RemoteDesk attended TCP';Protocol='TCP';Port=$TcpPort}, @{Name='RemoteDesk LAN discovery';Protocol='UDP';Port=45832})) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow -Program $path -Protocol $rule.Protocol -LocalPort $rule.Port -Profile Private -RemoteAddress LocalSubnet
}
Write-Host 'Private / LocalSubnet rules added. No Public or internet access enabled.'
