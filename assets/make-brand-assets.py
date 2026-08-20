"""Draws Asobu's icon and installer splash.

Both are generated rather than drawn by hand so they cannot drift from the launcher: the mark
here is the same rounded square and offset play triangle the sidebar draws, in the same accent,
and the splash uses the same background and pink as the welcome the installer hands over to.

    python assets/make-brand-assets.py

Needs Pillow. Writes asobu.ico and installer-splash.gif beside this file.
"""

import math
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))

# Straight out of Styles/Asobu.axaml.
ACCENT = (196, 67, 112)        # Accent, light theme — reads on both light and dark desktops
ACCENT_DARK = (255, 158, 192)  # Accent, dark theme
ON_ACCENT = (255, 255, 255)
BG_DARK = (22, 17, 20)         # Bg, dark theme
TEXT_MUTED = (156, 140, 147)   # TextMuted, dark theme

FONT_SEMIBOLD = "C:/Windows/Fonts/seguisb.ttf"
FONT_REGULAR = "C:/Windows/Fonts/segoeui.ttf"


def draw_mark(size, fill=ACCENT, supersample=8):
    """The launcher's own logo: a rounded square with a play triangle, drawn big and scaled down.

    Supersampled because at 16 pixels a directly-drawn rounded corner is a staircase, and the
    triangle's diagonal is the whole shape.
    """
    big = size * supersample
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    # The sidebar uses a 32px box with a 10px radius.
    radius = int(big * (10 / 32))
    draw.rounded_rectangle([0, 0, big - 1, big - 1], radius=radius, fill=fill + (255,))

    # And a 9x11 triangle, nudged 3px right of centre so it reads as centred: a triangle's
    # visual weight sits left of its bounding box.
    width = big * (9 / 32)
    height = big * (11 / 32)
    left = (big - width) / 2 + big * (2.2 / 32)
    top = (big - height) / 2

    draw.polygon(
        [(left, top), (left + width, top + height / 2), (left, top + height)],
        fill=ON_ACCENT + (255,),
    )

    return image.resize((size, size), Image.Resampling.LANCZOS)


def build_icon():
    """A .ico carrying every size Windows asks for, each scaled from one clean drawing."""
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [draw_mark(s) for s in sizes]

    out = os.path.join(HERE, "asobu.ico")
    frames[-1].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    print(f"wrote {out}  ({', '.join(str(s) for s in sizes)})")


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

        draw = ImageDraw.Draw(frame)
        centred(draw, "Welcome to", hello_font, 78, TEXT_MUTED)
        centred(draw, "Setting things up…", sub_font, 236, TEXT_MUTED)

        frames.append(frame.convert("P", palette=Image.Palette.ADAPTIVE, colors=96))

    out = os.path.join(HERE, "installer-splash.gif")
    frames[0].save(
        out, format="GIF", save_all=True, append_images=frames[1:],
        duration=55, loop=0, optimize=True,
    )
    print(f"wrote {out}  ({width}x{height}, {frames_count} frames, seamless)")


if __name__ == "__main__":
    build_icon()
    build_splash()
