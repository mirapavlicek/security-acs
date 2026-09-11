# Schvalování nadřízeným zaměstnance (z AD)

Schvalovací matice umí kromě konkrétních uživatelů i schvalovatele typu
**„nadřízený zaměstnance“**. Kdo to je, se nezadává do matice — vyhodnotí se
u každé žádosti podle cílového zaměstnance. Nadřízený se do ACS dostává ze
zdroje zaměstnanců (AD atribut `manager`), nebo ručně u zaměstnance.

## Ověření, zda AD nadřízené vůbec vede

Standardní AD atribut je `manager` a obsahuje **DN nadřízeného**
(`CN=Novák Jan,OU=IT,DC=fnmh,DC=cz`). Zda ho FNMH/NNH plní, se zjistí bez
zásahu do kódu:

- **Nastavení → Zdroj zaměstnanců → Co vrací AD o účtu** (`/Admin/LdapDump`):
  v tabulce sestaveného zaměstnance je řádek **Nadřízený (manager)**. Když je
  prázdný, projděte seznam všech atributů — pokud je nadřízený v jiném
  (např. `extensionAttribute…`), přepne se název atributu v Nastavení →
  Zdroj zaměstnanců → *AD atribut s nadřízeným*.
- Po **importu zaměstnanců** se v Nastavení → Zdroj zaměstnanců zobrazí
  statistika poslední synchronizace: *nadřízený nalezen u X z Y zaměstnanců
  (zdroj ho uvádí u Z)*. Když je Z nula, AD atribut neplní; když Z > X, část
  nadřízených je mimo importovaný filtr (jiná OU, zablokovaný účet).
- **Katalog → Zaměstnanci** má sloupec *Nadřízený*, počet zaměstnanců
  s nadřízeným a filtr *bez nadřízeného*. „nespárováno“ znamená, že zdroj
  nadřízeného uvádí, ale v ACS se nenašel (DN je vidět po najetí myší a
  v editaci zaměstnance).

## Jak to funguje

### Import z AD

1. Synchronizace zaměstnanců si k účtům vyžádá navíc `manager` a
   `distinguishedName`.
2. Po načtení celé domény se DN nadřízeného převede na sAMAccountName podle
   DN ostatních načtených účtů (`LdapAttributes.ResolveManagers`). Nadřízený
   mimo filtr zůstane nespárovaný; jeho DN se uloží do `Employee.ManagerAdAccount`
   pro diagnostiku. Účet, který má za nadřízeného sám sebe, nadřízeného nemá.
3. Druhý průchod `EmployeeSyncService` nastaví `Employee.ManagerId` podle
   `ExternalId` nadřízeného. Zaměstnanec se zaškrtnutým **Nadřízený zadán ručně**
   se nepřepisuje. Když zdroj nadřízeného přestane uvádět, vazba se odebere.

Zdroje MSSQL/API mohou vracet volitelný sloupec/pole `ManagerExternalId`
(`ExternalId` nadřízeného ve stejném zdroji); když chybí, nic se nemění.

### Výchozí matice — SPZ i karty schvalují nadřízení

Aby žádosti o parkovací povolení (SPZ) i o přístupy (karty) šly standardně
k nadřízenému, nemusí se matice přiřazovat každé čtečce a druhu povolení zvlášť.
**Katalog → Schvalovací matice → Výchozí matice**: tlačítko *Založit matici
„Nadřízený zaměstnance“ a nastavit jako výchozí* vytvoří matici s jednou úrovní
(schvalovatel = přímý nadřízený) a označí ji jako výchozí (`ApprovalMatrix.IsDefault`,
nejvýše jedna). Výchozí matice se použije pro:

- čtečky bez vlastní matice,
- skupiny čteček bez matice (a bez matice nadřazených skupin),
- parkovací povolení, když ani druh povolení, ani vybrané areály matici nemají.

Vlastní matice čtečky / skupiny / druhu / areálu má vždy přednost — výchozí je
jen záchytná síť. Bez výchozí matice takové položky rozhoduje administrátor
(jako dosud). Po schválení nadřízeným jde položka standardně do fronty správce
karet (karty) nebo správce parkování (SPZ), který ji vydá.

### Matice

V editoru matice (Katalog → Schvalovací matice → úroveň → *Přidat
schvalovatele*) se vybere typ:

