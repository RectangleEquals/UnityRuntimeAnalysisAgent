"""Fetches the overlay's fonts at pinned releases and writes the files the asset bundles are built from.

Output: Source/Overlay/Fonts/*.ttf (built into the bundles) and their licences in assets/overlay/licenses/ (shipped loose). Needs Python 3.10+ and fontTools
(`pip install fonttools`). Run it only to change a font or its version; the results are committed.

- Ark Pixel (SIL OFL 1.1, no Reserved Font Name): 12 px proportional and monospaced, 10 px proportional, trimmed to
  Latin, punctuation, arrows, maths, box drawing, blocks, geometric shapes and common symbols (the full font covers CJK).
- Rubik (SIL OFL 1.1): static Regular (400) and SemiBold (600) instances of the variable font.
- JetBrains Mono (SIL OFL 1.1): Regular.
"""

import io
import pathlib
import sys
import urllib.request
import zipfile

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

ARK_VERSION = "2026.09.25"
JETBRAINS_VERSION = "2.304"
RUBIK_COMMIT = "8b0a1d0f5983c89bc2b93f1b5fb55f9e252744b5"  # google/fonts: the last commit touching ofl/rubik

ARK_URL = "https://github.com/TakWolf/ark-pixel-font/releases/download/{v}/ark-pixel-font-{size}-ttf-v{v}.zip"
JETBRAINS_URL = "https://github.com/JetBrains/JetBrainsMono/releases/download/v{v}/JetBrainsMono-{v}.zip"
RUBIK_URL = "https://github.com/google/fonts/raw/{c}/ofl/rubik/Rubik%5Bwght%5D.ttf"
RUBIK_LICENSE_URL = "https://github.com/google/fonts/raw/{c}/ofl/rubik/OFL.txt"

UNICODES = [
    (0x0020, 0x007E), (0x00A0, 0x024F), (0x2000, 0x206F), (0x20A0, 0x20CF), (0x2100, 0x214F), (0x2190, 0x21FF),
    (0x2200, 0x22FF), (0x2300, 0x23FF), (0x2500, 0x259F), (0x25A0, 0x25FF), (0x2600, 0x26FF), (0x2700, 0x27BF),
]

ROOT = pathlib.Path(__file__).resolve().parent / "Source" / "Overlay"
FONTS = ROOT / "Fonts"
LICENSES = pathlib.Path(__file__).resolve().parents[2] / "assets" / "overlay" / "licenses"


def fetch(url: str) -> bytes:
    print("  fetching", url)
    with urllib.request.urlopen(url) as response:
        return response.read()


def ark(size: str) -> None:
    archive = zipfile.ZipFile(io.BytesIO(fetch(ARK_URL.format(v=ARK_VERSION, size=size))))
    font = TTFont(io.BytesIO(archive.read(f"ark-pixel-{size}-latin.ttf")), recalcTimestamp=False)
    options = subset.Options()
    options.layout_features = ["*"]
    options.name_IDs = ["*"]
    subsetter = subset.Subsetter(options)
    subsetter.populate(unicodes=[c for low, high in UNICODES for c in range(low, high + 1)])
    subsetter.subset(font)
    font.save(FONTS / f"ark-pixel-{size}.ttf")
    (LICENSES / "ArkPixel-OFL.txt").write_bytes(archive.read("OFL.txt"))


def rubik() -> None:
    variable = TTFont(io.BytesIO(fetch(RUBIK_URL.format(c=RUBIK_COMMIT))), recalcTimestamp=False)
    for weight, name in ((400, "Regular"), (600, "SemiBold")):
        instance = instancer.instantiateVariableFont(TTFont(io.BytesIO(variable_bytes(variable)), recalcTimestamp=False), {"wght": weight})
        instance.recalcTimestamp = False  # reproducible: the same bytes on every run
        instance.save(FONTS / f"Rubik-{name}.ttf")
    (LICENSES / "Rubik-OFL.txt").write_bytes(fetch(RUBIK_LICENSE_URL.format(c=RUBIK_COMMIT)))


def variable_bytes(font: TTFont) -> bytes:
    buffer = io.BytesIO()
    font.save(buffer)
    return buffer.getvalue()


def jetbrains() -> None:
    archive = zipfile.ZipFile(io.BytesIO(fetch(JETBRAINS_URL.format(v=JETBRAINS_VERSION))))
    (FONTS / "JetBrainsMono-Regular.ttf").write_bytes(archive.read("fonts/ttf/JetBrainsMono-Regular.ttf"))
    (LICENSES / "JetBrainsMono-OFL.txt").write_bytes(archive.read("OFL.txt"))


def main() -> int:
    FONTS.mkdir(parents=True, exist_ok=True)
    LICENSES.mkdir(parents=True, exist_ok=True)
    for size in ("12px-proportional", "12px-monospaced", "10px-proportional"):
        ark(size)
    rubik()
    jetbrains()
    for file in sorted(FONTS.glob("*.ttf")):
        print(f"  {file.name}: {file.stat().st_size // 1024} KB")
    return 0


if __name__ == "__main__":
    sys.exit(main())
