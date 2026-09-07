# Napojení parkovacího systému GreenCenter

Jak ACS spolupracuje s parkovacím systémem (vjezdy na SPZ, závory, parkoviště) a co je
k napojení potřeba od dodavatele. Obecný kontrakt je v [README.md](README.md); tento
dokument popisuje, co z něj je v ACS **hotové** a jak se to zapojí konkrétně pro
GreenCenter.

## 1. Co GreenCenter je a co má k dispozici

GREEN Center s.r.o. je český výrobce parkovacích systémů (terminály GP4/GP6, kamerové
čtení značek GPP LPR / GPSW Falcon, závory). Pro integraci třetích stran nabízí:

| Rozhraní | K čemu | Poznámka |
|---|---|---|
| **API parkovacího systému 5.6.1xx** (GPSW Kernel) | správa dlouhodobých karet/abonentů, SPZ, whitelisty, tarifní skupiny, stav vjezdů | REST/JSON, dokumentace ve formátu OpenAPI. Jen varianta *Variant* (s centrálním serverem); varianta *Economy* API nemá. |
| **LPRServer (GP6 / Falcon)** | události z kamer (DETECTED / UPDATED / LEFT) přes REST, TCP a SSE | Realtime proud, vhodný pro hlášení průjezdů. |
| **PMC** | cloudová správa více parkovišť, mobilní platby | API pro platby, ne pro autorizaci. |

