"""Rasterises the overlay's icons (Phosphor, MIT) into an atlas: assets/overlay/icons/phosphor.png + phosphor.json.

Icons are drawn white on transparent, one per cell, and tinted by the theme at runtime; both styles (regular, fill) of
every icon listed below are included. Needs Python 3.10+ and Pillow (`pip install pillow`). Run it only to add icons
or change the Phosphor version; the results are committed (no Unity editor is involved).
"""

import io
import json
import pathlib
import sys
import tarfile
import urllib.request

from PIL import Image, ImageDraw, ImageFont

PHOSPHOR_VERSION = "2.1.2"
URL = "https://registry.npmjs.org/@phosphor-icons/web/-/web-{v}.tgz"
CELL = 32          # pixels per icon cell
GLYPH = 28         # glyph size inside the cell
COLUMNS = 16

ICONS = """
caret-left caret-right caret-up caret-down x x-circle check check-circle push-pin lock lock-open eye eye-slash gear sliders
list list-bullets bug play pause stop skip-forward clock lightning warning warning-circle info copy magnifying-glass funnel
terminal cube tree-structure crosshair hand-pointing pencil trash arrow-counter-clockwise arrow-clockwise flask puzzle-piece
plug plugs-connected cpu chart-line camera file-code code brackets-curly scroll power link arrows-out arrows-in dots-three
plus minus circle square star stack heartbeat gauge timer broadcast wrench folder-simple file-text export download upload
stop-circle hand-palm target
""".split()

REPO = pathlib.Path(__file__).resolve().parents[2]
OUT = REPO / "assets" / "overlay" / "icons"
LICENSES = REPO / "assets" / "overlay" / "licenses"


def main() -> int:
    print("  fetching", URL.format(v=PHOSPHOR_VERSION))
    with urllib.request.urlopen(URL.format(v=PHOSPHOR_VERSION)) as response:
        package = tarfile.open(fileobj=io.BytesIO(response.read()), mode="r:gz")

    def read(name: str) -> bytes:
        return package.extractfile("package/" + name).read()

    styles = [("regular", "src/regular/selection.json", "src/regular/Phosphor.ttf"),
              ("fill", "src/fill/selection.json", "src/fill/Phosphor-Fill.ttf")]
    cells = []
    for style, selection, ttf in styles:
        codes = {i["properties"]["name"]: i["properties"]["code"] for i in json.loads(read(selection))["icons"]}
        font = ImageFont.truetype(io.BytesIO(read(ttf)), GLYPH)
        missing = [name for name in ICONS if (name if style == "regular" else name + "-fill") not in codes]
        if missing:
            print(f"  unknown {style} icons: {', '.join(missing)}", file=sys.stderr)
            return 1
        for name in ICONS:
            code = codes[name if style == "regular" else name + "-fill"]
            cells.append((name if style == "regular" else name + ":fill", chr(code), font))

    rows = (len(cells) + COLUMNS - 1) // COLUMNS
    atlas = Image.new("RGBA", (COLUMNS * CELL, rows * CELL), (0, 0, 0, 0))
    draw = ImageDraw.Draw(atlas)
    index = {}
    for i, (name, char, font) in enumerate(cells):
        x, y = (i % COLUMNS) * CELL, (i // COLUMNS) * CELL
        draw.text((x + CELL / 2, y + CELL / 2), char, font=font, fill=(255, 255, 255, 255), anchor="mm")
        index[name] = [x, y, CELL, CELL]

    OUT.mkdir(parents=True, exist_ok=True)
    LICENSES.mkdir(parents=True, exist_ok=True)
    atlas.save(OUT / "phosphor.png", optimize=True)
    (OUT / "phosphor.json").write_text(json.dumps({
        "source": f"Phosphor Icons {PHOSPHOR_VERSION} (MIT)",
        "cell": CELL,
        "size": [atlas.width, atlas.height],
        "icons": index,
    }, indent=1, sort_keys=True) + "\n", encoding="utf-8")
    (LICENSES / "Phosphor-MIT.txt").write_bytes(read("LICENSE"))
    print(f"  {len(cells)} icons ({len(ICONS)} × regular/fill) in a {atlas.width}×{atlas.height} atlas")
    return 0


if __name__ == "__main__":
    sys.exit(main())
