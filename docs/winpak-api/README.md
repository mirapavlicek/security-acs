# Honeywell WIN-PAK — rešerše API (stav k srpnu 2026)

## Aktuální verze produktu

- Nejnovější řada je **WIN-PAK 4.9**, poslední vydání **4.9.5 SP1** (build 1095.x).
  SP1 přineslo podporu LDAPS a bezpečnostní opravy.
- WIN-PAK běží pouze na Windows (Server 2016/2019, Windows 10 Pro) nad **MS SQL 2019** (Standard/Express).
- Edice: XE (Express), SE (Standard), PE (Professional), CS. **API je zahrnuto až od edice SE/PE** (v XE není).

## Jaké API WIN-PAK nabízí

Oficiální integrační rozhraní je **WIN-PAK API** (obchodní označení `SRVWPPAPI` — „WIN-PAK API Developer Support“, dříve `WPP3API`/`WPP4API`). Skládá se ze dvou částí:

1. **Database API** — CRUD nad záznamy WIN-PAK databáze:
   - karty (aktivace/expirace, PIN, stav karty),
   - držitelé karet (card holders) včetně note fields,
   - časové zóny (time zones), svátky (holiday schedules),
   - **přístupové úrovně (access levels)** — včetně možnosti získat „complete access tree including timezones“,
   - vyhledávání držitelů karet, účty (accounts).
2. **Communication API** — monitoring a ovládání hardwaru:
   - panely (NetAXS-123/4, PRO3200/PRO2200, PRO4200, MPA2, N-1000, NS2+),
   - vstupy, výstupy, **čtečky** (změna režimu, zamknutí/odemknutí dveří),
   - odběr událostí v reálném čase.

## Zásadní zjištění (rizika pro projekt)

- **WIN-PAK nemá veřejné REST/HTTP API.** Oficiální API je klient–server SDK
  (historicky C++/COM, dokumentace uvádí „Visual C++ programming knowledge“),
  které běží proti WIN-PAK serveru na Windows.
- **Přístup k plné dokumentaci a SDK vyžaduje podepsanou NDA s Honeywellem**
  a zakoupení/aktivaci `SRVWPPAPI`. Veřejně jsou k dispozici pouze datasheety
  (uložené v tomto adresáři).
- Webové rozhraní WIN-PAK 4.9 interně používá vlastní webové služby, ty ale
  nejsou oficiálně dokumentované ani podporované pro integrace.
- Praktický důsledek: naše aplikace (Linux, .NET 10) nemůže volat WIN-PAK přímo.
  Bude potřeba **konektor („WinPak Connector“) běžící na Windows** vedle WIN-PAK
  serveru, který obalí SDK (nebo čtení z WIN-PAK MSSQL databáze) a vystaví
  interní REST rozhraní pro naši aplikaci. Detaily viz `../PLAN.md`,
  kapitola „Integrace WIN-PAK“.

## Soubory v tomto adresáři

| Soubor | Obsah | Zdroj |
| --- | --- | --- |
| `winpak-pe-api-datasheet-se4-pe4.pdf` | Datasheet WIN-PAK PE API (SE 4.0 / PE 4.0) — popis Database API a Communication API | files.autospec.com (Honeywell datasheet) |
| `winpak-4.9-datasheet.pdf` | Datasheet WIN-PAK 4.9 — edice, systémové požadavky, `SRVWPPAPI` | commgear.com (Honeywell datasheet) |

Datasheet pro SE3/PE3 na webu Honeywell (prod-edam.honeywell.com) je za
anti-bot ochranou a nešel stáhnout automatizovaně; obsahově se shoduje
s verzí pro SE4/PE4.

## Odkazy

- Produktová stránka: <https://buildings.honeywell.com/us/en/products/by-category/access-control/software/win-pak-integrated-security-software>
- Datasheet WIN-PAK PE API: <http://files.autospec.com/za/honeywell/datasheets/access/wp4api.pdf>
- Datasheet WIN-PAK 4.9: <https://www.commgear.com/Documents/WIN-PAK_4.9.pdf>
