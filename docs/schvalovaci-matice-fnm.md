# Schvalovací matice FN Motol (EKV, parkování, kamery, EZS)

Dokument *Schvalovací matice – EKV, parkování, kamery, EZS* předepisuje schvalovací
postup podle **kategorie žadatele** (řadový zaměstnanec / primář, vrchní sestra, vedoucí /
náměstek a vedoucí přímo podřízený řediteli / ředitel) a podle toho, zda jde o vstup
**v rámci vlastního úseku**, nebo **mimo vlastní úsek**. ACS to umí nastavit takto.

## Mapování dokumentu na nastavení

### 1. EKV – vstupní oprávnění (matice „EKV – vstupní oprávnění (FN Motol)“, výchozí)

| Žadatel | Oprávnění | Dokument | V ACS |
|---|---|---|---|
| Řadový zaměstnanec | vlastní úsek | nadřízený → realizace | úroveň 1 *Nadřízený zaměstnance* (řadový + vedoucí, vždy) → fronta správce karet |
| Řadový zaměstnanec | mimo úsek | nadřízený → odpovědná osoba cílového úseku → vedoucí OVBKŘ → realizace | úroveň 1 → úroveň 3 *Odpovědná osoba cílového úseku* (řadový, jen mimo úsek) → úroveň 4 *Vedoucí OVBKŘ* (řadový + vedoucí, jen mimo úsek) |
| Primář / vrchní sestra / vedoucí | vlastní úsek | příslušný náměstek → realizace | úroveň 1 (nadřízený vedoucího = náměstek) |
| Primář / vrchní sestra / vedoucí | mimo úsek | náměstek → vedoucí OVBKŘ → realizace | úroveň 1 → úroveň 4 (úroveň 3 pro vedoucí neplatí) |
| Náměstek / přímo podřízený řediteli | úsek, který řídí | bez dalšího stupně → realizace | žádná úroveň neplatí → *schváleno bez schvalovacího stupně* (matice má zapnuto „bez platné úrovně schválit“) |
| Náměstek / přímo podřízený řediteli | mimo úsek | ředitel → realizace | úroveň 2 *Ředitel* (přímo pod ředitelem, jen mimo úsek; schvalovatel = nadřízený náměstka) |
| Ředitel | obecně | přímo realizace | žádná úroveň neplatí → bez stupně |

„Útvar pověřený realizací“ = fronta správce karet (zápis do WIN-PAK).

### 2. Parkování (matice „Parkování – vjezd do nemocnice (FN Motol)“, přiřazená druhům povolení)

| Žadatel | Dokument | V ACS |
|---|---|---|
| Řadový zaměstnanec | nadřízený → úsek parkování | úroveň 1 *Nadřízený zaměstnance* (řadový + vedoucí) → fronta správce parkování |
| Primář / vrchní sestra / vedoucí | vedoucí odboru / kliniky / náměstek → úsek parkování | úroveň 1 (nadřízený vedoucího) |
| Náměstek / přímo podřízený řediteli / ředitel | přímo úsek parkování | žádná úroveň → bez stupně → fronta správce parkování |

Parkovací povolení nemá „prostor“, proto se u něj podmínka vlastní / cizí úsek neuplatní.

### 3. Kamerové systémy / EZS (matice „Kamery / EZS – zřízení a rozšíření (FN Motol)“)

| Žadatel | Dokument | V ACS |
|---|---|---|
| Vedoucí daného úseku | vedoucí OVBKŘ → realizace | úroveň 1 *Vedoucí OVBKŘ* (konkrétní uživatel) → fronta realizace ICT |
| Vedoucí OVBKŘ | provozně-technický náměstek → realizace | vedoucí OVBKŘ je sám cílovým zaměstnancem → místo něj rozhoduje jeho **nadřízený** (z AD) |

