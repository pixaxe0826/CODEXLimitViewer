from pathlib import Path

from PIL import Image


PROJECT_ROOT = Path(__file__).resolve().parent.parent
SOURCE = PROJECT_ROOT / "assets" / "codex-quota-icon.png"
OUTPUT = PROJECT_ROOT / "assets" / "codex-quota-icon.ico"
SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]


def main() -> None:
    with Image.open(SOURCE) as image:
        rgba = image.convert("RGBA")
        alpha = rgba.getchannel("A")
        if alpha.getbbox() is None:
            raise RuntimeError("Icon source is fully transparent")

        corners = [
            alpha.getpixel((0, 0)),
            alpha.getpixel((rgba.width - 1, 0)),
            alpha.getpixel((0, rgba.height - 1)),
            alpha.getpixel((rgba.width - 1, rgba.height - 1)),
        ]
        if max(corners) > 8:
            raise RuntimeError(f"Icon corners are not transparent: {corners}")

        rgba.save(OUTPUT, format="ICO", sizes=SIZES, bitmap_format="png")

    with Image.open(OUTPUT) as icon:
        embedded_sizes = sorted(icon.ico.sizes())
    expected = sorted(SIZES)
    if embedded_sizes != expected:
        raise RuntimeError(f"Unexpected ICO sizes: {embedded_sizes}; expected {expected}")

    print(f"Built {OUTPUT} with sizes: {embedded_sizes}")


if __name__ == "__main__":
    main()
