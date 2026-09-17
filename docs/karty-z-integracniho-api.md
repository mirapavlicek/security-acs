# Karty zaměstnanců z integrační služby (Identifiers)

Čísla identifikačních karet nemusí být v MSSQL — nemocnice je vydává přes
integrační službu `ws-integrations`. ACS je z ní umí synchronizovat stejně
jako z SQL: *Správa → Nastavení → Karty*, zdroj **Integrační API**.

## Jak se ACS ptá

**Výchozí režim — vše jedním dotazem.** Služba funguje i bez filtrů: ACS pošle

```
POST https://ws-integrations.nnh.local/api/v0/Identifiers
{}
```

a dostane identifikátory všech osob (`employeeNo`, `initialCode`,
`idIdentifierSubType`). Odpověď si uloží jako **otisk** do tabulky
`ImportedIdentifiers` (co přišlo, jak se číslo převedlo, ke komu se spárovalo)
a **spárování se zaměstnanci udělá nad databází ACS** podle osobního čísla.
Osobní čísla, která v ACS nejsou, zůstávají v otisku k dohledání a ve výsledku
synchronizace jako „N osobních čísel v ACS není“. Synchronizace běží podle
intervalu v Nastavení → Karty (výchozí 60 minut).

**Režim po zaměstnancích** (přepínač *Režim čtení*) je původní chování: pro
každého aktivního zaměstnance s osobním číslem a každý podtyp z pravidel
`POST { "employeeNo": "<osobní číslo>", "idIdentifierSubType": 3 }`; volání
jdou po čtyřech souběžně, zaměstnanec bez karty smí dostat `404`.

## Podtypy a čísla pro čtečky

Co je karta, co SPZ a jak se z hodnoty služby udělá číslo, které čtou čtečky
(WIN-PAK), určují **pravidla podtypů** v Nastavení → Karty — řádek
`podtyp = typ : formát`. Výchozí:

```
3 = Card : Auto             # podle tvaru: 4d-07782 / 4D-7782 → 07782, 22B-11012 → 22B011012
100003 = Card : DashToZero  # FN Motol (NATIVE nnn-nnnnn): 123-45678 → 123045678
4 = LicensePlate            # SPZ: 1TN7287-CZE → 1TN7287
```

Služba vrací pod podtypem 3 karty Homolky (`4d-07782`, někdy zkráceně
`4D-7782`) i karty v nativním tvaru FN Motol (`22B-11012`), proto je výchozí
formát `Auto` — pozná se podle tvaru hodnoty. Formáty: `Auto`, `Last5`
(posledních 5 číslic doplněných nulami — `4D-7782` i `4d-07782` dají `07782`),
`DashToZero` (pomlčka → 0), `Raw` (beze změny). Typ i formát jdou zapsat i česky (`karta`, `SPZ`, `posledních 5`,
`pomlčka→0`). Podtypy bez pravidla se přeskočí (zkouška je vypíše). Původní
hodnota ze služby zůstává v poznámce identifikátoru (`podtyp 100003: 123-45678`).

Po přepnutí na tyto převody se dřívější identifikátory v původním tvaru
(např. `4D07782`) při první synchronizaci deaktivují a vzniknou nové v tvaru
pro čtečky — přístupy ve WIN-PAKu se pak zapisují na správná čísla karet.

## Přihlášení

Služba na dotaz bez přihlášení odpovídá `401 … "No valid token available."` —
chce **token** v hlavičce `Authorization: Bearer`.

- **Token z přihlášení** — ACS pošle na přihlašovací endpoint služby
  `POST` s tělem podle šablony (výchozí `{"username":"{user}","password":"{password}"}`;
  `{user}`/`{password}` je zadaný účet, bez něj servisní účet pro AD), z odpovědi
  vezme token (`token`, `accessToken`, `access_token`, `jwt`… i o úroveň hlouběji
  v `data`/`result`; nebo název pole zadejte) a posílá ho jako Bearer. Token drží
  50 minut. Adresu endpointu a tvar těla najdete ve Swaggeru služby.
- **Pevný token** — vydaný správcem služby, vloží se do nastavení.
- **Windows účet domény (NTLM)** — pro služby s integrovaným přihlášením Windows:
  bez vyplněného uživatele se použije **servisní účet, kterým ACS čte AD**
  (Nastavení → Active Directory), jiný účet jde zadat jako `DOMÉNA\uživatel`
  nebo `uživatel@doména`. Účet se ověřuje proti AD **výhradně přes NTLM** —
  ACS registruje přihlašovací údaje jen pro schéma `NTLM`, takže výzvu
  `Negotiate` (SPNEGO → Kerberos) ignoruje. Kerberos by z linuxových nodů
  vyžadoval ticket a konfiguraci krb5 a bez nich končil 401; NTLM potřebuje jen
  jméno, heslo a doménu. NTLM vyřizuje spravovaná implementace .NET
  (`System.Net.Security.UseManagedNtlm` v `Acs.Web.csproj`), nody tedy nepotřebují
  balík `gssntlmssp`. Služba musí NTLM nabízet (u IIS poskytovatel *NTLM* ve
  Windows Authentication) — zkouška v Nastavení vypíše hlavičku `WWW-Authenticate`
  a když NTLM chybí, řekne to. Zkouška také vypíše, jakým **uživatelem a doménou**
  se ACS hlásí — má to odpovídat tomu, co funguje v Postmanu (typ *NTLM
  Authentication*: Username, Password, Domain, např. `nnh.local`).
