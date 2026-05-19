[CmdletBinding(PositionalBinding=$false)]
param (
  [string]$pat = "",

  [Parameter(Mandatory=$true)]
  [string]$dropName,

  [string]$destination = ""
)

Set-StrictMode -version 2.0
$ErrorActionPreference="Stop"

function Get-SafePathName([string]$name) {
  $safeName = $name
  foreach ($invalidChar in [System.IO.Path]::GetInvalidFileNameChars()) {
    $safeName = $safeName.Replace($invalidChar, [char]'_')
  }

  return $safeName.Replace('/', '_').Replace('\\', '_')
}

function Read-Pat() {
  if ($pat -ne "") {
    return $pat
  }

  if ($env:DEV_DIV_DROP_PAT -ne $null -and $env:DEV_DIV_DROP_PAT -ne "") {
    return $env:DEV_DIV_DROP_PAT
  }

  $securePat = Read-Host "DevDiv drop PAT" -AsSecureString
  $bstr = [System.IntPtr]::Zero
  try {
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePat)
    return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  }
  finally {
    if ($bstr -ne [System.IntPtr]::Zero) {
      [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }
  }
}

$pushedLocation = $false
try {
  . (Join-Path $PSScriptRoot "build-utils.ps1")
  Push-Location $RepoRoot
  $pushedLocation = $true

  $internalToolingProject = Join-Path $RepoRoot 'eng/common/internal/Tools.csproj'
  $restoreConfigFile = Join-Path $RepoRoot 'eng/common/internal/NuGet.config'

  Write-Host "Restoring internal tooling"
  MSBuild $internalToolingProject /t:Restore /p:RestoreConfigFile=$restoreConfigFile

  $dropPackageDir = Join-Path (Get-PackagesDir) "drop.app"
  if (-not (Test-Path $dropPackageDir)) {
    throw "Could not find restored Drop.App package under '$dropPackageDir'."
  }

  $dropPackage = Get-ChildItem -Path $dropPackageDir -Directory |
    Sort-Object -Property Name -Descending |
    Select-Object -First 1

  if (-not $dropPackage) {
    throw "Could not find a restored Drop.App package under '$dropPackageDir'."
  }

  $dropExe = Join-Path $dropPackage.FullName "lib/net45/drop.exe"

  if (-not (Test-Path $dropExe)) {
    throw "Could not find drop.exe at '$dropExe'."
  }

  if ($destination -eq "") {
    $downloadsDir = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)) "Downloads"
    $destination = Join-Path $downloadsDir (Get-SafePathName $dropName)
  }

  New-Item -ItemType Directory -Force -Path $destination | Out-Null

  $logPath = Join-Path $destination "drop-download.log"

  Write-Host "Downloading '$dropName' to '$destination'"
  $dropPat = Read-Pat
  & $dropExe get --dropservice "https://devdiv.artifacts.visualstudio.com" --patAuth $dropPat --name $dropName --dest $destination --traceto $logPath
  if ($LASTEXITCODE -ne 0) {
    throw "drop.exe get failed with exit code $LASTEXITCODE. See log: $logPath"
  }

  Write-Host "Downloaded '$dropName' to '$destination'"
  exit 0
}
catch {
  Write-Host $_
  exit 1
}
finally {
  if ($pushedLocation) {
    Pop-Location
  }
}