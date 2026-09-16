# Nasazení ACS

## Architektura provozu

- **2× RHEL node** (`10.84.7.146`, `10.84.7.147`) — na každém běží `acs-web`
  (Kestrel, port **52000**, HTTP) jako systemd služba pod účtem `acs`.
- **HAProxy** (existující) směruje `acs.fnmh.network` na oba nody,
  healthcheck `GET /health`. Aplikace je bezestavová — bez sticky sessions.
- **MariaDB Galera** `10.84.12.170-172`, DB `winpak`, connection string
  s `LoadBalance=Failover`.
- **Auto-update**: systemd timer `acs-updater` na obou nodech každých ~10 min
  kontroluje Git `main`; při nové verzi sestaví (`dotnet publish`), spustí
  testy, atomicky přepne symlink `/opt/acs/current` a restartuje službu.
  Náhodný rozptyl timeru brání současné aktualizaci obou nodů; HAProxy po
  dobu restartu drží provoz na druhém nodu.

## První instalace

Z vývojového stroje (macOS-ai proxyhub), s funkčním SSH klíčem na nody:

```bash
cp deploy/acs.env.example deploy/acs.env
#  → doplňte heslo k MariaDB (deploy/acs.env je v .gitignore)

./deploy/install.sh <ssh-user>              # nainstaluje oba nody
./deploy/install.sh <ssh-user> 10.84.7.146  # případně jen jeden
```

Skript na každém nodu: nainstaluje .NET 10 SDK, git a písmo `dejavu-sans-fonts`
(generování PDF — kartičky parkovacích povolení a reporty potřebují TrueType
písmo s českou diakritikou; jiný adresář s `.ttf` lze určit proměnnou
`ACS_PDF_FONT_DIR`), založí účet `acs`, naklonuje repozitář do `/opt/acs/src`,
nahraje `/etc/acs/acs.env` (600), zaregistruje systemd služby, otevře port
52000 (firewalld + SELinux), provede první build a spustí aplikaci.

Pozn.: pokud je repozitář privátní, nastavte na nodech přístup ke čtení
(deploy key / `git config credential…`) — updater potřebuje `git fetch`.

## Po instalaci

1. HAProxy: přidejte backend dle `haproxy.cfg.example` (terminuje HTTPS).
2. **Počáteční heslo administrátora**:
   - Pokud jste v `deploy/acs.env` nastavili `ACS_BOOTSTRAP_ADMIN_PASSWORD`,
     přihlašte se tímto heslem.
   - Jinak se vygenerovalo náhodné heslo do logu — přečtěte ho na nodu, kde
     proběhla první inicializace DB:

     ```bash
     journalctl -u acs-web | grep "počátečním heslem"
     ```

3. Otevřete `https://acs.fnmh.network`, přihlaste se jménem `admin` a tímto
   heslem — aplikace vynutí okamžitou změnu.
4. V **Nastavení** (GUI) nakonfigurujte Active Directory (LDAPS, mapování
   skupin na role), WIN-PAK konektor (adresa + API klíč) a zdroj zaměstnanců.
5. **Přihlášení účtem Windows (NTLM / Kerberos)**: zaregistrujte SPN
   `HTTP/acs.fnmh.network` na servisní účet, vytvořte keytab (`ktpass`), nahrajte
   ho na oba nody jako `/etc/acs/acs.keytab` (`KRB5_KTNAME` v `acs.env`; při
   instalaci stačí soubor `deploy/acs.keytab`) a zapněte sekci *Přihlášení
   Windows* v Nastavení. Podrobný postup: [`docs/prihlaseni-windows.md`](../docs/prihlaseni-windows.md).

## Bezpečnostní poznámky k nasazení

- Auto-update jede ve výchozím režimu `ACS_UPDATE_MODE=tag` — na nody se
  nasazují jen release tagy `vX.Y.Z`. Pro nasazení vydání vytvořte a pushněte
  tag: `git tag v1.0.0 && git push origin v1.0.0`.
- Port 52000 je firewallem otevřen jen pro app servery; HTTPS terminuje
  HAProxy. Nikdy nevystavujte 52000 veřejně.
- Viz `docs/SECURITY.md` pro kompletní přehled bezpečnostních opatření.

## Paměť a soužití s dalšími službami na nodu

Nody (8 CPU / 7,5 GB) sdílí `acs-web` s aplikací `unisapi`. V září 2026 rostl
`acs-web` na obou nodech zhruba každou hodinu k ~6,5 GB, stroj swapoval a OOM
killer službu zabíjel; mezitím ztuhlý `unisapi` nestíhal handshake s MariaDB a
Galera klienta `10.84.7.147` zablokovala (`max_connect_errors`). Příčina a opatření:

