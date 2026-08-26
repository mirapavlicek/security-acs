# Bezpečnost ACS

Shrnutí bezpečnostního review a zavedených opatření.

## Provedený test

- **Bezpečnostní review kódu** (aplikace i WinPak Connector) — 6 nálezů
  (1 High, 5 Medium), všechny ošetřeny (viz níže).
- **Sken zranitelných závislostí** — `dotnet list package --vulnerable
  --include-transitive`: **0 zranitelných balíčků** ve všech projektech.
- **Lint nasazovacích skriptů** — `shellcheck` bez nálezů.
- **Runtime ověření** — bezpečnostní hlavičky, rate-limit (429 po překročení),
  náhodné počáteční heslo administrátora.

## Zavedená opatření

| # | Nález | Závažnost | Řešení |
| --- | --- | --- | --- |
| 1 | Žádost bylo možné podat za kohokoli; čtečky bez matice se schvalovaly samy | High | `CreateRequestAsync` vyžaduje oprávnění „za jiného" (Admin/CardAdmin/CatalogManager), jinak jen sám za sebe. Čtečka bez matice se **neschvaluje automaticky** — vyžaduje rozhodnutí administrátora. |
| 2 | IDOR — detail cizí žádosti | Medium | Detail vidí jen žadatel, cílový zaměstnanec, aktuální schvalovatel, správce karet nebo admin. |
| 3 | Důvěra ve WIN-PAK konektor při zpětné synchronizaci | Medium | Doporučení mTLS / síťové ACL / rotace klíče v `src/Acs.WinPakConnector/README.md`; konektor je fail-closed a přístupný jen app serverům. |
| 4 | Auto-update nasazoval každý commit v `main` | Medium | Výchozí režim `ACS_UPDATE_MODE=tag` nasazuje jen release tagy `vX.Y.Z`; `branch` je volitelný pro rychlé iterace. |
| 5 | Bootstrap `admin`/`admin` | Medium | Počáteční heslo je buď zadané operátorem (`ACS_BOOTSTRAP_ADMIN_PASSWORD` v `acs.env`), nebo **náhodné** (20 znaků) jednorázově vypsané do logu (`journalctl -u acs-web`); v obou případech je při prvním přihlášení vynucena změna. |
| 6 | Cookies bez `Secure` za TLS-terminující HAProxy | Medium | `ForwardedHeaders` (čtení `X-Forwarded-Proto/For`), auth cookie `SecurePolicy=Always` v produkci, `HttpOnly`, `SameSite=Lax`; HSTS v produkci. |

## Další zavedená hardening opatření

- **Rate-limit přihlašování** — 10 pokusů / minutu / IP (fixed window),
  po překročení HTTP 429. Klientská IP se zjišťuje z `X-Forwarded-For`
  (za HAProxy). Pozn.: limit je per-node (obrana do hloubky).
- **Bezpečnostní hlavičky** na všech odpovědích — `Content-Security-Policy`,
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Referrer-Policy: same-origin`.
- **Hesla** — PBKDF2-SHA256, 210 000 iterací, náhodná sůl, porovnání
  v konstantním čase.
- **Tajemství** — šifrovaná (ASP.NET Data Protection, klíče v DB) — LDAP bind
  heslo, API klíč konektoru, MSSQL connection string, SMTP heslo.
- **LDAP** — hodnoty ve filtru se escapují (prevence LDAP injection).
- **Konektor** — ochrana API klíčem (fail-closed, konstantní čas porovnání).
- **Audit** — přihlášení, změny číselníků, rozhodnutí, synchronizace.

## Provozní doporučení

- HTTPS terminovat na HAProxy; port 52000 na app serverech otevřít firewallem
  jen pro HAProxy (ne veřejně).
- WIN-PAK konektor provozovat za TLS a přístupný jen z 10.84.7.146/147.
- Používat release tagy (`git tag vX.Y.Z`) pro řízené nasazení do produkce.
- Pravidelně kontrolovat `dotnet list package --vulnerable` (lze doplnit do CI).
