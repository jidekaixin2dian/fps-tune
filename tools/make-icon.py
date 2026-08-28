# -*- coding: utf-8 -*-
"""
FPS 帧律 应用图标生成器：速度表盘意象。

产出：
  FpsTune.Wpf/Assets/app.ico        多尺寸 ICO (16..256)
  FpsTune.Wpf/Assets/app-icon.png   1024 主图（标题栏 / 首页引用）
  docs/icon-preview-512.png         512 预览图

运行：python tools/make-icon.py   （需要 Pillow）
"""
import math
import os

from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "FpsTune.Wpf", "Assets")

BASE = 1024          # 输出主图尺寸
SS = 4               # 超采样倍率（抗锯齿）
S = BASE * SS        # 绘制画布

# ---------- 画布与背景 ----------

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

radius = int(S * 0.225)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=255)

base = Image.new("RGBA", (S, S))
bd = ImageDraw.Draw(base)
top, bottom = (23, 29, 41), (10, 13, 18)   # #171D29 -> #0A0D12
for y in range(S):
    t = y / (S - 1)
    bd.line([(0, y), (S, y)], fill=(
        int(top[0] + (bottom[0] - top[0]) * t),
        int(top[1] + (bottom[1] - top[1]) * t),
        int(top[2] + (bottom[2] - top[2]) * t), 255))

# 表盘背后的柔光：蓝靛径向渐晕
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gr = int(S * 0.46)
gd.ellipse([S * 0.5 - gr, S * 0.42 - gr, S * 0.5 + gr, S * 0.42 + gr],
           fill=(77, 140, 255, 56))
glow = glow.filter(ImageFilter.GaussianBlur(S * 0.10))
base.alpha_composite(glow)

img.paste(base, (0, 0), mask)
d = ImageDraw.Draw(img)

# 内描边发丝线
d.rounded_rectangle([3, 3, S - 4, S - 4], radius=radius - 3,
                    outline=(255, 255, 255, 22), width=max(2, int(S * 0.004)))

# ---------- 表盘 ----------

cx, cy = S * 0.50, S * 0.54
R = S * 0.295            # 弧中线半径
W = int(S * 0.052)       # 弧宽
A0, A1 = 135.0, 405.0    # 速度表开口朝下：135° 顺时针扫到 405°（PIL 角度，0°=3点钟方向）
C0, C1 = (69, 176, 254), (127, 111, 255)   # #45B0FE -> #7F6FFF


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def polar(r, deg):
    a = math.radians(deg)
    return cx + r * math.cos(a), cy + r * math.sin(a)


# 渐变弧：分段绘制，每段取中点色
seg = 2.5
a = A0
while a < A1:
    b = min(a + seg, A1)
    col = lerp(C0, C1, ((a + b) / 2 - A0) / (A1 - A0))
    d.arc([cx - R, cy - R, cx + R, cy + R], start=a, end=b, fill=col + (255,), width=W)
    a = b

# 圆头端帽
for ang in (A0, A1):
    ex, ey = polar(R, ang)
    r = W / 2
    d.ellipse([ex - r, ey - r, ex + r, ey + r],
              fill=lerp(C0, C1, 0.0 if ang == A0 else 1.0) + (255,))

# 刻度：10 大格 + 中间小格（画在弧内侧）
def tick(deg, length, width, alpha):
    x0, y0 = polar(R - W * 0.95, deg)
    x1, y1 = polar(R - W * 0.95 - length, deg)
    d.line([(x0, y0), (x1, y1)], fill=(237, 241, 247, alpha), width=width)

for i in range(11):
    deg = A0 + i * (A1 - A0) / 10
    if i % 5 == 0:
        tick(deg, S * 0.040, int(S * 0.010), 150)
    else:
        tick(deg, S * 0.024, int(S * 0.006), 80)

# ---------- 指针 ----------

NEEDLE = 322.0   # 指向上方偏右的"高性能"区
nx, ny = polar(R + W * 0.10, NEEDLE)       # 针尖略探出弧
bx, by = polar(S * 0.055, NEEDLE)          # 针根
px, py = math.cos(math.radians(NEEDLE + 90)), math.sin(math.radians(NEEDLE + 90))
hw = S * 0.011                              # 针半宽
d.polygon([(bx + px * hw, by + py * hw), (nx, ny), (bx - px * hw, by - py * hw)],
          fill=(242, 246, 255, 255))

# 针尖标记点：弧上的亮点 + 光环
mx, my = polar(R, NEEDLE)
mr = S * 0.018
d.ellipse([mx - mr * 2.1, my - mr * 2.1, mx + mr * 2.1, my + mr * 2.1],
          outline=(255, 255, 255, 90), width=int(S * 0.006))
d.ellipse([mx - mr, my - mr, mx + mr, my + mr], fill=(255, 255, 255, 245))

# 轴心：暗底 + 白环 + 靛芯
hr = S * 0.052
d.ellipse([cx - hr, cy - hr, cx + hr, cy + hr], fill=(13, 16, 22, 255))
d.ellipse([cx - hr, cy - hr, cx + hr, cy + hr], outline=(255, 255, 255, 235),
          width=int(S * 0.009))
cr = S * 0.018
d.ellipse([cx - cr, cy - cr, cx + cr, cy + cr], fill=(127, 111, 255, 255))

# ---------- 输出 ----------

final = img.resize((BASE, BASE), Image.LANCZOS)
os.makedirs(ASSETS, exist_ok=True)
final.save(os.path.join(ASSETS, "app-icon.png"))

sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [final.resize((sz, sz), Image.LANCZOS) for sz in sizes]
frames[-1].save(os.path.join(ASSETS, "app.ico"), format="ICO",
                append_images=frames[:-1])

os.makedirs(os.path.join(ROOT, "docs"), exist_ok=True)
final.resize((512, 512), Image.LANCZOS).save(
    os.path.join(ROOT, "docs", "icon-preview-512.png"))

print("icon regenerated:", ", ".join(str(s) for s in sizes))
