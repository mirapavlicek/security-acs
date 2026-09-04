# Přístupové úrovně WIN-PAKu z ACS

Přístupová úroveň (access level) je ve WIN-PAKu skupina oprávnění: seznam
čteček a u každé časová zóna, ve které na ní úroveň platí. **Kartě se
přiděluje úroveň, ne čtečka.** ACS proto potřebuje pro každou čtečku, o kterou
se žádá, „její“ úroveň (`Reader.AccessLevelExternalId`) — tu pak při schválení
přidělí kartám držitele.

Dosud ACS úrovně jen četla a přidělovala; zakládat a upravovat se musely ve
WIN-PAKu a mapování na čtečky se psalo ručně. Teď je správa v ACS:
*Katalog → Úrovně*.

## Zrcadlo

ACS drží kopii úrovní (`AccessLevels`) a jejich složení (`AccessLevelEntries`:
čtečka + časová zóna). Zrcadlo plní **synchronizace**:

- tlačítkem na stránce Úrovně (běží **na pozadí** — u 55 úrovní je to 55 volání
  do WIN-PAKu a v HTTP požadavku by vypršela proxy; stránka ukazuje průběh
  a sama se obnovuje), nebo automaticky **spolu se synchronizací čteček**
  (stejný interval, nastavení *WIN-PAK → Synchronizace čteček*),
- seznam úrovní je v zrcadle hned po prvním volání, složení se doplňuje po
  jedné úrovni a ukládá průběžně — přerušení nic neztratí,
- seznam úrovní je jedno volání konektoru; složení (`GET
  /api/v1/access-levels/{name}/tree`) jedno volání na úroveň, proto se čte jen
  u nových a změněných úrovní — volba „včetně složení všech úrovní“ vynutí
  všechny,
- úroveň, která z WIN-PAKu zmizela, se v ACS jen označí jako zrušená (kvůli
  historii žádostí).

**Strom přístupů** vrací WIN-PAK jako text, jehož podobu příručka nepopisuje.
ACS ho ukládá celý a čte z něj čtečky a zóny podle známých názvů prvků
(`Reader`/`Entrance`, `HWDeviceID`, `TimeZoneName`…). Když mu nerozumí, úroveň
to ukáže („?“ v seznamu, varování v detailu) a surový strom je vidět v detailu
— pošlete ho vývoji, parser se doplní.

## Automatické mapování čteček

Úroveň s **jedinou čtečkou** je úroveň té čtečky. Po každé synchronizaci ACS
doplní `AccessLevelExternalId` čtečkám, které ho nemají a mají právě jednu
takovou úroveň. Víc kandidátů = rozhodne správce v detailu čtečky, kde se
úroveň vybírá ze zrcadla (ne opisuje).

Stránka Úrovně upozorní, kolik aktivních čteček z WIN-PAKu úroveň nemá —
žádosti o ně ACS do WIN-PAKu nezapíše.

## Založení, úprava, zrušení

Detail úrovně: název, popis, zaškrtnuté čtečky ACS (jen ty s id ve WIN-PAKu)
a u každé časová zóna z WIN-PAKu (`GET /api/v1/time-zones`, načítá se živě).
Uložení zapíše **celou definici jedním voláním** (`PUT
/api/v1/access-levels/{id}` → `AddUpdateAL`; `id = 0` zakládá) — WIN-PAK
přepíše složení, čtečky mimo seznam z úrovně zmizí. Po zápisu se zrcadlo
obnoví a úroveň s jedinou čtečkou se té čtečce rovnou namapuje.

Zrušení (`DELETE /api/v1/access-levels/{name}`) úroveň ve WIN-PAKu smaže —
karty, které ji měly, o ni přijdou (to říká i potvrzovací dialog). V ACS se
označí jako zrušená a čtečkám se odmapuje.

Vše se audituje (`access-levels-synced`, `access-level-created`,
`access-level-updated`, `access-level-deleted`).

## Co ACS zatím nedělá

- Časové zóny nezakládá ani neupravuje (jen vybírá z existujících); správa zón
  je v administraci konektoru (*Funkce → Časové zóny*).
- Skupiny čteček WIN-PAKu (`ReaderGroupIds`) a podúčty posílá prázdné.
- Přeřazení karet z rušené úrovně na jinou (`ReassignAccessLevel`) je jen
  v konektoru.
