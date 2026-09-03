#!/usr/bin/env python3
"""Pozice čteček z výkresů „čtečky EKV – celý objekt“.

Výkresy z dokumentace skutečného provedení (jeden list na patro) mají u každé
čtečky popisek se skutečným šestimístným číslem. Skript z nich vytáhne, kde
které číslo leží, přepočte souřadnice do soustavy původních půdorysů
(rooms.json) a uloží to do ekv-readers.json. Import čteček
(`--import-readers … --positions ekv-readers.json`) pak čtečkám nastaví polohu,
ze které se generuje plán patra.

Proč přepočet: listy EKV mají stejné měřítko jako původní půdorysy, ale jiný
počátek — na 1PP jsou všechny místnosti posunuté přesně o 379 pt, na jiných
patrech o jinou konstantu, a části A/B téhož patra (v ACS samostatná patra)
mají každá svůj posun. Posun se pro každou dvojici list → patro ACS spočítá
jako medián rozdílu poloh místností, které jsou na obou výkresech.

Použití:
    python3 import/moc/extract_ekv.py import/moc/pdf-ekv/*.pdf

Vyžaduje pdftotext (poppler-utils) a rooms.json ve stejném adresáři.
"""

from __future__ import annotations

import html
import json
import math
import re
import statistics
import subprocess
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).parent
ROOMS = HERE / "rooms.json"
OUT = HERE / "ekv-readers.json"

WORD_RE = re.compile(
    r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">(.*?)</word>'
)
DEVICE_RE = re.compile(r"^3\d{5}$")
ROOM_RE = re.compile(r"^23-[0-9A-Z]{5}(?:/\d{1,2})?$")

# Pořadové číslo listu („…_260902 4.pdf“) → označení patra ve výkresech.
SHEET_FLOOR = {1: "2PP", 2: "1PP", 3: "1NP", 4: "2NP", 5: "3NP", 6: "4NP", 7: "5NP", 8: "6NP", 9: "TP"}

# Kolik společných místností je potřeba, aby se posunu dalo věřit.
MIN_SHARED = 3

# Kolik čísel místností v jednom svislém sloupci už znamená výpisovou tabulku.
LEGEND_MIN = 15

# Popisek dál než tohle od popisku své místnosti není u čtečky, ale vyvedený
# šipkou mimo půdorys (řada popisků pod plánem). Polohu čtečky pak nese čára,
# ne text — taková poloha se nepřenese a plán čtečku položí do její místnosti.
MAX_ROOM_DISTANCE = 400


def sheet_number(pdf: Path) -> int | None:
    """Samostatná číslice listu („…_260902 4.pdf“, „…_260902_4_c4a8.pdf“) — poslední v názvu."""
    matches = re.findall(r"(?:^|[ _-])(\d)(?=[ _-]|\.pdf$)", pdf.name, flags=re.IGNORECASE)
    return int(matches[-1]) if matches else None


def acs_floor_name(floor: str, section: str | None) -> str:
    """Název patra tak, jak ho založil import z výkresů: „2NP A“, „1PP“."""
    return f"{floor} {section}" if section else floor


def read_words(pdf: Path) -> list[tuple[float, float, float, float, str]]:
    xml = subprocess.run(
        ["pdftotext", "-bbox-layout", str(pdf), "-"],
        capture_output=True, text=True, check=True,
    ).stdout
    return [
        (float(x0), float(y0), float(x1), float(y1), html.unescape(text).strip())
        for x0, y0, x1, y1, text in WORD_RE.findall(xml)
    ]


def read_callouts(words: list[tuple[float, float, float, float, str]]) -> dict[str, dict]:
    """Popisky čteček na plánu.

    Popisek má tři řádky — „čtečka 362021 / vstup do: 23-02306 / rozv.: ACS.03“.
    Kotvou je slovo „čtečka“ těsně vlevo od čísla na stejném řádku; ve výpisové
    tabulce na okraji listu u čísla není, takže se tabulka sama vyloučí.
    """
    numbers = [w for w in words if DEVICE_RE.match(w[4])]
    anchors = [w for w in words if w[4].lower() == "čtečka"]
    rooms = [w for w in words if ROOM_RE.match(w[4])]

    callouts: dict[str, dict] = {}
    for x0, y0, x1, y1, number in numbers:
        cy = (y0 + y1) / 2
        if not any(abs((a[1] + a[3]) / 2 - cy) < 4 and 0 < x0 - a[2] < 25 for a in anchors):
            continue

        room = next(
            (r[4] for r in rooms if 6 < (r[1] + r[3]) / 2 - cy < 20 and abs(r[0] - x0) < 40),
            None,
        )
        callouts[number] = {"x": (x0 + x1) / 2, "y": cy, "room": room}
    return callouts


