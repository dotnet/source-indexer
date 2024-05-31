
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

$script:TTLS = @{}

function Get-ContainerTTL
{
  param(
    [string]$StorageAccountName,
    [string]$ContainerName
  )

  $script:TTLS[$StorageAccountName] = 10

  {
    $res = az storage container metadata show --name $ContainerName --account-name $StorageAccountName --auth-mode login --query 'TTL' | ConvertFrom-Json
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

  {
    Write-Verbose "Writing TTL $TTL to container $ContainerName"
    az storage container metadata update --metadata "TTL=$TTL" --name $ContainerName --account-name $StorageAccountName --auth-mode login | Out-Null
  } | Check-Failure
}