- API klíč v hlavičce (výchozí `X-Api-Key`), nebo Basic autentizace.
- U interní CA, kterou nody neznají, jde dočasně vypnout ověření certifikátu
  (lepší je CA na nody nainstalovat).

## Odpověď

Služba odpovídá obálkou:

```json
{"output":[{"employeeNo":"13483","initialCode":"4d-07782","idIdentifierSubType":3}, …],
 "conclusion":true,"resultType":"Ok"}
```

Hodnota karty/SPZ je `initialCode`. Při chybě je `conclusion: false` a
`errorDescription` (`errorType`, `errorMessage`) — takovou odpověď ACS bere jako
chybu synchronizace, **ne** jako „zaměstnanec nic nemá“ (jinak by mu karty zrušil);
zkouška v Nastavení ji vypíše. Služba tentýž identifikátor vrací opakovaně, ACS
každou hodnotu bere jednou. U SPZ odstraní příponu země za pomlčkou
(`1TN7287-CZE` → `1TN7287`), aby seděla na čtení kamer u brány.

Rozbor zůstává tolerantní i k jiným tvarům:

- pole záznamů, nebo objekt s polem (`output`, `identifiers`, `items`, `data`, …
  nebo jediné pole v objektu), nebo jediný záznam,
- hodnota pod `initialCode`, `identifier`, `identifierNo`, `cardNumber`, `cardNo`,
  `number`, `code`, `value`, `serialNumber`… (nebo prostý řetězec),
- platnost `validFrom`/`validTo` (`dateFrom`/`dateTo`…), stav `active`/`isActive`
  nebo textový `state`/`status` („Aktivní“, „Blokovaná“…) — neaktivní se vynechají.

**Než zapnete automatickou synchronizaci, vyzkoušejte to** tlačítkem
*Vyzkoušet* v Nastavení: s **prázdným osobním číslem** pošle dotaz bez filtrů a
vypíše, kolik záznamů přišlo, po podtypech s ukázkou převodu čísel (`4d-07782 →
07782`) a kolik osobních čísel ze služby v ACS je; s osobním číslem se zeptá
po zaměstnanci pro každý podtyp a ukáže surovou odpověď i rozbor. Když nepřečte
nic, pošlete surovou odpověď vývoji — rozbor se doplní o skutečné názvy polí.

## Výsledek

Stejný jako u MSSQL: identifikátory typu *karta* a *SPZ* u zaměstnance
(`Katalog → Zaměstnanci`), první platná karta jako hlavní číslo karty,
identifikátory, které ze zdroje zmizely, se deaktivují. Karty pak používá
fronta karet a zápis přístupů do WIN-PAKu, SPZ parkovací systém.

Člověk může mít **víc karet** — evidují se všechny. **Stejný identifikátor u
téhož člověka se bere jen jednou**: když ho zdroj vrátí opakovaně (třeba víc
záznamů k jedné kartě), platí první a další se přeskočí (počet je ve výsledku
synchronizace jako „přeskočeno duplicit“). Stejné číslo u jiného člověka nebo
jiného typu (karta vs. SPZ) je samostatný identifikátor.

Přístup do WIN-PAKu se uděluje držiteli a konektor ho zapíše **na všechny jeho
karty**. Fronta správce karet proto u zaměstnance ukazuje všechny platné karty
a po předání do systému se u položky vypíše, na kterých kartách přístup je a
které karty z ACS WIN-PAK nezná nebo je má u jiného držitele — ty musí správce
karet dořešit ve WIN-PAKu. Držitel se k zaměstnanci dopáruje přes kteroukoli
z jeho karet.

## Když to v Postmanu jde a v ACS ne

- Na nody se nasazují jen **release tagy** (`ACS_UPDATE_MODE=tag`, viz
  `deploy/README.md`) — změna v `main` bez tagu na nodech neběží. Verze
  aplikace je v patičce.
- Porovnejte řádek *Přihlášení:* ve výstupu zkoušky s Postmanem: stejný
  uživatel (v Nastavení → Karty pole *Uživatel*, jinak servisní účet pro AD) a
  stejná doména (z `DOMÉNA\uživatel`, jinak doména z Nastavení → Active
  Directory). UPN `uživatel@doména` se posílá bez domény — když s ním služba
  odpoví 401, zadejte účet jako `DOMÉNA\uživatel`.
- Řádek *WWW-Authenticate:* musí obsahovat `NTLM`.
- Tělo požadavku musí být stejné jako v Postmanu:
  `{"employeeNo":"13483","idIdentifierSubType":3}` — zkouška ho vypíše.
