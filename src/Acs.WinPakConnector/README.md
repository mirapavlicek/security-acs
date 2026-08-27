# Acs.WinPakConnector

Malá webová API aplikace, která z proprietárního WIN-PAK API udělá **normální
REST API**. Instaluje se **přímo na WIN-PAK server** (Windows služba) a hlavní
ACS aplikace na RHEL s ní komunikuje přes HTTP + API klíč.

Proč vůbec existuje: WIN-PAK 4.9 žádné REST rozhraní nemá. Obě jeho API
(Database Server API v `NCIHelper.dll` a Communication Server API v `ACCW.dll`)
jsou COM objekty vystavené přes COM+/DCOM, tedy Windows-only. Konektor je
jediné místo v systému, které mluví COM; všechno ostatní jede po REST.
Podrobný rozbor API i mapování na endpointy je v `docs/winpak-api/README.md`.

## Administrační GUI

Konektor má vlastní webovou administraci na `http://<winpak-server>:52001/ui`.
Nemusí se tedy ručně editovat `appsettings.json` a restartovat služba — změny se
zapisují do `appsettings.Local.json` vedle programu a použijí se okamžitě
(provider se přestaví sám).

| Stránka | Obsah |
| --- | --- |
| Přehled | verze, režim, podpora zápisu a dveří, maskovaný API klíč, stav serverů WIN-PAK a seznam věcí, které je potřeba dořešit |
| Nastavení | režim, API klíč (i generování nového), heslo administrace, přihlášení operátora WIN-PAK, účet a podúčet, komunikační server, ProgID objektů, SQL dotazy pro režim Mssql |
| Funkce | GUI na části API, které ACS nepoužívá (viz níže) |
| Diagnostika | živé volání WIN-PAKu — účty, čtečky, přístupové úrovně, držitelé, systémové údaje, časové zóny, panely — a poslední události z panelů |

### Funkce

Konektor umí celé WIN-PAK API, ale ACS z něj při schvalování přístupů využívá jen
část. Zbytek má GUI v sekci **Funkce** (`/ui/features`) — ať je vidět, co systém
umí, a ať to jde bez psaní HTTP požadavků vyzkoušet nebo v nouzi rovnou použít:

| Stránka | Co obsahuje |
| --- | --- |
| Dveře a zařízení | stav dveří, zamknutí a odemknutí, puls i časovaný, režim dveří a NetAXS režim, vstupní body podle bodu, alarmy (potvrzení, zrušení, poznámka, detail transakce), shunt, buffer, spínání výstupů, návrat pod časovou zónu, vlastní příkaz |
| Panely | výstupy a skupiny s jejich časovými zónami, časové zóny a skupiny svátků panelu, inicializace panelu a její zrušení, refresh zón, hromadné zamknutí dveří účtu, refresh dveří, door schedule |
| Karty a držitelé | hledání a zápis karty včetně NetAXS voleb, hromadné založení a rušení rozsahu, karty bez držitele, správa držitelů, vyhledávání v databázi WIN-PAK, poznámková pole, fotky a podpisy |
| Přístupové úrovně | detail a strom přístupů, zakládání (obě varianty), konfigurace čteček a jednotlivých vstupů, úplný zápis, dotčené karty, přeřazení a smazání i s náhradou |
| Číselníky | časové zóny s intervaly, přehled kdo zónu používá, přeřazení na jinou zónu, odebrání z panelů, svátky a skupiny svátků |
| Systém a události | údaje o instalaci, účty, drobné dotazy na názvy, plány a šablony reportů, odznaky, muster report, filtry událostí a živý výpis událostí z panelů |

Akce se provádějí okamžitě a v režimu Com proti ostrému WIN-PAKu — odemknutí dveří
opravdu odemkne dveře. V režimu Mock funguje celá sekce proti datům v paměti, takže
se dá projít i bez WIN-PAKu. Co daný režim neumí, skončí hláškou, ne chybou.
Zařízení se adresují číselným `HID`, stejně jako je adresuje komunikační server.

Přihlášení: heslem z pole „Heslo administrace“. Dokud není nastavené, přihlašuje
se **API klíčem** — ten už dnes umožňuje i odemykat dveře, takže tím nevzniká
slabší ochrana, ale samostatné heslo se doporučuje, aby se klíč nemusel zadávat
do prohlížeče. Formulář nikdy nezobrazuje tajné hodnoty celé (jen maskovaně) a
prázdné pole znamená „nechat beze změny“.

Původní `appsettings.json` z instalace zůstává nedotčený a slouží jako výchozí
vrstva; hodnoty z proměnných prostředí a z GUI ho přepisují. Soubor
`appsettings.Local.json` obsahuje hesla — omezte k němu přístup na účet služby
a administrátory (na Windows přes ACL složky s programem).

## Režimy (provider)

Nastavuje se v GUI (Nastavení → Režim) nebo v `appsettings.json` → `WinPak:Mode`:

