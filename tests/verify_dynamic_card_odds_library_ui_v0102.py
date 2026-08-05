#!/usr/bin/env python3
"""Focused structural audit for DynamicCardOdds encyclopedia detail cleanup."""

from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT if (ROOT / "DynamicCardOddsCode").is_dir() else ROOT / "src"


def main() -> int:
    source = (SOURCE / "DynamicCardOddsCode" / "CardLibraryUi.cs").read_text(encoding="utf-8-sig")
    manifest = json.loads((SOURCE / "DynamicCardOdds.json").read_text(encoding="utf-8-sig"))
    checks = {
        "manifest is v1.0.2": manifest.get("version") == "1.0.2",
        "ApplyGrid cleans the live library grid while detail is visible": (
            "if (IsInspectVisible())" in source
            and "CleanupGrid(grid);" in source
            and source.index("CleanupGrid(grid);") < source.index("if (!IsLibraryContext(grid))")
        ),
        "ApplyCard never injects controls into the detail transition": (
            "if (IsInspectVisible() || (!assumeLibrary && !IsLibraryContext(cardNode)))" in source
        ),
        "watcher cleans existing encyclopedia controls before returning": (
            "if (IsInspectVisible())" in source
            and "CleanupOutsideLibrary();\n                    return;" in source
        ),
        "button removal still hides and frees the existing node": (
            "HideAndFree(cardNode.GetNodeOrNull<Button>(ButtonName));" in source
        ),
        "overlay removal still hides both current and legacy masks": (
            "overlay.Visible = false;" in source
            and "legacyOverlay.Visible = false;" in source
        ),
    }
    failed = [name for name, passed in checks.items() if not passed]
    for name, passed in checks.items():
        print(f"  [{'OK' if passed else 'FAIL'}] {name}")
    print(f"Passed: {len(checks) - len(failed)}; Failed: {len(failed)}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
