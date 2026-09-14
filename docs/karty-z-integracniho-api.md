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

Podtyp `3` = identifikační karta (nastavitelné). Volání jdou po čtyřech
souběžně; zaměstnanec bez karty smí dostat `404` — to není chyba.

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
  a když NTLM chybí, řekne to.
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
čísle** tlačítkem *Vyzkoušet* v Nastavení: ukáže surovou odpověď a to, co z ní
ACS přečetl. Když nepřečte nic, pošlete surovou odpověď vývoji — rozbor se
doplní o skutečné názvy polí.

## Výsledek

Stejný jako u MSSQL: identifikátory typu *karta* u zaměstnance (`Katalog →
Zaměstnanci`), první platná karta jako hlavní číslo karty, karty, které ze
zdroje zmizely, se deaktivují. Karty pak používá fronta karet a zápis přístupů
do WIN-PAKu.