- **Konkrétní uživatel** — jako dosud,
- **Nadřízený zaměstnance (z AD)** — přímý nadřízený cílového zaměstnance,
- **Nadřízený nadřízeného** — o úroveň výš,
- **Odpovědná osoba cílového úseku** — vlastník prostoru žádosti (čtečka →
  místnost → patro → budova, skupina), nebo vedoucí úseku, kterému prostor patří.

Úroveň může platit jen pro některé **kategorie zaměstnance** (řadový / vedoucí /
přímo pod ředitelem / ředitel) a jen pro vstup **v rámci vlastního / mimo vlastní
úsek** — viz [Schvalovací matice FN Motol](schvalovaci-matice-fnm.md).

Typy lze na jedné úrovni kombinovat, např. „nadřízený **nebo** vedoucí IT“
(režim *stačí kterýkoli*) nebo „nadřízený **a** bezpečnost“ (režim *všichni*).
V režimu *všichni* / *alespoň N* se nadřízený počítá jako jeden schvalovatel.

### Vyhodnocení u žádosti (`ApproverResolver`)

- Nadřízený se hledá od **cílového zaměstnance** žádosti (ne od žadatele).
- **Samoschválení**: je-li nalezený nadřízený zároveň **žadatelem** (vedoucí
  žádá za podřízeného), rozhoduje jeho nadřízený — tak dlouho, dokud je kam jít.
- Nadřízený **bez účtu v ACS** (ještě se nepřihlásil) se pozná podle vazby
  zaměstnanec ↔ AD účet; e-mail s výzvou dostane na adresu ze záznamu
  zaměstnance a po prvním přihlášení Windows účtem rovnou rozhoduje.
- **Zástupy** platí i pro nadřízeného — zástupce vedoucího rozhoduje v zástupu.
- Neaktivní nadřízený nebo cyklus v hierarchii se bere jako „bez nadřízeného“.

### Když nadřízený není

Úroveň, která po vyhodnocení nemá žádného schvalovatele (typicky ředitel bez
nadřízeného, nebo zaměstnanec ještě bez importu), se chová jako položka
bez matice: **rozhodne administrátor**, administrátoři dostanou e-mail
„žádost nemá schvalovatele“ a připomínky jdou jim. Rozhodnutí se zapíše
s poznámkou *rozhodl administrátor — <důvod>* a položka pokračuje na další
úrovně matice (bezpečnost apod. rozhoduje dál).

Alternativy pro správce: doplnit zaměstnanci nadřízeného ručně (Katalog →
Zaměstnanci → Upravit), nebo přidat na tutéž úroveň náhradního konkrétního
uživatele v režimu *stačí kterýkoli* — pak rozhodne on a administrátor se
nezapojuje.

### Kde je to vidět

- **Žádosti**: u čekající položky *Čeká na: Nadřízený: Jan Novák* (nebo
  *rozhodne správce* s důvodem); ve frontě schvalovatele popisek
  *nadřízený: …* / *bez schvalovatele — rozhodne správce*.
- **Můj přístup**: *Váš nadřízený: …*.
- **Zaměstnanec**: pole *Nadřízený* (výběr hledáním jako u nové žádosti),
  zaškrtnutí *zadán ručně*, seznam podřízených.

## Datový model

- `Employee.ManagerId` (FK na `Employee`, při smazání nadřízeného se nuluje),
  `ManagerAdAccount` (co přišlo ze zdroje), `ManagerManual`.
- `Approver.Kind` (`User` / `AdGroup` / `LineManager`), `Approver.ManagerDepth`
  (1 = přímý, 2 = nadřízený nadřízeného). Stávající řádky matic jsou `User`.
- `ApprovalMatrix.IsDefault` — výchozí matice pro položky bez vlastní matice
  (migrace `DefaultMatrix`).
- Nastavení `Employees:LdapManagerAttribute` (výchozí `manager`) a
  `Employees:LastManagerStats` (jen zobrazení).
- Migrace `ManagerApprovers`.

## Testy

`tests/Acs.Tests/ManagerApprovalTests.cs` (workflow: nadřízený rozhoduje,
bez účtu přes AD účet, zástup, samoschválení, fallback na admina s upozorněním,
náhradní uživatel, režim *všichni*, hloubka 2, neaktivní nadřízený, výchozí
matice pro čtečku / skupinu / SPZ a její přednost) a
`SyncServiceTests` (DN → účet, nadřízený mimo dávku, ruční zadání, statistika,
odebrání vazby).
