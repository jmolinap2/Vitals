param(
    [Parameter(Mandatory)]
    [string]$StagedPublishDirectory,

    [Parameter(Mandatory)]
    [string]$InstalledDirectory,

    [Parameter(Mandatory)]
    [string]$StagedSettingsDirectory
)

$stagedDirectory = [System.IO.Path]::GetFullPath($StagedPublishDirectory)
$installedDirectory = [System.IO.Path]::GetFullPath($InstalledDirectory)
$stagedExecutable = Join-Path $stagedDirectory 'Vitals.exe'
$installedExecutable = Join-Path $installedDirectory 'Vitals.exe'
$installedSettingsExecutable = Join-Path $installedDirectory 'VitalsSettings.exe'

if (-not (Test-Path -LiteralPath $stagedExecutable -PathType Leaf)) {
    throw "No se encontró el ejecutable preparado para instalar: $stagedExecutable"
}

# La publicación se genera primero fuera de la instalación. Vitalis y su
# configuración tienen nombres de proceso propios; cerrarlos por nombre evita
# que una DLL de ajustes mantenga la instalación bloqueada a mitad del cambio.
function Stop-VitalsProcess([string]$Name) {
    $running = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($running) {
        $running | Stop-Process -Force -ErrorAction Stop
        $running | Wait-Process -Timeout 5 -ErrorAction Stop
        return $true
    }

    return $false
}

$settingsWasRunning = Stop-VitalsProcess 'VitalsSettings'
[void](Stop-VitalsProcess 'Vitals')

New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null
Copy-Item -LiteralPath $stagedExecutable -Destination $installedExecutable -Force -ErrorAction Stop

foreach ($symbol in @('Vitals.pdb')) {
    $source = Join-Path $stagedDirectory $symbol
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $installedDirectory $symbol) -Force -ErrorAction Stop
    }
}

# VitalsSettings is a separate WPF executable, not a reference bundled into
# the NativeAOT monitor. Install its complete publish output beside Vitals so
# opening “Configuración” always uses the code from this same publication.
if (-not (Test-Path -LiteralPath $StagedSettingsDirectory -PathType Container)) {
    throw "No se encontró la publicación preparada de Vitals Settings: $StagedSettingsDirectory"
}

Get-ChildItem -LiteralPath $StagedSettingsDirectory -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installedDirectory $_.Name) -Force -ErrorAction Stop
}

Start-Process -FilePath $installedExecutable -WorkingDirectory (Split-Path -Parent $installedExecutable)

if ($settingsWasRunning) {
    Start-Process -FilePath $installedSettingsExecutable -WorkingDirectory $installedDirectory
}