Nový typ žádosti: **Žádosti → + Žádost o kamery / EZS** (druh kamera / EZS / obojí, název,
popis, zdůvodnění, umístění budova / patro / místnost + text). Schválené žádosti jdou do
**Správa → Fronta kamer / EZS (ICT)** (role *IctAdmin*), kde ICT potvrdí realizaci nebo
zamítne s důvodem. Role vedoucího OBP (kontrola nastavení a přístupů, řešení sporů):
role *Auditor* — čte všechny žádosti, detaily a reporty, nerozhoduje.

## Postup zavedení

1. **Nadřízení z AD** — Nastavení → Zdroj zaměstnanců, import zaměstnanců
   (viz [Schvalování nadřízeným](schvalovani-nadrizenym.md)).
2. **Úseky** — Číselníky → Úseky: založte úseky náměstků, pod ně odbory / kliniky / oddělení.
   U každého úseku vedoucí (odpovědná osoba) a *mapování oddělení* — texty oddělení z HR/AD
   (přesný název, nebo předpona s `*`, jeden na řádek). Tlačítko *Přepočítat úseky a kategorie*;
   tabulka *Oddělení bez úseku* ukáže, co ještě nikdo nemapuje. Přepočet běží automaticky po
   každém importu zaměstnanců.
3. **Kategorie zaměstnanců** se odvodí z hierarchie: ředitel = jediný vrchol hierarchie,
   který někoho řídí (jinak označte ředitele ručně); přímo podřízený řediteli = přímí
   podřízení ředitele (Dostálová, Šimek, Kouba…); vedoucí = má podřízené nebo vede úsek;
   ostatní řadoví. Ručně: Číselníky → Zaměstnanci → Upravit → *Kategorie* (ruční hodnota
   se nepřepisuje). Seznam zaměstnanců má sloupec a filtr podle kategorie.
4. **Vlastníci prostorů** — kdo „vlastní“ čtečku: úsek a/nebo odpovědná osoba u budovy,
   patra, chodby, místnosti (Číselníky → Budovy a místnosti → *Vlastník …*), u čtečky
   (editace čtečky) a u skupiny čteček. Prázdné dědí z nadřazeného prostoru
   (čtečka → místnost / chodba → patro → budova); bez odpovědné osoby platí vedoucí úseku.
   Stačí tedy nastavit úsek u pater / budov a jen výjimky u místností.
5. **Matice** — Číselníky → Schvalovací matice → *Založit matice FN Motol* (vyberte účet
   vedoucího OVBKŘ). Průvodce založí tři matice, EKV nastaví jako výchozí (čtečky a skupiny
   bez vlastní matice), Parkování přiřadí druhům povolení bez matice a Kamery / EZS nastaví
   jako matici pro žádosti o kamery / EZS. Existující matice stejného názvu se nepřepíší.
6. **Role** — Správa → Uživatelé a role: *IctAdmin* pracovníkům ICT (fronta realizace),
   *Auditor* vedoucímu OBP.

## Jak se rozhoduje „vlastní úsek“

Prostor je ve vlastním úseku zaměstnance, když platí alespoň jedno:

- úsek prostoru je stejný jako úsek zaměstnance, nebo je jeho předkem / potomkem ve stromu
  úseků (náměstek LPP → chirurgická klinika → oddělení),
- odpovědná osoba prostoru je zaměstnanec sám nebo někdo z jeho nadřízených,
- zaměstnanec je v linii nadřízených odpovědné osoby (náměstek žádá o prostor kliniky,
  kterou řídí).

Když prostor nemá úsek ani odpovědnou osobu, ACS vztah nezná. Výchozí chování: bere se
jako **vlastní úsek** (uplatní se jen úrovně bez podmínky a „jen vlastní úsek“ — tj. jako
dosud jen nadřízený). Přísnější volba u matice *Prostor bez úseku … brát jako mimo vlastní
úsek* pošle takovou žádost i přes odpovědnou osobu (bez ní rozhodne administrátor) a OVBKŘ.

## Podmíněné úrovně matice

V editoru matice má každá úroveň:

- **Platí pro kategorie** — zaškrtnutí řadový / vedoucí / přímo pod ředitelem / ředitel
  (nic = pro všechny),
