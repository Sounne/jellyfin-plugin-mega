"""Builds subtitles.mkv (video + SRT, ASS and PGS subtitle tracks) with ffmpeg, and what Jellyfin's
extraction (ffmpeg -c:s srt / copy) writes for each track: the reference for SubtitleReaderTests.
Run from this folder: python make_fixtures.py"""
import struct
import subprocess

DURATION = 150

srt = []
for i in range(48):
    start = 1.0 + i * 3.0
    text = [f"Line {i} é ü 日本", "<i>second line</i>" if i % 3 == 0 else None, r"{\an8}top" if i % 7 == 0 else None]
    srt.append((start, start + 2.25, "\n".join(t for t in text if t)))


def ts(t, sep=','):
    ms = round(t * 1000)
    return f"{ms // 3600000:02}:{ms // 60000 % 60:02}:{ms // 1000 % 60:02}{sep}{ms % 1000:03}"


with open('in.srt', 'w', encoding='utf-8', newline='\n') as f:
    for n, (a, b, t) in enumerate(srt, 1):
        f.write(f"{n}\n{ts(a)} --> {ts(b)}\n{t}\n\n")

ass_head = """[Script Info]
Title: fixture
ScriptType: v4.00+
PlayResX: 384
PlayResY: 288

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1
Style: Top,Arial,18,&H0000FFFF,&H000000FF,&H00000000,&H00000000,1,0,0,0,100,100,0,0,1,2,0,8,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""


def ats(t):
    cs = round(t * 100)
    return f"{cs // 360000}:{cs // 6000 % 60:02}:{cs // 100 % 60:02}.{cs % 100:02}"


with open('in.ass', 'w', encoding='utf-8', newline='\n') as f:
    f.write(ass_head)
    for i in range(30):
        start = 2.0 + i * 4.5
        f.write(f"Dialogue: {i % 2},{ats(start)},{ats(start + 3.5)},{'Top' if i % 4 == 0 else 'Default'},Bob,0,0,0,,"
                rf"Event {i}, with, commas{{\i1}} and tags\Nnext line" + "\n")
        if i % 5 == 0:  # overlapping event
            f.write(f"Dialogue: 2,{ats(start + 1)},{ats(start + 2)},Default,,0,0,0,,Overlap {i}\n")


def seg(kind, data):
    return bytes([kind]) + struct.pack('>H', len(data)) + data


def display_set(number, width, height, fill, clear=False):
    # PCS: video size, frame rate, composition number/state, palette update, palette id, objects
    if clear:
        pcs = struct.pack('>HHBHBBBB', 1920, 1080, 0x10, number, 0x00, 0, 0, 0)
        return [seg(0x16, pcs), seg(0x17, bytes([1, 0]) + struct.pack('>HHHH', 0, 0, width, height)), seg(0x80, b'')]
    pcs = struct.pack('>HHBHBBBB', 1920, 1080, 0x10, number, 0x80, 0, 0, 1) + struct.pack('>HBBHH', 0, 0, 0, 100, 900)
    wds = bytes([1, 0]) + struct.pack('>HHHH', 100, 900, width, height)
    pds = bytes([0, 0]) + bytes([1, 235, 128, 128, 255])
    rle = b''.join(bytes([fill]) * width + b'\x00\x00' for _ in range(height))
    ods = struct.pack('>HBB', 0, 0, 0xC0) + struct.pack('>I', len(rle) + 4)[1:] + struct.pack('>HH', width, height) + rle
    return [seg(0x16, pcs), seg(0x17, wds), seg(0x14, pds), seg(0x15, ods), seg(0x80, b'')]


with open('in.sup', 'wb') as f:
    for i in range(12):
        t = 3.0 + i * 11.0
        w = 40 + i * 37  # some display sets larger than the first 32 KiB read
        for when, segments in ((t, display_set(2 * i, w, 30 + i * 9, 1)), (t + 4.0, display_set(2 * i + 1, w, 30 + i * 9, 1, clear=True))):
            pts = round(when * 1000) * 90
            for s in segments:
                f.write(b'PG' + struct.pack('>II', pts, pts) + s)

run = lambda *a: subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', *a], check=True)
run('-f', 'lavfi', '-i', f'testsrc2=size=160x120:rate=4:duration={DURATION}',
    '-i', 'in.srt', '-i', 'in.ass', '-i', 'in.sup',
    '-map', '0:v', '-map', '1', '-map', '2', '-map', '3',
    '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '40', '-g', '8', '-c:s', 'copy',
    '-metadata:s:s:0', 'language=fre', 'subtitles.mkv')
# What Jellyfin's extraction writes (SubtitleEncoder: -c:s srt for text, copy for ass and pgs).
run('-i', 'subtitles.mkv', '-map', '0:1', '-an', '-vn', '-c:s', 'srt', 'expected.1.srt',
    '-map', '0:2', '-an', '-vn', '-c:s', 'copy', 'expected.2.ass',
    '-map', '0:3', '-an', '-vn', '-c:s', 'copy', 'expected.3.sup')
for name in ('in.srt', 'in.ass', 'in.sup'):
    import os
    os.remove(name)
