<#
  publish-agent.ps1 — Compila el exe del agente y lo deja en public/agent del POS
  para el instalador manual (Instalar-VentoryPrint.bat) de cajas NUEVAS.

  Las ACTUALIZACIONES ya no pasan por aqui. El flujo es:
    1) Subir <Version> en VentoryPrint.csproj.
    2) git tag vX.Y.Z && git push --tags
    3) GitHub Actions (.github/workflows/release.yml) corre los tests, compila y
       publica VentoryPrint.exe + version.json en GitHub Releases.
  Las cajas 1.2.1+ leen ese manifiesto directo; las 1.2.0 y anteriores lo
  reciben via {POS}/agent/version.json, que es una ruta del POS que redirige a
  GitHub. Por eso este script YA NO escribe version.json: un archivo estatico
  con ese nombre taparia la ruta y dejaria a las cajas viejas congeladas.

  Uso:
    ./publish-agent.ps1                         # usa la ruta por defecto del POS
    ./publish-agent.ps1 -PosPublic "D:\ruta\public"
#>
param(
    [string]$PosPublic = "F:\MacSoft\ventoryPOS\public"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==> Compilando VentoryPrint (Release, self-contained)..." -ForegroundColor Cyan
dotnet publish "$here\VentoryPrint.csproj" -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)." }

$exe = Join-Path $here "bin\Release\net8.0-windows\win-x64\publish\VentoryPrint.exe"
if (-not (Test-Path $exe)) { throw "No se encontro el exe publicado: $exe" }

[xml]$csproj = Get-Content (Join-Path $here "VentoryPrint.csproj")
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$destDir = Join-Path $PosPublic "agent"
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
$destExe = Join-Path $destDir "VentoryPrint.exe"
Copy-Item $exe $destExe -Force

# Si quedo un version.json viejo, se retira: tapa la ruta /agent/version.json del POS.
$staleManifest = Join-Path $destDir "version.json"
if (Test-Path $staleManifest) {
    Remove-Item $staleManifest -Force
    Write-Host "    (se elimino un version.json estatico viejo de public/agent)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==> Listo." -ForegroundColor Green
Write-Host "    Version : $version"
Write-Host "    Exe     : $destExe"
Write-Host ""
Write-Host "    Para que las cajas se actualicen: git tag v$version && git push --tags" -ForegroundColor Yellow
