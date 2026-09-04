# ACS — Systém pro schvalování přístupů k místnostem

Návrhový plán aplikace `acs.fnmh.network`. Tento dokument je podklad pro
schválení návrhu **před zahájením vývoje**. Na konci je seznam otevřených
otázek, které je potřeba doplnit.

---

## 1. Shrnutí zadání

Webová aplikace pro schvalování přístupů k místnostem v prostředí s
Honeywell WIN-PAK (řízení přístupu). Aplikace:

- načte **seznam čteček** z WIN-PAK (přes API) a **seznam zaměstnanců**
  (MSSQL nebo API — zatím otevřené),
- všechny číselníky lze **ručně editovat i přidávat**,
- ke každé čtečce lze definovat **schvalovací matici** — neomezeně hlubokou,
  s jedním i více schvalovateli na úrovni a s možností **zástupů**,
- podporuje **řetězce čteček** (přístup do místnosti vyžaduje i chodbu,
  patro, budovu…),
- zaměstnanec vidí **přehled svých přístupů**,
- **správci karet** mají frontu schválených požadavků; tlačítko „předat do
  systému“ zavolá WIN-PAK API, nebo požadavek potvrdí ručně,
- přihlašování přes **Active Directory** + **lokální admin** účet,
- **veškerá nastavení v GUI**, GUI podporuje **barevná témata**,
  volitelně **grafická schémata místností, pater a budov**.

### Prostředí a provoz