def read_plan_rooms(words: list[tuple[float, float, float, float, str]]) -> dict[str, tuple[float, float]]:
    """Popisky místností na plánu — bez výpisové tabulky a bez řádků v popiscích čteček."""
    vstup = {(round(w[0]), round(w[1])) for w in words if w[4] == "vstup"}
    columns: dict[int, int] = defaultdict(int)
    for w in words:
        if ROOM_RE.match(w[4]):
            columns[round(w[0] / 20)] += 1
    legend = {column for column, count in columns.items() if count >= LEGEND_MIN}

    occurrences: dict[str, list[tuple[float, float]]] = defaultdict(list)
    for x0, y0, x1, y1, text in words:
        if not ROOM_RE.match(text) or round(x0 / 20) in legend:
            continue
        # V popisku čtečky je před číslem místnosti „vstup do:“ o ~38 pt vlevo.
        if any(abs(vx - (x0 - 38)) < 12 and abs(vy - y0) < 4 for vx, vy in vstup):
            continue
        occurrences[text].append(((x0 + x1) / 2, (y0 + y1) / 2))

    return {room: points[0] for room, points in occurrences.items() if len(points) == 1}


def load_reference() -> dict[str, dict[str, tuple[float, float]]]:
    """Polohy místností z původních půdorysů: patro ACS → číslo místnosti → (x, y)."""
    reference: dict[str, dict[str, tuple[float, float]]] = defaultdict(dict)
    for sheet in json.loads(ROOMS.read_text(encoding="utf-8")):
        floor_name = acs_floor_name(sheet["floor"], sheet.get("section"))
        for room in sheet["rooms"]:
            reference[floor_name].setdefault(room["number"], (room["x"], room["y"]))
    return reference


def offsets_for_sheet(
    plan_rooms: dict[str, tuple[float, float]],
    floor: str,
    reference: dict[str, dict[str, tuple[float, float]]],
) -> dict[str, tuple[float, float, int]]:
    """Posun (dx, dy) z listu EKV do soustavy každého patra ACS téhož podlaží."""
    offsets: dict[str, tuple[float, float, int]] = {}
    for floor_name, rooms in reference.items():
        if not floor_name.startswith(floor):
            continue
        shared = [(rooms[r], plan_rooms[r]) for r in plan_rooms if r in rooms]
        if len(shared) < MIN_SHARED:
            continue
        dx = statistics.median(old[0] - new[0] for old, new in shared)
        dy = statistics.median(old[1] - new[1] for old, new in shared)
        offsets[floor_name] = (dx, dy, len(shared))
    return offsets


def main(paths: list[str]) -> int:
    if not paths:
        print(__doc__)
        return 2

    reference = load_reference()
    positions: dict[str, list[dict]] = defaultdict(list)
    skipped = 0
    far = 0

    for path in sorted(paths, key=lambda p: sheet_number(Path(p)) or 0):
        pdf = Path(path)
        sheet = sheet_number(pdf)
        floor = SHEET_FLOOR.get(sheet or 0)
        if floor is None:
            print(f"přeskakuji {pdf.name}: z názvu nejde určit list", file=sys.stderr)
            continue

        words = read_words(pdf)
        callouts = read_callouts(words)
        plan_rooms = read_plan_rooms(words)
        offsets = offsets_for_sheet(plan_rooms, floor, reference)
        if not offsets:
            print(f"{pdf.name}: list {sheet} = {floor}, čteček {len(callouts)} — bez společných místností, "
                  f"polohy se nepřenesou", file=sys.stderr)
            skipped += len(callouts)
            continue

        placed = 0
        for number, callout in callouts.items():
            # Patro ACS podle místnosti z popisku; bez ní (výtahy) to patro, do kterého bod padne.
            target = next(
                (name for name, rooms in reference.items() if callout["room"] in rooms and name in offsets),
                None,
            )
            candidates = [target] if target else list(offsets)
            for floor_name in candidates:
                dx, dy, _ = offsets[floor_name]
                x, y = callout["x"] + dx, callout["y"] + dy
                if target is None and not inside(reference[floor_name], x, y):
                    continue
                room_position = reference[floor_name].get(callout["room"] or "")
                if room_position and math.hypot(x - room_position[0], y - room_position[1]) > MAX_ROOM_DISTANCE:
                    far += 1
                    break
                positions[number].append({"floor": floor_name, "x": round(x, 1), "y": round(y, 1), "room": callout["room"]})
                placed += 1
                break
            else:
                skipped += 1

        detail = ", ".join(f"{name}: {n} společných, posun ({dx:.0f}, {dy:.0f})" for name, (dx, dy, n) in offsets.items())
        print(f"{pdf.name}: list {sheet} = {floor}, čteček {len(callouts)}, umístěno {placed} — {detail}")

    OUT.write_text(json.dumps(positions, ensure_ascii=False, indent=1, sort_keys=True), encoding="utf-8")
    multi = sum(1 for v in positions.values() if len(v) > 1)
    print(f"\nuloženo {OUT}: čteček {len(positions)}, z toho na více patrech {multi} (výtahy); "
          f"bez polohy {skipped}, popisek vyvedený mimo místnost {far} (plán je položí do místnosti)")
    return 0


def inside(rooms: dict[str, tuple[float, float]], x: float, y: float, margin: float = 150) -> bool:
    xs = [p[0] for p in rooms.values()]
    ys = [p[1] for p in rooms.values()]
    return min(xs) - margin <= x <= max(xs) + margin and min(ys) - margin <= y <= max(ys) + margin


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
