"""Regenerate the original DualMeter artwork with Pillow (optional)."""

from pathlib import Path

from PIL import Image, ImageDraw


assets = Path(__file__).resolve().parents[1] / "src"
side = 512
white = "#ffffff"
terminal = Image.new("RGBA", (side, side))
pen = ImageDraw.Draw(terminal)
pen.line([(82, 119), (219, 256), (82, 393)], fill=white, width=45, joint="curve")
pen.rounded_rectangle((243, 349, 454, 394), radius=22, fill=white)
terminal.save(assets / "CodexMark.png")

spark = Image.new("RGBA", (side, side))
pen = ImageDraw.Draw(spark)
pen.polygon([(256, 20), (307, 165), (351, 209), (493, 256),
             (351, 307), (307, 351), (256, 493), (205, 351),
             (161, 307), (19, 256), (162, 205), (205, 161)], fill=white)
spark.save(assets / "AntigravityMark.png")

icon = Image.new("RGBA", (side, side))
pen = ImageDraw.Draw(icon)
pen.rounded_rectangle((6, 6, 506, 506), radius=120, fill="#111713")
for cx, end, mark in [(152, 200, terminal), (360, 136, spark)]:
    box = (cx - 91, 156, cx + 91, 338)
    pen.ellipse(box, outline="#34473c", width=26)
    pen.arc(box, -90, end, fill="#39e79e", width=26)
    icon.alpha_composite(mark.resize((106, 106), Image.Resampling.LANCZOS), (cx - 53, 194))
icon.save(assets / "dual.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
