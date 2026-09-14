# Karty zaměstnanců z integrační služby (Identifiers)

Čísla identifikačních karet nemusí být v MSSQL — nemocnice je vydává přes
integrační službu `ws-integrations`. ACS je z ní umí synchronizovat stejně
jako z SQL: *Správa → Nastavení → Karty*, zdroj **Integrační API**.

## Jak se ACS ptá

Pro každého aktivního zaměstnance s osobním číslem pošle

```
POST https://ws-integrations.nnh.local/api/v0/Identifiers
{ "employeeNo": "<osobní číslo>", "idIdentifierSubType": 3 }
```

a totéž s `"idIdentifierSubType": 4`. Podtyp `3` = identifikační karta,
podtyp `4` = SPZ vozidla (oba nastavitelné; prázdný podtyp SPZ = SPZ z API
nestahovat). Z podtypu 3 vznikají identifikátory typu *karta*, z podtypu 4
typu *SPZ*. Volání jdou po čtyřech souběžně; zaměstnanec bez karty či SPZ smí
dostat `404` — to není chyba.

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

Schéma odpovědi dokumentace neuvádí, rozbor je proto tolerantní:

- pole záznamů, nebo objekt s polem (`identifiers`, `items`, `data`, … nebo
  jediné pole v objektu), nebo jediný záznam,
- hodnota karty pod `identifier`, `identifierNo`, `cardNumber`, `cardNo`,
  `number`, `code`, `value`, `serialNumber`… (nebo prostý řetězec),
- platnost `validFrom`/`validTo` (`dateFrom`/`dateTo`…), stav `active`/`isActive`
  nebo textový `state`/`status` („Aktivní“, „Blokovaná“…) — neaktivní se vynechají.

**Než zapnete automatickou synchronizaci, vyzkoušejte to na jednom osobním
čísle** tlačítkem *Vyzkoušet* v Nastavení: pro karty i SPZ ukáže surovou
odpověď a to, co z ní ACS přečetl. Když nepřečte nic, pošlete surovou odpověď
vývoji — rozbor se doplní o skutečné názvy polí.

## Výsledek

Stejný jako u MSSQL: identifikátory typu *karta* a *SPZ* u zaměstnance
(`Katalog → Zaměstnanci`), první platná karta jako hlavní číslo karty,
identifikátory, které ze zdroje zmizely, se deaktivují. Karty pak používá
fronta karet a zápis přístupů do WIN-PAKu, SPZ parkovací systém.

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
