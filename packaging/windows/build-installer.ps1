[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $PublishDirectory,
    [string] $OutputDirectory = 'dist',
    [Parameter(Mandatory)][string] $CompilerPath
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.[0-9]+)?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')
{
    throw "Invalid application version: $Version"
}
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
foreach ($relative in @('Quartermaster.Gui.exe', 'winupdater/Quartermaster.Updater.exe', 'icon.png'))
{
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relative) -PathType Leaf))
    {
        throw "The Windows publish directory is missing $relative."
    }
}
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& $compiler '/Qp' "/DAppVersion=$Version" "/DPublishDir=$publishPath" "/DOutputDir=$outputPath" (Join-Path $PSScriptRoot 'Quartermaster.iss')
if ($LASTEXITCODE -ne 0)
{ throw "Inno Setup failed with exit code $LASTEXITCODE." 
}
$installer = Join-Path $outputPath 'Quartermaster-win-x64-setup.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf))
{ throw 'Inno Setup did not produce the installer.' 
}
Write-Output $installer
