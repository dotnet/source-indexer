param(
  [string]$Name,
  [string]$Version,
  [string]$TestPath = "/$Name.exe",
  [string]$BinPath = "/"
)

$package = Join-Path $env:TEMP "$Name.zip"
$installDir = "$($env:Agent_ToolsDirectory)/$Name/$Version"

$ProgressPreference = "SilentlyContinue"

Write-Host "Testing $installDir$TestPath"
if (-not (Test-Path "$installDir$TestPath")) {
 [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
 Write-Host "downloading $Name version $Version to $package"
 Invoke-WebRequest "https://netcorenativeassets.blob.core.windows.net/resource-packages/external/windows/$Name/$Name-$version.zip" -UseBasicParsing -OutFile $package

 Write-Host "Unpacking $Name into $installDir"
 Expand-Archive -LiteralPath $package -DestinationPath $installDir
}

Write-Host "##vso[task.prependpath]$installDir$BinPath"
