# Import čteček z tabulek EKV

Číselník čteček se plní ze dvou zdrojů, které se liší spolehlivostí:

| Zdroj | Co dává | Spolehlivost |
|---|---|---|
| Výkresy DPS (`import/moc/rooms.json`) | strukturu budovy (patra, chodby, místnosti) a **odhad** čteček podle popisků „ACS.NN“ | dobrá pro místnosti, špatná pro čtečky |
| Tabulky „čtečky EKV“ z dokumentace skutečného provedení | **skutečné číslo každé čtečky**, místnost, do které vstupuje, a rozvaděč | autoritativní |

Popisek „ACS.41“ ve výkresu **není číslo čtečky, ale rozvaděče** — pod jedním je
4 až 53 čteček. Výkresový import proto čtečky pojmenoval kódem rozvaděče
a místnost odhadl podle nejbližšího popisku. Když se tabulky porovnaly s tím,
co z výkresů vzniklo, seděla místnost jen u 263 z 651 čteček; u 28 z 33 rozvaděčů
vyšel jiný počet čteček a rozvaděč ACS.27 (39 čteček) ve výkresech chyběl celý.

## Co je v tabulce

Dodávají se tři soubory, obsahově dva:

- **čtečky EKV – celý objekt – bez výtahů** (a totéž „po patrech“ s mezisoučty):
  738 dveřních čteček. Sloupce: podlaží, vstup do m.č., číslo čtečky EKV,
  poznámka, rozvaděč EKV, stavební objekt.
- **čtečky EKV – ovládání výtahů**: 23 čteček v kabinách výtahů. Sloupce v jiném
  pořadí; místo místnosti je funkce („blokování volby vybraných stanic“).

### Struktura čísla čtečky

Číslo `362002` se čte jako **`362` `00` `2`**:

- první tři číslice = rozvaděč (`ACS.01` → 360, `ACS.02` → 361, `ACS.03` → 362 …),
- další dvě = dveře v rámci rozvaděče,
- poslední = **strana dveří** (1 nebo 2).

Z 380 dveří má 358 čtečku z obou stran a v 337 případech vstupují obě strany do
**různých** místností — jsou to dveře mezi dvěma místnostmi. ACS to drží jako dvě
čtečky se společným pětimístným prefixem; číslo dveří a strana jsou v popisu.

Číslo je uložené v `Reader.DeviceNumber` a je i na začátku názvu, takže se dá
hledat. Až se čtečky budou párovat s WIN-PAK, je to klíč — instalační firma
konfigurovala WIN-PAK podle této dokumentace.

## Polohy čteček z výkresů EKV

K tabulkám patří i výkresy **„čtečky EKV – celý objekt“** (jeden list na patro).
Na plánu je u každé čtečky třířádkový popisek:

```
čtečka 362021
vstup do: 23-02306
rozv.:  ACS.03
```

Skript `import/moc/extract_ekv.py` z něj vytáhne polohu každé čtečky a uloží ji
do `import/moc/ekv-readers.json`; import ji pak čtečce nastaví jako polohu ve
výkresu (`Reader.SourceX/SourceY`), ze které se generuje plán patra:

```bash
python3 import/moc/extract_ekv.py import/moc/pdf-ekv/*.pdf
```

Tři věci, na které skript musel přijít a které se hodí vědět při další dodávce:

- **Číslo čtečky je na listu dvakrát** — v popisku u čtečky a ve výpisové tabulce
  na okraji. Kotvou je slovo „čtečka“ těsně vlevo od čísla; v tabulce není.
- **Listy EKV mají stejné měřítko jako původní půdorysy, ale jiný počátek** —
  na 1PP jsou všechny místnosti posunuté přesně o 379 pt, části B o ~1000 pt v y.
  Posun se pro každou dvojici list → patro ACS spočítá jako medián rozdílu poloh
  místností, které jsou na obou výkresech, a polohy se přepočtou. Patro s méně
  než třemi společnými místnostmi (TP) se přenést nedá.
- **Část popisků je vyvedená šipkou mimo půdorys** (řada popisků pod plánem).
  Polohu čtečky nese čára, ne text — popisek dál než 400 pt od popisku své
  místnosti se proto nepřenese a plán čtečku položí do středu její místnosti.
  Na MOC je to 49 z 761 čteček.

Popisky na výkresu se s tabulkou shodují v místnosti u 732 z 735 čteček
(zbylé tři jsou „střecha“ zkrácená na jedno slovo) — výkres a tabulka říkají
totéž.

