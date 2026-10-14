[CmdletBinding()]
param([Parameter(Mandatory)][string] $Destination)

$ErrorActionPreference = 'Stop'
# Pin the compiler and verify the official download before running it.
# Hash: microsoft/winget-pkgs, JRSoftware.InnoSetup 6.7.3 installer manifest.
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
$expectedHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
$download = Join-Path ([System.IO.Path]::GetTempPath()) "innosetup-$([Guid]::NewGuid()).exe"
try
{
    Invoke-WebRequest -Uri $url -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedHash)
    {
        throw 'The Inno Setup download checksum does not match.'
    }
    $process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', "/DIR=`"$destinationPath`"") -Wait -PassThru
    if ($process.ExitCode -ne 0)
    { throw "Inno Setup installation failed with exit code $($process.ExitCode)." 
    }
    $compiler = Join-Path $destinationPath 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf))
    { throw 'The Inno Setup compiler is missing.' 
    }
    Write-Output $compiler
} finally
{
    if (Test-Path -LiteralPath $download)
    { Remove-Item -LiteralPath $download 
    }
}
