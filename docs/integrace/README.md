# Univerzální integrační API ACS

Podklad do zadávací dokumentace. Popisuje, jak se na ACS napojují další systémy —
vjezdové a parkovací (typu GreenCenter), systém pro stravování, docházka,
rezervace — a co k tomu musí dodat jejich dodavatel.

Strojově čitelné kontrakty jsou přílohou:

| Soubor | Kdo to implementuje | K čemu |
|---|---|---|
| [`acs-integration-api.yaml`](acs-integration-api.yaml) | ACS (my) | Navázaný systém si čte osoby, skupiny a oprávnění, hlásí události, ptá se na rozhodnutí. |
| [`connector-api.yaml`](connector-api.yaml) | **dodavatel navázaného systému** | ACS do systému zapisuje osoby a oprávnění. |

---

## 1. Proč univerzální API

Dnes je každá integrace napsaná zvlášť: konektor k WIN-PAK, adaptér na
personální systém přes MSSQL, adaptér na REST zdroj zaměstnanců. Každý další
systém by tímto tempem znamenal další kus kódu v jádru ACS a další věc, která se
rozbije při aktualizaci.

Návrh to obrací: **jeden kontrakt, N konektorů**. ACS umí mluvit jednou řečí,
a co je za ní — WIN-PAK, jídelna, parkovací systém — řeší tenká převodní vrstva,
která tu řeč splňuje. Přidání systému pak není zásah do jádra, ale konfigurace
a jeden konektor.

Ten vzor už v projektu funguje: `src/Acs.WinPakConnector` obaluje proprietární
COM API Honeywellu do REST a ACS s ním mluví přes HTTP. Tenhle dokument z toho
dělá obecné pravidlo.

### Kdo je zdrojem pravdy

**ACS pro identitu a oprávnění.** Ne proto, že by chtěl vlastnit data, ale proto,
že v něm probíhá schvalování: kdo o přístup požádal, kdo ho schválil, do kdy
platí a kdy se má odebrat. Bez toho nejde dohledat, na základě čeho někdo
někam smí.

Navázané systémy jsou zdrojem pravdy pro **svá zařízení a svou provozní realitu**
— jídelna pro ceny a odebrané obědy, parkovací systém pro obsazenost, WIN-PAK pro
stav dveří.

```
   personální systém / AD                          ACS
   (zaměstnanci, osobní čísla)  ──────►   ┌────────────────────────┐
                                          │ osoby, identifikátory  │
                                          │ skupiny, oprávnění     │
                                          │ SCHVALOVÁNÍ + AUDIT    │
                                          └───────┬────────┬───────┘
                                                  │        │
                             (A) zápis konektorem │        │ (B) odběr / (C) online dotaz
                                                  ▼        ▼
                            ┌──────────────┬──────────────┬──────────────┐
                            │   WIN-PAK    │  stravování  │  vjezd/SPZ   │
                            └──────────────┴──────────────┴──────────────┘
                                     └── události (průchody, obědy, vjezdy) ──┐
                                                                              ▼
                                                                        audit ACS
```

---

## 2. Tři interakční vzory

Systémy se od sebe liší v tom, jestli si drží vlastní evidenci a jestli umí
volat cizí API. Kontrakt proto nabízí tři vzory a každý systém si vybere podle
svých možností. Nejde o alternativy „lepší/horší“, ale o různé situace.

| | **A — zápis konektorem** | **B — odběr** | **C — online autorizace** |
|---|---|---|---|
| Kdo koho volá | ACS volá konektor | systém volá ACS | systém volá ACS |
| Kdy | při schválení, změně, odebrání | pravidelně nebo na webhook | v okamžiku průchodu/vjezdu |
| Systém drží vlastní evidenci | ano | ano | ne (jen krátkodobá keš) |
| Funguje při nedostupnosti ACS | ano | ano | **ne** — nutná záložní strategie |
| Vhodné pro | WIN-PAK, stravování | systémy s vlastní synchronizací | vjezd na SPZ, návštěvy |
| Co dodavatel implementuje | `connector-api.yaml` | klienta k `acs-integration-api.yaml` | klienta k `POST /authorization-checks` |

