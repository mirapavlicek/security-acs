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

## Co import dělá

**Číselníky → Čtečky → Import čteček z tabulky EKV** (`/Admin/ImportReaders`),
nebo z příkazové řádky:

```bash
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - cely objekt.xlsx" --dry-run
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - cely objekt.xlsx"
dotnet run --project src/Acs.Web -- --import-readers "ctecky EKV - vytahy.xlsx"
```

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

Import je idempotentní: opakovaný běh existující čtečky (podle čísla) jen
aktualizuje. Náhled běží stejnou cestou v transakci, která se odvolá, takže
počty odpovídají ostrému běhu.

### Výsledek na budově MOC

Nad číselníkem z výkresů (651 čteček): 475 nových, 263 převzatých, 388
deaktivovaných, 4 založené části místností → **761 aktivních čteček se skutečným
číslem** (738 dveřních + 23 výtahových), 0 duplicit.

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
