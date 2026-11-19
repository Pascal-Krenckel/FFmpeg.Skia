"""Creates the files in Test-Files (PyAV 18.x = FFmpeg 8.1).

    pip install av numpy
    python make_test_files.py Test-Files
"""
import sys
from fractions import Fraction
import numpy as np
import av

out = sys.argv[1]

# 1) APNG, RGBA, odd width (row size not a multiple of the decoder's alignment), varying delays.
W, H = 333, 201
delays_ms = [100, 300, 100, 500, 100]
c = av.open(f"{out}/anim.apng", "w", format="apng")
s = c.add_stream("apng", rate=1000)
s.width, s.height, s.pix_fmt = W, H, "rgba"
s.codec_context.time_base = Fraction(1, 1000)
s.time_base = Fraction(1, 1000)
pts = 0
for i, d in enumerate(delays_ms):
    a = np.zeros((H, W, 4), np.uint8)
    a[..., 0] = (np.arange(W) % 256)[None, :]          # R = x
    a[..., 1] = (np.arange(H) % 256)[:, None]          # G = y
    a[..., 2] = 40 * i                                 # B = frame index
    a[..., 3] = 128                                    # half transparent
    f = av.VideoFrame.from_ndarray(a, format="rgba")
    f.pts, f.time_base, f.duration = pts, Fraction(1, 1000), d
    pts += d
    for p in s.encode(f):
        c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

# 2) Single still PNG (RGBA, odd width) via the image2 muxer
c = av.open(f"{out}/still.png", "w", format="image2")
s = c.add_stream("png", rate=1)
s.width, s.height, s.pix_fmt = W, H, "rgba"
a = np.zeros((H, W, 4), np.uint8); a[..., 0] = 200; a[..., 3] = 255
for p in s.encode(av.VideoFrame.from_ndarray(a, format="rgba")):
    c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

# 3) H.264 MP4, yuv420p, 25 fps, 100 frames, GOP 25 with B-frames. Luma encodes the frame index.
c = av.open(f"{out}/count.mp4", "w")
s = c.add_stream("libx264", rate=25)
s.width, s.height, s.pix_fmt = 320, 240, "yuv420p"
s.options = {"g": "25", "bf": "2", "crf": "10", "preset": "veryfast"}
s.codec_context.color_range = 1  # tv
for i in range(100):
    y = np.full((240, 320), 16 + 2 * i, np.uint8)
    f = av.VideoFrame.from_ndarray(y, format="gray")
    f = f.reformat(format="yuv420p")
    f.pts = i
    for p in s.encode(f):
        c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

# 4) Audio only
c = av.open(f"{out}/audio.wav", "w")
s = c.add_stream("pcm_s16le", rate=8000, layout="mono")
fr = av.AudioFrame.from_ndarray(np.zeros((1, 8000), np.int16), format="s16", layout="mono")
fr.sample_rate = 8000
fr.pts = 0
for p in s.encode(fr):
    c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

# 5) GIF with varying delays (palette)
c = av.open(f"{out}/anim.gif", "w", format="gif")
s = c.add_stream("gif", rate=100)
s.width, s.height, s.pix_fmt = 64, 48, "bgr8"
s.codec_context.time_base = Fraction(1, 100)
s.time_base = Fraction(1, 100)
pts = 0
for i, d in enumerate([10, 50, 10]):  # 1/100 s
    a = np.zeros((48, 64, 3), np.uint8); a[..., i] = 255
    f = av.VideoFrame.from_ndarray(a, format="rgb24").reformat(format="bgr8")
    f.pts, f.time_base = pts, Fraction(1, 100)
    pts += d
    for p in s.encode(f):
        c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

for name in ["anim.apng", "count.mp4", "anim.gif", "still.png"]:
    with av.open(f"{out}/{name}") as ic:
        v = ic.streams.video[0]
        frames = [(fr.pts, fr.duration, fr.format.name, fr.width) for fr in ic.decode(v)]
        print(name, len(frames), frames[:6])

# 6) Anamorphic H.264: 720x480 pixels with sample aspect ratio 32:27 (displayed as 853x480)
c = av.open(f"{out}/anamorphic.mp4", "w")
s = c.add_stream("libx264", rate=25)
s.width, s.height, s.pix_fmt = 720, 480, "yuv420p"
s.codec_context.sample_aspect_ratio = Fraction(32, 27)
for i in range(3):
    f = av.VideoFrame.from_ndarray(np.full((480, 720), 128, np.uint8), format="gray").reformat(format="yuv420p")
    f.pts = i
    for p in s.encode(f):
        c.mux(p)
for p in s.encode():
    c.mux(p)
c.close()

# 7) Phone-style video: count.mp4 with a track matrix that rotates it 90 degrees clockwise for display
import struct
data = bytearray(open(f"{out}/count.mp4", "rb").read())
pos = data.index(b"tkhd")                      # box type; version 0 header follows
matrix = pos + 4 + 4 + 4 * 5 + 8 + 2 * 4       # version/flags, 5 x 32 bit, reserved 64 bit, layer..reserved
struct.pack_into(">9i", data, matrix, 0, 0x10000, 0, -0x10000, 0, 0, 0, 0, 0x40000000)
open(f"{out}/rotated.mp4", "wb").write(bytes(data))

for name in ["anamorphic.mp4", "rotated.mp4"]:
    with av.open(f"{out}/{name}") as ic:
        v = ic.streams.video[0]
        print(name, v.sample_aspect_ratio, v.codec_context.sample_aspect_ratio, getattr(v, "side_data", None))