Vzory se kombinují. Typicky: **A** pro provisioning osob a oprávnění + hlášení
**událostí** zpět do ACS. Parkovací systém může mít **C** pro rozhodnutí u brány
a **B** pro noční dorovnání stavu.

### Kdy který zvolit

- Systém má vlastní databázi držitelů a chce fungovat i při odstávce ACS → **A**.
- Systém má vlastní synchronizační mechanismus a nechce cizí zápisy → **B**.
- Systém nemá kde držet oprávnění, nebo se rozhoduje podle okamžitého stavu
  (platnost do minuty, jednorázová návštěva) → **C**.

---

## 3. Datový model

Model je záměrně malý. Univerzálnost nedělá množství polí, ale to, že se stejný
objekt použije pro různé účely — skupina strávníků a skupina čteček je tentýž
objekt s jiným druhem.

| Objekt | Co to je | Klíčové vlastnosti |
|---|---|---|
| **Osoba** (`Person`) | Zaměstnanec, externista, návštěva. | `id` (neměnné), `personalNumber`, jméno, oddělení, `status`, platnost, `externalIds` |
| **Identifikátor** (`Credential`) | Čím se osoba prokazuje. | `type` (karta, **SPZ**, PIN, čip, biometrie, stravovací konto), normalizovaná `value`, platnost |
| **Skupina** (`Group`) | Pojmenovaná množina s druhem. | `kind` (`readers`, **`diners`**, `parking`, `zone`), `parentGroupId`, `attributes` |
| **Přístupový bod** (`AccessPoint`) | Kde se osoba prokazuje. | `kind` (dveře, **brána**, závora, turniket, **výdejní terminál**), umístění, `externalIds` |
| **Oprávnění** (`Entitlement`) | Osoba × cíl × platnost. | cíl = přístupový bod nebo skupina, `status`, platnost, `source` (z jaké žádosti) |
| **Událost** (`Event`) | Co se stalo. | průchod, **zamítnutí**, vjezd, výjezd, **odebraný oběd** |
| **Změna** (`Change`) | Záznam v proudu změn. | pro přírůstkový odběr a webhooky |

### Provazba na stávající model ACS

| Objekt API | Dnes v ACS | Poznámka |
|---|---|---|
| Osoba | `Employee` | Beze změny. |
| Identifikátor | `EmployeeIdentifier` + `IdentifierType` | SPZ (`LicensePlate`) i normalizace už existují. Chybí typ pro stravovací konto. |
| Skupina druhu `readers` | `ReaderGroup` + `ReaderGroupMember` | Včetně vnořování a expanze. |
| Skupina druhu `diners`, `parking` | **chybí** | Potřeba zobecnit skupinu na druh + atributy. |
| Přístupový bod | `Reader` | Dnes jen dveřní čtečka; potřeba druh a brány/terminály. |
| Oprávnění | `AccessRequestItem` ve stavu `PushedToWinPak` / `ManuallyConfirmed` | Oprávnění je dnes odvozené ze žádosti; pro API se hodí samostatný pohled. |
| Událost | **chybí** | Dnes se do ACS události nehlásí. |
| Cílový systém | `SettingKeys` `WinPak:*` | Potřeba evidence více integrací místo jedné sady klíčů. |

### Terminologie

Cesty a názvy polí v API jsou anglicky (jako zbytek kódu), texty pro člověka
česky. Pro zadávačku platí tento slovník:

| API | Česky |
|---|---|
| person | osoba (zaměstnanec) |
| credential | identifikátor |
| card / licensePlate / pin | karta / SPZ / PIN |
| group, kind=diners | skupina, skupina strávníků |
| accessPoint | přístupový bod (čtečka, brána, terminál) |
| entitlement | oprávnění |
| event | událost |
| capabilities | přiznané možnosti |
| authorization check | online autorizace |

