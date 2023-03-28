param(
  [string]$Root
)

$ErrorActionPreference = "Stop"

Get-ChildItem $Root -Recurse | %{
  if ($_.Name -cne $_.Name.ToLowerInvariant()) {
    $name = $_.Name
    $full = $_.FullName.Replace('`', '``')
    "$_.FullName -> $($name.ToLowerInvariant())"
    Rename-Item $full "$name.tmp"
    Rename-Item "$full.tmp" $name.ToLowerInvariant()
  }
}
