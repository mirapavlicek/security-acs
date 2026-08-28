# Osobní čísla z Active Directory

Osobní číslo má každá doména jinde. ACS ho proto bere z konfigurovatelného
seznamu atributů a má nástroj, kterým se dohledá, čím doména jednotlivé atributy
plní.

## Jak zjistit, co AD o účtu vrací

**V aplikaci:** Nastavení → Zdroj zaměstnanců → odkaz **Co vrací AD o účtu**
(přímo `/Admin/LdapDump`). Zadá se přihlašovací jméno, osobní číslo, e-mail nebo
příjmení. Stránka vypíše:

- **všechny atributy**, které o účtu vrátí doménový řadič (binární hodnoty jako
  délku a hex náhled),
- **co z nich ACS sestaví** — jméno, oddělení, e-mail a hlavně osobní číslo,
  včetně toho, ze **kterého atributu** hodnota doopravdy přišla.

Namapované atributy jsou v tabulce zvýrazněné, takže je vidět, co se používá
a co je k dispozici navíc.

**Z příkazové řádky** (hodí se pro vložení do issue nebo e-mailu):

```bash
dotnet run --project src/Acs.Web -- --ldap-dump 13483
dotnet run --project src/Acs.Web -- --ldap-dump Pavlíček
```

Na nasazeném serveru se spouští publikovaná binárka se stejným přepínačem.
Nástroj čte nastavení z databáze (řadič, Base DN, servisní účet), takže funguje
jen tam, kde je AD nakonfigurované.

Dotaz do výpisu se zapisuje do auditu jako `ldap-dump` — obsahuje osobní údaje.

## Odkud se osobní číslo bere

Nastavení → Zdroj zaměstnanců → **AD atribut s osobním číslem**. Zadává se jeden
nebo víc atributů oddělených čárkou; bere se **první, který má neprázdnou
hodnotu**. Výchozí pořadí je `employeeID, employeeNumber`.

Typické situace:

| Co výpis ukáže | Co nastavit |
|---|---|
| `employeeNumber` má osobní číslo, `employeeID` něco jiného | `employeeNumber` |
| osobní číslo je v `extensionAttribute3` | `extensionAttribute3` |
| část účtů má číslo v jednom, část v druhém atributu | `extensionAttribute3, employeeNumber` |
| osobní číslo je přihlašovací jméno | `sAMAccountName` |

Změna se projeví při dalším importu zaměstnanců (Zaměstnanci → Importovat z AD).

## Proč se dřív načítala divná osobní čísla

Čtení hodnot z LDAP mělo tři pasti. Všechny tři se projevily právě na osobním
čísle, protože je to jediné pole, kde se hodnota bere z více atributů za sebou.

1. **Binární hodnota se uložila jako `System.Byte[]`.**
   `SearchResultEntry` vrací hodnotu jako `string`, ale u dat, která nejsou
   platné UTF-8 (SID, GUID, fotky, některá custom schémata), vrátí `byte[]`.
   Původní kód na tom volal `ToString()`, což dá doslova text `System.Byte[]`.

2. **Prázdná hodnota zablokovala záložní atribut.**
   Zápis `GetAttr("employeeID") ?? GetAttr("employeeNumber")` vypadá jako
   „když první chybí, vezmi druhý“, ale u atributu, který v AD existuje a je
   prázdný, vrátil prázdný řetězec. Ten není `null`, takže se na `employeeNumber`
   nikdy nepřešlo a osobní číslo zůstalo prázdné.

3. **Hodnoty se neořezávaly.**
   `"  13483 "` se uložilo s mezerami. Takové číslo se nespáruje s kartou
   (párování karet jde přes osobní číslo) a v seznamech vypadá divně.

Dnes se binární hodnoty pro mapování přeskakují (ve výpisu se ukážou jako hex),
prázdné hodnoty se přeskočí, takže záložní atribut dostane šanci, a hodnoty se
ořezávají. Ořezání platí i pro zdroje MSSQL a API — `CHAR` sloupce vracejí
hodnoty doplněné mezerami na pevnou délku.

## Co když se účet nenajde

- **Base DN** musí pokrývat organizační jednotku, kde účet leží
  (Nastavení → Active Directory).
- Servisní účet musí mít právo čtení na daný objekt; atributy, které nesmí číst,
  řadič prostě nevrátí — ve výpisu pak chybí.
- Výpis hledá v `sAMAccountName`, `userPrincipalName`, `sn`, `cn`, `displayName`,
  `mail` a v nastavených atributech osobního čísla. Příjmení se hledá na přesnou
  shodu, zobrazované jméno na část.