---

## 4. Scénáře

### 4.1 Vjezd do areálu na SPZ

Kamera přečte značku, systém se rozhodne, závora se zvedne nebo ne.

```
kamera ──► parkovací systém ──► POST /authorization-checks ──► ACS
                                {accessPointId, credential:{licensePlate,"1AB2345"}, direction:in}
           závora ◄── {decision:"allow", personName:"M. Pavlíček", cacheTtlSeconds:300}
                  └──► POST /events  {type:"vehicleIn"}  ──► audit ACS
```

Co musí být v zadání ošetřené, protože jinak to v provozu selže:

- **Normalizace značky.** ACS ukládá SPZ velkými písmeny bez mezer a pomlček
  (`1AB 2345` → `1AB2345`). Systém musí normalizovat stejně, jinak se hodnoty
  nikdy nesejdou. Platí i pro cizí značky.
- **Chybné čtení kamerou.** Záměna `0`/`O` a `1`/`I` je běžná. ACS nedělá
  přibližné porovnání záměrně — jinak by pustil cizí vozidlo. Řešení je na straně
  systému (více pokusů, potvrzení obsluhou) a osoba může mít v ACS více SPZ.
  Spolehlivost čtení se posílá v `confidence` a ukládá k události, aby se sporné
  případy daly dohledat.
- **Chování při nedostupnosti ACS.** Musí být nastavitelné a pro každý směr
  jiné: **vjezd zamítnout** (fail-closed), **výjezd povolit** (fail-open) — vozidlo
  nesmí zůstat zavřené v areálu. Doporučený timeout dotazu je 1 s, keš podle
  `cacheTtlSeconds`.
- **Zamítnutí se hlásí jako událost** s důvodem. Bez toho není dohledatelné, proč
  brána někoho nepustila, a každá stížnost je neřešitelná.
- **Kdo o vjezd žádá.** Vjezd je oprávnění jako každé jiné — vzniká schválenou
  žádostí v ACS, s platností a s tím, kdo ho schválil. To je proti dnešnímu
  stavu, kdy jsou SPZ v ACS jen evidované.

### 4.2 Systém pro stravování se skupinami strávníků

Skupina strávníků je skupina druhu `diners`. Cenová hladina, výše příspěvku
a podobné údaje jsou v `attributes`, protože jsou vlastní jen tomuto druhu
a nemají zatěžovat obecný model.

```json
{
  "id": "grp_diners_zam",
  "kind": "diners",
  "name": "Strávníci — zaměstnanecká cena",
  "attributes": { "priceList": "ZAM", "subsidyCzk": "35" }
}
```

Průběh:

1. Správce v ACS spáruje skupiny ACS s cenovými hladinami jídelny — vybere je ze
   seznamu z `GET /groups` konektoru, nepřepisuje kódy z hlavy.
2. Zařazení do skupiny vzniká schválenou žádostí nebo automatickým zařazením
   podle oddělení (v ACS už existuje `AutoAssignmentRule`).
3. ACS pošle konektoru osobu (`PUT /persons/{id}`), její kartu
   (`PUT /persons/{id}/credentials`) a zařazení (`PUT /persons/{id}/entitlements`
   s `groupId`).
4. Jídelna hlásí odebraná jídla jako události `mealTaken` s kódem jídla
   a hladinou v `details`.

Na co si dát pozor v zadání:

- **Ukončení zaměstnance musí dojít až do jídelny.** ACS má offboarding
  automatiku; kontrakt proto posílá `status: ended` a systém musí zneplatnit
  konto, ne ho smazat — historie odběrů musí zůstat.
- **Změna oddělení mění cenovou hladinu.** Vyplývá z automatického zařazení;
  systém dostane změnu skupiny jako běžnou aktualizaci oprávnění.
- **Jídelna nemá vidět přístupy ke dveřím.** Řeší se rozsahem integrace
  (kapitola 5) — dostane `persons.read` a svoje skupiny, nic víc.
