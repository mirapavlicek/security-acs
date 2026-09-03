#Requires -Version 5.1
<#
.SYNOPSIS
    Aktualizuje ACS WinPak Connector na WIN-PAK serveru na vydanou verzi z GitHubu.

.DESCRIPTION
    Stáhne balík releasu (nebo použije už stažený zip), ověří SHA-256, zastaví
    službu, zazálohuje stávající instalaci, přepíše soubory programu a službu
    znovu spustí. appsettings.Local.json (nastavení včetně hesel) se nechává.
    Když služba po startu neodpoví na /health, obnoví se záloha.

.EXAMPLE
    .\Update-WinPakConnector.ps1 -Version 1.12.7

.EXAMPLE
    .\Update-WinPakConnector.ps1                       # nejnovější release

.EXAMPLE
    .\Update-WinPakConnector.ps1 -ZipPath .\AcsWinPakConnector-1.12.7-win-x64.zip   # bez přístupu na GitHub
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    # Verze bez úvodního „v“ (1.12.7). Bez ní se vezme nejnovější release.
    [string]$Version,
    # Už stažený balík; vedle něj může ležet soubor .sha256 k ověření.
    [string]$ZipPath,
    [string]$InstallDir = 'C:\Program Files\AcsWinPakConnector',
    [string]$ServiceName = 'AcsWinPakConnector',
    [string]$HealthUrl = 'http://localhost:52001/health',
    [string]$Repo = 'mirapavlicek/security-acs',
    # Soubory, které se při přepisu nechávají na místě (relativně k InstallDir).
    [string[]]$Keep = @('appsettings.Local.json', 'appsettings.Production.json', 'logs')
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Spusťte PowerShell jako administrátor — skript zastavuje a spouští službu.' }

$exe = Join-Path $InstallDir 'Acs.WinPakConnector.exe'
if (-not (Test-Path $exe)) { throw "V $InstallDir není nainstalovaný konektor ($exe nenalezen)." }
$currentVersion = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host "Běžící verze: $currentVersion  ($InstallDir)"

$work = Join-Path $env:TEMP ("AcsWinPakConnector-update-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $work | Out-Null

# ---------- balík ----------
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
    if (-not $asset) { throw "Release v$Version nemá balík AcsWinPakConnector-*-win-x64.zip." }

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
    if ($expected -ne $actual) { throw "SHA-256 balíku nesouhlasí: očekáváno $expected, spočteno $actual." }
    Write-Host "SHA-256 ověřen."
}
else {
    Write-Warning 'Soubor .sha256 není k dispozici — balík se neověřuje.'
}

Write-Step 'Rozbaluji'
$unpacked = Join-Path $work 'unpacked'
Expand-Archive $zip -DestinationPath $unpacked
$source = if (Test-Path (Join-Path $unpacked 'winpak-connector')) { Join-Path $unpacked 'winpak-connector' } else { $unpacked }
if (-not (Test-Path (Join-Path $source 'Acs.WinPakConnector.exe'))) { throw 'Balík neobsahuje Acs.WinPakConnector.exe.' }
$newVersion = (Get-Item (Join-Path $source 'Acs.WinPakConnector.exe')).VersionInfo.ProductVersion
Write-Host "Nová verze: $newVersion"

if (-not $PSCmdlet.ShouldProcess("$ServiceName $currentVersion -> $newVersion", 'Aktualizovat')) { return }

# ---------- výměna ----------
$backup = "$InstallDir.bak-$currentVersion-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Write-Step "Zastavuji službu $ServiceName"
Stop-Service $ServiceName -ErrorAction SilentlyContinue
(Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))

Write-Step "Zálohuji do $backup"
Copy-Item $InstallDir $backup -Recurse

Write-Step 'Přepisuji soubory programu (nastavení zůstává)'
$keepArgs = $Keep | ForEach-Object { if ($_ -like '*.*') { '/XF', $_ } else { '/XD', $_ } }
# robocopy: 0–7 = úspěch (bity: kopírováno, navíc, neshody), 8+ = chyba.
& robocopy $source $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NP @keepArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy selhal (kód $LASTEXITCODE); záloha je v $backup." }

Write-Step 'Spouštím službu'
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
    Write-Host "Konektor $newVersion běží a odpovídá na $HealthUrl." -ForegroundColor Green
    Write-Host "Zálohu $backup můžete po ověření Diagnostiky smazat."
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    return
}

Write-Warning "Služba do 60 s neodpověděla na $HealthUrl — vracím zálohu $currentVersion."
Stop-Service $ServiceName -ErrorAction SilentlyContinue
(Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
& robocopy $backup $InstallDir /MIR /R:3 /W:2 /NFL /NDL /NJH /NP | Out-Null
Start-Service $ServiceName
throw "Aktualizace vrácena. Podívejte se do Prohlížeče událostí (Application, zdroj AcsWinPakConnector); balík zůstal v $work."
