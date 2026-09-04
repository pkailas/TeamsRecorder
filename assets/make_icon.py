"""Generate assets/TeamsRecorder.ico (and PNG previews) with Pillow.

Design: rounded indigo tile, white microphone capsule with a stand, a
sound-wave arc on each side, and a red recording dot at the top right.
Run:  sidecar\\.venv\\Scripts\\python.exe assets\\make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

HERE = Path(__file__).parent
SIZES = [16, 24, 32, 48, 64, 128, 256]

BG_TOP = (68, 76, 210)
BG_BOT = (38, 40, 130)
WHITE = (255, 255, 255)
RED = (235, 64, 52)
RED_RING = (255, 255, 255)


GREY = (150, 155, 185)


def render(size: int, dot=RED) -> Image.Image:
    scale = 8  # supersample for clean anti-aliasing
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Vertical gradient tile with rounded corners.
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        c = tuple(int(BG_TOP[i] * (1 - t) + BG_BOT[i] * t) for i in range(3)) + (255,)
        gd.line([(0, y), (s, y)], fill=c)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    # Microphone capsule.
    cx = s * 0.5
    cap_w, cap_top, cap_bot = s * 0.22, s * 0.20, s * 0.55
    d.rounded_rectangle([cx - cap_w / 2, cap_top, cx + cap_w / 2, cap_bot],
                        radius=int(cap_w / 2), fill=WHITE)
    # Capsule grille lines.
    for i in range(3):
        y = cap_top + cap_w * 0.55 + i * (cap_bot - cap_top - cap_w) / 3.2
        d.line([(cx - cap_w * 0.28, y), (cx + cap_w * 0.28, y)], fill=BG_BOT, width=max(1, int(s * 0.012)))

    # Cradle (U shape) and stand.
    lw = max(2, int(s * 0.045))
    cr = s * 0.19
    cy = cap_bot - cr * 0.55
    d.arc([cx - cr, cy - cr, cx + cr, cy + cr], start=0, end=180, fill=WHITE, width=lw)
    d.line([(cx - cr, cy - s * 0.02), (cx - cr, cy)], fill=WHITE, width=lw)
    d.line([(cx + cr, cy - s * 0.02), (cx + cr, cy)], fill=WHITE, width=lw)
    d.line([(cx, cy + cr), (cx, s * 0.82)], fill=WHITE, width=lw)
    d.line([(cx - s * 0.14, s * 0.82), (cx + s * 0.14, s * 0.82)], fill=WHITE, width=lw)

    # Sound-wave arcs left and right.
    for sign in (-1, 1):
        for k, r in enumerate((s * 0.28, s * 0.35)):
            box = [cx - r, cy - r * 0.9, cx + r, cy + r * 0.9]
            start, end = (155, 205) if sign < 0 else (-25, 25)
            alpha = 230 if k == 0 else 150
            d.arc(box, start=start, end=end, fill=WHITE + (alpha,), width=max(2, int(s * 0.035)))

    # Recording dot, top-right, with a white ring so it reads at 16 px.
    r = s * 0.09
    dx, dy = s * 0.79, s * 0.21
    d.ellipse([dx - r - lw * 0.6, dy - r - lw * 0.6, dx + r + lw * 0.6, dy + r + lw * 0.6], fill=RED_RING)
    d.ellipse([dx - r, dy - r, dx + r, dy + r], fill=dot)

    return img.resize((size, size), Image.LANCZOS)


def save_ico(frames, path):
    frames[-1].save(path, format="ICO", sizes=[(sz, sz) for sz in SIZES], append_images=frames[:-1])


def main() -> None:
    frames = [render(sz) for sz in SIZES]
    frames[-1].save(HERE / "TeamsRecorder.png")
    frames[0].save(HERE / "TeamsRecorder-16.png")
    save_ico(frames, HERE / "TeamsRecorder.ico")            # app icon + tray while recording
    save_ico([render(sz, GREY) for sz in SIZES], HERE / "TeamsRecorder-idle.ico")  # tray when idle
    print("wrote", HERE / "TeamsRecorder.ico", "and TeamsRecorder-idle.ico")


if __name__ == "__main__":
    main()