- **Kdo účtuje.** ACS neřeší ceny, platby ani zůstatky. Je zdrojem toho, *kdo* má
  na jakou hladinu právo; kolik to stojí, je věc jídelny.

### 4.3 Obecný systém (typu GreenCenter)

Pro systém, o kterém dopředu nevíme, co umí, je klíčové **přiznání možností**:
konektor v `GET /capabilities` vyjmenuje operace, které zvládne, a zbytek odmítá
stavem `501`. ACS podle toho volá jen to, co má smysl.

Praktický důsledek pro zadávačku: **integrace se dá dodat po částech a přejímat
po částech.** První etapa může být jen čtení a párování přístupových bodů, druhá
zápis osob, třetí oprávnění, čtvrtá události. Každá etapa je samostatně
akceptovatelná podle kritérií v kapitole 7.

> **K doplnění zadavatelem:** u GreenCenter potřebujeme vědět, co to v areálu
> obsluhuje (vjezdy a parkování, technologie budov, něco dalšího) a jestli má
> vlastní evidenci osob. Podle toho se vybere vzor A, B, nebo C. Do té doby je
> návrh držený tak, aby vyhověl každé z těch možností.

---

## 5. Bezpečnost a osobní údaje

| Požadavek | Jak |
|---|---|
| Šifrovaný přenos | TLS 1.2+ povinně, i ve vnitřní síti. Bez TLS se integrace nezprovozní. |
| Přihlášení systému k ACS | OAuth2 client credentials (doporučeno), API klíč jen pro systémy bez OAuth2. |
| Přihlášení ACS ke konektoru | API klíč v `X-Api-Key` + omezení na IP adresy ACS nodů. Konektor bez nastaveného klíče musí volání odmítat — nenakonfigurovaný konektor nesmí běžet otevřený. |
| Rozsah dat | Každá integrace má vlastní rozsah (scope) a vidí jen své skupiny a osoby. Jídelna nevidí přístupy ke dveřím, parkoviště nevidí strávníky. |
| Minimalizace | Přenáší se jen to, co systém potřebuje k provozu. Do stravování nepatří umístění kanceláře, do parkování nepatří e-mail. |
| Ověření webhooků | HMAC-SHA256 podpis v `X-Acs-Signature` + čas v `X-Acs-Timestamp`; příjemce odmítne zprávy starší než 5 minut. |
| Dohledatelnost | Každé volání má `traceId`; v ACS se do auditu zapisuje, která integrace si co vyžádala. |
| Retence událostí | Události drží ACS po dobu danou spisovým řádem; delší archivace je na straně systému, který je vytvořil. |
| Biometrie | ACS eviduje jen *že* je biometrie zavedená, ne šablonu. Šablony zůstávají v systému, který je pořídil. |

Zvláštní pozornost si zaslouží **online autorizace**: dotaz obsahuje SPZ nebo
číslo karty a odpověď jméno osoby. Provoz proto musí být v odděleném síťovém
segmentu a odpověď nesmí obsahovat jméno u zamítnutí — jinak by se z brány dalo
zjišťovat, komu značka patří.

---

## 6. Provozní požadavky

Tohle jsou věci, které se v integracích běžně opomenou a pak dělají potíže,
u kterých nikdo neví, čí jsou.

- **Idempotence.** Každé měnící volání nese `Idempotency-Key`. Příjemce si klíč
  pamatuje 24 hodin a opakované volání neprovede podruhé, jen vrátí původní
  výsledek. Bez toho vznikají po timeoutech duplicity — a timeouty budou.
  *(Poznámka k současnému stavu: stávající volání do WIN-PAK idempotenci nemá,
  což je známý nedostatek k dorovnání.)*
- **Celý stav místo přírůstku.** Identifikátory a oprávnění se posílají jako
  úplný platný seznam, ne jako „přidej/odeber“. Stavy se pak nemohou rozejít
  kvůli jednomu nedoručenému volání. Cena je větší objem dat; u počtů v jednotkách
  tisíc osob je to zanedbatelné proti riziku rozejití.
