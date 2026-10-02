"""Extract authoritative item limits and icons from an installed Raft copy.

This is an offline build tool. It never attaches to the running game and never
touches a save file.
"""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator


def load_generator(game_dir: Path, unity_version: str) -> TypeTreeGenerator:
    generator = TypeTreeGenerator(unity_version)
    managed = game_dir / "Raft_Data" / "Managed"
    preferred = ["mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll"]
    dlls = [path for path in managed.glob("*.dll") if path.name not in preferred and path.name != "Assembly-CSharp.dll"]
    ordered = [managed / name for name in preferred] + sorted(dlls) + [managed / "Assembly-CSharp.dll"]
    for dll in ordered:
        if not dll.exists():
            continue
        try:
            generator.load_dll(dll.read_bytes())
        except (AssertionError, RuntimeError):
            # This Raft build contains two files declaring PlayFab.dll. Loading
            # either one is sufficient; the duplicate is intentionally ignored.
            pass
    return generator


def parse_game_rows(path: Path) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    with path.open(encoding="utf-8-sig") as stream:
        for raw in stream:
            if not raw or raw.startswith("#"):
                continue
            fields = raw.rstrip("\r\n").split("|", 8)
            if len(fields) != 9:
                continue
            rows.append({
                "id": int(fields[0]),
                "unique": fields[1],
                "asset_file": fields[7],
                "path_id": int(fields[8]),
            })
    return rows


def inventory_eligible(unique_name: str, stack_size: int) -> bool:
    if stack_size <= 0 or unique_name.startswith("Block_"):
        return False
    return unique_name not in {
        "Barrel",
        "Blueprint",
        "DropItem",
        "FakeTitaniumTools",
        "Placeable",
        "Placeable_Recipe",
        "Repair",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-dir", type=Path, required=True)
    parser.add_argument("--game-catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--icons", type=Path, required=True)
    args = parser.parse_args()

    game_data = args.game_dir / "Raft_Data"
    rows = parse_game_rows(args.game_catalog)
    files: dict[str, UnityPy.Environment] = {}
    object_maps: dict[str, dict[int, object]] = {}
    unity_version = "2021.3.45f2"
    generator = load_generator(args.game_dir, unity_version)
    args.icons.mkdir(parents=True, exist_ok=True)

    for asset_file in sorted({str(row["asset_file"]) for row in rows}):
        environment = UnityPy.load(str(game_data / asset_file))
        environment.typetree_generator = generator
        files[asset_file] = environment
        object_maps[asset_file] = {obj.path_id: obj for obj in environment.objects}

    extracted: list[dict[str, object]] = []
    for row in rows:
        asset_file = str(row["asset_file"])
        item_object = object_maps[asset_file][int(row["path_id"])]
        tree = item_object.read_typetree()
        inventory = tree["settings_Inventory"]
        buildable = tree["settings_buildable"]
        sprite_ref = inventory["sprite"]
        sprite_file_id = int(sprite_ref["m_FileID"])
        sprite_path_id = int(sprite_ref["m_PathID"])
        icon_file = ""
        if sprite_file_id == 0 and sprite_path_id:
            sprite_object = object_maps[asset_file].get(sprite_path_id)
            if sprite_object is not None and sprite_object.type.name == "Sprite":
                icon_file = f"Game_{row['id']}.png"
                sprite_object.read().image.save(args.icons / icon_file)

        stack_size = int(inventory["stackSize"])
        unique_name = str(tree["uniqueName"])
        block_prefabs = buildable.get("blockPrefabs") or []
        extracted.append({
            "ID": int(tree["uniqueIndex"]),
            "UniqueName": unique_name,
            "EnglishName": str(inventory["displayName"]),
            "LocalizationTerm": str(inventory["localizationTerm"]),
            "StackSize": stack_size,
            "MaxUses": int(tree["maxUses"]),
            "Icon": icon_file,
            "HasBuildablePrefabs": any(int(prefab.get("m_PathID", 0)) != 0 for prefab in block_prefabs),
            "Placeable": bool(buildable.get("placeable", False)),
            "CanAdd": inventory_eligible(unique_name, stack_size),
        })

    with args.output.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=extracted[0].keys(), delimiter="|")
        writer.writeheader()
        writer.writerows(extracted)

    print(f"Extracted {len(extracted)} items, {sum(bool(row['Icon']) for row in extracted)} icons, "
          f"{sum(bool(row['CanAdd']) for row in extracted)} inventory items.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
