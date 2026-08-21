"""Builds Asobu's icon and installer splash.

The icon is artwork: asobu-source.png beside this file is drawn by hand, and all this script
does with it is resample it into the sizes Windows and Linux each want. The splash is composed
here -- the same artwork over the same two lines the launcher opens with, in the launcher's own
background and pink, so installing Asobu and first running it read as one continuous thing
rather than two screens that happen to share a colour.

    python assets/make-brand-assets.py

Needs Pillow. Writes asobu.ico, asobu.png and installer-splash.gif beside this file.
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

# The artwork the launcher's icon is made of. Kept as a file rather than drawn below, because
# the icon somebody picks out of a taskbar is worth more than something a script can produce.
SOURCE = os.path.join(HERE, "asobu-source.png")

# Straight out of Styles/Asobu.axaml.
ACCENT_DARK = (255, 158, 192)  # Accent, dark theme
BG_DARK = (22, 17, 20)         # Bg, dark theme
TEXT_MUTED = (156, 140, 147)   # TextMuted, dark theme

FONT_SEMIBOLD = "C:/Windows/Fonts/seguisb.ttf"
FONT_REGULAR = "C:/Windows/Fonts/segoeui.ttf"


def artwork():
    """The icon, cropped to what is actually inked.

    The file is a square canvas with the gamepad sitting in the middle of it, so cropping to the
    opaque pixels is what lets a caller place the mark by what shows rather than by the empty
    room around it.
    """
    with Image.open(SOURCE) as art:
        art = art.convert("RGBA")
        return art.crop(art.getchannel("A").getbbox())


def build_icon():
    """A .ico carrying every size Windows asks for, each resampled from the artwork."""
    sizes = [16, 24, 32, 48, 64, 128, 256]

    with Image.open(SOURCE) as art:
        # Lanczos rather than nearest. The source is drawn at 256 rather than being a small grid
        # blown up, so there is no block size to line up with, and point sampling would just
        # throw away fifteen pixels in sixteen by the time it reached 16x16.
        frames = [art.convert("RGBA").resize((s, s), Image.Resampling.LANCZOS) for s in sizes]

    out = os.path.join(HERE, "asobu.ico")
    frames[-1].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    print(f"wrote {out}  ({', '.join(str(s) for s in sizes)})")


def build_png():
    """The same artwork as a plain PNG, which is what Linux packaging wants.

    AppImage and the .desktop entry beside it take a PNG; neither reads an .ico. 256 is the
    largest size freedesktop's icon spec defines, and every smaller one a desktop needs is
    scaled from it.
    """
    out = os.path.join(HERE, "asobu.png")

    with Image.open(SOURCE) as art:
        art.convert("RGBA").save(out, format="PNG")

    print(f"wrote {out}  (256)")


def build_splash():
    """The installer's splash: the welcome, held.

    Deliberately the same two lines the launcher opens with, in the same weights and the same
    pink, so installing and first running Asobu read as one continuous thing rather than two
    screens that happen to share a colour. The launcher animates those lines in; this holds
    them, because Velopack shows the image for however long the install takes and loops it —
    a fade-in would restart again and again in front of whoever is watching.

    What moves is a glow behind the name, breathing on a sine across the whole frame count, so
    the last frame leads back into the first and the loop has no seam.
    """
    width, height = 520, 320
    frames_count = 44

    hello_font = ImageFont.truetype(FONT_REGULAR, 21)
    name_font = ImageFont.truetype(FONT_SEMIBOLD, 62)
    sub_font = ImageFont.truetype(FONT_REGULAR, 14)

    hello_y = 78

    # The icon above the greeting, sized by its width and centred in the room the greeting
    # leaves above itself. Nothing else moves: the space was already there.
    mark = artwork()
    mark = mark.resize((88, round(88 * mark.height / mark.width)), Image.Resampling.LANCZOS)
    mark_at = ((width - mark.width) // 2, (hello_y - mark.height) // 2)

    def centred(draw, text, font, y, fill):
        box = draw.textbbox((0, 0), text, font=font)
        draw.text(((width - (box[2] - box[0])) / 2 - box[0], y), text, font=font, fill=fill)

    # The name is drawn once on its own layer, blurred into the glow beneath itself.
    name_layer = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    centred(ImageDraw.Draw(name_layer), "asobu", name_font, 118, ACCENT_DARK + (255,))
    halo = name_layer.filter(ImageFilter.GaussianBlur(22))

    frames = []
    for index in range(frames_count):
        # 0 -> 1 -> 0 across the loop, easing at both ends.
        phase = (1 - math.cos(2 * math.pi * index / frames_count)) / 2

        frame = Image.new("RGB", (width, height), BG_DARK)

        glow = halo.copy()
        glow.putalpha(glow.getchannel("A").point(lambda a: int(a * (0.30 + 0.30 * phase))))
        frame.paste(glow, (0, 0), glow)
        frame.paste(name_layer, (0, 0), name_layer)

        # After the glow rather than before it: the halo reaches up this far, and the icon is
        # meant to sit in front of it rather than be washed through by it.
        frame.paste(mark, mark_at, mark)

        draw = ImageDraw.Draw(frame)
        centred(draw, "Welcome to", hello_font, hello_y, TEXT_MUTED)
        # Kept well clear of the bottom: Velopack draws a progress bar over this image
        # while installing and while updating, and it draws it along the lower edge.
        centred(draw, "Just a moment…", sub_font, 208, TEXT_MUTED)

        frames.append(frame.convert("P", palette=Image.Palette.ADAPTIVE, colors=96))

    out = os.path.join(HERE, "installer-splash.gif")
    frames[0].save(
        out, format="GIF", save_all=True, append_images=frames[1:],
        duration=55, loop=0, optimize=True,
    )
    print(f"wrote {out}  ({width}x{height}, {frames_count} frames, seamless)")


if __name__ == "__main__":
    build_icon()
    build_png()
    build_splash()