- **Opakování a prodlevy.** Přechodné chyby (`502`, `503`, timeout) ACS opakuje
  s narůstající prodlevou (1 min, 5, 30, 2 h, 12 h). Chyby dat (`400`, `422`)
  neopakuje — ukáže je správci karet ve frontě.
- **Chybové texty pro člověka.** `detail` v odpovědi se zobrazuje správci v ACS.
  Musí být česky a srozumitelně: „Číslo karty 100234 už patří jinému držiteli“,
  ne „ERR_0x8004“. Původní kód systému patří do `targetErrorCode`.
- **Časy** v UTC podle ISO 8601 s časovou zónou. Zdroj času synchronizovaný přes
  NTP — u událostí z bran se rozdíl v hodinách projeví jako nedohledatelnost.
- **Výkon.** Online autorizace: odpověď do 300 ms v 95 % případů. Provisioning:
  jedna osoba do 2 s. Stránky do 500 položek.
- **Sledování.** `GET /health` bez ověření pro monitoring, `GET /capabilities`
  pro kontrolu verze. Konektor loguje volání s `traceId` a udrží log 30 dní.
- **Verzování.** Cesta `/api/v1`. Do verze se přidávají jen nepovinná pole;
  odebrání pole nebo změna významu znamená novou verzi. Starou verzi provozujeme
  nejméně 6 měsíců po ohlášení konce.
- **Testovací prostředí.** Dodavatel poskytne testovací instanci s testovacími
  daty. Bez ní není integrace přejímatelná — nelze ověřit chování při chybách.

---

## 7. Požadavky na dodavatele a akceptační kritéria

### Co dodavatel dodá

1. Službu podle [`connector-api.yaml`](connector-api.yaml) pro operace, které
   jeho systém umožňuje, a `GET /capabilities`, kde je vyjmenuje.
2. Dokumentaci: jak se instaluje, na čem běží, jak se konfiguruje, jak se
   nastavuje API klíč, kde jsou logy.
3. Mapovací tabulku: co v jeho systému odpovídá přístupovým bodům a skupinám
   (například cenovým hladinám), aby se dalo spárovat.
4. Testovací instanci a přístup k ní po dobu integrace a záruky.
5. Kontakt na řešení incidentů a dohodnutou dobu odezvy.

### Akceptační kritéria

Každé kritérium je ověřitelné a bude ověřeno při přejímce.

| # | Kritérium | Jak se ověří |
|---|---|---|
| 1 | `GET /health` odpovídá bez ověření a hlásí dostupnost cílového systému. | Volání s vypnutým cílovým systémem vrátí `degraded` nebo `503`, ne timeout. |
| 2 | `GET /capabilities` vyjmenuje operace a odpovídá skutečnosti. | Každá nevyjmenovaná operace vrátí `501`. |
| 3 | Volání bez klíče nebo s vadným klíčem je odmítnuto. | `401`. Konektor bez nastaveného klíče odmítá vše (`503`). |
| 4 | Osoba se založí a při druhém volání aktualizuje, nezdvojí. | Dvakrát `PUT /persons/{id}` s různým `Idempotency-Key` → jeden záznam, `created` = true a pak false. |
| 5 | Opakované volání se **stejným** `Idempotency-Key` neprovede operaci podruhé. | Dvakrát totéž volání → jeden zápis, druhá odpověď shodná s první. |
| 6 | Nastavení identifikátorů nastaví celý stav. | Poslat dvě karty, pak jednu → v systému zůstane jedna platná, druhá je zneplatněná. |
| 7 | Oprávnění se zapíše i odebere. | `PUT` se dvěma cíli, pak s jedním → `applied` = 1, `removed` = 1, ověřeno v cílovém systému. |
| 8 | Neznámý cíl oprávnění se ohlásí, nepřeskočí. | `422` s výpisem v `unknownTargets`. |
| 9 | Ukončení osoby zneplatní oprávnění a zachová historii. | `status: ended` → osoba neprojde, záznamy o průchodech/odběrech zůstávají. |
| 10 | Chyba cílového systému se ohlásí srozumitelně. | Vyvolat konflikt (obsazené číslo karty) → `422`, `detail` česky, `targetErrorCode` vyplněn. |
| 11 | Nedostupnost cílového systému je odlišena od chyby dat. | Vypnout cílový systém → `502`, ne `422` ani `500`. |
| 12 | Události se dají získat bez duplicit a bez ztrát. | Vyzvednout dávku, použít `nextCursor`, restartovat konektor → žádná událost dvakrát ani chybějící. |
| 13 | Komunikace jen po TLS. | Volání po HTTP je odmítnuto. |
| 14 | Zátěž. | Provisioning 1 000 osob v dávce doběhne bez chyb; online autorizace 20 dotazů/s s odezvou do 300 ms v 95 %. |
| 15 | Chování při nedostupnosti ACS (jen vzor C). | Vjezd fail-closed, výjezd fail-open, obojí nastavitelné; po obnovení se dorovná stav. |

