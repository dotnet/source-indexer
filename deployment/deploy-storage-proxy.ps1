param(
  [string]$ProxyUrlFile,
  [string]$ResourceGroup,
  [string]$WebappName,
  [string]$Slot
)
# This script is run after uploading the index to azure storage and publishing the website
# It needs to update the staging slot with the azure storage url

$ErrorActionPreference = "Stop"
Import-Module $PSScriptRoot/util.ps1

$proxyUrl = Get-Content -Raw $ProxyUrlFile

{
  az webapp config appsettings set --resource-group $ResourceGroup --name $WebappName --slot $Slot --settings "SOURCE_BROWSER_INDEX_PROXY_URL=$proxyUrl"
} | Check-Failure
