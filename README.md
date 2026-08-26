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
  matice, zástupy, řetězce čteček, žádosti).
- [`src/Acs.Infrastructure`](src/Acs.Infrastructure) — EF Core (MariaDB
  Galera / SQLite pro vývoj), LDAP, šifrovaná nastavení, klient konektoru.
- [`src/Acs.WinPakConnector`](src/Acs.WinPakConnector/README.md) — Windows
  služba instalovaná na WIN-PAK server; překládá proprietární WIN-PAK API na
  normální REST API (režimy Mock / MSSQL read-only / SDK).
- [`deploy/`](deploy/README.md) — instalace na RHEL nody přes SSH, systemd,
  auto-update z Git `main`, ukázka HAProxy.

## Dokumentace

- [Návrhový plán a otevřené otázky](docs/PLAN.md)
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

Hotová etapa 1 (skeleton, autentizace, nastavení v GUI, deploy + auto-update)
a WinPak Connector. Následuje etapa 2 — číselníky a synchronizace.
Otevřené otázky: kapitola 8 v `docs/PLAN.md`.
