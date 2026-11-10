<#
.SYNOPSIS
  Generates the backend's own secrets (JWT signing, field encryption, fingerprint keys) into
  dotnet user-secrets for KasaSika.Api. Stored outside the repository; safe to re-run (existing keys kept
  unless -Force). Provider keys (Khaya, MTN) are yours to add - see README "Configure secrets".
#>
param([switch]$Force)
$ErrorActionPreference = "Stop"
$project = Join-Path (Split-Path -Parent $PSScriptRoot) "backend/src/KasaSika.Api"
$existing = dotnet user-secrets list --project $project 2>$null

function Set-Key([string]$name, [int]$bytes) {
  if (-not $Force -and ($existing -match [regex]::Escape($name))) { Write-Host "$name already set"; return }
  $b = New-Object byte[] $bytes
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
  dotnet user-secrets set $name ([Convert]::ToBase64String($b)) --project $project | Out-Null
  Write-Host "$name generated"
}

Set-Key "Security:JwtSigningKey" 48
Set-Key "Security:FieldEncryptionKey" 32
Set-Key "Security:FingerprintKey" 32
Write-Host "Done. Add provider keys with:"
Write-Host "  dotnet user-secrets set Khaya:SubscriptionKey <key> --project backend/src/KasaSika.Api"