Výsledek na MOC: 695 čteček s polohou; čtečka je od popisku své místnosti
medián 108 pt, 90 % pod 235 pt — stejně jako na samotném výkresu.

## Co import dělá

**Číselníky → Čtečky → Import čteček z tabulky EKV** (`/Admin/ImportReaders`),
nebo z příkazové řádky:

```bash
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - cely objekt.xlsx" --dry-run
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - cely objekt.xlsx" \
  --positions import/moc/ekv-readers.json
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - vytahy.xlsx" \
  --positions import/moc/ekv-readers.json
```

Soubor s polohami je volitelný; když je zadaný, je pro polohy autoritativní —
čtečka, která v něm není, polohu ztratí a plán ji položí do místnosti.

Pro každý řádek tabulky:

1. **Najde místnost** podle čísla („vstup do m.č.“) mezi místnostmi i chodbami.
   Výkresy ukazují místnost i na listech sousedních pater, takže jedno číslo bývá
   v číselníku vícekrát — vybere se záznam na patře, které je v čísle zakódované
   (`23-02xxx` = 2PP, `23-00xxx` = 1NP, `23-10xxx` = 2NP … `23-60xxx` = TP). Když
   zbývá víc záznamů (části A/B téhož patra), dá se přednost tomu, u kterého
   výkres ukazoval čtečku stejného rozvaděče.
2. **Části místností** typu `23-00502/01`, které ve výkresech nejsou, založí na
   patře základní místnosti.
3. **Převezme čtečku z výkresů**, když se shoduje rozvaděč i místnost — je to tentýž
   záznam (stejné Id), takže žádosti na něj navázané zůstávají. Doplní číslo,
   přejmenuje ho a původní název nechá v popisu („Výkres: …“).
4. Jinak čtečku **založí**.
5. Na konci **deaktivuje** čtečky z výkresů, které v tabulce protějšek nemají —
   to jsou odhady, které neseděly. Nemažou se, historie zůstává. U výtahové
   tabulky se nedeaktivuje nikdy, protože dveřní čtečky neobsahuje.
   Deaktivované čtečky se nekreslí do plánů ani nenabízejí v editoru plánu.

Import je idempotentní: opakovaný běh existující čtečky (podle čísla) jen
aktualizuje. Náhled běží stejnou cestou v transakci, která se odvolá, takže
počty odpovídají ostrému běhu.

### Výsledek na budově MOC

Nad číselníkem z výkresů (651 čteček): 475 nových, 263 převzatých, 388
deaktivovaných, 4 založené části místností → **761 aktivních čteček se skutečným
číslem** (738 dveřních + 23 výtahových), 0 duplicit, 695 s polohou z výkresů EKV.
Vygenerované plány pak umístí 718 čteček (aktivní čtečky navázané na místnost
nebo chodbu).

## Úklid deaktivovaných čteček

Deaktivované odhady z půdorysů v číselníku zůstávají (nekreslí se do plánů
a nenabízejí v žádostech). Kdo je chce mít pryč: **Číselníky → Čtečky**, filtr
**jen neaktivní**, tlačítko **Smazat neaktivní podle filtru**. Maže se jen to,
co nemá historii — neaktivní čtečka bez žádosti, skupiny a závislosti. Cokoli
s vazbou zůstane neaktivní a hláška to vypíše jmenovitě; u přístupu musí jít
zpětně dohledat, ke které čtečce byl. Aktivní čtečky se takhle smazat nedají.

## Co zůstává na ruční dořešení

Import vypíše místnosti, které v číselníku nenašel; čtečky k nim založí bez
místnosti, aby se neztratily. Na MOC je to 18 čísel:

| Důvod | Čísla |
|---|---|
| Místnost ve výkresech chybí | 23-10285, 23-10287, 23-10288, 23-10349, 23-30147, 23-30560, 23-40147, 23-40212, 23-60005 |
| Část místnosti, jejíž základ chybí | 23-10287/02, 23-30201/01, 23-40212/01, 23-50213/01 |
| Překlep v tabulce (šest číslic) | 23-2025, 23-401073, 23-550122 |
| Není místnost | střecha SO101-M1, střecha SO101.02 |

Ruční postup: založit místnost na správném patře (u překlepů po ověření
s projektantem), pak čtečce v editaci nastavit místnost. Střešní čtečky se hodí
navázat na chodbu nebo nechat bez místnosti se zařazením do skupiny čteček.

Výtahové čtečky nemají místnost — jsou v kabině a řídí volbu stanic. Pro
žádosti se hodí dát je do skupiny čteček „Výtahy“.