- **Vztah k prostoru** — vždy / jen vstup v rámci vlastního úseku / jen mimo vlastní úsek.

Úroveň, která pro cílového zaměstnance a prostor neplatí, se při průchodu přeskočí (i mezi
fázemi řetězu matic u skupin a parkování). Když neplatí žádná úroveň:

- matice s volbou **Bez platné úrovně schválit bez schvalovacího stupně** → položka je
  rovnou *schváleno* a jde do fronty realizace; v detailu má štítek *bez schvalovacího stupně*,
- jinak položku rozhoduje administrátor (jako položku bez matice).

Nové typy schvalovatelů:

- **Odpovědná osoba cílového úseku** — odpovědná osoba prostoru žádosti (nebo vedoucí
  úseku, kterému prostor patří). Je-li zároveň cílovým zaměstnancem nebo žadatelem,
  rozhoduje její nadřízený.
- **Konkrétní uživatel**, který je sám cílovým zaměstnancem žádosti (např. vedoucí OVBKŘ žádá
  kamery pro svůj úsek), se nahradí svým nadřízeným (z AD). Podání žádosti za jiného
  zaměstnance samoschválením není — schvalovatel rozhoduje dál.

V detailu žádosti je u položky vidět kategorie zaměstnance, úsek a odpovědná osoba prostoru,
odkud se vzaly (čtečka / místnost / patro / budova / vedoucí úseku) a zda jde o vlastní, nebo
cizí úsek; u čekající úrovně pak *Čeká na: Odpovědná osoba úseku: …* apod.

## Datový model

- `OrgUnit` (úsek): `Name`, `Code`, `ParentId`, `HeadEmployeeId`, `DepartmentPatterns`.
- `Employee.OrgUnitId` / `OrgUnitManual`, `Employee.Rank` (`Auto` / `Staff` / `Manager` /
  `Executive` / `Director`) / `RankManual`.
- `OrgUnitId` + `ResponsibleEmployeeId` na `Building`, `Floor`, `Corridor`, `Room`, `Reader`,
  `ReaderGroup` (rozhraní `IOwnedArea`).
- `ApprovalLevel.AppliesToRanks` (bitová maska), `ApprovalLevel.Scope`
  (`Always` / `InsideOwnUnit` / `OutsideOwnUnit`).
- `ApprovalMatrix.AutoApproveWhenNoLevels`, `ApprovalMatrix.TreatUnknownUnitAsOutside`.
- `ApproverKind.AreaOwner`; `AccessRequestItem.AutoApproved`.
- `SecurityRequest` (kamery / EZS) + `AccessRequestItem.SecurityRequestId`; stav
  `ManuallyConfirmed` u kamer znamená *realizováno*.
- Role `AppRole.IctAdmin`, `AppRole.Auditor`; nastavení `Security:MatrixId`.
- Migrace `ApprovalPolicy`.

Kód: `Acs.Infrastructure/Organization/OrgStructureService` (úseky z oddělení, kategorie
z hierarchie), `Acs.Infrastructure/Workflow/ApprovalContext` (vlastnictví prostoru, vztah
vlastní / cizí), `ApproverResolver` (odpovědná osoba, eskalace), `RequestWorkflowService`
(průchod platnými úrovněmi, schválení bez stupně, žádosti o kamery / EZS),
`MatrixTemplateService` (průvodce), `SecurityAdminService` (fronta ICT).

## Testy

`tests/Acs.Tests/FnmMatrixTests.cs` — každý řádek tabulek dokumentu jako scénář (kategorie ×
vlastní / cizí úsek → řetěz schvalovatelů), schválení bez stupně, přísný režim pro prostor
bez vlastníka, dědění vlastnictví, vlastní úsek podle linie nadřízených, parkování, kamery /
EZS včetně eskalace na nadřízeného vedoucího OVBKŘ a fronty ICT, odvození kategorií a úseků,
průvodce. `WebAppTests` — otevření nových stránek a průvodce přes UI.
