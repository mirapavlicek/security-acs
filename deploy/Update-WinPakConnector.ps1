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
    [string[]]$Keep = @('appsettings.Local.json', 'appsettings.Production.json', 'logs', 'updates'),
    # Protokol krok za krokem (kdyz skript spousti sam konektor, jinak se nic nevidi).
    [string]$LogPath,
    # Naplanovana uloha, ze ktere skript bezi (spousti ji konektor) - po skonceni se smaze.
    [string]$TaskName
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Log([string]$text) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $text
    Write-Host $line
    if ($LogPath) { try { Add-Content -Path $LogPath -Value $line -Encoding UTF8 } catch { } }
}
function Write-Step([string]$text) { Write-Log "==> $text" }

# robocopy: 0-7 = uspech (bity: kopirovano, navic, neshody), 8+ = chyba. Vystup se pri chybe zapise do protokolu.
function Invoke-Robocopy([string[]]$arguments, [string]$what) {
    $output = & robocopy @arguments
    $code = $LASTEXITCODE
    if ($code -ge 8) {
        Write-Log "robocopy ($what) skoncil kodem $code; vystup:"
        $output | Select-Object -Last 40 | ForEach-Object { Write-Log "  $_" }
        throw "$what selhalo (robocopy kod $code)."
    }
    Write-Log "robocopy ($what) ok, kod $code."
}

