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
    """The installer's splash: the mark, the name, and a halo that breathes.

    The pulse is a sine over the whole frame count, so the last frame leads back into the first
    and the loop has no seam — an install takes a few seconds and the image is shown throughout,
    so anything that restarts visibly would blink at the person watching it.
    """
    width, height = 480, 300
    frames_count = 40

    name_font = ImageFont.truetype(FONT_SEMIBOLD, 46)
    sub_font = ImageFont.truetype(FONT_REGULAR, 15)

    mark = draw_mark(64, fill=ACCENT_DARK)

    frames = []
    for index in range(frames_count):
        # 0 -> 1 -> 0 across the loop, easing at both ends.
        phase = (1 - math.cos(2 * math.pi * index / frames_count)) / 2

        frame = Image.new("RGB", (width, height), BG_DARK)

        # The halo: the mark blurred and brightened, drawn under it.
        glow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        glow.paste(mark, (width // 2 - 32, 64), mark)
        glow = glow.filter(ImageFilter.GaussianBlur(18))

        faded = glow.copy()
        alpha = faded.getchannel("A").point(lambda a: int(a * (0.25 + 0.35 * phase)))
        faded.putalpha(alpha)
        frame.paste(faded, (0, 0), faded)

        frame.paste(mark, (width // 2 - 32, 64), mark)

        draw = ImageDraw.Draw(frame)

        name = "asobu"
        box = draw.textbbox((0, 0), name, font=name_font)
        draw.text(
            ((width - (box[2] - box[0])) / 2 - box[0], 152),
            name, font=name_font, fill=ACCENT_DARK,
        )

        sub = "Getting things ready…"
        box = draw.textbbox((0, 0), sub, font=sub_font)
        draw.text(
            ((width - (box[2] - box[0])) / 2 - box[0], 218),
            sub, font=sub_font, fill=TEXT_MUTED,
        )

        frames.append(frame.convert("P", palette=Image.Palette.ADAPTIVE, colors=128))

    out = os.path.join(HERE, "installer-splash.gif")
    frames[0].save(
        out, format="GIF", save_all=True, append_images=frames[1:],
        duration=60, loop=0, optimize=True,
    )
    print(f"wrote {out}  ({width}x{height}, {frames_count} frames, seamless)")


if __name__ == "__main__":
    build_icon()
    build_splash()
