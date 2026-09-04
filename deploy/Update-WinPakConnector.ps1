#Requires -Version 5.1
<#
.SYNOPSIS
    Aktualizuje ACS WinPak Connector na WIN-PAK serveru na vydanou verzi z GitHubu.

.DESCRIPTION
    Stahne balik releasu (nebo pouzije uz stazeny zip), overi SHA-256, zastavi
    sluzbu, zazalohuje stavajici instalaci, prepise soubory programu a sluzbu
    znovu spusti. appsettings.Local.json (nastaveni vcetne hesel) se nechava.
    Kdyz sluzba po startu neodpovi na /health, obnovi se zaloha.

.EXAMPLE
    .\Update-WinPakConnector.ps1 -Version 1.12.7

.EXAMPLE
    .\Update-WinPakConnector.ps1                       # nejnovejsi release

.EXAMPLE
    .\Update-WinPakConnector.ps1 -ZipPath .\AcsWinPakConnector-1.12.7-win-x64.zip   # bez pristupu na GitHub
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Verze bez uvodniho "v" (1.12.7). Bez ni se vezme nejnovejsi release.
    [string]$Version,
    # Uz stazeny balik; vedle nej muze lezet soubor .sha256 k overeni.
    [string]$ZipPath,
    [string]$InstallDir = 'C:\Program Files\AcsWinPakConnector',
    [string]$ServiceName = 'AcsWinPakConnector',
    [string]$HealthUrl = 'http://localhost:52001/health',
    [string]$Repo = 'mirapavlicek/security-acs',
    # Soubory, ktere se pri prepisu nechavaji na miste (relativne k InstallDir).
    [string[]]$Keep = @('appsettings.Local.json', 'appsettings.Production.json', 'logs', 'updates')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Spustte PowerShell jako administrator - skript zastavuje a spousti sluzbu.' }

$exe = Join-Path $InstallDir 'Acs.WinPakConnector.exe'
if (-not (Test-Path $exe)) { throw "V $InstallDir neni nainstalovany konektor ($exe nenalezen)." }
$currentVersion = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Bezici verze: $currentVersion  ($InstallDir)"

$work = Join-Path $env:TEMP ("AcsWinPakConnector-update-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $work | Out-Null

# ---------- balik ----------
if ($ZipPath) {
    $zip = (Resolve-Path $ZipPath).Path
    $sha = "$zip.sha256"
}
else {
    $headers = @{ 'User-Agent' = 'AcsWinPakConnector-updater' }
    if ($Version) {
        $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/tags/v$Version" -Headers $headers
    }
    else {
        $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
        $Version = $release.tag_name.TrimStart('v')
    }
    $asset = $release.assets | Where-Object name -like 'AcsWinPakConnector-*-win-x64.zip' | Select-Object -First 1
    if (-not $asset) { throw "Release v$Version nema balik AcsWinPakConnector-*-win-x64.zip." }

    Write-Step "Stahuji $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    $zip = Join-Path $work $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -Headers $headers
    $sha = "$zip.sha256"
    $shaAsset = $release.assets | Where-Object name -eq "$($asset.name).sha256" | Select-Object -First 1
    if ($shaAsset) { Invoke-WebRequest $shaAsset.browser_download_url -OutFile $sha -Headers $headers }
}

if (Test-Path $sha) {
    $expected = ((Get-Content $sha -Raw) -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "SHA-256 baliku nesouhlasi: ocekavano $expected, spocteno $actual." }
    Write-Host "SHA-256 overen."
}
else {
    Write-Warning 'Soubor .sha256 neni k dispozici - balik se neoveruje.'
}

Write-Step 'Rozbaluji'
$unpacked = Join-Path $work 'unpacked'
Expand-Archive $zip -DestinationPath $unpacked
$source = if (Test-Path (Join-Path $unpacked 'winpak-connector')) { Join-Path $unpacked 'winpak-connector' } else { $unpacked }
if (-not (Test-Path (Join-Path $source 'Acs.WinPakConnector.exe'))) { throw 'Balik neobsahuje Acs.WinPakConnector.exe.' }
$newVersion = (Get-Item (Join-Path $source 'Acs.WinPakConnector.exe')).VersionInfo.ProductVersion
Write-Host "Nova verze: $newVersion"

if (-not $PSCmdlet.ShouldProcess("$ServiceName $currentVersion -> $newVersion", 'Aktualizovat')) { return }

# ---------- vymena ----------
$backup = "$InstallDir.bak-$currentVersion-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Step "Zastavuji sluzbu $ServiceName"
Stop-Service $ServiceName -ErrorAction SilentlyContinue
(Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))

Write-Step "Zalohuji do $backup"
# Slozka updates (prijate baliky, protokol) se nezalohuje - byla by v zaloze zbytecne a protokol se prave zapisuje.
& robocopy $InstallDir $backup /E /R:3 /W:2 /NFL /NDL /NJH /NP /XD updates | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Zaloha selhala (robocopy kod $LASTEXITCODE)." }

Write-Step 'Prepisuji soubory programu (nastaveni zustava)'
$keepArgs = $Keep | ForEach-Object { if ($_ -like '*.*') { '/XF', $_ } else { '/XD', $_ } }
# robocopy: 0-7 = uspech (bity: kopirovano, navic, neshody), 8+ = chyba.
& robocopy $source $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NP @keepArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy selhal (kod $LASTEXITCODE); zaloha je v $backup." }

Write-Step 'Spoustim sluzbu'
Start-Service $ServiceName

$deadline = (Get-Date).AddSeconds(60)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    try {
        $health = Invoke-RestMethod $HealthUrl -TimeoutSec 5
        if ($health.status -eq 'ok') { $healthy = $true; break }
    }
    catch { Start-Sleep -Seconds 2 }
}

if ($healthy) {
    Write-Host "Konektor $newVersion bezi a odpovida na $HealthUrl." -ForegroundColor Green
    Write-Host "Zalohu $backup muzete po overeni Diagnostiky smazat."
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    return
}

Write-Warning "Sluzba do 60 s neodpovedela na $HealthUrl - vracim zalohu $currentVersion."
Stop-Service $ServiceName -ErrorAction SilentlyContinue
(Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
& robocopy $backup $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NP | Out-Null
Start-Service $ServiceName
throw "Aktualizace vracena. Podivejte se do Prohlizece udalosti (Application, zdroj AcsWinPakConnector); balik zustal v $work."