Dokumentace (<https://devsrv.green.cz/api-doc/>) je **přístupná až po registraci**
u výrobce (support@green.cz), včetně demoserveru `kernel56.greencenter.site`. Bez ní
nelze naprogramovat konkrétní adaptér — proto je strana ACS připravená tak, aby
fungovala pro kterýkoli ze tří vzorů napojení a adaptér se dopsal až podle skutečné
dokumentace.

**Co je třeba zjistit u dodavatele / provozovatele parkoviště:**

1. Verzi systému (5.4.x / 5.6.x / 5.6.1xx / GP6) a zda jde o variantu s centrálním
   serverem.
2. Zda umí systém u vjezdu **volat externí autorizaci** (HTTP dotaz „smí tato SPZ
   dovnitř?“) — pak stačí vzor C, který ACS už poskytuje.
3. Pokud ne: zda umí **přijímat whitelist** SPZ s platností (vzor A přes konektor),
   nebo si ho **stahovat** sám (vzor B, čtecí endpointy ACS).
4. Identifikátory vjezdů / parkovišť (čísla terminálů, `gmtpId`, názvy) — zapíší se
   k areálům v ACS.
5. Zda systém drží platnost SPZ (od–do). Pokud ne, ACS musí odebírat po expiraci sám
   (konektor to hlásí v `capabilities.supportsValidity`).

## 2. Co je v ACS hotové

### 2.1 Datový základ (bez integrace)

- Právo na vjezd vzniká **vydaným parkovacím povolením** (druh povolení, areály,
  platnost, schvalování). Při vydání se SPZ zapíší jako identifikátory zaměstnance
  (`EmployeeIdentifier`, typ `LicensePlate`) s platností povolení; při odebrání se
  zneplatní.
- **Areál** (Číselníky → Areály) má pole *Vjezdy v parkovacím systému*
  (`GateExternalIds`) — identifikátory vjezdů / parkoviště tak, jak je posílá
  parkovací systém. Areál se pozná i podle kódu (`MOT`) nebo `site:MOT`.
- Zaměstnanec vidí v **Moje přístupy** své identifikátory (karty, SPZ), vydaná povolení
  s areály (= vjezdy), platností a kartičkou PDF a poslední průjezdy bránou.

### 2.2 Integrační API ACS (vzory B a C)

Adresa: `https://<acs>/api/integration/v1`, hlavička `X-Api-Key`. Zapíná se
v Nastavení → **Parkovací systém (GreenCenter)**. Bez zapnutí nebo bez klíče vrací
`503` (fail-closed); lze omezit i zdrojové IP adresy.

| Metoda | Cesta | Účel |
|---|---|---|
| `POST` | `/authorization-checks` | **Online autorizace u brány** — systém pošle `accessPointId` (id vjezdu), `credential` (`licensePlate` / `card`, hodnota), `direction` (`in`/`out`), volitelně `occurredAt`, `confidence`. ACS odpoví `allow`/`deny` s důvodem, jménem držitele, platností a `cacheTtlSeconds`. |
| `POST` | `/events` | Hlášení průjezdů a zamítnutí (`vehicleIn`, `vehicleOut`, `denied`, `passed`, …). Dávka do 500, duplicitní `id` se tiše přijme. |
| `GET` | `/persons`, `/persons/{id}`, `/persons/{id}/credentials` | Osoby s parkovacím povolením nebo SPZ a jejich SPZ / karty (jen to, co parkoviště potřebuje). |
| `GET` | `/entitlements` | Vydaná povolení jako oprávnění: jedno na povolení a areál (cíl `site:MOT`, u všech areálů `site:*`), stav `active` / `expired` / `revoked`, platnost, SPZ. Filtry `personId`, `status`, `changedSince`, stránkování `cursor`/`limit`. |
| `GET` | `/access-points` | Areály jako přístupové body druhu `gate` s id vjezdů v `externalIds`. |

Rozhodování u brány (`GateAuthorizationService`):

1. `accessPointId` → areál (podle vjezdů, kódu, `site:KÓD`); neznámý → `accessPointUnknown`.
2. SPZ se normalizuje jako v ACS (velká písmena, bez mezer a pomlček: `1AB 23-45` → `1AB2345`).
   Přibližné porovnání (`0`/`O`) se **nedělá** — pustilo by cizí vozidlo; řešení je na
   straně kamery / obsluhy a osoba může mít v ACS více SPZ.
3. Identifikátor musí existovat a platit (`credentialUnknown` / `credentialExpired`),
   zaměstnanec musí být aktivní (`personEnded`).
4. Musí existovat vydané povolení pro daný areál (`noEntitlement`), které právě platí
   (`entitlementExpired`). Povolení vázané na SPZ platí jen pro své značky; povolení na
   funkci a průjezd na kartu platí pro osobu.
5. **Výjezd** (`direction: out`) se ve výchozím nastavení povolí vždy — vozidlo nesmí
   zůstat zavřené v areálu; původní důvod zůstane v záznamu (`exitOverride`). Vypnout
   lze v Nastavení.

Každé rozhodnutí i každá nahlášená událost se ukládá (`IntegrationEvent`) a je
k dohledání ve **Správa → Parkovací systém — události** (filtr podle SPZ, areálu, jen
zamítnutí, trace id) a zaměstnanci v Moje přístupy.

Příklad:

```http
POST /api/integration/v1/authorization-checks
X-Api-Key: …
Content-Type: application/json

{ "accessPointId": "GATE-N", "credential": { "type": "licensePlate", "value": "1AB 2345" },
  "direction": "in", "confidence": 0.97 }
```

```json
{ "decision": "allow", "reason": "allowed", "personId": "42", "personName": "Jan Novák",
  "entitlementId": "permit-17", "validTo": "2027-03-01T00:00:00Z", "cacheTtlSeconds": 300,
  "decidedAt": "2026-09-07T15:40:12Z", "traceId": "6f1c…" }
```

Doporučení pro stranu parkovacího systému (viz README, kapitola 4.1): timeout dotazu
1 s, při nedostupnosti ACS **vjezd zamítnout, výjezd povolit**, rozhodnutí kešovat
nejdéle `cacheTtlSeconds`, zamítnutí hlásit jako událost s důvodem.

### 2.3 Konektor (vzor A — ACS zapisuje do parkovacího systému)

Pro systém, který chce držet vlastní whitelist a nevolat ACS u každé závory. ACS volá
konektor podle [connector-api.yaml](connector-api.yaml) — malou službu, která překládá
volání na API GreenCenter (GPSW Kernel) a běží u parkovacího systému (stejně jako
`Acs.WinPakConnector` u WIN-PAK):

- `GET /capabilities` — co konektor umí (test spojení v Nastavení),
- `PUT /persons/{id}` — osoba (osobní číslo, jméno, oddělení, stav),
- `PUT /persons/{id}/credentials` — **celý** platný seznam SPZ a karet,
- `PUT /persons/{id}/entitlements` — **celý** seznam platných povolení (cíl = areál
  `site:MOT`, `site:*` pro všechny; konektor mapuje na parkoviště / tarifní skupinu).

ACS volá konektor po **vydání** a **odebrání** povolení (i automatickém při expiraci
nebo odchodu zaměstnance). Chyba vydání v ACS nezablokuje — zapíše se k povolení a
správce ji vidí v detailu povolení s tlačítkem *Předat znovu*. Id osoby vrácené
konektorem se ukládá (`Employee.ParkingSystemId`).

Zapnutí: Nastavení → Parkovací systém → *Adresa konektoru*, *API klíč konektoru*,
*Předávat automaticky*.

## 3. Postup napojení

1. **Registrace u Green Center** — přístup k dokumentaci API 5.6.1xx a demoserveru
   (support@green.cz, uvést projekt: „napojení přístupového systému nemocnice na vjezdy“).
2. **Rozhodnout vzor** podle odpovědí v kapitole 1. Pořadí preference:
   - **C** (online dotaz) — nejmenší práce, žádná duplicitní evidence, okamžitá platnost
     odebrání. Podmínka: systém umí volat externí autorizaci nebo to dodavatel doplní
     (malý modul u LPRServeru: kamera → dotaz na ACS → závora).
   - **A** (konektor) — když systém potřebuje vlastní whitelist. Konektor se napíše
     proti GPSW API podle dokumentace; ACS strana je hotová.
   - **B** (odběr) — když si systém umí stahovat data sám; ACS endpointy jsou hotové.
3. **Nastavení v ACS**: zapnout integrační API, vygenerovat klíč, omezit IP, doplnit id
   vjezdů k areálům, případně adresu konektoru.
4. **Zkušební provoz** na jednom vjezdu: sledovat *Parkovací systém — události*
   (zamítnutí s důvodem `accessPointUnknown` = chybí párování vjezdu, `credentialUnknown`
   = špatně přečtená značka nebo chybějící povolení).
5. **Přejímka** podle kritérií v README, kapitola 7; po etapách (nejdřív události a
   autorizace, potom zápis).

## 4. Nastavení a klíče

| Klíč | Význam |
|---|---|
| `ParkingSystem:Enabled` | zapnutí integračního API |
| `ParkingSystem:ApiKey` (tajný) | klíč, kterým se parkovací systém prokazuje ACS |
| `ParkingSystem:AllowedIps` | povolené IP / sítě volajícího (prázdné = bez omezení) |
| `ParkingSystem:CacheTtlSeconds` | doporučená doba kešování povolení (výchozí 300) |
| `ParkingSystem:ExitAlwaysAllowed` | výjezd povolit vždy (výchozí ano) |
| `ParkingSystem:ConnectorBaseUrl`, `ParkingSystem:ConnectorApiKey` (tajný) | konektor (vzor A) |
| `ParkingSystem:PushEnabled` | předávat povolení konektoru automaticky |

## 5. Co zbývá dodělat po získání dokumentace

- Adaptér GPSW ↔ `connector-api.yaml` (pokud padne volba na vzor A), včetně mapování
  areálů na parkoviště / tarifní skupiny GreenCenter.
- Případný modul u LPRServeru, který překládá událost kamery na `POST /authorization-checks`
  a výsledek na povel závoře (pokud to systém neumí sám).
- Retence událostí (kolik měsíců průjezdů držet) — zatím se nemažou.
- Návštěvy a externisté bez záznamu v personálním systému (nový zdroj osob, viz README 9.4).