| Oblast | Hodnota |
| --- | --- |
| Platforma | .NET 10 (C#), webová aplikace |
| Databáze | MariaDB Galera: `10.84.12.170,171,172:3306`, DB `winpak`, režim Failover |
| Aplikační servery | RHEL: `10.84.7.146`, `10.84.7.147` — HA, aplikace **bezestavová** |
| Port / protokol | `52000` / HTTP (před aplikací stojí HAProxy) |
| Veřejná adresa | `acs.fnmh.network` |
| Nasazení | přes SSH lokálním klíčem, vývoj přes macOS-ai proxyhub |
| Aktualizace | automaticky z Git větve `main` na obou nodech |

---

## 2. Klíčové zjištění: WIN-PAK API

Rozbor obou oficiálních příruček (Database Server API a Communication Server API,
build 1090.7.6) je v [`docs/winpak-api/README.md`](winpak-api/README.md).
Podstatné pro návrh:

- Nejnovější verze je **WIN-PAK 4.9.5 SP1**; běží jen na Windows nad MSSQL.
- **WIN-PAK nemá žádné REST rozhraní.** Obě API jsou COM objekty vystavené přes
  COM+/DCOM: Database Server API v `NCIHelper.dll` (karty, držitelé, přístupové
  úrovně, čtečky, časové zóny) a Communication Server API v `ACCW.dll` (stav a
  ovládání dveří, události z panelů). API je součástí edic SE/PE s licencí
  `SRVWPPAPI`.
- Oprávnění nese ve WIN-PAKu **karta**, ne držitel — přístup se přiděluje
  zápisem seznamu úrovní na karty držitele (`AddUpdateCard`).

**Důsledek pro architekturu:** aplikace na RHEL nemůže WIN-PAK volat přímo.
Komponenta **WinPak Connector** — Windows služba (.NET) nasazená na WIN-PAK
serveru — je jediné místo, které mluví COM, a pro zbytek systému z něj dělá
REST (HTTPS + API klíč / mTLS). Má tři režimy: `Com` (ostrý provoz),
`Mssql` (read-only záloha, zápis zůstává na ručním potvrzení správcem karet)
a `Mock` (vývoj).

> Otevřená otázka č. 1–4 níže: verze/edice WIN-PAK, licence `SRVWPPAPI`,
> síťová dostupnost WIN-PAK serveru, přípustnost read-only přístupu do MSSQL.

---

## 3. Architektura

```
                        ┌─────────────────────────┐
   uživatelé ── http ──▶│  HAProxy (existující)   │  acs.fnmh.network
                        └───────────┬─────────────┘
                             :52000 │ http (failover / round-robin)
              ┌─────────────────────┴────────────────────┐
              ▼                                          ▼
   ┌──────────────────────┐                  ┌──────────────────────┐
   │ RHEL 10.84.7.146     │                  │ RHEL 10.84.7.147     │
   │ acs-web (Kestrel)    │                  │ acs-web (Kestrel)    │
   │ acs-updater (systemd)│                  │ acs-updater (systemd)│
   └──────────┬───────────┘                  └──────────┬───────────┘
              │          bezestavové – bez sticky session│
              └───────────────┬──────────────────────────┘
                              ▼
               MariaDB Galera 10.84.12.170-172 (Failover)
                              ▲
              ┌───────────────┴───────────────┐
              │                               │
   ┌──────────┴───────────┐        ┌──────────┴──────────┐
   │ WinPak Connector     │        │ Zdroj zaměstnanců    │
   │ (Windows, u WIN-PAK) │        │ (MSSQL / API — TBD)  │
   └──────────────────────┘        └─────────────────────┘
              +  Active Directory (LDAPS) pro přihlašování
```

### 3.1 Technologický stack

| Vrstva | Volba | Zdůvodnění |
| --- | --- | --- |
| Backend + frontend | ASP.NET Core **Razor Pages** (.NET 10 LTS), server-rendered | plně bezestavové (žádné SignalR circuity jako u Blazor Server), jediný projekt, jednoduché auto-update; interaktivita doplňována cíleně JS |
| ORM | EF Core + **Pomelo.EntityFrameworkCore.MySql** | podpora MariaDB; `MySqlConnector` umí `Server=10.84.12.170,10.84.12.171,10.84.12.172;LoadBalance=Failover` přesně dle zadání |
| Autentizace | cookie auth; AD přes **LDAPS** (`System.DirectoryServices.Protocols`, funguje na Linuxu) + lokální účty (admin) s Argon2/PBKDF2 hash | |
| Bezestavovost | ASP.NET **Data Protection keys v MariaDB** (sdílené oběma nody), žádný in-memory session state, cache jen jako lokální read-through | uživatel může kdykoli přejít na druhý node |
| UI témata | CSS custom properties + přepínač témat (světlé/tmavé/barevné palety), uloženo per uživatel v DB | |
| Schémata budov | upload SVG/PNG per budova/patro + klikací „hotspoty“ navázané na místnosti/čtečky | viz otázka č. 15 |

### 3.2 Bezestavovost (požadavek HA)

- žádný stav v paměti procesu: přihlášení = šifrovaná cookie, Data Protection
  klíče v DB, antiforgery tokeny odvozené z týchž klíčů,
- nahrané soubory (schémata budov, loga) se ukládají do DB (BLOB) — oba nody
  vidí totéž bez sdíleného filesystému,
- plánované úlohy (synchronizace číselníků, notifikace) běží na obou nodech
  s **DB zámkem (leader election přes MariaDB)** — provede je vždy jen jeden.

---

## 4. Datový model (jádro)

```
Building ─┬─ Floor ─┬─ Room ─┬─ Reader (import z WIN-PAK + ruční editace)
          │         │        └─ poloha na schématu (hotspot)
          │         └─ schéma (SVG/PNG)
          └─ schéma

Employee (import MSSQL/API + ruční editace, vazba na AD účet)

ReaderDependency  — orientovaný graf „čtečka vyžaduje čtečku“
                    (místnost → chodba → patro → budova), kontrola cyklů

ApprovalMatrix    — per čtečka (nebo skupina čteček); strom úrovní:
  ApprovalLevel   — pořadí, režim (všichni / kterýkoli / N z M)
    Approver      — zaměstnanec či AD skupina
    Deputy        — zástup (kdo, od–do, za koho)

AccessRequest     — žadatel, cílový zaměstnanec, čtečka/y (včetně
                    automaticky doplněných závislostí), stav:
                    Draft → Pending(level n) → Approved → Queued
                    → PushedToWinPak / ManuallyConfirmed → Active
                    → (Revoked / Expired)
ApprovalDecision  — kdo, kdy, jak, komentář (audit)

CardAdminQueue    — pohled na Approved požadavky; akce „Předat do systému“
                    (volá WinPak Connector) nebo „Potvrzeno ručně“

Site              — areál (Motol, Homolka…), volitelně vlastní matice
ParkingPermitType — druh parkovacího povolení: vazba (SPZ / funkce),
                    max. SPZ, výchozí platnost, matice, texty kartičky
ParkingPermit     — povolení přidělené zaměstnanci: SPZ / funkce, areály
                    (nebo všechny), platnost, číslo, vydání, odebrání;
                    stav nese AccessRequestItem (třetí předmět položky):
                    Pending → Approved (fronta parkování) → Issued
                    → Revoked; SPZ se při vydání zapíší jako
                    EmployeeIdentifier(LicensePlate)

Setting           — všechna nastavení aplikace (klíč/hodnota, editace v GUI)
AuditLog          — každá změna číselníků, rozhodnutí, přihlášení, sync
```

---

## 5. Co všechno musíme vyvinout (rozpad na moduly)

### A. Základ aplikace
1. **Skeleton řešení** — ASP.NET Core Razor Pages, EF Core migrace,
   healthcheck endpoint (`/health` pro HAProxy), strukturované logování.
   ✅ Implementováno (`src/Acs.Web`, `src/Acs.Infrastructure`, `src/Acs.Domain`).
2. **Autentizace a autorizace** — AD (LDAPS) login, lokální admin (seed při
   prvním startu, vynucená změna hesla), role: `Admin`, `CatalogManager`,
   `Approver`, `CardAdmin`, `Employee`. ✅ Implementováno včetně mapování
   AD skupin na role (konfigurace v GUI, přepočet při každém přihlášení).
3. **Nastavení v GUI** — administrace všech parametrů: připojení WinPak
   Connector, LDAP, zdroj zaměstnanců, SMTP, plány synchronizací, témata;
   citlivé hodnoty šifrované (Data Protection) v DB. ✅ Implementováno.
4. **Audit log** + prohlížečka v GUI. ✅ Implementováno.

### B. Číselníky a integrace
5. **WinPak Connector** (Windows služba) — REST fasáda nad COM API WIN-PAKu;
   fallback read-only nad MSSQL; pokrývá účty, čtečky, přístupové úrovně,
   držitele, karty, ovládání dveří a odběr událostí z panelů.
   ✅ **Implementováno** v [`src/Acs.WinPakConnector`](../src/Acs.WinPakConnector/README.md)
   (režimy Mock / Mssql / Com, API klíč, integrační testy a testy mapování
   na dokumentovaná COM volání).
6. **Synchronizace čteček** — plánovaný import z Connectoru, párování na
   existující záznamy, ruční editace a přidávání, označení „ručně vytvořeno /
   importováno“. ✅ Implementováno (ruční tlačítko + automatický plánovač
   `SyncScheduler` s DB zámkem GET_LOCK — běží vždy jen na jednom nodu).
6b. **Zpětná synchronizace stavu z WIN-PAK** — změny provedené přímo ve
   WIN-PAK se propíší do ACS: externí udělení přístupu se zaeviduje jako
   systémová potvrzená žádost, externí odebrání označí položku jako
   odebranou, položky z fronty správce karet zadané rovnou ve WIN-PAK se
   automaticky potvrdí; aktualizují se i čísla karet a dopáruje card holder
   podle čísla karty. ✅ Implementováno (`AccessSyncService` — plánovač
   + ruční tlačítko ve frontě správce karet).
7. **Zdroj zaměstnanců** — adaptérové rozhraní `IEmployeeSource`
   s implementacemi `MssqlEmployeeSource` a `ApiEmployeeSource`
   (výběr a konfigurace v GUI — zadání nechává otevřené). ✅ Implementováno.
8. **Správa budov / pater / místností** — CRUD + přiřazení čteček.
   ✅ Implementováno (`/Catalog/Places`).
   ✅ Čtečky se skutečnými čísly z dokumentace skutečného provedení —
   viz [Import čteček z tabulek EKV](import-ctecek-ekv.md).
9. **Grafická schémata** — upload podkladu, editor hotspotů (umístění
   místnosti/čtečky na plán), zobrazení stavu přístupů na plánu.
   ✅ Implementováno (upload PNG/JPEG/SVG per patro v Budovách, editor pozic
   kliknutím do plánu, prohlížeč `/Plans` se zvýrazněním vlastních přístupů;
   obrázky v DB → dostupné z obou HA nodů).
   ✅ Plány se generují na tlačítko z dat — viz [Generování plánů](plany-generovani.md).

### C. Schvalovací workflow
10. **Editor schvalovací matice** — neomezený počet úrovní, na každé úrovni
    jeden/více schvalovatelů, režim „všichni“ / „kterýkoli“ / „N z M“;
    matice je znovupoužitelná pro více čteček. ✅ Implementováno
    (`/Catalog/Matrices`; schvalovatel = uživatel, AD skupiny připraveny
    v modelu).
11. **Zástupy** — delegace schvalování (kdo, za koho, od–do), automatické
    uplatnění v běžících žádostech, auditované. ✅ Implementováno
    (`/Deputies`).
12. **Řetězce čteček** — definice závislostí, automatické rozšíření žádosti
    o vyžadované čtečky (tranzitivní uzávěr), detekce cyklů.
    ✅ Implementováno.
13. **Životní cyklus žádosti** — podání (pro sebe / pro jiného zaměstnance),
    postup po úrovních matice, zamítnutí s povinným důvodem; čtečky bez
    matice jdou rovnou do fronty správce karet; duplicitní žádosti se
    přeskakují. ✅ Implementováno (revokační workflow má připravený model,
    GUI přijde s frontou správce karet).
14. **Notifikace** — e-mail schvalovatelům a žadateli při změně stavu
    (SMTP — viz otázka č. 13). ✅ Implementováno (schvalovatelům při čekající
    úrovni, žadateli při rozhodnutí/zápisu; bez SMTP konfigurace se tiše
    vynechají a nikdy neshodí workflow).

### D. Výstupy
15. **„Moje přístupy“** — přehled pro zaměstnance: jaké přístupy má, co čeká
    na schválení, historie. ✅ Implementováno (`/MyAccess`).
16. **Fronta správce karet** — seznam schváleného k zadání; tlačítko
    **Předat do systému** (volání Connectoru, výsledek se zapíše) nebo
    **Potvrdit ručně** (zadal do WIN-PAK sám); stav synchronizace.
    ✅ Implementováno (`/CardQueue`) včetně revokací („Požádat o odebrání“
    v Moje přístupy prochází stejným workflow a po provedení označí původní
    přístup jako odebraný).
17. **Reporty** — kdo má kam přístup (per čtečka / per člověk), export CSV.
    ✅ Implementováno (`/Reports`).

### E. Provoz a nasazení
18. **Deploy tooling** — instalace .NET na RHEL, systemd unit `acs-web`
    (Kestrel na 0.0.0.0:52000), firewalld, SELinux kontext; spouštěno přes
    SSH z macOS-ai proxyhubu lokálním klíčem. ✅ Implementováno
    (`deploy/install.sh`, `deploy/systemd/`).
19. **Auto-update z Gitu** — timer `acs-updater` na obou nodech: periodicky
    kontroluje `main`, sestaví novou verzi, spustí testy, atomicky přepne
    symlink a restartuje službu; DB migrace běží s distribuovaným zámkem
    (GET_LOCK), náhodný rozptyl timeru brání současné aktualizaci obou nodů.
    ✅ Implementováno (`deploy/bin/acs-updater.sh`).
20. **CI** — build + testy na push/PR (GitHub Actions).
    ✅ Implementováno (`.github/workflows/ci.yml`).

### F. Parkování a parkovací povolení
Přístupy nejsou jen karty ke dveřím — druhým typem oprávnění je parkovací
povolení. Schvalování používá **stejné jádro** jako přístupy (matice, úrovně,
zástupy, řetěz fází, notifikace, připomínky); položka žádosti
`AccessRequestItem` má vedle čtečky a skupiny třetí předmět —
`ParkingPermit`.
21. **Číselníky** — **areály** (`Site`: Motol, Homolka…, volitelně vlastní
    matice) a **druhy povolení** (`ParkingPermitType`: např. „Vedení
    nemocnice“, „Zaměstnanec“, „Dodavatel“; vazba na **SPZ** nebo na
    **funkci**, max. počet SPZ, výchozí platnost, vlastní matice, texty
    kartičky). ✅ Implementováno (`/Catalog/Parking/Sites`,
    `/Catalog/Parking/PermitTypes`).
22. **Žádost a schvalování** — povolení je přidělené konkrétnímu zaměstnanci,
    platí pro jeden, více nebo všechny areály; řetěz fází = matice druhu →
    matice zvolených areálů (bez matice rozhoduje administrátor, nic se
    neschvaluje automaticky). Duplicity na stejný druh se odmítají, SPZ se
    normalizují. ✅ Implementováno (`/Parking/New`,
    `RequestWorkflowService.CreateParkingRequestAsync`; schvaluje se ve
    společném inboxu `/Requests`).
23. **Fronta správce parkování** — nová role `ParkingAdmin`; vydání povolení
    přidělí číslo (`P-RRRR-NNNN`), zapíše SPZ jako identifikátory zaměstnance
    (`EmployeeIdentifier` typu `LicensePlate` s platností povolení — připraveno
    pro online autorizaci vjezdu přes integrační API) a umožní **tisk kartičky
    za čelní sklo** (HTML + tiskové CSS, 150 × 70 mm, podle předlohy FNMH).
    Odebrání: na žádost držitele (jde rovnou do fronty, bez schvalování) nebo
    přímo správcem. ✅ Implementováno (`/Parking/Queue`, `/Parking/Permit`,
    `/Parking/Print`, `ParkingAdminService`).
24. **Automatizace a výstupy** — expirace platnosti a offboarding odebírají
    vydaná povolení a deaktivují SPZ; report „Parkovací povolení“ s CSV;
    přehled „Parkování“ pro zaměstnance. ✅ Implementováno.

---

## 6. Bezpečnost

- HTTP pouze mezi HAProxy a aplikací (interní síť); TLS řeší HAProxy
  (viz otázka č. 10),
- LDAPS pro AD, lokální admin s vynucenou změnou hesla při prvním přihlášení,
- rate-limit na login, uzamčení účtu, kompletní audit,
- tajemství (hesla DB, LDAP bind, API klíč Connectoru): v GUI editovatelná,
  v DB šifrovaná Data Protection klíči; bootstrap connection string k MariaDB
  jako jediný mimo GUI (env soubor `/etc/acs/acs.env`, mode 600),
- WinPak Connector přijímá pouze volání s API klíčem/mTLS z adres app serverů.

---

## 7. Etapy realizace

| Etapa | Obsah | Výstup |
| --- | --- | --- |
| 0 | Schválení tohoto plánu, odpovědi na otázky | zafixované zadání |
| 1 | Skeleton, DB, autentizace (AD + lokální admin), nastavení v GUI, deploy na oba nody + auto-update | přihlásitelná „prázdná“ aplikace v HA na acs.fnmh.network |
| 2 | Číselníky (čtečky, zaměstnanci, budovy/patra/místnosti) + synchronizace, ruční editace | naplněná data |
| 3 | Schvalovací matice, zástupy, řetězce čteček, workflow žádostí, notifikace | funkční schvalování end-to-end |
| 4 | Fronta správce karet, WinPak Connector (zápis), „Moje přístupy“, reporty | uzavřená smyčka do WIN-PAK |
| 5 | Grafická schémata, témata GUI, ladění UX, zátěžové a failover testy | produkční verze 1.0 |
| 6 | Parkovací povolení — areály, druhy povolení, žádost a schvalování, fronta správce parkování, tisk kartičky za sklo (viz kapitola 5 F) | parkování schvalované stejným jádrem jako přístupy |
| 7 | Napojení dalších systémů (vjezdy na SPZ, stravování) přes univerzální integrační API — [podklad do zadávačky](integrace/README.md) | jeden kontrakt, N konektorů místo integrace na míru; vydaná parkovací povolení už SPZ evidují jako identifikátory |

---

## 8. Otevřené otázky (prosím doplnit)

**WIN-PAK**
1. Jakou přesnou verzi a edici WIN-PAK provozujete (4.9.5? SE/PE)? API je
   součástí až SE/PE.
2. Máte (nebo můžete získat) licenci **`SRVWPPAPI`** a podepsanou **NDA
   s Honeywellem** pro přístup k SDK a dokumentaci? Bez toho není oficiální
   API cesta možná.
3. Je ze sítě app serverů (10.84.7.x) dostupný WIN-PAK server a můžeme na
   něj (nebo vedle něj) nasadit Windows službu **WinPak Connector**?
4. Je do doby získání SDK přijatelný **read-only přístup do WIN-PAK MSSQL**
   databáze pro import čteček (zápis by zůstal ruční přes správce karet)?

**Zaměstnanci a AD**
5. Zdroj zaměstnanců: jaký MSSQL server / jaké API? Jaká pole potřebujeme
   (osobní číslo, jméno, oddělení, nadřízený, číslo karty…)?
6. AD: doména/DC, povolen LDAPS (636)? Servisní účet pro bind? Mají všichni
   žadatelé AD účet a jak se páruje na zaměstnance (sAMAccountName, mail)?
7. Mají se role (správce karet, admin…) mapovat na **AD skupiny**, nebo se
   budou přidělovat jen ručně v aplikaci?

**Workflow**
8. Když schvaluje více lidí na jedné úrovni: platí „stačí kterýkoli“, nebo
   „musí všichni“ (případně obojí volitelně per úroveň)?
9. Mají přístupy **expirovat** (např. roční recertifikace) a má workflow
   umět i **odebrání** přístupu?
10. Kdo smí žádat: každý sám za sebe, nebo i nadřízený/personalista za
    jiného zaměstnance?

**Provoz**
11. TLS: terminuje HTTPS HAProxy (aplikace poslouchá čistě HTTP na 52000)?
    Kdo spravuje certifikát pro acs.fnmh.network?
12. RHEL: jaká verze (9/10)? Pod jakým uživatelem se připojíme přes SSH a
    máme `sudo` pro instalaci .NET runtime, systemd unit, firewalld?
13. Notifikace e-mailem: jaký SMTP server je k dispozici?
14. Git: kde bude repozitář (tento GitHub?), je pro auto-update preferováno
    „každý commit v main“, nebo **release tag** (bezpečnější — doporučuji)?

**GUI**
15. Grafická schémata: postačí nahrání půdorysů (SVG/PNG) s klikacími body,
    nebo očekáváte kreslení plánů přímo v aplikaci?
16. Jazyk UI: pouze čeština, nebo i angličtina (vícejazyčnost)?
17. Přibližný počet zaměstnanců, čteček a žádostí/měsíc (dimenzování)?

**Databáze**
18. DB `winpak` na Galeře je určena čistě pro naši aplikaci (tabulky si
    vytvoříme migracemi), nebo v ní už něco je? Pozn.: Galera vyžaduje
    InnoDB a primární klíče — s EF Core migracemi zajistíme.

**Parkování**
19. Napojení na parkovací systém (GreenCenter): vydání povolení je zatím
    ruční krok správce parkování; SPZ se ale už zapisují jako identifikátory
    zaměstnance, takže online autorizace vjezdu přes integrační API na ně
    může rovnou navázat. Má se SPZ do parkovacího systému propisovat
    automaticky při vydání, nebo stačí dotaz u brány?
20. Kartička za sklo: stačí tisk z prohlížeče (HTML, 150 × 70 mm), nebo je
    potřeba PDF s pevnou šablonou / potisk plastových karet? Má na kartičce
    být i jméno držitele u povolení na funkci?
21. Má odebrání povolení na žádost držitele procházet schvalováním, nebo
    stačí, že ho provede správce parkování (současný stav)?

---

## 9. Struktura repozitáře (návrh)

```
/src
  Acs.Web/            ASP.NET Core host + API (bezestavový)
  Acs.Client/         Blazor WebAssembly frontend
  Acs.Domain/         doménový model, workflow engine
  Acs.Infrastructure/ EF Core (Pomelo/MariaDB), LDAP, adaptéry zaměstnanců
  Acs.WinPakConnector/ Windows služba – REST fasáda nad WIN-PAK COM API/MSSQL
/deploy
  ansible/ nebo skripty (systemd unity, instalace, HAProxy poznámky)
  acs-updater/        auto-update služba
/docs
  PLAN.md             tento dokument
  winpak-api/         rozbor WIN-PAK API a mapování na REST konektoru
/tests
```
