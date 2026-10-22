# Builds the IT Assets site package (run on a developer PC): checks first, then a Release publish of Itam.Host, zipped to
# outputs\IT Assets site package (<date> <commit>).zip. The package holds no settings with values: the server keeps its own
# appsettings.Production.json. Nothing is installed by this script.
param([switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $SkipChecks) {
  dotnet build -c Release -nologo -v q
  dotnet run --project tests/Itam.Checks -c Release --no-build
  if ($LASTEXITCODE -ne 0) { throw 'The checks failed: no package.' }
}
$commit = (git rev-parse --short HEAD).Trim()
$stamp = Get-Date -Format 'yyyy-MM-dd'
$out = Join-Path $root "outputs\publish-$stamp-$commit"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish src/Itam.Host -c Release -o $out -nologo -v q --no-self-contained
if ($LASTEXITCODE -ne 0) { throw 'The publish failed.' }
Remove-Item -Force -ErrorAction SilentlyContinue "$out\appsettings.Production.json", "$out\appsettings.Development.json"
Set-Content -Path "$out\PACKAGE.txt" -Encoding utf8 -Value "IT Assets site package`r`nCommit: $commit`r`nBuilt: $(Get-Date -Format 'yyyy-MM-dd HH:mm')`r`nDatabase scripts: $((Get-ChildItem db/pending/*.sql).Name -join ', ')"
$zip = Join-Path $root "outputs\IT Assets site package ($stamp $commit).zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Package: $zip"
