from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = ROOT / "src" if (ROOT / "src").is_dir() else ROOT
UI = (SOURCE_ROOT / "DynamicCardOddsCode" / "CardLibraryUi.cs").read_text(encoding="utf-8")
ODDS = (SOURCE_ROOT / "DynamicCardOddsCode" / "AdaptiveOdds.cs").read_text(encoding="utf-8")
MANIFEST = (SOURCE_ROOT / "DynamicCardOdds.json").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


# Normal encyclopedia cards must retain the game's original description layout.
apply_card = UI.split("private static void ApplyCard", 1)[1].split(
    "private static void EnsureButton", 1
)[0]
require("RemoveOddsFromCard(cardNode);" in apply_card, "ApplyCard must remove legacy odds text")
require("AppendOddsLine" not in UI, "odds must never be appended to a card description")
require("GetDisplayLine(model" not in UI, "card effect labels must not render odds")

# Detail odds are a separate panel and are gated by a visible encyclopedia host.
require("DynamicCardOddsDetailPanel" in UI, "missing detail side panel")
require("IsLibraryHostContext(grid)" in UI, "detail panel must require encyclopedia context")
require("IsInspectVisible(inspect)" in UI, "detail panel must require an open card detail")
require("UpdateDetailPanel(_library);" in UI, "watcher must refresh the selected detail card")
require("这是历史倾向倍率" in UI, "panel must distinguish tendency from exact probability")

# The disabled mask must follow the real card container without fighting the
# game's Body.Modulate tweens or participating in Control layout.
disabled_look = UI.split("private static void ApplyDisabledLook", 1)[1].split(
    "private static void RemoveOddsFromCard", 1
)[0]
require("body.Modulate" not in disabled_look, "disabled mask must not write Body.Modulate")
require("body.AddChild(overlay);" in disabled_look, "mask must follow CardContainer transforms")
require("var visualSize = body.Size;" in disabled_look, "mask must use the live container size")
require("var halfSize = visualSize * 0.5f;" in disabled_look, "mask must use CardContainer's centred coordinates")
require("overlay.Visible = disabled;" in disabled_look, "mask must be persistent and visibility-driven")
require("HideAndFree(overlay)" not in UI, "mask must not be queued for deletion during refresh")

require("DcoDisplayOdds GetDisplayOdds" in ODDS, "UI requires structured display odds")
require('"version": "1.0.7"' in MANIFEST, "manifest version must be v1.0.7")

print("PASS: DynamicCardOdds detail-only odds UI and centred CardContainer disabled overlay")
