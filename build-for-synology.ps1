<#
  Builds the container image on this PC and saves it as blackvue-sync.tar, ready to import in
  Synology Container Manager (Image > Add > Add From File). Requires the .NET 10 SDK and Docker Desktop.

  Which -Arch? In DSM open Control Panel > Info Center and check the CPU:
    Intel / AMD (most "+" models, e.g. DS224+, DS923+, DS1522+)  -> amd64 (default)
    ARM / Realtek (value and "j" models)                           -> arm64
#>
param([ValidateSet('amd64', 'arm64')][string]$Arch = 'amd64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$rid = if ($Arch -eq 'amd64') { 'linux-x64' } else { 'linux-arm64' }
if (Test-Path publish) { Remove-Item -Recurse -Force publish }

dotnet publish src/BlackVueSync -c Release -r $rid --self-contained false -o publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

docker build --platform "linux/$Arch" -f Dockerfile.prebuilt -t blackvue-sync:latest .
if ($LASTEXITCODE) { throw 'docker build failed' }

docker save blackvue-sync:latest -o blackvue-sync.tar
if ($LASTEXITCODE) { throw 'docker save failed' }

Write-Host "Created blackvue-sync.tar for $Arch. Import it in Container Manager > Image > Add > Add From File."
