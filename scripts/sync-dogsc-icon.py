"""Convert the pinned upstream DogSC icon to the Windows ICO container.

Requires Pillow. The upstream artwork is unchanged except for size resampling.
"""

from hashlib import sha256
from pathlib import Path
from shutil import copyfile

from PIL import Image


ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "upstream/dogsc/Resources/AppIconMaster.png"
OUTPUT = ROOT / "src/FrameTrail.App/Assets/DogSC.ico"
README_LOGO = ROOT / "docs/assets/DogSC.png"
SOURCE_SHA256 = "1f1a16c58905c0901d408f3d3df39ccca5070c34c820a8bd6b0255230e554af8"
SIZES = [(n, n) for n in (16, 24, 32, 48, 64, 128, 256)]


def main():
    if sha256(SOURCE.read_bytes()).hexdigest() != SOURCE_SHA256:
        raise SystemExit("Pinned DogSC artwork hash mismatch; review upstream provenance first.")
    with Image.open(SOURCE) as artwork:
        artwork.convert("RGBA").save(OUTPUT, format="ICO", sizes=SIZES)
    with Image.open(OUTPUT) as icon:
        if icon.ico.sizes() != set(SIZES):
            raise SystemExit("ICO does not contain the requested Windows icon sizes.")
    README_LOGO.parent.mkdir(parents=True, exist_ok=True)
    copyfile(SOURCE, README_LOGO)
    print(f"DogSC icon: {OUTPUT}")
    print(f"SHA256: {sha256(OUTPUT.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
