# security-acs

Webová aplikace pro schvalování přístupů k místnostem (`acs.fnmh.network`).

- .NET 10 (C#), Blazor WebAssembly + ASP.NET Core, MariaDB Galera
- HA nasazení (bezestavové) na dvou RHEL serverech za HAProxy, port 52000
- Integrace s Honeywell WIN-PAK (řízení přístupu), přihlašování přes AD

## Komponenty

- [`src/Acs.WinPakConnector`](src/Acs.WinPakConnector/README.md) — Windows
  služba instalovaná na WIN-PAK server; překládá proprietární WIN-PAK API na
  normální REST API (režimy Mock / MSSQL read-only / SDK).

## Dokumentace

- [Návrhový plán a otevřené otázky](docs/PLAN.md)
- [Rešerše WIN-PAK API](docs/winpak-api/README.md)

## Vývoj

```bash
dotnet build          # sestavení
dotnet test           # testy
dotnet run --project src/Acs.WinPakConnector   # konektor v režimu Mock na :52001
```

## Stav

Fáze návrhu + první komponenta (WinPak Connector). Čeká se na odpovědi na
otevřené otázky (kapitola 8 v `docs/PLAN.md`), poté začne etapa 1 — hlavní
ACS aplikace.
