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
Bez dalších parametrů čte nástroj nastavení z databáze (řadič, Base DN, servisní
účet), takže funguje tam, kde je AD nakonfigurované.

Dotaz do výpisu se zapisuje do auditu jako `ldap-dump` — obsahuje osobní údaje.

### Proti řadiči mimo nastavení (ladění, jiná síť, tunel)

Když se potřebuje ověřit řadič, na který ACS ještě nastavené není, zadají se
parametry spojení přímo:

```bash
export ACS_LDAP_BIND_PASSWORD='heslo servisního účtu'
dotnet run --project src/Acs.Web -- --ldap-dump 13483 \
  --server dc01.domena.local --base-dn "DC=domena,DC=local" \
  --bind-user "svc-acs@domena.local"
```

Heslo se bere **jen z proměnné** `ACS_LDAP_BIND_PASSWORD`. V argumentu by ho
viděl každý, kdo si vypíše běžící procesy.

Další přepínače: `--port`, `--no-ssl` (LDAP na 389 místo LDAPS na 636),
`--attribute` pro vyzkoušení jiného atributu s osobním číslem bez zásahu do
nastavení.

Když řadič není ze stroje dostupný přímo, dá se protáhnout SSH tunelem přes
počítač, který v té síti je:

```bash
ssh -N -L 1389:dc01.domena.local:389 uzivatel@stroj-v-siti &
dotnet run --project src/Acs.Web -- --ldap-dump 13483 \
  --server 127.0.0.1 --port 1389 --no-ssl \
  --base-dn "DC=domena,DC=local" --bind-user "svc-acs@domena.local"
```

Přes tunel se používá `--no-ssl`: provoz šifruje SSH a certifikát řadiče by na
adresu `127.0.0.1` neplatil.

### Bez ACS, jen z počítače v síti

Pokud stačí jednorázově zjistit, co AD vrací, a nikde neběží ACS, udělá totéž
`ldapsearch` (na macOS i v Linuxu je předinstalovaný):

```bash
# 1) najít doménový řadič
dig +short -t SRV _ldap._tcp.domena.local

# 2) vypsat vše, co AD o účtu vrací
ldapsearch -LLL -x -H ldap://dc01.domena.local:389 \
  -D "svc-acs@domena.local" -W -b "DC=domena,DC=local" \
  "(|(sAMAccountName=13483)(employeeID=13483)(employeeNumber=13483))" > vypis.ldif
```

Ve výstupu jsou hodnoty za `::` kódované v base64 — to jsou právě ty binární,
kvůli kterým se dřív ukládalo `System.Byte[]` (kapitola níže). Výpis obsahuje
osobní údaje, takže se před předáním prochází.

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