# Sluzba hlasi 'Stopped' drive, nez jeji proces pusti soubory - cekame na proces i na exe.
function Wait-ServiceFilesReleased([string]$exePath, [int]$seconds) {
    $processName = [IO.Path]::GetFileNameWithoutExtension($exePath)
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $running = Get-Process -Name $processName -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath }
        if (-not $running) {
            try {
                $stream = [IO.File]::Open($exePath, 'Open', 'ReadWrite', 'None'); $stream.Close()
                return $true
            }
            catch { }
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Start-ConnectorService {
    try {
        Start-Service $ServiceName
        (Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        Write-Log "Sluzba $ServiceName bezi."
        return $true
    }
    catch {
        Write-Log "Sluzbu $ServiceName se nepodarilo spustit: $_"
        return $false
    }
}

function Remove-UpdateTask {
    if ($TaskName) { & schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null }
}

function Test-ConnectorHealth([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod $HealthUrl -TimeoutSec 5
            if ($health.status -eq 'ok') { return $true }
        }
        catch { Start-Sleep -Seconds 2 }
    }
    return $false
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Spustte PowerShell jako administrator - skript zastavuje a spousti sluzbu.' }

$exe = Join-Path $InstallDir 'Acs.WinPakConnector.exe'
if (-not (Test-Path $exe)) { throw "V $InstallDir neni nainstalovany konektor ($exe nenalezen)." }
# ProductVersion nese i git hash za '+', do nazvu zalohy patri jen cislo.
function Get-ShortVersion([string]$path) { ((Get-Item $path).VersionInfo.ProductVersion -split '\+')[0] }
$currentVersion = Get-ShortVersion $exe
Write-Log "Bezici verze: $currentVersion  ($InstallDir)"

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
$newVersion = Get-ShortVersion (Join-Path $source 'Acs.WinPakConnector.exe')
Write-Log "Nova verze: $newVersion"

if (-not $PSCmdlet.ShouldProcess("$ServiceName $currentVersion -> $newVersion", 'Aktualizovat')) { return }

# ---------- vymena ----------
# Od zastaveni sluzby dal plati jedno: at se stane cokoli, skript skonci se spustenou
# sluzbou - novou verzi, nebo vracenou zalohou. Zastavena sluzba bez konektoru je
# horsi nez neuspesna aktualizace.
$backup = "$InstallDir.bak-$currentVersion-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$swapped = $false
$failure = $null

Write-Step "Zastavuji sluzbu $ServiceName"
try {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
    Write-Log "Sluzba zastavena."
}
catch { Write-Log "Zastaveni sluzby: $_" }

if (-not (Wait-ServiceFilesReleased $exe 90)) {
    Write-Log "Proces $exe stale drzi soubory (90 s) - zkusim kopirovat i tak."
}

# Vymena prejmenovanim slozek: bud se prehodi cela instalace, nebo nic - zadny stav
# napul zkopirovany po souborech, kdyby proces zanikl uprostred. Kopirovani po
# souborech (robocopy) zustava jako nahrada, kdyz prejmenovani neprojde (otevrene
# soubory, jiny svazek).
$renamed = $false
try {
    Write-Step "Odsouvam soucasnou instalaci do $backup"
    try {
        [IO.Directory]::Move($InstallDir, $backup)
        $renamed = $true
        Write-Log "Instalace prejmenovana na zalohu."
    }
    catch {
        Write-Log "Prejmenovani instalace neproslo ($($_.Exception.Message)) - zalohuji kopii."
        Invoke-Robocopy @($InstallDir, $backup, '/E', '/R:5', '/W:3', '/NFL', '/NDL', '/NJH', '/NP', '/XD', 'updates') 'Zaloha'
    }

    Write-Step 'Nasazuji nove soubory programu'
    if ($renamed) {
        try { [IO.Directory]::Move($source, $InstallDir); Write-Log "Novy balik prejmenovan na instalaci." }
        catch {
            Write-Log "Presun baliku neprosel ($($_.Exception.Message)) - kopiruji."
            Invoke-Robocopy @($source, $InstallDir, '/E', '/R:5', '/W:3', '/NFL', '/NDL', '/NJH', '/NP') 'Nasazeni'
        }
        # Nastaveni a protokoly z puvodni instalace zpet.
        foreach ($item in $Keep) {
            $from = Join-Path $backup $item
            if (Test-Path $from) {
                $to = Join-Path $InstallDir $item
                if (Test-Path -PathType Container $from) { Invoke-Robocopy @($from, $to, '/E', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NP') "Prenos $item" }
                else { Copy-Item $from $to -Force }
                Write-Log "Zachovano: $item"
            }
        }
    }
    else {
        $keepArgs = @()
        foreach ($item in $Keep) { if ($item -like '*.*') { $keepArgs += '/XF', $item } else { $keepArgs += '/XD', $item } }
        Invoke-Robocopy (@($source, $InstallDir, '/MIR', '/R:5', '/W:3', '/NFL', '/NDL', '/NJH', '/NP') + $keepArgs) 'Prepis souboru'
    }
    $swapped = $true
}
catch {
    $failure = "$_"
    Write-Log "CHYBA: $failure"
}

if (-not $failure) {
    Write-Step 'Spoustim sluzbu'
    if ((Start-ConnectorService) -and (Test-ConnectorHealth 90)) {
        Write-Log "HOTOVO: konektor $newVersion bezi a odpovida na $HealthUrl. Zalohu $backup muzete po overeni Diagnostiky smazat."
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
        Remove-UpdateTask
        return
    }
    $failure = "Sluzba po vymene souboru nenabehla nebo do 90 s neodpovedela na $HealthUrl."
    Write-Log "CHYBA: $failure"
}

# ---------- navrat ----------
if (($swapped -or $renamed) -and (Test-Path $backup)) {
    Write-Step "Vracim zalohu $currentVersion z $backup"
    try {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
        [void](Wait-ServiceFilesReleased $exe 60)
        if ($renamed) {
            $failed = "$InstallDir.failed-$newVersion-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
            if (Test-Path $InstallDir) { [IO.Directory]::Move($InstallDir, $failed); Write-Log "Neuspesna verze odsunuta do $failed." }
            [IO.Directory]::Move($backup, $InstallDir)
            Write-Log "Zaloha vracena prejmenovanim."
        }
        else {
            Invoke-Robocopy @($backup, $InstallDir, '/MIR', '/R:5', '/W:3', '/NFL', '/NDL', '/NJH', '/NP', '/XD', 'updates') 'Navrat zalohy'
        }
    }
    catch { Write-Log "Navrat zalohy: $_" }
}
else {
    Write-Log "Soubory programu se nemenily - spoustim puvodni verzi $currentVersion."
}

Write-Step 'Spoustim sluzbu (puvodni verze)'
if ((Start-ConnectorService) -and (Test-ConnectorHealth 90)) {
    Write-Log "Puvodni verze $currentVersion bezi. Aktualizace neprobehla: $failure"
}
else {
    Write-Log "POZOR: sluzba $ServiceName nebezi ani po navratu. Spustte ji rucne (Start-Service $ServiceName) a podivejte se do Prohlizece udalosti (Application, zdroj AcsWinPakConnector)."
}
Remove-UpdateTask
throw "Aktualizace neprobehla: $failure Balik zustal v $work, protokol: $LogPath"
