"""PLC Sim のアイコン（案 A: CPU ユニット）を生成する。

python assets/icon/make_icons.py
  -> src/PlcSim.Slmp.Gui/Assets/PlcSim.ico      GUI 版（exe・ウィンドウ）
  -> src/PlcSim.Slmp.Host/PlcSim.Console.ico    コンソール版（右下に >_ の印）
  -> src/PlcSim.Slmp.Gui/Assets/PlcSim.png      バージョン情報ウィンドウ用（256px）
  -> assets/icon/PlcSim.png, PlcSim.Console.png  256px のプレビュー

座標は 256x256 基準。各サイズを 4 倍で描いて縮小する。Pillow が必要。
"""

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SIZES = [16, 24, 32, 48, 64, 128, 256]
KEY = "#0017C1"      # DADS blue-900（画面のキーカラー）
WHITE = "#FFFFFF"
RUN = "#1BAA5F"      # RUN LED
OFF = "#C9CBE0"      # 消灯 LED
INK = "#1A1A1C"


def draw(size: int, console: bool) -> Image.Image:
    scale = 4
    px = size * scale
    k = px / 256
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def rect(x, y, w, h, r, fill, outline=None, width=0):
        d.rounded_rectangle([x * k, y * k, (x + w) * k, (y + h) * k], radius=r * k, fill=fill,
                            outline=outline, width=round(width * k))

    def circle(cx, cy, r, fill):
        d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], fill=fill)

    rect(0, 0, 256, 256, 56, KEY)
    rect(72, 36, 112, 184, 14, WHITE)          # CPU ユニット
    circle(104, 78, 13, RUN)                   # RUN
    circle(104, 118, 13, OFF)                  # ERR
    for y in (64, 96, 128):
        rect(132, y, 34, 18, 4, KEY)           # 端子
    rect(92, 176, 72, 20, 5, KEY)              # コネクタ

    if console:
        rect(140, 152, 108, 96, 20, INK, outline=WHITE, width=8)
        w = round(12 * k)
        d.line([(166 * k, 178 * k), (188 * k, 200 * k), (166 * k, 222 * k)], fill=WHITE, width=w, joint="curve")
        d.line([(198 * k, 222 * k), (226 * k, 222 * k)], fill=WHITE, width=w)

    return img.resize((size, size), Image.LANCZOS)


def save(path: Path, console: bool, preview: Path) -> None:
    images = [draw(s, console) for s in SIZES]
    path.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(path, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    images[-1].save(preview)
    print(path.relative_to(ROOT))


save(ROOT / "src/PlcSim.Slmp.Gui/Assets/PlcSim.ico", False, ROOT / "assets/icon/PlcSim.png")
draw(256, False).save(ROOT / "src/PlcSim.Slmp.Gui/Assets/PlcSim.png")  # バージョン情報ウィンドウ用
save(ROOT / "src/PlcSim.Slmp.Host/PlcSim.Console.ico", True, ROOT / "assets/icon/PlcSim.Console.png")
