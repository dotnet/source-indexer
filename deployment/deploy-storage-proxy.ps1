param(
  [string]$NewContainerName,
  [string]$ResourceGroup,
  [string]$StorageAccountName,
  [string]$WebappName,
  [string]$Slot
)
# This script is run after uploading the index to azure storage and publishing the website
# It needs to update the staging slot with the azure storage url

$ErrorActionPreference = "Stop"
Import-Module $PSScriptRoot/util.ps1

# validate arguments
if ([string]::IsNullOrEmpty($NewContainerName)) {
    throw "NewContainerName is null or empty"
}
if ([string]::IsNullOrEmpty($ResourceGroup)) {
    throw "ResourceGroup is null or empty"
}
if ([string]::IsNullOrEmpty($StorageAccountName)) {
    throw "StorageAccountName is null or empty"
}
if ([string]::IsNullOrEmpty($WebappName)) {
    throw "WebappName is null or empty"
}
if ([string]::IsNullOrEmpty($Slot)) {
    throw "Slot is null or empty"
}

$proxyUrl = "https://$StorageAccountName.blob.core.windows.net/$NewContainerName"

Write-Host "Setting SOURCE_BROWSER_INDEX_PROXY_URL to: '$proxyUrl'"

{
  az webapp config appsettings set --resource-group $ResourceGroup --name $WebappName --slot $Slot --settings "SOURCE_BROWSER_INDEX_PROXY_URL=$proxyUrl"
} | Check-Failure