---

## 8. Co to znamená pro ACS

Kontrakt je navržený tak, aby se dal plnit postupně. Na naší straně je potřeba:

| Krok | Rozsah | Závislost |
|---|---|---|
| Zobecnit skupinu na druh + atributy | `ReaderGroup` → skupina s `kind`; migrace stávajících skupin na `readers` | žádná |
| Zavést přístupový bod | `Reader` dostane druh; brány a terminály jako nové druhy | skupiny |
| Samostatný pohled na oprávnění | odvozený z položek žádostí, ať API nemusí znát workflow | žádná |
| Evidence integrací | více cílových systémů místo jedné sady `WinPak:*` klíčů, každý s rozsahem a tajemstvím | žádná |
| Proud změn a výdej webhooků | tabulka změn + odesílání s opakováním | evidence integrací |
| Příchozí API | autentizace (OAuth2 nebo klíč), rozsahy, limity volání, `problem+json` | evidence integrací |
| Online autorizace | vyhodnocení oprávnění k okamžiku dotazu, keš | oprávnění, přístupové body |
| Příjem událostí | úložiště událostí + zobrazení v auditu | žádná |
| Obecný odchozí konektor | zobecnit `WinPakClient` na kontrakt konektoru | idempotence |

Stávající konektor k WIN-PAK tomuto kontraktu **zatím neodpovídá**: chybové
odpovědi má ve tvaru `{"error": "..."}` místo `problem+json`, nemá idempotenci
ani `capabilities` v tomto tvaru a nestránkuje. Není to důvod ho přepisovat hned
— dokud je jediný, funguje. Až se přidá druhý systém, dorovná se na kontrakt,
aby ACS neměl dvě cesty pro totéž.

---

## 9. Otevřené otázky k rozhodnutí

1. **GreenCenter** — co v areálu obsluhuje a má vlastní evidenci osob? Podle toho
   se vybere vzor (kapitola 4.3).
2. **Vjezdy** — vzniká právo na vjezd žádostí se schválením jako přístup ke
   dveřím, nebo se jen eviduje? Kdo ho schvaluje?
3. **Stravování** — kdo je zdrojem cenových hladin: jídelna (a ACS jen páruje),
   nebo se definují v ACS?
4. **Návštěvy a externisté** — mají vjezd a stravování řešit i pro osoby, které
   nejsou v personálním systému? To je nový zdroj osob, ne jen integrace.
5. **Autentizace** — zavést OAuth2 (vlastní vydavatel tokenů, nebo AD FS /
   Entra ID), nebo pro první etapu vystačit s API klíči?
6. **Retence událostí** — jak dlouho v ACS držet průchody a odběry? Ovlivňuje to
   velikost databáze i spisový řád.
