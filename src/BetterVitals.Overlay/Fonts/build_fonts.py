"""Rebuilds the bundled overlay fonts from the official Google Fonts sources (OFL).

Usage: python -I build_fonts.py <download-dir> <output-dir>

Downloads each family's variable (or static) TTF + OFL.txt into <download-dir>, pins the variable axes to
Regular 400 and SemiBold 600, subsets to Basic Latin + Latin-1 + Latin Extended-A (Polish) + common punctuation,
keeps all OpenType layout features (tnum, kern…), drops hinting, and writes <Family>-<Weight>.ttf + <Family>-OFL.txt
into <output-dir>. Requires fontTools (pip install fonttools).
"""
import os
import sys
import urllib.request

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

BASE = "https://raw.githubusercontent.com/google/fonts/main/ofl/"

# family name, folder, source files ({weight: file} for static, or a single variable file), extra pinned axes
FAMILIES = [
    ("Inter", "inter", "Inter%5Bopsz%2Cwght%5D.ttf", {"opsz": 14}),
    ("Geist", "geist", "Geist%5Bwght%5D.ttf", {}),
    ("SpaceGrotesk", "spacegrotesk", "SpaceGrotesk%5Bwght%5D.ttf", {}),
    ("JetBrainsMono", "jetbrainsmono", "JetBrainsMono%5Bwght%5D.ttf", {}),
    ("Rajdhani", "rajdhani", {400: "Rajdhani-Regular.ttf", 600: "Rajdhani-SemiBold.ttf"}, {}),
    ("Orbitron", "orbitron", "Orbitron%5Bwght%5D.ttf", {}),
]

WEIGHTS = {400: "Regular", 600: "SemiBold"}

UNICODES = (
    list(range(0x20, 0x7F))
    + list(range(0xA0, 0x180))
    + [0x2007, 0x2009, 0x200A, 0x202F, 0x2013, 0x2014, 0x2018, 0x2019, 0x201A, 0x201C, 0x201D, 0x201E,
       0x2022, 0x2026, 0x2030, 0x2032, 0x2033, 0x2039, 0x203A, 0x20AC, 0x2122, 0x2190, 0x2191, 0x2192,
       0x2193, 0x2212, 0x2248, 0x2264, 0x2265]
)


def fetch(url, path):
    if not os.path.exists(path):
        with urllib.request.urlopen(url) as r, open(path, "wb") as f:
            f.write(r.read())
    return path


def set_names(font, style):
    """Static-instance names: typographic family (16/17) + legacy RIBBI family (1/2), so DirectWrite groups
    Regular and SemiBold under one family name and picks them by weight."""
    names = font["name"]
    family = names.getDebugName(16) or names.getDebugName(1)
    ps = family.replace(" ", "")
    legacy = family if style == "Regular" else f"{family} {style}"
    for rec in list(names.names):
        if rec.nameID in (1, 2, 3, 4, 6, 16, 17, 25):
            names.removeNames(nameID=rec.nameID)
    for nid, text in ((1, legacy), (2, "Regular"), (3, f"{ps}-{style}"), (4, f"{family} {style}"),
                      (6, f"{ps}-{style}"), (16, family), (17, style)):
        names.setName(text, nid, 3, 1, 0x409)
        names.setName(text, nid, 1, 0, 0)
    if "fvar" in font:
        del font["fvar"]
    if "STAT" in font:
        del font["STAT"]


def subset_font(font, out_path):
    opts = subset.Options()
    opts.layout_features = ["*"]
    opts.name_IDs = ["*"]
    opts.name_languages = ["*"]
    opts.hinting = False
    opts.notdef_outline = True
    opts.glyph_names = False
    opts.drop_tables += ["DSIG"]
    sub = subset.Subsetter(options=opts)
    sub.populate(unicodes=UNICODES)
    sub.subset(font)
    font.save(out_path)


def main():
    dl, out = sys.argv[1], sys.argv[2]
    os.makedirs(dl, exist_ok=True)
    os.makedirs(out, exist_ok=True)
    for name, folder, src, axes in FAMILIES:
        fetch(BASE + folder + "/OFL.txt", os.path.join(out, f"{name}-OFL.txt"))
        for weight, style in WEIGHTS.items():
            target = os.path.join(out, f"{name}-{style}.ttf")
            if isinstance(src, dict):
                font = TTFont(fetch(BASE + folder + "/" + src[weight], os.path.join(dl, src[weight])))
            else:
                var = TTFont(fetch(BASE + folder + "/" + src, os.path.join(dl, folder + "-var.ttf")))
                font = instancer.instantiateVariableFont(var, {"wght": weight, **axes})
                font["OS/2"].usWeightClass = weight
                set_names(font, style)
            subset_font(font, target)
            print(f"{target}: {os.path.getsize(target)} bytes")


if __name__ == "__main__":
    main()
