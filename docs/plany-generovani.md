# Generování plánů pater

Plán patra se dá sestavit na tlačítko z dat, která už v systému jsou. Bez toho
by správce musel v editoru ručně přetáhnout každou místnost — jen budova MOC má
1 410 místností a 651 čteček ve 13 patrech.

## Kde se generuje

| Kde | Rozsah | Cesta |
|-----|--------|-------|
| Budovy → rozbalená budova → **Plány pater** | všechna patra budovy | `/Catalog/Places` |
| Editor plánu patra → **Vygenerovat plán z dat** | jedno patro | `/Catalog/FloorPlan/{id}` |

Na obou místech jsou dvě tlačítka:

- **Generuj chybějící** — doplní jen prvky bez pozice a ruční práci nechá být.
  Tohle je bezpečná volba, dá se pouštět opakovaně.
- **Generuj celé patro / všechny znovu** — přerovná vše včetně ručně
  umístěných prvků (ptá se na potvrzení).

Vygenerované rozvržení je výchozí návrh. Cokoli se v editoru dá dál posunout
myší a uložit.

## Jak generátor rozhoduje

`PlanGenerationService` (`src/Acs.Infrastructure/Plans/PlanGenerationService.cs`)
má dva režimy a vybírá si podle toho, co má patro k dispozici:

### Z výkresů (`FromDrawing`)

Import z projektových výkresů DPS ukládá u každé místnosti a čtečky polohu
popisku ve výkresu (`Room.SourceX/SourceY`, `Reader.SourceX/SourceY`,
souřadnice PDF). Generátor je přepočte na procenta plochy plánu:

1. Z **všech** prvků patra spočítá ohraničující obdélník. Bere i prvky, které se
   právě negenerují — jinak by se doplňovaný prvek vešel do jiného měřítka než
   ten už umístěný.
2. Souřadnice lineárně přeškáluje na 4–96 % (okraj, ať prvky nelepí na hranu).
3. Velikost obdélníku místnosti odvodí z **mediánu vzdálenosti k nejbližšímu
   sousedovi**. Průměrná hustota by nestačila: patra mají místnosti nahloučené
   v křídlech a jinde volnou plochu, takže box spočítaný z průměru by se
   v hustých místech překrýval.
4. Popisek ve výkresu je uprostřed místnosti, proto se obdélník vycentruje na
   souřadnici.
5. Čtečka se umístí podle své polohy ve výkresu; když ji nemá, položí se do
   středu své místnosti.

Výsledek odpovídá skutečnému rozložení budovy — poznají se křídla, chodby
i hloučky technických místností.

### Schéma podle chodeb (`Schematic`)

Když patro polohy z výkresů nemá (vzniklo ručně nebo starším importem), sestaví
se čitelné schéma: každá chodba je jeden vodorovný pás a její místnosti jdou
v pásu za sebou. Místnosti bez chodby skončí v pásu navíc. Čtečka místnosti se
položí na její hranu (u dveří), čtečka chodby do pásu své chodby.

Schéma neodpovídá skutečnému rozložení, ale je přehledné a hlavně se dá dál
ručně upravit.

## Čitelnost: přiblížení a popisky

Do jedné obrazovky se 247 místností jednoho patra čitelně nevejde, takže plán
(prohlížeč `/Plans` i editor) má **přiblížení a posun**:

- kolečko myši přibližuje k bodu pod kurzorem, tažení podkladu posouvá,
- tlačítka `+`, `−`, `Celé patro`,
- **popisky se dopisují podle přiblížení** — při pohledu na celé patro je vidět
  jen struktura, po přiblížení čísla místností a při větším přiblížení i kódy
  čteček,
- celý název místnosti i čtečky je vždy v bublině po najetí myší,
- kroužky čteček a texty zůstávají na obrazovce stejně velké, aby z nich po
  přiblížení nebyly kotouče.

Společná logika je v `src/Acs.Web/wwwroot/js/plan-view.js`.

V prohlížeči plánů jsou navíc **zeleně zvýrazněné místnosti, kam má přihlášený
přístup** (má aktivní grant na některou jejich čtečku), takže i při oddáleném
pohledu bez popisků je vidět, kam smí.

## Postup pro novou budovu

1. Naimportovat výkresy: `/Admin/ImportPlan` nebo
   `Acs.Web --import-plan rooms.json --building MOC`. Import uloží strukturu
   i souřadnice z výkresů.
2. Volitelně nahrát podkladový obrázek patra (Budovy → patro → nahrát schéma).
   Generátor ho nepotřebuje, plán funguje i bez podkladu.
3. Budovy → rozbalit budovu → **Generuj chybějící plány**.
4. Zkontrolovat v `/Plans` a případně doladit v editoru patra.

## Audit

Generování se zapisuje do auditu jako `floor-plan-generated` (jedno patro)
a `building-plans-generated` (celá budova) s počty umístěných prvků a použitým
režimem.