- **Strom přístupů na každém řádku položek.** Hodinová synchronizace přístupových
  úrovní načítala `AccessLevels.Include(a => a.Entries)`: 217 úrovní × 785 položek
  = 170 tisíc řádků JOINu a na každém z nich sloupec `AccessTree` (~70 KB XML) —
  přes 10 GB dat na jeden dotaz. V logu je to poznat tak, že po `GET …/access-levels`
  následuje tento SELECT, pak už jen varování Kestrelu o thread pool starvation a za
  ~10 minut `Killed process (Acs.Web)`; audit `access-levels-synced` chybí. Stejný
  dotaz měl i přehled *Číselníky → Přístupové úrovně*. Od této verze je strom
  samostatná entita `AccessLevelTree` ve stejném řádku (table splitting, bez změny
  schématu DB), synchronizace dotahuje položky jen u úrovně, jejíž složení obnovuje,
  a přehled počítá čtečky v DB.
- **Stejný vzor u podkladů plánů.** Obrázek schématu patra (až 5 MB, `longblob`) byl
  obyčejná vlastnost entity `Floor`; každý dotaz s `Include(Floor)` / `Include(Building)`
  by ho tahal z DB znovu, u výpisu čteček za každý řádek. Od této verze je samostatná
  entita `FloorSchema` / `BuildingSchema` a načte se jen s `Include(f => f.Schema)`
  nebo přes `/floors/{id}/schema`.
- **Garbage collector.** Aplikace přešla ze serverového GC (heap na každé jádro,
  úklid až při nedostatku RAM celého stroje) na souběžný workstation GC
  (`Acs.Web.csproj`).
- **Strop v systemd.** `acs-web.service` má `MemoryHigh=1536M`, `MemoryMax=2G`,
  `MemorySwapMax=0` a `OOMScoreAdjust=500`: .NET si z cgroupu odvodí limit heapu
  a uklízí včas; kdyby proces přesto rostl, zabije OOM killer jen `acs-web`
  (systemd ho zvedne), ne `unisapi`. Na nodech se unit přepíše až při
  `deploy/install.sh` — na běžících nodech ho nasaďte ručně:

  ```bash
  scp deploy/systemd/acs-web.service root@10.84.7.147:/etc/systemd/system/
  ssh root@10.84.7.147 'systemctl daemon-reload && systemctl restart acs-web'
  systemctl show acs-web -p MemoryCurrent -p MemoryPeak -p MemoryMax   # kontrola
  journalctl -k -g oom                                                  # zásahy OOM killeru
  ```

Souvislost s Galerou: `acs-web` i `unisapi` se do MariaDB hlásí ze stejné IP nodu,
takže `max_connect_errors` (výchozích 100) počítá nedokončené handshaky obou
aplikací dohromady a blokace hostitele postihne obě. Po zásahu OOM killeru na nodu
proto zkontrolujte `Host '10.84.7.14x' is blocked` v logu a nechte DBA provést
`FLUSH HOSTS`; trvalé řešení je `max_connect_errors` řádově vyšší (např. 100000).

## Užitečné příkazy na nodech

```bash
systemctl status acs-web             # stav aplikace
journalctl -u acs-web -f             # logy
systemctl start acs-updater.service  # ruční vynucení aktualizace
ls -l /opt/acs/current               # jaká verze běží (symlink na sha)
curl http://127.0.0.1:52000/health   # healthcheck
```

## WinPak Connector

Konektor je samostatná Windows služba na WIN-PAK serveru — **auto-update ACS ho
nenasazuje**, publikuje a instaluje se zvlášť.

- Postup zprovoznění krok za krokem:
  [`docs/winpak-connector-zprovozneni.md`](../docs/winpak-connector-zprovozneni.md)
- Aktualizace z ACS bez přístupu WIN-PAK serveru na internet: *Administrace →
  Konektor* (ACS stáhne release, pošle ho konektoru, ten se vymění sám).
- Aktualizace na vydanou verzi jedním příkazem na WIN-PAK serveru:
  `.\Update-WinPakConnector.ps1 -Version 1.12.7` (skript
  [`deploy/Update-WinPakConnector.ps1`](Update-WinPakConnector.ps1), je i v balíku releasu)
- Referenční popis a nastavení:
  [`src/Acs.WinPakConnector/README.md`](../src/Acs.WinPakConnector/README.md)
