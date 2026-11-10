<#
.SYNOPSIS
  Runs a private PostgreSQL cluster for Kasa Sika development, without Docker and without touching any
  PostgreSQL service already installed on the machine.

.DESCRIPTION
  Creates (once) a cluster under .data/pg in the repository, listening ONLY on 127.0.0.1:5433, with a
  single database "kasa_sika" owned by role "kasa_sika". Local connections use "trust" authentication,
  which is acceptable only because the cluster is bound to the loopback interface on a developer machine.
  Never use this configuration on a shared or reachable host.

  Connection string for the backend (already the Development default in appsettings.Development.json):
    Host=127.0.0.1;Port=5433;Database=kasa_sika;Username=kasa_sika

.EXAMPLE
  ./scripts/dev-postgres.ps1           # create if needed, then start
  ./scripts/dev-postgres.ps1 -Stop
  ./scripts/dev-postgres.ps1 -Status
#>
param(
  [switch]$Stop,
  [switch]$Status,
  [int]$Port = 5433,
  [string]$PgBin = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$data = Join-Path $repo ".data/pg"
$log = Join-Path $repo ".data/pg.log"

if (-not $PgBin) {
  $candidate = Get-ChildItem "C:\Program Files\PostgreSQL" -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.Name -replace '\D', '') } -Descending | Select-Object -First 1
  if ($candidate) { $PgBin = Join-Path $candidate.FullName "bin" }
  elseif (Get-Command pg_ctl -ErrorAction SilentlyContinue) { $PgBin = Split-Path (Get-Command pg_ctl).Source }
  else { throw "PostgreSQL binaries not found. Install PostgreSQL 16+ or pass -PgBin <path-to-bin>." }
}
$pgCtl = Join-Path $PgBin "pg_ctl.exe"
$initdb = Join-Path $PgBin "initdb.exe"
$psql = Join-Path $PgBin "psql.exe"

if ($Status) { & $pgCtl -D $data status; exit $LASTEXITCODE }
if ($Stop) { & $pgCtl -D $data stop -m fast; exit $LASTEXITCODE }

if (-not (Test-Path (Join-Path $data "PG_VERSION"))) {
  Write-Host "Creating development cluster in $data"
  New-Item -ItemType Directory -Force (Split-Path $data) | Out-Null
  & $initdb -D $data -U postgres --auth=trust --encoding=UTF8 --locale=C | Out-Null
  Add-Content (Join-Path $data "postgresql.conf") "`nlisten_addresses = '127.0.0.1'`nport = $Port`n"
}

& $pgCtl -D $data status *> $null
if ($LASTEXITCODE -ne 0) {
  # Start-Process, not "&": the server inherits the caller's output handles otherwise, and the script
  # (or any tool capturing its output) never sees end-of-output. WaitForExit, not -Wait: -Wait also waits
  # for the server process that pg_ctl leaves running.
  $p = Start-Process -FilePath $pgCtl -ArgumentList @("-D", "`"$data`"", "-l", "`"$log`"", "-o", "`"-p $Port`"", "start", "-w") `
    -WindowStyle Hidden -PassThru
  $p.WaitForExit()
}

$exists = & $psql -h 127.0.0.1 -p $Port -U postgres -tAc "select 1 from pg_roles where rolname='kasa_sika'"
if ($exists -ne "1") {
  & $psql -h 127.0.0.1 -p $Port -U postgres -c "create role kasa_sika login createdb" | Out-Null
  & $psql -h 127.0.0.1 -p $Port -U postgres -c "create database kasa_sika owner kasa_sika" | Out-Null
  & $psql -h 127.0.0.1 -p $Port -U postgres -c "create database kasa_sika_test owner kasa_sika" | Out-Null
}
Write-Host "PostgreSQL ready on 127.0.0.1:$Port (database kasa_sika, test database kasa_sika_test)"
