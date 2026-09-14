#!/usr/bin/env python3
"""Generate and check Nyx's pinned Genshin artifact mapping."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from decimal import Decimal
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
DEFAULT_OUTPUT = REPOSITORY_ROOT / "contracts" / "genshin-artifact-map-7.0-v1.json"

RAW_COMMIT = "26df1dfbdf05a82bbb1d97506859f3e1c40718d8"
RAW_REPOSITORY = "https://gitlab.com/Dimbreath/animegamedata2"
OPTIMIZER_COMMIT = "984d82cda1e37a3a634ab14d2059b6ad91b90a4a"
OPTIMIZER_REPOSITORY = "https://github.com/frzyc/genshin-optimizer"

RAW_FILES = (
    (
        "ExcelBinOutput/ReliquaryExcelConfigData.json",
        "1b0ea4e5642f183d579e1f2701359a5e5afebfc886f7379d0f6ddf3dc7d9b4e5",
        4352,
    ),
    (
        "ExcelBinOutput/ReliquaryMainPropExcelConfigData.json",
        "c7c9ea5520fd090a090c0c7a12e750ac85fd80985a687194a30b9aa254d5c60b",
        66,
    ),
    (
        "ExcelBinOutput/ReliquaryAffixExcelConfigData.json",
        "0e1f1461d86597b3126b4f9ed61ee8975a7839e47111060458e51ae1b756bc39",
        350,
    ),
)
OPTIMIZER_FILES = (
    (
        "libs/gi/dm/src/mapping/artifact.ts",
        "0619c7e58d77d04c5f3da37649f4bb860dbe6d9d2c18aeec016ee6ca16facda3",
    ),
    (
        "libs/gi/consts/src/artifact.ts",
        "704ea84c1555e999ad6057e822e29922c58cfbc0cd7d8c52a616af9d5fc35781",
    ),
    (
        "libs/gi/dm/src/dm/character/AvatarExcelConfigData_idmap_gen.json",
        "1c8f30d9aa78c0ad8afcd3f27bb3c0cecb6e26409174c6238a476d15a7b3c12e",
    ),
    (
        "libs/gi/consts/src/character.ts",
        "1594571fb4a96c184f99e0f424313ff2c1ea8c749abd50a1b38f1dfde2962fdc",
    ),
)

PROPERTY_KEYS = {
    "FIGHT_PROP_HP": "hp",
    "FIGHT_PROP_HP_PERCENT": "hp_",
    "FIGHT_PROP_ATTACK": "atk",
    "FIGHT_PROP_ATTACK_PERCENT": "atk_",
    "FIGHT_PROP_DEFENSE": "def",
    "FIGHT_PROP_DEFENSE_PERCENT": "def_",
    "FIGHT_PROP_ELEMENT_MASTERY": "eleMas",
    "FIGHT_PROP_CHARGE_EFFICIENCY": "enerRech_",
    "FIGHT_PROP_CRITICAL": "critRate_",
    "FIGHT_PROP_CRITICAL_HURT": "critDMG_",
    "FIGHT_PROP_PHYSICAL_ADD_HURT": "physical_dmg_",
    "FIGHT_PROP_WIND_ADD_HURT": "anemo_dmg_",
    "FIGHT_PROP_ROCK_ADD_HURT": "geo_dmg_",
    "FIGHT_PROP_ELEC_ADD_HURT": "electro_dmg_",
    "FIGHT_PROP_WATER_ADD_HURT": "hydro_dmg_",
    "FIGHT_PROP_FIRE_ADD_HURT": "pyro_dmg_",
    "FIGHT_PROP_ICE_ADD_HURT": "cryo_dmg_",
    "FIGHT_PROP_GRASS_ADD_HURT": "dendro_dmg_",
    "FIGHT_PROP_HEAL_ADD": "heal_",
}
MAIN_KEYS = {
    "hp",
    "hp_",
    "atk",
    "atk_",
    "def_",
    "eleMas",
    "enerRech_",
    "critRate_",
    "critDMG_",
    "physical_dmg_",
    "anemo_dmg_",
    "geo_dmg_",
    "electro_dmg_",
    "hydro_dmg_",
    "pyro_dmg_",
    "cryo_dmg_",
    "dendro_dmg_",
    "heal_",
}
SUBSTAT_KEYS = {
    "hp",
    "hp_",
    "atk",
    "atk_",
    "def",
    "def_",
    "eleMas",
    "enerRech_",
    "critRate_",
    "critDMG_",
}
PERCENT_KEYS = {"hp_", "atk_", "def_", "enerRech_", "critRate_", "critDMG_"}
UNSUPPORTED_SET_IDS = {15000, 15004, 15012}
EXCLUSION_COUNTS = {"rarity12": 625, "unsupportedSets": 175, "missingSets": 32, "unexplained": 0}
EXPECTED_COUNTS = {
    "items": 3520,
    "lowRarityItems": 625,
    "mainProps": 56,
    "affixes": 198,
    "locationIds": 124,
    "locationKeys": 119,
}
EXPECTED_SIZE = 613555
EXPECTED_SHA256 = "377e333336e6a94d01785612533c4241a83e49e1d414efe283e1458fefe78b1b"


class MappingError(ValueError):
    pass


def reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise MappingError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def reject_constant(value: str) -> None:
    raise MappingError(f"non-finite JSON number: {value}")


def normalized_bytes(path: Path) -> bytes:
    try:
        data = path.read_bytes()
    except OSError as error:
        raise MappingError(f"cannot read {path}: {error}") from error
    if b"\r" in data.replace(b"\r\n", b""):
        raise MappingError(f"{path} contains a bare carriage return")
    return data.replace(b"\r\n", b"\n")


def parse_json(path: Path) -> Any:
    data = normalized_bytes(path)
    try:
        return json.loads(
            data.decode("utf-8"),
            parse_float=Decimal,
            object_pairs_hook=reject_duplicate_keys,
            parse_constant=reject_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, MappingError) as error:
        raise MappingError(f"invalid JSON in {path}: {error}") from error


def checked_file(root: Path, relative: str, expected_hash: str) -> tuple[Path, bytes]:
    path = root / Path(relative)
    data = normalized_bytes(path)
    actual_hash = hashlib.sha256(data).hexdigest()
    if actual_hash != expected_hash:
        raise MappingError(f"{relative} hash changed: {actual_hash}")
    return path, data


def checked_source_json(root: Path, relative: str, expected_hash: str, expected_rows: int) -> Any:
    path, data = checked_file(root, relative, expected_hash)
    try:
        value = json.loads(
            data.decode("utf-8"),
            parse_float=Decimal,
            object_pairs_hook=reject_duplicate_keys,
            parse_constant=reject_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, MappingError) as error:
        raise MappingError(f"invalid JSON in {path}: {error}") from error
    if not isinstance(value, list) or len(value) != expected_rows:
        raise MappingError(f"{relative} must contain {expected_rows} rows")
    return value


def unique_row_ids(rows: list[dict[str, Any]], label: str) -> None:
    seen: set[int] = set()
    for row in rows:
        raw_id = row.get("id")
        if isinstance(raw_id, bool) or not isinstance(raw_id, int) or raw_id <= 0:
            raise MappingError(f"{label} contains an invalid id")
        if raw_id in seen:
            raise MappingError(f"{label} contains duplicate id {raw_id}")
        seen.add(raw_id)


def parse_ts_map(text: str, export_name: str) -> dict[int, str]:
    match = re.search(rf"export const {export_name}[^=]*= \{{(.*?)\}} as const", text, re.DOTALL)
    if not match:
        raise MappingError(f"missing {export_name}")
    rows = re.findall(r"^\s*(\d+):\s*'([^']+)',?\s*$", match.group(1), re.MULTILINE)
    result: dict[int, str] = {}
    for raw_id, value in rows:
        key = int(raw_id)
        if key in result:
            raise MappingError(f"duplicate {export_name} id {key}")
        result[key] = value
    if not result:
        raise MappingError(f"empty {export_name}")
    return result


def parse_slot_map(text: str) -> dict[str, str]:
    match = re.search(r"export const artifactSlotMap[^=]*=\s*\{(.*?)\}\s*as const", text, re.DOTALL)
    if not match:
        raise MappingError("missing artifactSlotMap")
    rows = re.findall(r"^\s*(EQUIP_[A-Z]+):\s*'([^']+)',?\s*$", match.group(1), re.MULTILINE)
    result: dict[str, str] = {}
    for key, value in rows:
        if key in result:
            raise MappingError(f"duplicate artifact slot {key}")
        result[key] = value
    if len(result) != 5:
        raise MappingError("artifactSlotMap must contain five slots")
    return result


def parse_string_array(text: str, export_name: str, expected_count: int) -> set[str]:
    match = re.search(rf"export const {re.escape(export_name)}\s*=\s*\[(.*?)\]\s*as const", text, re.DOTALL)
    if not match:
        raise MappingError(f"missing {export_name}")
    values = re.findall(r"'([^']+)'", match.group(1))
    if len(values) != expected_count or len(set(values)) != len(values):
        raise MappingError(f"{export_name} changed")
    return set(values)


def parse_optimizer_sources(root: Path) -> tuple[dict[int, str], dict[str, str], dict[str, str], set[str]]:
    _, artifact_data = checked_file(root, OPTIMIZER_FILES[0][0], OPTIMIZER_FILES[0][1])
    _, artifact_consts = checked_file(root, OPTIMIZER_FILES[1][0], OPTIMIZER_FILES[1][1])
    character_id_path, _ = checked_file(root, OPTIMIZER_FILES[2][0], OPTIMIZER_FILES[2][1])
    _, character_consts = checked_file(root, OPTIMIZER_FILES[3][0], OPTIMIZER_FILES[3][1])
    artifact_text = artifact_data.decode("utf-8")
    constants_text = artifact_consts.decode("utf-8")
    sets = parse_ts_map(artifact_text, "artifactIdMap")
    slots = parse_slot_map(artifact_text)
    expected_sets = parse_string_array(constants_text, "allArtifactSetKeys", 63)
    expected_slots = parse_string_array(constants_text, "allArtifactSlotKeys", 5)
    expected_main_keys = parse_string_array(constants_text, "allMainStatKeys", 18)
    expected_substat_keys = parse_string_array(constants_text, "allSubstatKeys", 10)
    if set(sets.values()) != expected_sets or set(slots.values()) != expected_slots:
        raise MappingError("Optimizer artifact set or slot keys changed")
    if MAIN_KEYS != expected_main_keys or SUBSTAT_KEYS != expected_substat_keys:
        raise MappingError("Optimizer artifact stat keys changed")
    if not re.search(r"export const allArtifactRarityKeys\s*=\s*\[\s*5,\s*4,\s*3\s*\]\s*as const", constants_text):
        raise MappingError("Optimizer artifact rarity keys changed")
    character_keys = parse_string_array(character_consts.decode("utf-8"), "nonTravelerCharacterKeys", 119)
    character_ids = parse_json(character_id_path)
    if not isinstance(character_ids, dict):
        raise MappingError("character id map must be an object")
    for key, value in character_ids.items():
        if not isinstance(key, str) or not key.isdecimal() or int(key) <= 0 or not isinstance(value, str):
            raise MappingError("character id map contains an invalid row")
    return sets, slots, character_ids, character_keys


def number(value: Decimal) -> int | float:
    if not isinstance(value, Decimal) or not value.is_finite():
        raise MappingError("affix value must be a finite decimal")
    if value == value.to_integral_value():
        return int(value)
    return float(value)


def build_items(
    rows: list[dict[str, Any]], sets: dict[int, str], slots: dict[str, str]
) -> tuple[dict[str, dict[str, Any]], list[int], dict[str, int]]:
    unique_row_ids(rows, "artifact rows")
    items: dict[str, dict[str, Any]] = {}
    low_rarity_ids: list[int] = []
    exclusions = {"rarity12": 0, "unsupportedSets": 0, "missingSets": 0, "unexplained": 0}
    for row in rows:
        rank = row.get("rankLevel")
        set_id = row.get("setId")
        if rank in (1, 2) and set_id in sets:
            low_rarity_ids.append(row["id"])
            exclusions["rarity12"] += 1
            continue
        if rank in (1, 2) and "setId" not in row:
            exclusions["missingSets"] += 1
            continue
        if rank in (3, 4, 5) and set_id in UNSUPPORTED_SET_IDS:
            exclusions["unsupportedSets"] += 1
            continue
        if rank not in (3, 4, 5) or set_id not in sets or row.get("equipType") not in slots:
            exclusions["unexplained"] += 1
            continue
        item_id = row["id"]
        for field in ("mainPropDepotId", "appendPropDepotId"):
            if not isinstance(row.get(field), int) or row[field] <= 0:
                raise MappingError(f"artifact {item_id} has an invalid {field}")
        items[str(item_id)] = {
            "setKey": sets[set_id],
            "slotKey": slots[row["equipType"]],
            "rarity": rank,
            "mainPropDepotId": row["mainPropDepotId"],
            "appendPropDepotId": row["appendPropDepotId"],
        }
    low_rarity_ids.sort()
    if (
        exclusions != EXCLUSION_COUNTS
        or len(items) != EXPECTED_COUNTS["items"]
        or len(low_rarity_ids) != EXPECTED_COUNTS["lowRarityItems"]
    ):
        raise MappingError(f"artifact coverage changed: {exclusions}, {len(items)} items")
    return dict(sorted(items.items(), key=lambda pair: int(pair[0]))), low_rarity_ids, exclusions


def build_main_props(rows: list[dict[str, Any]], items: dict[str, dict[str, Any]]) -> dict[str, dict[str, Any]]:
    unique_row_ids(rows, "main property rows")
    referenced = {row["mainPropDepotId"] for row in items.values()}
    result: dict[str, dict[str, Any]] = {}
    for row in sorted(rows, key=lambda row: row["id"]):
        key = PROPERTY_KEYS.get(row.get("propType"))
        if key not in MAIN_KEYS:
            continue
        if not isinstance(row.get("propDepotId"), int) or row["propDepotId"] <= 0:
            raise MappingError(f"main property {row.get('id')} has an invalid depot")
        result[str(row["id"])] = {"depotId": row["propDepotId"], "key": key}
    if len(result) != EXPECTED_COUNTS["mainProps"]:
        raise MappingError(f"expected {EXPECTED_COUNTS['mainProps']} main properties, got {len(result)}")
    if {row["depotId"] for row in result.values()} != referenced:
        raise MappingError("main property depot coverage changed")
    return result


def build_affixes(rows: list[dict[str, Any]], items: dict[str, dict[str, Any]]) -> dict[str, dict[str, Any]]:
    unique_row_ids(rows, "affix rows")
    referenced = {row["appendPropDepotId"] for row in items.values()}
    result: dict[str, dict[str, Any]] = {}
    for row in sorted(rows, key=lambda row: row["id"]):
        if row.get("depotId") not in referenced:
            continue
        key = PROPERTY_KEYS.get(row.get("propType"))
        if key not in SUBSTAT_KEYS:
            raise MappingError(f"affix {row.get('id')} has an unsupported property")
        value = row.get("propValue")
        if isinstance(value, bool) or not isinstance(value, (int, Decimal)):
            raise MappingError(f"affix {row.get('id')} value is not numeric")
        value = Decimal(value)
        if key in PERCENT_KEYS:
            value *= Decimal(100)
        result[str(row["id"])] = {
            "depotId": row["depotId"],
            "key": key,
            "value": number(value),
        }
    depots = {row["depotId"] for row in result.values()}
    if len(referenced) != 12 or depots != referenced or len(result) != EXPECTED_COUNTS["affixes"]:
        raise MappingError("affix depot coverage changed")
    return result


def build_locations(character_ids: dict[str, str], character_keys: set[str]) -> dict[str, str]:
    allowed = character_keys | {"Traveler"}
    result = {key: value for key, value in character_ids.items() if value in allowed}
    result = dict(sorted(result.items(), key=lambda pair: int(pair[0])))
    if len(result) != EXPECTED_COUNTS["locationIds"] or len(set(result.values())) != EXPECTED_COUNTS["locationKeys"]:
        raise MappingError("character location coverage changed")
    return result


def build_document(raw_root: Path, optimizer_root: Path) -> dict[str, Any]:
    items_rows = checked_source_json(raw_root, *RAW_FILES[0])
    main_rows = checked_source_json(raw_root, *RAW_FILES[1])
    affix_rows = checked_source_json(raw_root, *RAW_FILES[2])
    sets, slots, character_ids, character_keys = parse_optimizer_sources(optimizer_root)
    items, low_rarity_ids, _ = build_items(items_rows, sets, slots)
    main_props = build_main_props(main_rows, items)
    affixes = build_affixes(affix_rows, items)
    locations = build_locations(character_ids, character_keys)
    document: dict[str, Any] = {
        "schemaVersion": 1,
        "game": "gi",
        "gameVersion": "7.0",
        "source": {
            "repository": RAW_REPOSITORY,
            "commit": RAW_COMMIT,
            "files": [
                {"path": path, "sha256": sha256, "rowCount": row_count}
                for path, sha256, row_count in RAW_FILES
            ],
        },
        "validation": {
            "repository": OPTIMIZER_REPOSITORY,
            "commit": OPTIMIZER_COMMIT,
            "files": [{"path": path, "sha256": sha256} for path, sha256 in OPTIMIZER_FILES],
        },
        "itemCount": len(items),
        "lowRarityItemCount": len(low_rarity_ids),
        "mainPropCount": len(main_props),
        "affixCount": len(affixes),
        "locationIdCount": len(locations),
        "locationKeyCount": len(set(locations.values())),
        "items": items,
        "lowRarityItemIds": low_rarity_ids,
        "mainProps": main_props,
        "affixes": affixes,
        "locations": locations,
    }
    validate_document(document)
    return document


def json_ready(value: Any) -> Any:
    if isinstance(value, Decimal):
        return number(value)
    if isinstance(value, list):
        return [json_ready(item) for item in value]
    if isinstance(value, dict):
        return {key: json_ready(item) for key, item in value.items()}
    return value


def canonical_bytes(document: dict[str, Any]) -> bytes:
    try:
        text = json.dumps(json_ready(document), ensure_ascii=False, indent=2, separators=(",", ": "), allow_nan=False)
    except (TypeError, ValueError) as error:
        raise MappingError(f"cannot serialize canonical map: {error}") from error
    return (text + "\n").encode("utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise MappingError(message)


def validate_id_map(mapping: Any, label: str) -> None:
    require(isinstance(mapping, dict), f"{label} must be an object")
    ids = list(mapping)
    numeric_ids: list[int] = []
    for key in ids:
        require(isinstance(key, str) and key.isdecimal() and int(key) > 0, f"{label} contains an invalid id")
        require(str(int(key)) == key, f"{label} contains a non-canonical id")
        numeric_ids.append(int(key))
    require(numeric_ids == sorted(numeric_ids), f"{label} ids are not sorted")


def lookup_affix(document: dict[str, Any], affix_id: int, depot_id: int) -> dict[str, Any] | None:
    row = document["affixes"].get(str(affix_id))
    if not isinstance(row, dict) or row.get("depotId") != depot_id:
        return None
    return row


def validate_document(document: dict[str, Any]) -> None:
    expected_top_level = [
        "schemaVersion",
        "game",
        "gameVersion",
        "source",
        "validation",
        "itemCount",
        "lowRarityItemCount",
        "mainPropCount",
        "affixCount",
        "locationIdCount",
        "locationKeyCount",
        "items",
        "lowRarityItemIds",
        "mainProps",
        "affixes",
        "locations",
    ]
    require(list(document) == expected_top_level, "top-level field order changed")
    require(document["schemaVersion"] == 1 and document["game"] == "gi" and document["gameVersion"] == "7.0", "map identity changed")
    expected_source = {
        "repository": RAW_REPOSITORY,
        "commit": RAW_COMMIT,
        "files": [{"path": path, "sha256": sha256, "rowCount": count} for path, sha256, count in RAW_FILES],
    }
    expected_validation = {
        "repository": OPTIMIZER_REPOSITORY,
        "commit": OPTIMIZER_COMMIT,
        "files": [{"path": path, "sha256": sha256} for path, sha256 in OPTIMIZER_FILES],
    }
    require(document["source"] == expected_source, "source pin changed")
    require(document["validation"] == expected_validation, "validation pin changed")
    for name, count in EXPECTED_COUNTS.items():
        field = {
            "items": "itemCount",
            "lowRarityItems": "lowRarityItemCount",
            "mainProps": "mainPropCount",
            "affixes": "affixCount",
            "locationIds": "locationIdCount",
            "locationKeys": "locationKeyCount",
        }[name]
        require(document[field] == count, f"{field} changed")
    validate_id_map(document["items"], "items")
    validate_id_map(document["mainProps"], "mainProps")
    validate_id_map(document["affixes"], "affixes")
    validate_id_map(document["locations"], "locations")
    low_rarity_ids = document["lowRarityItemIds"]
    require(isinstance(low_rarity_ids, list), "lowRarityItemIds must be an array")
    require(
        all(isinstance(item_id, int) and not isinstance(item_id, bool) and item_id > 0 for item_id in low_rarity_ids),
        "lowRarityItemIds contains an invalid id",
    )
    require(len(low_rarity_ids) == EXPECTED_COUNTS["lowRarityItems"], "lowRarityItemIds count changed")
    require(low_rarity_ids == sorted(set(low_rarity_ids)), "lowRarityItemIds must be sorted and unique")
    require(not ({str(item_id) for item_id in low_rarity_ids} & set(document["items"])), "artifact ID groups overlap")
    item_fields = ["setKey", "slotKey", "rarity", "mainPropDepotId", "appendPropDepotId"]
    for item in document["items"].values():
        require(list(item) == item_fields, "item row fields changed")
        require(item["rarity"] in (3, 4, 5), "item rarity is unsupported")
    for row in document["mainProps"].values():
        require(list(row) == ["depotId", "key"], "main property row fields changed")
    for row in document["affixes"].values():
        require(list(row) == ["depotId", "key", "value"], "affix row fields changed")
        require("initialValue" not in row, "affix mapping must not invent initialValue")
    require(all(isinstance(value, str) for value in document["locations"].values()), "location value is invalid")
    main_depots = {row["depotId"] for row in document["mainProps"].values()}
    affix_depots = {row["depotId"] for row in document["affixes"].values()}
    referenced_main = {row["mainPropDepotId"] for row in document["items"].values()}
    referenced_affix = {row["appendPropDepotId"] for row in document["items"].values()}
    require(main_depots == referenced_main and len(referenced_main) == 29, "main depot coverage changed")
    require(affix_depots == referenced_affix and len(referenced_affix) == 12, "append depot coverage changed")
    require(len(set(document["locations"].values())) == EXPECTED_COUNTS["locationKeys"], "location key coverage changed")
    require(EXCLUSION_COUNTS["rarity12"] + EXCLUSION_COUNTS["unsupportedSets"] + EXCLUSION_COUNTS["missingSets"] + EXCLUSION_COUNTS["unexplained"] + document["itemCount"] == RAW_FILES[0][2], "source exclusion counts changed")
    require(document["items"]["31533"] == {"setKey": "MarechausseeHunter", "slotKey": "circlet", "rarity": 5, "mainPropDepotId": 3000, "appendPropDepotId": 501}, "synthetic item lookup changed")
    require(51110 in low_rarity_ids, "known low-rarity item is missing")
    require(31533 not in low_rarity_ids and 1 not in low_rarity_ids, "invalid low-rarity item was accepted")
    require(document["mainProps"]["13007"] == {"depotId": 3000, "key": "critRate_"}, "synthetic main lookup changed")
    for affix_id, expected in {
        "501022": {"depotId": 501, "key": "hp", "value": 239},
        "501201": {"depotId": 501, "key": "critRate_", "value": 2.72},
        "501241": {"depotId": 501, "key": "eleMas", "value": 16.32},
        "501221": {"depotId": 501, "key": "critDMG_", "value": 5.44},
    }.items():
        require(document["affixes"][affix_id] == expected, f"synthetic affix lookup {affix_id} changed")
    require(document["locations"]["10000061"] == "Kirara", "synthetic character lookup changed")
    require(lookup_affix(document, 401021, document["items"]["31533"]["appendPropDepotId"]) is None, "depot mismatch was accepted")
    def no_initial_value(value: Any) -> None:
        if isinstance(value, dict):
            require("initialValue" not in value, "map must not contain initialValue")
            for child in value.values():
                no_initial_value(child)
        elif isinstance(value, list):
            for child in value:
                no_initial_value(child)
    no_initial_value(document)


def check_map(path: Path) -> tuple[int, str]:
    try:
        data = path.read_bytes()
    except OSError as error:
        raise MappingError(f"cannot read {path}: {error}") from error
    require(b"\r" not in data, "canonical map must use LF line endings")
    parsed = parse_json(path)
    require(isinstance(parsed, dict), "canonical map must be an object")
    expected = canonical_bytes(parsed)
    require(data == expected, "canonical bytes changed")
    validate_document(json_ready(parsed))
    actual_hash = hashlib.sha256(data).hexdigest()
    require(len(data) == EXPECTED_SIZE, f"canonical size changed: {len(data)}")
    require(actual_hash == EXPECTED_SHA256, f"canonical hash changed: {actual_hash}")
    return len(data), actual_hash


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw-root", type=Path)
    parser.add_argument("--optimizer-root", type=Path)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    try:
        if args.check:
            size, digest = check_map(args.output)
            print(f"checked {args.output}: {size} bytes {digest}")
            return 0
        if args.raw_root is None or args.optimizer_root is None:
            parser.error("generation requires both --raw-root and --optimizer-root")
        document = build_document(args.raw_root, args.optimizer_root)
        data = canonical_bytes(document)
        digest = hashlib.sha256(data).hexdigest()
        require(len(data) == EXPECTED_SIZE, f"generated size changed: {len(data)}")
        require(digest == EXPECTED_SHA256, f"generated hash changed: {digest}")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(data)
        print(f"generated {args.output}: {len(data)} bytes {digest}")
        return 0
    except (MappingError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
