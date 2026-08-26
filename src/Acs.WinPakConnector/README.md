# Acs.WinPakConnector

Malá webová API aplikace, která z proprietárního WIN-PAK API udělá **normální
REST API**. Instaluje se **přímo na WIN-PAK server** (Windows služba) a hlavní
ACS aplikace na RHEL s ní komunikuje přes HTTP + API klíč.

## Režimy (provider)

Konfiguruje se v `appsettings.json` → `WinPak:Mode`:

| Režim | Popis | Zápis do WIN-PAK |
| --- | --- | --- |
| `Mock` | ukázková data v paměti — pro vývoj hlavní aplikace | ano (jen v paměti) |
| `Mssql` | read-only čtení přímo z WIN-PAK MSSQL databáze; SQL dotazy jsou konfigurovatelné (schéma WIN-PAK je pod NDA a liší se mezi verzemi) | ne (501) |
| `Sdk` | oficiální WIN-PAK SDK (`SRVWPPAPI`) — připraveno, čeká na NDA/licenci od Honeywellu | ano |

Hlavní aplikace pozná dostupnost zápisu z `GET /api/v1/info` → `supportsWrite`.
Dokud zápis není dostupný, správce karet zadává přístupy ve WIN-PAK ručně
a v ACS je jen potvrdí.

## REST API

Všechny `/api/*` endpointy vyžadují hlavičku `X-Api-Key` (viz `Security:ApiKey`).
Bez nakonfigurovaného klíče API odmítá všechny požadavky (fail-closed).
OpenAPI popis: `GET /openapi/v1.json`.

| Metoda | Cesta | Popis |
| --- | --- | --- |
| GET | `/health` | healthcheck (bez klíče) |
| GET | `/api/v1/info` | verze, režim, podpora zápisu |
| GET | `/api/v1/readers` | seznam čteček |
| GET | `/api/v1/access-levels` | seznam přístupových úrovní |
| GET | `/api/v1/cardholders?search=` | vyhledání držitelů karet |
| GET | `/api/v1/cardholders/{id}` | detail držitele |
| POST | `/api/v1/cardholders/{id}/access-levels` | přiřazení access level (`{"accessLevelId": "..."}`) |
| DELETE | `/api/v1/cardholders/{id}/access-levels/{alId}` | odebrání access level |

Chování chyb: `401` špatný klíč, `404` neexistující záznam, `501` provider
nepodporuje zápis, `503` klíč není nakonfigurován.

## Lokální spuštění (vývoj)

```bash
dotnet run --project src/Acs.WinPakConnector
# běží na http://localhost:52001, režim Mock
curl -H "X-Api-Key: <klic>" http://localhost:52001/api/v1/readers
```

## Instalace na WIN-PAK server (Windows)

1. Publikace (na build stroji):

   ```bash
   dotnet publish src/Acs.WinPakConnector -c Release -r win-x64 --self-contained \
     -o publish/winpak-connector
   ```

   Self-contained = na WIN-PAK server není potřeba instalovat .NET runtime.

2. Zkopírujte obsah `publish/winpak-connector` např. do
   `C:\Program Files\AcsWinPakConnector`.

3. Upravte `appsettings.json`:
   - `Security:ApiKey` — vygenerujte silný klíč (např. `openssl rand -hex 32`),
     tentýž klíč se nastaví v ACS aplikaci,
   - `Kestrel:Endpoints:Http:Url` — ponechte `http://0.0.0.0:52001`
     (nebo omezte na konkrétní interní IP),
   - `WinPak:Mode` — `Mssql` (a doplňte connection string + SQL dotazy),
     později `Sdk`.

4. Registrace Windows služby (PowerShell jako administrátor):

   ```powershell
   New-Service -Name "AcsWinPakConnector" `
     -BinaryPathName '"C:\Program Files\AcsWinPakConnector\Acs.WinPakConnector.exe"' `
     -DisplayName "ACS WinPak Connector" -StartupType Automatic
   Start-Service AcsWinPakConnector
   ```

5. Firewall: povolte TCP 52001 **pouze** z adres aplikačních serverů
   (10.84.7.146, 10.84.7.147):

   ```powershell
   New-NetFirewallRule -DisplayName "ACS WinPak Connector" -Direction Inbound `
     -Protocol TCP -LocalPort 52001 -RemoteAddress 10.84.7.146,10.84.7.147 -Action Allow
   ```

6. Ověření: `curl http://<winpak-server>:52001/health` → `{"status":"ok"}`.

## Napojení na skutečný WIN-PAK

- **Režim `Mssql`:** doplňte do `WinPak:Mssql` connection string k WIN-PAK
  databázi (stačí SQL login s právem `SELECT`) a SQL dotazy vracející sloupce
  popsané v `MssqlProviderOptions`. Názvy tabulek ověřte proti vaší verzi
  WIN-PAK (4.9.x).
- **Režim `Sdk`:** po podpisu NDA a získání `SRVWPPAPI` od Honeywellu se
  implementace doplní do `Providers/SdkWinPakProvider.cs` (COM interop).
  REST rozhraní konektoru se nezmění.
