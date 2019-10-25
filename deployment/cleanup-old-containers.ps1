param(
  [string]$ResourceGroup,
  [string]$WebappName,
  [string]$StorageAccountName
)

# This script runs after the deployment is completely finished
# It deletes containers that aren't in use by the web app


$ErrorActionPreference = "Stop"
Import-Module $PSScriptRoot/util.ps1 -Force

$StorageAccountKey = Get-StorageAccountKey -StorageAccountName $StorageAccountName

$allContainers = New-Object System.Collections.Generic.HashSet[string]

Write-Host "Finding containers..."
{
  az storage container list --account-name netsourceindex --account-key $StorageAccountKey --auth-mode key --query '[*].name' | ConvertFrom-Json | Write-Output | %{
    $allContainers.Add($_)
  } | Out-Null
} | Check-Failure

Write-Host "Found containers $($allContainers -join ",")"

Write-Host "Reading appsettings from webapp..."
{
  $script:ProdSlotUrl = az webapp config appsettings list --resource-group $ResourceGroup --name $WebappName --query "[?name=='SOURCE_BROWSER_INDEX_PROXY_URL'].value | [0]" | ConvertFrom-Json
} | Check-Failure

{
  $script:StagingSlotUrl = az webapp config appsettings list --resource-group $ResourceGroup --name $WebappName --slot staging --query "[?name=='SOURCE_BROWSER_INDEX_PROXY_URL'].value | [0]" | ConvertFrom-Json
} | Check-Failure

function Get-ContainerName
{
  param(
    $Url
  )
  $accountUrl = "https://$StorageAccountName.blob.core.windows.net/"
  if (-not $Url.StartsWith($accountUrl)) {
    throw "Unexpected config url $Url"
  }

  Write-Host "Processing Url $Url"
  return $Url.Substring($accountUrl.Length).Trim("/")
}

$usedContainers = [array]@(
  $(Get-ContainerName $script:ProdSlotUrl),
  $(Get-ContainerName $script:StagingSlotUrl)
)

Write-Host "Used containers $($usedContainers -join ",")"

if ($usedContainers.Count -eq $allContainers.Count) {
  return
}

$toDelete = New-Object System.Collections.Generic.HashSet[string] -ArgumentList $allContainers
$usedContainers | %{
  if (-not $toDelete.Remove($_))
  {
    throw "Used container $_ not found, aborting."
  }
}

Write-Host "Need to delete containers $($toDelete -join ",")"

$toDelete | %{
  Write-Host "Need to delete $_..."

  $ttl = Get-ContainerTTL $StorageAccountName $_

  if ($ttl -gt 0) {
    Write-Host "Container $_ has TTL $ttl, not deleting yet"
    Set-ContainerTTL $StorageAccountName $_ ($ttl - 1)
  } else {
    Write-Host "Container $_ has TTL $ttl, deleting..."
    {
      az storage container delete --name $_ --account-name $StorageAccountName --account-key $StorageAccountKey --auth-mode key
    } | Check-Failure
    Write-Host "Container $_ deleted."
  }
}
