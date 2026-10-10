"""Builds the image-preprocessing fixture for ClipModelTests.

synthetic.png is a 640x427 test image (gradients, shapes, fine stripes, noise). reference_224.rgb is
what Hugging Face's CLIPImageProcessor feeds CLIP before normalising: shortest edge resized to 224
with PIL bicubic (preprocessor_config.json "resample": 3), then a 224x224 centre crop, as raw RGB
bytes. Run with Pillow installed: python make_reference.py
"""
import random
from PIL import Image, ImageDraw

W, H, SIZE = 640, 427, 224
random.seed(7)
img = Image.new("RGB", (W, H))
px = img.load()
for y in range(H):
    for x in range(W):
        px[x, y] = (int(255 * x / W), int(255 * y / H), int(255 * (1 - x / W) * (y / H)))
d = ImageDraw.Draw(img)
d.ellipse((60, 60, 260, 260), fill=(240, 200, 40), outline=(20, 20, 20), width=6)
d.rectangle((330, 80, 560, 300), fill=(30, 90, 200))
d.polygon([(420, 360), (600, 410), (500, 250)], fill=(200, 40, 60))
for i in range(0, 200, 4):  # fine stripes stress the resampler
    d.line((i + 200, 300, i + 200, 420), fill=(255, 255, 255) if i % 8 == 0 else (0, 0, 0))
for _ in range(4000):      # noise
    x, y = random.randrange(W), random.randrange(H)
    px[x, y] = tuple(random.randrange(256) for _ in range(3))
img.save("synthetic.png")

short, long_ = min(W, H), max(W, H)
new_w, new_h = (int(SIZE * long_ / short), SIZE) if W >= H else (SIZE, int(SIZE * long_ / short))
resized = img.resize((new_w, new_h), resample=Image.BICUBIC)
left, top = (new_w - SIZE) // 2, (new_h - SIZE) // 2
crop = resized.crop((left, top, left + SIZE, top + SIZE))
open("reference_224.rgb", "wb").write(crop.tobytes())
print(f"resized to {new_w}x{new_h}, crop at {left},{top}")
