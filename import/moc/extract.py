#!/usr/bin/env python3
"""
Extrakce místností, chodeb a čteček z DPS půdorysů budovy MOC (PDF s textovou vrstvou).

Popisek místnosti je v plánech blok tří řádků, např.:
    23-30506/01     ← číslo místnosti s pomlčkou (platí přednostně)
    04.5.07         ← sekundární „tečkové“ číslo (platí, když pomlčkové chybí)
    WC PERSONÁL     ← název místnosti (může být na dvou řádcích)

Čtečka je popisek ACS.NN u symbolu dveří; každý výskyt = jedna fyzická čtečka
(číslo označuje typ/okruh, ne unikátní zařízení). Čtečka se přiřadí k nejbližšímu
popisku místnosti; navíc se ukládá nejbližší nechodbová místnost jako alternativa.

Výstup: import/moc/rooms.json
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

PDF_DIR = Path(__file__).parent / "pdf"
OUT = Path(__file__).parent / "rooms.json"

WORD_RE = re.compile(
    r'<word xMin="([\d.]+)" yMin="([\d.]+)" xMax="([\d.]+)" yMax="([\d.]+)">(.*?)</word>'
)
DASH_NO = re.compile(r"^\d{2}-[0-9A-Z]{4,7}(?:/\d{1,2})?$")      # 23-30504, 23-502S9/01
DOT_NO = re.compile(r"^[0-9A-Z]{1,2}\.\d\.\d{2,3}[a-zA-Z]?$")     # 04.5.07, 1S.3.10, 06.2.605
READER = re.compile(r"^(ACS\.\d{2})")
LOWER = re.compile(r"[a-záčďéěíňóřšťúůýž]")
UPPER = re.compile(r"[A-ZÁČĎÉĚÍŇÓŘŠŤÚŮÝŽ]")


def is_name_word(text: str) -> bool:
    """Název místnosti je ve výkresech verzálkami (může mít čárku, závorku, číslo)."""
    if len(text) < 2 or DASH_NO.match(text) or DOT_NO.match(text) or READER.match(text):
        return False
    return bool(UPPER.search(text)) and not LOWER.search(text)

CORRIDOR_WORDS = ("CHODBA", "KORIDOR", "SCHODIŠTĚ", "SCHODY", "PŘEDSÍŇ",
                  "ZÁDVEŘÍ", "RAMPA", "VÝTAH", "VESTIBUL", "HALA")


@dataclass(frozen=True)
class Word:
    x0: float
    y0: float
    x1: float
    y1: float
    text: str

    @property
    def cx(self) -> float:
        return (self.x0 + self.x1) / 2


def read_words(pdf: Path) -> list[Word]:
    xml = subprocess.run(["pdftotext", "-bbox-layout", str(pdf), "-"],
                         capture_output=True, text=True, check=True).stdout
    return [Word(float(a), float(b), float(c), float(d), t.strip())
            for a, b, c, d, t in WORD_RE.findall(xml) if t.strip()]


def group_lines(words: list[Word], tol: float = 2.5) -> list[list[Word]]:
    rows: list[list[Word]] = []
    for w in sorted(words, key=lambda w: (w.y0, w.x0)):
        for row in rows:
            if abs(row[0].y0 - w.y0) < tol:
                row.append(w)
                break
        else:
            rows.append([w])
    return [sorted(r, key=lambda w: w.x0) for r in rows]


def parse_rooms(words: list[Word]) -> list[dict]:
    """Ke každému pomlčkovému číslu dohledá tečkové číslo a název pod ním."""
    rooms: list[dict] = []
    for anchor in (w for w in words if DASH_NO.match(w.text)):
        # Sloupec pod kotvou — popisek je zarovnaný na střed.
        below = [w for w in words
                 if abs(w.cx - anchor.cx) < 42 and 1.5 < w.y0 - anchor.y0 < 26]
        dotted, name_lines = None, []
        for line in group_lines(below):
            texts = [w.text for w in line]
            if dotted is None and any(DOT_NO.match(t) for t in texts):
                dotted = next(t for t in texts if DOT_NO.match(t))
                continue
            words_ok = [t for t in texts if is_name_word(t)]
            if words_ok:
                name_lines.append(" ".join(words_ok))

        # Bez tečkového čísla i názvu jde nejspíš o kód z rozpisky, ne o místnost.
        if dotted is None and not name_lines:
            continue

        name = " ".join(name_lines).strip() or None
        rooms.append({
            "number": anchor.text,                 # pomlčkové má přednost
            "numberDashed": anchor.text,
            "numberDotted": dotted,
            "name": name,
            "isCorridor": bool(name and any(k in name for k in CORRIDOR_WORDS)),
            "x": round(anchor.cx, 1),
            "y": round(anchor.y0, 1),
        })

    # Místnosti bez pomlčkového čísla (jen tečkové) — doplníme zvlášť.
    used_dotted = {r["numberDotted"] for r in rooms if r["numberDotted"]}
    for anchor in (w for w in words if DOT_NO.match(w.text) and w.text not in used_dotted):
        below = [w for w in words if abs(w.cx - anchor.cx) < 42 and 1.5 < w.y0 - anchor.y0 < 18]
        names = [" ".join(t.text for t in line if is_name_word(t.text))
                 for line in group_lines(below)]
        name = " ".join(n for n in names if n).strip() or None
        if not name:
            continue
        rooms.append({
            "number": anchor.text,                 # pomlčkové chybí → platí tečkové
            "numberDashed": None,
            "numberDotted": anchor.text,
            "name": name,
            "isCorridor": any(k in name for k in CORRIDOR_WORDS),
            "x": round(anchor.cx, 1),
            "y": round(anchor.y0, 1),
        })

    # Deduplikace podle čísla (stejný popisek se může v plánu opakovat).
    unique: dict[str, dict] = {}
    for r in rooms:
        unique.setdefault(r["number"], r)
    return sorted(unique.values(), key=lambda r: r["number"])


def parse_readers(words: list[Word], rooms: list[dict]) -> list[dict]:
    readers: list[dict] = []
    for w in words:
        m = READER.match(w.text)
        if not m:
            continue
        x, y = w.cx, w.y0
        entry = {"code": m.group(1), "x": round(x, 1), "y": round(y, 1),
                 "room": None, "roomDistance": None, "roomNonCorridor": None}
        if rooms:
            def dist(r: dict) -> float:
                return ((r["x"] - x) ** 2 + (r["y"] - y) ** 2) ** 0.5

            nearest = min(rooms, key=dist)
            entry["room"] = nearest["number"]
            entry["roomDistance"] = round(dist(nearest), 1)
            non_corr = [r for r in rooms if not r["isCorridor"]]
            if non_corr:
                alt = min(non_corr, key=dist)
                entry["roomNonCorridor"] = alt["number"]
        readers.append(entry)
    return readers


def floor_of(pdf: Path) -> str:
    m = re.search(r"PUDORYS[_ ]+([0-9]?[A-Z]{2})", pdf.name.upper())
    return m.group(1) if m else pdf.stem


def section_of(pdf: Path) -> str | None:
    m = re.search(r"PUDORYS[_ ]+[0-9]?[A-Z]{2}[_ ]+([AB])[_.]", pdf.name.upper())
    return m.group(1) if m else None


def main() -> int:
    pdfs = sorted(PDF_DIR.glob("MOC_DSPS_*.pdf"))
    if not pdfs:
        print("Nenalezeny žádné PDF v", PDF_DIR, file=sys.stderr)
        return 1

    floors = []
    for pdf in pdfs:
        words = read_words(pdf)
        rooms = parse_rooms(words)
        readers = parse_readers(words, rooms)
        floors.append({
            "file": pdf.name,
            "floor": floor_of(pdf),
            "section": section_of(pdf),
            "rooms": rooms,
            "readers": readers,
        })
        corridors = sum(1 for r in rooms if r["isCorridor"])
        named = sum(1 for r in rooms if r["name"])
        print(f"{floor_of(pdf):>3} {section_of(pdf) or '-':<2} "
              f"místností {len(rooms):>3} (chodeb {corridors:>2}, s názvem {named:>3}), "
              f"čteček {len(readers):>3}")

    OUT.write_text(json.dumps(floors, ensure_ascii=False, indent=1), encoding="utf8")
    print(f"\nCelkem: {len(floors)} výkresů, "
          f"{sum(len(f['rooms']) for f in floors)} místností, "
          f"{sum(len(f['readers']) for f in floors)} čteček → {OUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
