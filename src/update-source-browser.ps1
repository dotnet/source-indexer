param(
  $SourceBrowserCloneDir
)

$hashFile = Join-Path $PSScriptRoot SourceBrowser.hash
$patchFile = Join-Path $PSScriptRoot SourceBrowser.patch
$currentHash = Get-Content $hashFile

pushd $SourceBrowserCloneDir
try
{
  git diff --full-index --binary -p --output=$patchFile $currentHash HEAD
  $newHash = git rev-parse HEAD
}
finally
{
  popd
}

git apply --reject --whitespace=fix --directory=src/SourceBrowser SourceBrowser.patch 2>&1
$newHash | Out-File $hashFile
