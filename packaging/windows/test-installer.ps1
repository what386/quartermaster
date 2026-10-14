[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CompilerPath,
    [Parameter(Mandatory)][string] $TestApplicationPath,
    [Parameter(Mandatory)][string] $UpdaterPath
)

$ErrorActionPreference = 'Stop'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Quartermaster_is1'
if (Test-Path $uninstallKey)
{ throw 'Run installer tests on a Windows runner without Quartermaster installed.' 
}
$root = Join-Path ([IO.Path]::GetTempPath()) "Quartermaster installer ü $([Guid]::NewGuid())"
$publish = Join-Path $root 'publish'
$installed = Join-Path $root 'installed'
$output = Join-Path $root 'output'
$startShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Quartermaster.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Quartermaster.lnk'
if ((Test-Path $startShortcut) -or (Test-Path $desktopShortcut))
{ throw 'Quartermaster shortcuts already exist.' 
}
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Quartermaster'
$dataAlreadyExisted = Test-Path $data
$sentinel = Join-Path $data "installer-test-$([Guid]::NewGuid()).txt"

function Invoke-CheckedProcess([string] $FilePath, [string[]] $Arguments)
{
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0)
    { throw "$FilePath failed with exit code $($process.ExitCode)." 
    }
}

function Assert-Shortcut([string] $Path)
{
    if (-not (Test-Path -LiteralPath $Path))
    { throw "Missing shortcut: $Path" 
    }
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($Path)
    if ($shortcut.TargetPath -ne (Join-Path $installed 'Quartermaster.Gui.exe'))
    { throw "Incorrect shortcut target: $Path" 
    }
    if ($shortcut.IconLocation -ne "$(Join-Path $installed 'Quartermaster.Gui.exe'),0")
    { throw "Incorrect shortcut icon: $Path" 
    }
}

try
{
    New-Item -ItemType Directory -Path (Join-Path $publish 'winupdater'), $data -Force | Out-Null
    Copy-Item -LiteralPath $TestApplicationPath -Destination (Join-Path $publish 'Quartermaster.Gui.exe')
    Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $publish 'winupdater/Quartermaster.Updater.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/Quartermaster.Gui/Assets/icon.png') -Destination $publish
    Set-Content -LiteralPath (Join-Path $publish 'version.txt') -Value 'initial'
    Set-Content -LiteralPath $sentinel -Value 'library data'
    $installer = & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version '0.0.1' -PublishDirectory $publish -OutputDirectory $output -CompilerPath $CompilerPath | Select-Object -Last 1
    $silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$installed`"")
    Invoke-CheckedProcess $installer ($silent + '/TASKS=')
    foreach ($relative in @('Quartermaster.Gui.exe', 'winupdater/Quartermaster.Updater.exe', 'icon.png'))
    {
        if ((Get-FileHash (Join-Path $publish $relative)).Hash -ne (Get-FileHash (Join-Path $installed $relative)).Hash)
        {
            throw "Installed file differs from the publish payload: $relative"
        }
    }
    Assert-Shortcut $startShortcut
    if (Test-Path $desktopShortcut)
    { throw 'Desktop shortcut should be optional.' 
    }
    if ((Get-ItemProperty $uninstallKey).DisplayVersion -ne '0.0.1')
    { throw 'Incorrect uninstall registration.' 
    }

    # Reinstalling a newer version must reuse the directory and uninstall log.
    $installer = & (Join-Path $PSScriptRoot 'build-installer.ps1') -Version '0.0.2' -PublishDirectory $publish -OutputDirectory $output -CompilerPath $CompilerPath | Select-Object -Last 1
    Invoke-CheckedProcess $installer ($silent + '/TASKS=desktopicon')
    Assert-Shortcut $desktopShortcut
    if ((Get-ItemProperty $uninstallKey).DisplayVersion -ne '0.0.2')
    { throw 'Installer upgrade did not refresh its version.' 
    }
    if (@(Get-ChildItem $installed -Filter 'unins*.exe').Count -ne 1)
    { throw 'Upgrade created a duplicate uninstaller.' 
    }

    # Exercise the actual updater against an installer-created installation.
    $uninstallerHash = (Get-FileHash (Join-Path $installed 'unins000.exe')).Hash
    $uninstallLogHash = (Get-FileHash (Join-Path $installed 'unins000.dat')).Hash
    Set-Content -LiteralPath (Join-Path $publish 'version.txt') -Value 'updated'
    $parent = Start-Process -FilePath $env:ComSpec -ArgumentList '/c exit 0' -Wait -PassThru
    Invoke-CheckedProcess $UpdaterPath @("$($parent.Id)", "`"$publish`"", "`"$installed`"", 'Quartermaster.Gui.exe', '3000')
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (-not (Test-Path (Join-Path $installed 'restarted.txt')))
    {
        if ([DateTime]::UtcNow -gt $deadline)
        { throw 'The updated application did not restart.' 
        }
        Start-Sleep -Milliseconds 50
    }
    if ((Get-Content (Join-Path $installed 'version.txt')).Trim() -ne 'updated')
    { throw 'The updater did not replace the payload.' 
    }
    if ((Get-FileHash (Join-Path $installed 'unins000.exe')).Hash -ne $uninstallerHash -or
        (Get-FileHash (Join-Path $installed 'unins000.dat')).Hash -ne $uninstallLogHash)
    { throw 'The updater modified the uninstaller.' 
    }
    Assert-Shortcut $startShortcut
    Assert-Shortcut $desktopShortcut

    Invoke-CheckedProcess (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ((Test-Path (Join-Path $installed 'Quartermaster.Gui.exe')) -or (Test-Path $uninstallKey) -or
        (Test-Path $startShortcut) -or (Test-Path $desktopShortcut))
    { throw 'Uninstall left program files, shortcuts, or registration behind.' 
    }
    if ((Get-Content -LiteralPath $sentinel).Trim() -ne 'library data')
    { throw 'Uninstall modified user data.' 
    }
    Write-Output 'Installer installation, upgrade, self-update, shortcuts, and uninstall passed.'
} finally
{
    # Best-effort cleanup also runs if a check fails.
    $uninstaller = Join-Path $installed 'unins000.exe'
    if (Test-Path $uninstaller)
    {
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait
    }
    if (Test-Path $sentinel)
    { Remove-Item -LiteralPath $sentinel 
    }
    if (-not $dataAlreadyExisted -and (Test-Path $data) -and @(Get-ChildItem -LiteralPath $data -Force).Count -eq 0)
    {
        Remove-Item -LiteralPath $data
    }
    if (Test-Path $root)
    { Remove-Item -LiteralPath $root -Recurse -Force 
    }
}
