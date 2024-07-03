param(
  [string]$StorageAccountName,
  [string]$OutFile
)

$ErrorActionPreference = "Stop"
Import-Module $PSScriptRoot/util.ps1

$newContainerName = "index-$((New-Guid).ToString("N"))"

Write-Host "Creating new container '$newContainerName'..."
{
  az storage container create --name "$newContainerName" --auth-mode login --public-access off --fail-on-exist --account-name $StorageAccountName
} | Check-Failure

Write-Output "##vso[task.setvariable variable=NEW_CONTAINER_NAME]$newContainerName"

"https://$StorageAccountName.blob.core.windows.net/$newContainerName" | Out-File $OutFile
