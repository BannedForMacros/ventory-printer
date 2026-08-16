<#
  publish-agent.ps1 — Publica una nueva version del agente VentoryPrint.

  Que hace, en un solo paso:
   1) Compila el exe self-contained (dotnet publish -c Release).
   2) Lo copia a la carpeta public/agent del POS.
   3) Calcula su SHA256 y (re)escribe version.json con la version del csproj.

  Tras esto, cada caja detecta la nueva version y ofrece actualizar con 1 clic.

  Uso:
    ./publish-agent.ps1                         # usa la ruta por defecto del POS
    ./publish-agent.ps1 -PosPublic "D:\ruta\public"

  IMPORTANTE: subir al servidor los DOS archivos resultantes:
    public/agent/VentoryPrint.exe   y   public/agent/version.json
  (Este script los deja listos en la carpeta public/agent local; el deploy
   al servidor de cada cliente es tu paso habitual de publicacion.)
#>
param(
    [string]$PosPublic = "C:\MacSoft\ventoryPOS\public"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==> Compilando VentoryPrint (Release, self-contained)..." -ForegroundColor Cyan
dotnet publish "$here\VentoryPrint.csproj" -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)." }

$exe = Join-Path $here "bin\Release\net8.0-windows\win-x64\publish\VentoryPrint.exe"
if (-not (Test-Path $exe)) { throw "No se encontro el exe publicado: $exe" }

# Version desde el csproj (fuente unica de verdad).
[xml]$csproj = Get-Content (Join-Path $here "VentoryPrint.csproj")
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "No se pudo leer <Version> del csproj." }

$destDir = Join-Path $PosPublic "agent"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destExe = Join-Path $destDir "VentoryPrint.exe"

Copy-Item $exe $destExe -Force
$hash = (Get-FileHash $destExe -Algorithm SHA256).Hash.ToLower()

$manifest = [ordered]@{
    version = $version
    url     = "/agent/VentoryPrint.exe"
    sha256  = $hash
    notas   = "Version $version del agente VentoryPrint."
}
$json = $manifest | ConvertTo-Json
Set-Content -Path (Join-Path $destDir "version.json") -Value $json -Encoding utf8

Write-Host ""
Write-Host "==> Listo." -ForegroundColor Green
Write-Host "    Version : $version"
Write-Host "    SHA256  : $hash"
Write-Host "    Exe     : $destExe"
Write-Host "    Manifest: $(Join-Path $destDir 'version.json')"
Write-Host ""
Write-Host "    Ahora sube public/agent/ al servidor. Las cajas se actualizaran solas." -ForegroundColor Yellow