| Režim | Popis | Zápis | Dveře |
| --- | --- | --- | --- |
| `Mock` | ukázková data v paměti — pro vývoj hlavní aplikace | ano (jen v paměti) | ano (jen v paměti) |
| `Mssql` | read-only čtení přímo z WIN-PAK MSSQL databáze; SQL dotazy jsou konfigurovatelné (schéma WIN-PAK je pod NDA a liší se mezi verzemi) | ne (501) | ne (501) |
| `Com` | oficiální WIN-PAK API přes COM+ (`NCIHelper.Application`, `ACCW.MTSCBServer`) | ano | ano, pokud je zapnutý komunikační server |

Hlavní aplikace pozná dostupnost z `GET /api/v1/info` (`supportsWrite`,
`supportsDoorControl`). Dokud zápis není dostupný, správce karet zadává přístupy
ve WIN-PAK ručně a v ACS je jen potvrdí.

## REST API

Všechny `/api/*` endpointy vyžadují hlavičku `X-Api-Key` (viz `Security:ApiKey`).
Bez nakonfigurovaného klíče API odmítá všechny požadavky (fail-closed).
OpenAPI popis: `GET /openapi/v1.json`.

| Metoda | Cesta | Popis |
| --- | --- | --- |
| GET | `/health` | healthcheck (bez klíče) |
| GET | `/ui` | administrační GUI (přihlášení heslem, ne API klíčem v hlavičce) |
| GET | `/api/v1/info` | verze, režim, podpora zápisu a ovládání dveří |
| GET | `/api/v1/status` | stav spojení s databázovým a komunikačním serverem WIN-PAK |
| GET | `/api/v1/accounts` | účty a podúčty |
| GET | `/api/v1/readers` | seznam čteček (id = `HWDeviceID`, tedy i id dveří) |
| GET | `/api/v1/access-levels` | seznam přístupových úrovní |
| GET | `/api/v1/cardholders?search=` | vyhledání držitelů karet |
| GET | `/api/v1/cardholders/{id}` | detail držitele včetně karet |
| POST | `/api/v1/cardholders` | založení držitele |
| PUT | `/api/v1/cardholders/{id}` | úprava držitele |
| POST | `/api/v1/cardholders/{id}/access-levels` | přiřazení úrovně (`{"accessLevelId": "..."}`) |
| DELETE | `/api/v1/cardholders/{id}/access-levels/{alId}` | odebrání úrovně |
| GET | `/api/v1/cards/{cardNumber}` | karta podle čísla |
| PUT | `/api/v1/cards/{cardNumber}` | založení nebo úprava karty |
| DELETE | `/api/v1/cards/{cardNumber}` | zrušení karty |
| GET | `/api/v1/devices` | zařízení připojená ke komunikačnímu serveru |
| GET | `/api/v1/doors/{hid}` | stav dveří |
| POST | `/api/v1/doors/{hid}/pulse` | krátké otevření (`{"seconds": 5}`, volitelné) |
| POST | `/api/v1/doors/{hid}/lock` | zamknutí |
| POST | `/api/v1/doors/{hid}/unlock` | odemknutí |
| POST | `/api/v1/doors/{hid}/mode` | režim dveří (`{"mode": 5}` = jen karta) |
| GET | `/api/v1/events?limit=` | poslední události z panelů (jen režim `Com`) |

Přístupové úrovně patří ve WIN-PAKu **kartě**, ne držiteli. Endpointy nad
držitelem jsou proto zkratka: konektor načte jeho karty, přepočítá jim seznam
úrovní a uloží je zpět.

### Rozšířená část (správa systému)

Konektor pokrývá i zbytek API — číselníky, konfiguraci hardwaru a povely.
Úplný seznam endpointů s odpovídajícími COM voláními je v
`docs/winpak-api/README.md`, kapitola „Mapování na REST konektoru“. Ve zkratce:

| Oblast | Endpointy |
| --- | --- |
| Karty | `/cards` (výpis, `?withoutHolder=true`), `/cards/bulk`, `/cards/bulk-delete`, `/cards/{n}/netaxs` |
| Držitelé | `/cardholders/search-fields`, `/cardholders/search`, `/note-field-templates`, `/cardholders/{id}/photo/{i}`, `/cardholders/{id}/signature/{i}` |
| Přístupové úrovně | `/access-levels/{name}` (+ `/tree`, `/cards`, `/reassign-candidates`, `/readers`, `/entrance`, `/reassign`) |
| Časové zóny | `/time-zones` (+ `/{id}/ranges`, `/{id}/usage`, `/{id}/reassign-candidates`, `/reassign`, `/{id}/remove-from-panels`) |
| Svátky | `/holidays`, `/holiday-groups` |
| Hardware | `/hardware`, `/panels` (+ výstupy, skupiny, časové zóny, svátky), `/access-areas`, `/readers/{name}/time-zones` |
| Systém | `/system`, `/schedules/{id}`, `/templates/{id}`, `/badges/{id}` |
| Povely | `/devices/{hid}/…` (alarmy, shunt, buffer, výstupy), `/panels/{hid}/initialize`, `/doors/lock-all`, `/doors/schedule`, `/doors/{hid}/netaxs-mode`, `/event-filters`, `/muster` |

