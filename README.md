# security-acs

Webová aplikace pro schvalování přístupů k místnostem (`acs.fnmh.network`).

- .NET 10 (C#), Blazor WebAssembly + ASP.NET Core, MariaDB Galera
- HA nasazení (bezestavové) na dvou RHEL serverech za HAProxy, port 52000
- Integrace s Honeywell WIN-PAK (řízení přístupu), přihlašování přes AD

## Komponenty

- [`src/Acs.Web`](src/Acs.Web) — hlavní webová aplikace (Razor Pages):
  přihlašování AD + lokální admin, nastavení v GUI, barevná témata, správa
  uživatelů a rolí, audit, healthcheck pro HAProxy.
- [`src/Acs.Domain`](src/Acs.Domain) — doménový model (číselníky, schvalovací
  matice, zástupy, řetězce čteček, žádosti, parkovací povolení).
- [`src/Acs.Infrastructure`](src/Acs.Infrastructure) — EF Core (MariaDB
  Galera / SQLite pro vývoj), LDAP, šifrovaná nastavení, klient konektoru.
- [`src/Acs.WinPakConnector`](src/Acs.WinPakConnector/README.md) — Windows
  služba instalovaná na WIN-PAK server; překládá proprietární WIN-PAK API na
  normální REST API (režimy Mock / MSSQL read-only / SDK).
- [`deploy/`](deploy/README.md) — instalace na RHEL nody přes SSH, systemd,
  auto-update z Git `main`, ukázka HAProxy.

## Dokumentace

- [Návrhový plán a otevřené otázky](docs/PLAN.md)
- [Generování plánů pater](docs/plany-generovani.md)
- [Osobní čísla z Active Directory](docs/ad-osobni-cisla.md)
- [Univerzální integrační API (podklad do zadávačky)](docs/integrace/README.md)
- [Import čteček z tabulek EKV](docs/import-ctecek-ekv.md)
- [Přístupové úrovně WIN-PAKu z ACS](docs/pristupove-urovne.md)
- [Bezpečnost — review a opatření](docs/SECURITY.md)
- [Rešerše WIN-PAK API](docs/winpak-api/README.md)

## Vývoj

```bash
dotnet build          # sestavení
dotnet test           # testy
dotnet run --project src/Acs.Web              # hlavní aplikace na :52000 (SQLite bez konfigurace)
dotnet run --project src/Acs.WinPakConnector  # konektor v režimu Mock na :52001
```

První přihlášení: `admin` / `admin` (aplikace vynutí změnu hesla).

## Stav

Hotové: etapy 1–4 a grafická schémata — skeleton s autentizací (AD +
lokální admin), nastavení v GUI, barevná témata, deploy s auto-update
z Git `main`, WinPak Connector, číselníky se synchronizací, schvalovací
matice s neomezenou hloubkou, zástupy, žádosti s automatickým doplněním
řetězce čteček, moje přístupy (včetně žádosti o odebrání), fronta správce
karet s předáním do WIN-PAK přes API a plány pater s vyznačením čteček
a vlastních přístupů.
Dále hotovo: e-mailové notifikace (SMTP), reporty kdo-má-kam s CSV exportem,
mapování AD skupin na role a **obousměrná synchronizace s WIN-PAK** (změny
provedené přímo ve WIN-PAK — udělení, odebrání, zadání z fronty — se
automaticky propíší zpět do ACS).

Od v1.1: **skupiny čteček** (i vnořené) — žádat lze o skupinu jako celek,
skupina má vlastní matici a schvalování prochází **řetězem matic** (skupina →
nadřazené skupiny, např. Chirurgie → Bezpečnost); **zaměstnanci se načítají
z AD** a **karty z SQL**; **automatické zařazení dle oddělení** (nástup na
chirurgii → předschválený základní přístup skupiny Chirurgie).

Dále: **parkovací povolení** — druhý typ oprávnění vedle karet. Číselníky
areálů (Motol, Homolka…) a druhů povolení (např. „Vedení nemocnice“,
„Zaměstnanec“; vazba na **SPZ** nebo na **funkci**), povolení pro jeden,
více nebo všechny areály přidělené konkrétnímu zaměstnanci, schvalování
stejným jádrem (matice druhu → matice areálů, zástupy, notifikace), fronta
**správce parkování** (nová role) s vydáním čísla, zápisem SPZ mezi
identifikátory zaměstnance a **kartičkou za čelní sklo jako PDF** (i hromadně);
reporty přístupů a povolení se exportují do **PDF** a CSV; expirace a
offboarding povolení odebírají automaticky. PDF se generuje na serveru
(PDFsharp, MIT) — potřebuje TrueType písmo s diakritikou (`dejavu-sans-fonts`,
instaluje deploy skript; jinak `ACS_PDF_FONT_DIR`).
Zbývá: nasazení na cílové servery (skripty připraveny, spouští se z macOS-ai
proxyhubu) a napojení konektoru na reálný WIN-PAK (MSSQL přístup / licence
SDK — viz otevřené otázky, kapitola 8 v `docs/PLAN.md`).
