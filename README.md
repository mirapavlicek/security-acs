# security-acs

Webová aplikace pro schvalování přístupů k místnostem (`acs.fnmh.network`).

- .NET 10 (C#), Blazor WebAssembly + ASP.NET Core, MariaDB Galera
- HA nasazení (bezestavové) na dvou RHEL serverech za HAProxy, port 52000
- Integrace s Honeywell WIN-PAK (řízení přístupu), přihlašování přes AD

## Dokumentace

- [Návrhový plán a otevřené otázky](docs/PLAN.md)
- [Rešerše WIN-PAK API](docs/winpak-api/README.md)

## Stav

Fáze návrhu — čeká se na schválení plánu a odpovědi na otevřené otázky
(kapitola 8 v `docs/PLAN.md`). Vývoj začne po zafixování zadání.
