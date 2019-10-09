param(
  [string]$StorageAccountName,
  [string]$IndexSource,
  [string]$OutFile
)


$ErrorActionPreference = "Stop"
Import-Module $PSScriptRoot/util.ps1

$StorageAccountKey = Get-StorageAccountKey -StorageAccountName $StorageAccountName

$newContainerName = "index-$((New-Guid).ToString("N"))"

Write-Host "Creating new container '$newContainerName'..."
{
  az storage container create --name "$newContainerName" --auth-mode key --public-access container --fail-on-exist --account-name $StorageAccountName --account-key "$StorageAccountKey"
} | Check-Failure

Write-Host "Generating sas-token for container..."
{
  $script:sas = az storage container generate-sas --name "$newContainerName" --account-name $StorageAccountName --account-key "$StorageAccountKey" --auth-mode key --permissions cadlrw --expiry $((Get-Date).ToUniversalTime().AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ")) | convertfrom-json
} | Check-Failure

Write-Host "Copying index files into container with azcopy..."
{
  azcopy.exe sync $IndexSource "https://$StorageAccountName.blob.core.windows.net/$newContainerName/?$sas" --delete-destination true --log-level ERROR
} | Check-Failure

Write-Output "Uploaded index to 'https://$StorageAccountName.blob.core.windows.net/$newContainerName'"

"https://$StorageAccountName.blob.core.windows.net/$newContainerName" | Out-File $OutFile
