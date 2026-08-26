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

Skript na každém nodu: nainstaluje .NET 10 SDK a git, založí účet `acs`,
naklonuje repozitář do `/opt/acs/src`, nahraje `/etc/acs/acs.env` (600),
zaregistruje systemd služby, otevře port 52000 (firewalld + SELinux),
provede první build a spustí aplikaci.

Pozn.: pokud je repozitář privátní, nastavte na nodech přístup ke čtení
(deploy key / `git config credential…`) — updater potřebuje `git fetch`.

## Po instalaci

1. HAProxy: přidejte backend dle `haproxy.cfg.example`.
2. Otevřete `http://acs.fnmh.network`, přihlaste se `admin` / `admin`
   — aplikace vynutí změnu hesla.
3. V **Nastavení** (GUI) nakonfigurujte Active Directory (LDAPS),
   WIN-PAK konektor (adresa + API klíč) a zdroj zaměstnanců.

## Užitečné příkazy na nodech

```bash
systemctl status acs-web             # stav aplikace
journalctl -u acs-web -f             # logy
systemctl start acs-updater.service  # ruční vynucení aktualizace
ls -l /opt/acs/current               # jaká verze běží (symlink na sha)
curl http://127.0.0.1:52000/health   # healthcheck
```

## WinPak Connector

Instalace na WIN-PAK (Windows) server je popsaná v
[`src/Acs.WinPakConnector/README.md`](../src/Acs.WinPakConnector/README.md).
