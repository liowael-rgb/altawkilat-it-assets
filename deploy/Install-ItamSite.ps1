# Installs or updates the IT Assets site from a package (run ON the server by IT, as an administrator, after the IIS site and
# application pool exist; see deploy\README.md). It takes the site offline, backs up the current site folder, copies the
# new files (the server's appsettings.Production.json and logs are kept), optionally installs the database scripts, brings
# the site back and checks /health. It changes no IIS, SQL login, AD or firewall settings.
param(
  [Parameter(Mandatory)][string]$Package,
  [Parameter(Mandatory)][string]$SitePath,
  [Parameter(Mandatory)][string]$BackupRoot,
  [string]$HealthUrl,
  [switch]$InstallDatabase
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as an administrator.' }
if (-not (Test-Path $Package)) { throw "Package not found: $Package" }
if (-not (Test-Path "$SitePath\appsettings.Production.json")) { throw "First copy appsettings.Production.template.json to $SitePath\appsettings.Production.json and fill it in (README step 5)." }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $BackupRoot "ITAssets-site-$stamp.zip"
New-Item -ItemType Directory -Force $BackupRoot | Out-Null
# 1. Offline page (IIS stops the app while app_offline.htm exists), then back up the current files.
Set-Content -Path "$SitePath\app_offline.htm" -Encoding utf8 -Value '<!doctype html><title>IT Assets</title><p style="font-family:Calibri,Arial;padding:40px">IT Assets is being updated. Please try again in a few minutes.</p>'
Start-Sleep -Seconds 5
Compress-Archive -Path (Get-ChildItem $SitePath -Exclude 'logs','app_offline.htm').FullName -DestinationPath $backup
Write-Host "Backup: $backup"
try {
  # 2. New files; the server's settings and logs stay.
  $tmp = Join-Path $env:TEMP "itassets-$stamp"; Expand-Archive -Path $Package -DestinationPath $tmp
  Get-ChildItem $tmp -Recurse -File | Where-Object { $_.Name -ne 'appsettings.Production.json' } | ForEach-Object {
    $target = Join-Path $SitePath ($_.FullName.Substring($tmp.Length + 1)); New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null; Copy-Item $_.FullName $target -Force }
  Remove-Item -Recurse -Force $tmp
  # 3. Database scripts, as the administrator running this script (the site's own identity only reads and writes data).
  if ($InstallDatabase) {
    Push-Location $SitePath; try { $env:ASPNETCORE_ENVIRONMENT = 'Production'; & .\Itam.Host.exe --install-database; if ($LASTEXITCODE -ne 0) { throw 'The database install failed.' } } finally { Pop-Location }
  }
}
catch { Write-Warning "Update failed: $($_.Exception.Message). Restore $backup into $SitePath if needed. The site stays offline."; throw }
# 4. Back online, then check.
Remove-Item -Force "$SitePath\app_offline.htm"
if ($HealthUrl) {
  Start-Sleep -Seconds 5
  try { $r = Invoke-WebRequest -Uri $HealthUrl -UseDefaultCredentials -UseBasicParsing -TimeoutSec 30; Write-Host "Health: $($r.StatusCode) $($r.Content)" }
  catch { Write-Warning "Health check failed: $($_.Exception.Message). See the site's event log." }
}
Write-Host 'IT Assets updated.'