Režim `Mssql` na těchto endpointech vrací 501; režim `Mock` je obsluhuje
z paměti, aby šlo vyvíjet a testovat bez WIN-PAKu.

Chování chyb: `401` špatný klíč, `404` neexistující záznam, `422` WIN-PAK zápis
odmítl (hláška nese jeho stavový kód), `501` provider operaci nepodporuje,
`502` konektor se nedostal k WIN-PAKu, `503` klíč není nakonfigurován.

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

3. Upravte `appsettings.json` na minimum potřebné k prvnímu přihlášení:
   - `Security:ApiKey` — vygenerujte silný klíč (např. `openssl rand -hex 32`),
     tentýž klíč se nastaví v ACS aplikaci,
   - `Kestrel:Endpoints:Http:Url` — ponechte `http://0.0.0.0:52001`
     (nebo omezte na konkrétní interní IP).

   Zbytek (režim, přihlášení operátora WIN-PAK, účet, ProgID) se pohodlněji
   nastaví v administračním GUI na `/ui`.

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

6. Ověření: `curl http://<winpak-server>:52001/health` → `{"status":"ok"}`,
   pak otevřete `http://<winpak-server>:52001/ui`, přihlaste se API klíčem,
   nastavte režim `Com` a v Diagnostice ověřte, že konektor čte účty a čtečky.

## Nastavení režimu `Com`

V GUI stačí vyplnit sekci „WIN-PAK přes COM“. Odpovídající podoba v souboru:

```json
"WinPak": {
  "Mode": "Com",
  "Com": {
    "UserName": "acs-service",
    "Password": "…",
    "Domain": "FNMH",
    "AccountName": "FNMH",
    "SubAccountName": "",
    "EnableCommunicationServer": true
  }
}
```

Předpoklady na straně WIN-PAKu:

- WIN-PAK edice SE/PE s licencí `SRVWPPAPI`, nainstalovaný s volbou **Web**
  (tím se nasadí `DatabaseAPIServer`).
- V `dcomcnfg` existují COM+ aplikace **WIN-PAK CS DBServer Helper** a
  **WIN-PAK CS ComServer Helper**. Pokud konektor běží na jiném stroji než
  WIN-PAK, je nutné z obou exportovat *Application proxy* a nainstalovat ji
  na stroj s konektorem. Doporučené je nasazení přímo na WIN-PAK server.
- Účet služby má práva volat COM+ aplikace a `UserName`/`Password` je platný
  operátor WIN-PAK.

Pokud instalace registruje COM objekty pod jinými ProgID, přepište je v
`ApplicationProgId`, `CardHolderProgId` a `CommServerProgId`.

`EnableCommunicationServer: false` vypne ovládání dveří i odběr událostí —
databázová část funguje samostatně.

## Bezpečnost konektoru

ACS považuje data z konektoru za autoritativní (zpětná synchronizace zakládá
potvrzené přístupy) a konektor umí dveře i odemykat. Kompromitovaný konektor
nebo odposlech linky proto může ovlivnit fyzický přístup. Doporučení:

- **Síťové omezení** — port konektoru zpřístupněte firewallem výhradně
  aplikačním serverům a stanicím správců (viz pravidlo výše); nikdy ne veřejně.
  Na tomtéž portu běží i administrační GUI.
- **Heslo administrace** — nastavte ho v GUI, ať se API klíč nemusí zadávat
  do prohlížeče. Přihlášení má záměrné zpoždění po chybném pokusu.
- **API klíč** — silný, náhodný (`openssl rand -hex 32`), pravidelně rotovaný;
  týž klíč se zadává v ACS (Nastavení → WIN-PAK).
- **TLS/mTLS** — na produkci provozujte konektor za reverzní proxy s TLS
  (nebo nakonfigurujte Kestrel s certifikátem) a ideálně vyžadujte klientský
  certifikát aplikačních serverů (mTLS). Nešifrovaný HTTP používejte jen
  v izolované důvěryhodné síti.
- **Nejmenší oprávnění** — SQL login pro režim `Mssql` má mít pouze `SELECT`;
  operátor WIN-PAK pro režim `Com` jen práva potřebná ke správě karet.
- **Vypněte, co nepoužíváte** — bez ovládání dveří nechte
  `EnableCommunicationServer: false`.

## Ověření mapování na COM

Skutečný WIN-PAK v CI k dispozici není, proto testy volají COM přes atrapu
(`FakeComDispatch`) a kontrolují, že konektor používá přesně ty metody, pořadí
parametrů a stavové kódy, jaké popisuje příručka — včetně `AddUpdateCard`
se čtrnácti parametry, `AddUpdateCardEx` s devatenácti, `BulkAddCards`
s jedenácti a rozdílných tvarů `Isolate*`/`Reassign*` volání. Parser `<NLZ>`
zpráv je testovaný na ukázkách přímo z příručky.

Pokrytí: **139 ze 147** dokumentovaných metod databázového API a **všech 42**
funkcí komunikačního serveru. Nepokryté zůstávají jen ty, u kterých příručka
uvádí jen název bez signatury — seznam a důvody jsou v `docs/winpak-api/README.md`.
