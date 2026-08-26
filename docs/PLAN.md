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

Rešerše je v [`docs/winpak-api/README.md`](winpak-api/README.md) včetně
stažených datasheetů. Podstatné pro návrh:

- Nejnovější verze je **WIN-PAK 4.9.5 SP1**; běží jen na Windows nad MSSQL.
- Oficiální **WIN-PAK API (`SRVWPPAPI`)** je klient–server **SDK (C++/COM)**,
  ne REST. Plná dokumentace a SDK jsou **pod NDA s Honeywellem** a API je
  součástí až edic SE/PE.
- API umí přesně to, co potřebujeme: číst čtečky/panely, číst a zapisovat
  držitele karet, karty a **access levels** (tj. přidělení přístupu).

**Důsledek pro architekturu:** naše aplikace na RHEL nemůže WIN-PAK volat
přímo. Navrhuji samostatnou malou komponentu **WinPak Connector** — Windows
službu (.NET) nasazenou vedle WIN-PAK serveru, která:

1. obalí WIN-PAK SDK (po podpisu NDA a získání `SRVWPPAPI`),
2. vystaví **interní REST API** (HTTPS + API klíč / mTLS) pro naši aplikaci,
3. jako **záložní varianta** (než bude SDK k dispozici) umí číst seznam
   čteček a access levels **přímo z WIN-PAK MSSQL databáze** (read-only);
   zápis by v této variantě zůstal na ručním potvrzení správcem karet.

> Otevřená otázka č. 1–4 níže: verze/edice WIN-PAK, stav NDA a licence API,
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
| Backend | ASP.NET Core (.NET 10 LTS), Minimal API + Razor | LTS, běží na RHEL |
| Frontend | **Blazor WebAssembly** (hostovaný v téže aplikaci) | bohaté UI (editor matice, schémata, témata) a server zůstane plně bezestavový; Blazor Server by vyžadoval sticky sessions, což odporuje zadání |
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

Setting           — všechna nastavení aplikace (klíč/hodnota, editace v GUI)
AuditLog          — každá změna číselníků, rozhodnutí, přihlášení, sync
```

---

## 5. Co všechno musíme vyvinout (rozpad na moduly)

### A. Základ aplikace
1. **Skeleton řešení** — ASP.NET Core + Blazor WASM, EF Core migrace,
   healthcheck endpoint (`/health` pro HAProxy), strukturované logování.
2. **Autentizace a autorizace** — AD (LDAPS) login, lokální admin (seed při
   prvním startu), role: `Admin`, `Správce číselníků`, `Schvalovatel`,
   `Správce karet`, `Žadatel/Zaměstnanec`; mapování AD skupin na role v GUI.
3. **Nastavení v GUI** — administrace všech parametrů: připojení WinPak
   Connector, LDAP, zdroj zaměstnanců, SMTP, plány synchronizací, témata;
   citlivé hodnoty šifrované (Data Protection) v DB.
4. **Audit log** + prohlížečka v GUI.

### B. Číselníky a integrace
5. **WinPak Connector** (Windows služba) — REST fasáda nad WIN-PAK SDK;
   fallback read-only nad MSSQL; endpointy: seznam čteček (se všemi
   informacemi), access levels, zápis přístupu držiteli karty.
   ✅ **Implementováno** v [`src/Acs.WinPakConnector`](../src/Acs.WinPakConnector/README.md)
   (režimy Mock / Mssql / Sdk-placeholder, API klíč, integrační testy).
6. **Synchronizace čteček** — plánovaný import z Connectoru, párování na
   existující záznamy, ruční editace a přidávání, označení „ručně vytvořeno /
   importováno“.
7. **Zdroj zaměstnanců** — adaptérové rozhraní `IEmployeeSource`
   s implementacemi `MssqlEmployeeSource` a `ApiEmployeeSource`
   (výběr a konfigurace v GUI — zadání nechává otevřené).
8. **Správa budov / pater / místností** — CRUD + přiřazení čteček.
9. **Grafická schémata** — upload podkladu, editor hotspotů (umístění
   místnosti/čtečky na plán), zobrazení stavu přístupů na plánu.

### C. Schvalovací workflow
10. **Editor schvalovací matice** — stromová struktura bez omezení hloubky,
    na každé úrovni jeden/více schvalovatelů, režim „všichni“ / „kterýkoli“ /
    „N z M“; možnost šablon (stejná matice pro více čteček).
11. **Zástupy** — delegace schvalování (kdo, za koho, od–do), automatické
    uplatnění v běžících žádostech, auditované.
12. **Řetězce čteček** — definice závislostí, automatické rozšíření žádosti
    o vyžadované čtečky, detekce cyklů, vizualizace řetězce.
13. **Životní cyklus žádosti** — podání (pro sebe / pro podřízeného),
    postup po úrovních matice, zamítnutí s důvodem, eskalace/připomínky,
    odebrání přístupu (revokace) stejným workflow.
14. **Notifikace** — e-mail schvalovatelům a žadateli při změně stavu
    (SMTP — viz otázka č. 13).

### D. Výstupy
15. **„Moje přístupy“** — přehled pro zaměstnance: jaké přístupy má, co čeká
    na schválení, historie.
16. **Fronta správce karet** — seznam schváleného k zadání; tlačítko
    **Předat do systému** (volání Connectoru, výsledek se zapíše) nebo
    **Potvrdit ručně** (zadal do WIN-PAK sám); stav synchronizace.
17. **Reporty** — kdo má kam přístup (per místnost / per člověk), export CSV.

### E. Provoz a nasazení
18. **Deploy tooling** — skripty/Ansible playbook: instalace .NET runtime na
    RHEL, systemd unit `acs-web` (Kestrel na 0.0.0.0:52000), firewalld,
    SELinux kontext; spouštěno přes SSH z macOS-ai proxyhubu lokálním klíčem.
19. **Auto-update z Gitu** — služba/timer `acs-updater` na obou nodech:
    periodicky kontroluje `main` (nový release tag / commit), stáhne
    publikovaný artefakt, `dotnet ef` migrace s DB zámkem (spustí jen první
    node), atomický přepnutí symlinku a restart služby; nody se aktualizují
    **postupně** (druhý čeká, až je první zdravý) → bezvýpadková aktualizace.
20. **CI** — build + testy + publish artefaktu na push do `main`
    (GitHub Actions), verze = git tag.

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

---

## 9. Struktura repozitáře (návrh)

```
/src
  Acs.Web/            ASP.NET Core host + API (bezestavový)
  Acs.Client/         Blazor WebAssembly frontend
  Acs.Domain/         doménový model, workflow engine
  Acs.Infrastructure/ EF Core (Pomelo/MariaDB), LDAP, adaptéry zaměstnanců
  Acs.WinPakConnector/ Windows služba – REST fasáda nad WIN-PAK SDK/MSSQL
/deploy
  ansible/ nebo skripty (systemd unity, instalace, HAProxy poznámky)
  acs-updater/        auto-update služba
/docs
  PLAN.md             tento dokument
  winpak-api/         rešerše a datasheety WIN-PAK API
/tests
```
