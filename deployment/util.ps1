
function Check-Failure
{
    param(
        [parameter(Mandatory=$true,ValueFromPipeline=$true)]
        [ScriptBlock] $Action
    )

    &$Action
    if ($LastExitCode -ne 0) {
        throw "Commmand exit code: $LastExitCode - $Action"
    }
}

$script:AccountKeys = @{}

function Get-StorageAccountKey
{
  param(
    [string]$StorageAccountName
  )

  if ($script:AccountKeys[$StorageAccountName]) {
    return $script:AccountKeys[$StorageAccountName]
  }

  Write-Host "Retrieving keys for storage account '$StorageAccountName'..."

  {
    $script:AccountKeys[$StorageAccountName] = az storage account keys list --account-name $StorageAccountName | ConvertFrom-Json | Write-Output | select -expandproperty value | select -First 1
  } | Check-Failure


  Write-Output $script:AccountKeys[$StorageAccountName]
}


$script:TTLS = @{}

function Get-ContainerTTL
{
  param(
    [string]$StorageAccountName,
    [string]$ContainerName
  )
  $StorageAccountKey = Get-StorageAccountKey -StorageAccountName $StorageAccountName
  
  $script:TTLS[$StorageAccountName] = 10

  {
    $res = az storage container metadata show --name $ContainerName --account-name $StorageAccountName --account-key $StorageAccountKey --auth-mode key --query 'TTL' | ConvertFrom-Json
    Write-Verbose "Got TTL $res from container $ContainerName"
    if ($res -ne $null) {
      $script:TTLS[$StorageAccountName] = $res
    }
  } | Check-Failure

  return $script:TTLS[$StorageAccountName]
}

function Set-ContainerTTL
{

  param(
    [string]$StorageAccountName,
    [string]$ContainerName,
    [int]$TTL
  )
  $StorageAccountKey = Get-StorageAccountKey -StorageAccountName $StorageAccountName

  {
    Write-Verbose "Writing TTL $TTL to container $ContainerName"
    az storage container metadata update --metadata "TTL=$TTL" --name $ContainerName --account-name $StorageAccountName --account-key $StorageAccountKey --auth-mode key | Out-Null
  } | Check-Failure
}
