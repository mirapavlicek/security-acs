#!/usr/bin/env python3
"""Kontrola integračních specifikací.

Ověří, že oba OpenAPI dokumenty jsou platné a že si navzájem — a s popisem
v README — neodporují. Specifikace jsou dvě strany téhož kontraktu, takže
kterýkoli rozchod (jiné druhy identifikátorů, jiné stavy osoby, operace
vyjmenovaná v možnostech, ale bez definice) by se v provozu projevil jako
integrace, která „podle dokumentace“ funguje a přesto ne.

Použití:
    pip install openapi-spec-validator pyyaml
    python3 docs/integrace/kontrola-specifikaci.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

import yaml
from openapi_spec_validator import validate

BASE = Path(__file__).parent
NORTH = BASE / "acs-integration-api.yaml"
SOUTH = BASE / "connector-api.yaml"
README = BASE / "README.md"


def main() -> int:
    north, north_raw = load(NORTH)
    south, south_raw = load(SOUTH)
    readme = README.read_text(encoding="utf-8")

    problems: list[str] = []

    # --- platnost podle OpenAPI 3.1 ---
    for path, spec in ((NORTH, north), (SOUTH, south)):
        try:
            validate(spec)
        except Exception as error:  # noqa: BLE001 — chceme vypsat cokoli, co validátor řekne
            problems.append(f"{path.name} není platné OpenAPI: {error}")

    # --- společné výčty musí být na obou stranách shodné ---
    compare(
        problems,
        "druhy identifikátorů",
        set(north["components"]["schemas"]["CredentialType"]["enum"]),
        set(south["components"]["schemas"]["Credential"]["properties"]["type"]["enum"]),
    )
    compare(
        problems,
        "druhy událostí",
        set(north["components"]["schemas"]["EventType"]["enum"]),
        set(south["components"]["schemas"]["TargetEvent"]["properties"]["type"]["enum"]),
    )
    compare(
        problems,
        "stavy osoby",
        set(north["components"]["schemas"]["PersonStatus"]["enum"]),
        set(south["components"]["schemas"]["PersonUpsert"]["properties"]["status"]["enum"]),
    )

    # --- konektor nesmí v možnostech vyjmenovat operaci, kterou kontrakt nedefinuje ---
    defined = {
        operation["operationId"]
        for path_item in south["paths"].values()
        for operation in path_item.values()
        if isinstance(operation, dict) and "operationId" in operation
    }
    declared = set(
        south["components"]["schemas"]["ConnectorCapabilities"]["properties"]["operations"]["items"]["enum"]
    )
    if extra := declared - defined:
        problems.append(f"možnosti konektoru vyjmenovávají nedefinované operace: {sorted(extra)}")

    # --- pojmy, na které se README odvolává, musí ve specifikacích existovat ---
    for token in ("cacheTtlSeconds", "confidence", "X-Acs-Signature", "X-Acs-Timestamp",
                  "Idempotency-Key", "mealAccount", "traceId"):
        if token in readme and token not in north_raw:
            problems.append(f"README zmiňuje {token}, integrační API ho nedefinuje")

    for token in ("unknownTargets", "targetErrorCode", "applied", "removed"):
        if token in readme and token not in south_raw:
            problems.append(f"README zmiňuje {token}, kontrakt konektoru ho nedefinuje")

    # --- prodlevy opakování se uvádějí na dvou místech, musí souhlasit ---
    if re.search(r"1 min, 5, 30, 2 h, 12 h", readme) and not re.search(
        r"1 min, 5 min, 30 min, 2 h, 12 h", north_raw
    ):
        problems.append("prodlevy opakování v README neodpovídají popisu webhooku")

    # --- README odkazuje na oba kontrakty ---
    for path in (NORTH, SOUTH):
        if path.name not in readme:
            problems.append(f"README neodkazuje na {path.name}")

    if problems:
        print("NESHODY:")
        for problem in problems:
            print(f"  - {problem}")
        return 1

    print("Integrační specifikace jsou platné a konzistentní.")
    print(f"  {NORTH.name}: cest {len(north['paths'])}, schémat {len(north['components']['schemas'])}")
    print(f"  {SOUTH.name}: cest {len(south['paths'])}, operací {len(defined)}")
    return 0


def load(path: Path) -> tuple[dict, str]:
    raw = path.read_text(encoding="utf-8")
    return yaml.safe_load(raw), raw


def compare(problems: list[str], what: str, north: set[str], south: set[str]) -> None:
    if north != south:
        problems.append(f"{what} se rozcházejí: {sorted(north ^ south)}")


if __name__ == "__main__":
    sys.exit(main())
