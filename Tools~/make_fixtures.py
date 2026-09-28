#!/usr/bin/env python3
"""Generates the synthetic test fixtures used by the SplatPresso test suites.

Everything here is procedurally generated (no real capture or session data), deterministic (fixed seed)
and small (< 1.5 MB in total). Output goes to ``Tests/Runtime/Fixtures`` next to this ``Tools~`` folder:

    object.ply         8192 gaussians, binary_little_endian, the 17 float properties TripoSplat writes
                       (x y z nx ny nz f_dc_0..2 opacity scale_0..2 rot_0..3; no f_rest_*). A red chair-like
                       shape normalized so its longest axis spans [-0.5, 0.5], Y-DOWN (3DGS / TripoSplat
                       convention), normals all zero, some opacity logits +inf and a few -inf (as observed
                       in real TripoSplat output).
    edited.jpg         1280x720 synthetic room with the "added" red chair (the image-edit model output).
    cutout.png         1280x720 RGBA full-frame cutout of the chair (the SAM-3 masked output).
    enhanced.png       512x512 chair on white (the enhance model output).
    generated.png      512x512 chair on white (the text-to-image output of the direct mode).
    depth.png          1280x720 relative depth of edited.jpg (near = bright, like Depth-Anything), RGB gray.
    object.glb         unit cube glTF binary (mesh mode).
    decision.json      DECIDE result for one "red chair" (JSON names match PlacementDecisionService's schema).
    verification.json  VERIFY result whose bbox is the chair's exact pixel bounds in edited.jpg.

Usage:  python Tools~/make_fixtures.py [--out DIR]
Requires Python 3.8+, numpy and Pillow.
"""
import argparse
import json
import os
import struct

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

SEED = 1234
W, H = 1280, 720
SPLAT_COUNT = 8192
SH_C0 = 0.28209479177387814

# Chair rectangle in the edited image (normalized x0, y0, x1, y1). Its bottom edge sits on the floor, in the
# lower-middle of the frame, so the placement anchor lands on floor depth in the test scenes too.
CHAIR_BOX_NORM = (0.44, 0.46, 0.56, 0.86)
HORIZON_Y = 400  # back wall / floor boundary in edited.jpg


# ------------------------------------------------------------------------------------------
# 3D: gaussian splat chair

def chair_boxes():
    """Axis-aligned boxes (min, max, rgb) of a simple chair in Y-UP meters (seat height 0.45, back to 1.0)."""
    red = (0.78, 0.08, 0.08)
    dark = (0.36, 0.05, 0.04)
    boxes = [
        ((-0.25, 0.42, -0.25), (0.25, 0.48, 0.25), red),    # seat
        ((-0.25, 0.48, 0.19), (0.25, 1.00, 0.25), red),     # backrest (solid, so the chair's middle is opaque)
    ]
    for sx in (-1, 1):
        for sz in (-1, 1):
            cx, cz = 0.215 * sx, 0.215 * sz
            boxes.append(((cx - 0.025, 0.0, cz - 0.025), (cx + 0.025, 0.42, cz + 0.025), dark))  # legs
    return boxes


def sample_box_surfaces(boxes, count, rng):
    """Samples `count` splats on the box faces (area-proportional). Returns pos, rgb, per-splat normal axis."""
    faces = []  # (box index, axis, side, area)
    for bi, (mn, mx, _) in enumerate(boxes):
        size = np.subtract(mx, mn)
        for axis in range(3):
            a, b = [i for i in range(3) if i != axis]
            area = size[a] * size[b]
            for side in (0, 1):
                faces.append((bi, axis, side, area))
    areas = np.array([f[3] for f in faces], dtype=np.float64)
    raw = areas / areas.sum() * count
    per_face = np.floor(raw).astype(int)
    # distribute the rounding remainder to the faces with the largest fractional parts
    for i in np.argsort(-(raw - per_face))[: count - per_face.sum()]:
        per_face[i] += 1

    pos, rgb, normal_axis = [], [], []
    for (bi, axis, side, _), n in zip(faces, per_face):
        if n == 0:
            continue
        mn, mx, color = boxes[bi]
        p = rng.uniform(mn, mx, size=(n, 3))
        p[:, axis] = mx[axis] if side else mn[axis]
        pos.append(p)
        c = np.clip(np.array(color) + rng.normal(0.0, 0.025, size=(n, 3)), 0.0, 1.0)
        rgb.append(c)
        normal_axis.append(np.full(n, axis))
    return np.concatenate(pos), np.concatenate(rgb), np.concatenate(normal_axis)


def make_ply(path, rng):
    pos, rgb, normal_axis = sample_box_surfaces(chair_boxes(), SPLAT_COUNT, rng)
    n = len(pos)
    assert n == SPLAT_COUNT, n

    # normalize like TripoSplat: centered bounds, longest axis spans [-0.5, 0.5]
    mn, mx = pos.min(axis=0), pos.max(axis=0)
    pos = (pos - (mn + mx) * 0.5) / (mx - mn).max()
    pos[:, 1] = -pos[:, 1]  # Y-UP -> Y-DOWN (3DGS / TripoSplat convention; the package's content transform undoes it)

    order = rng.permutation(n)  # file order unrelated to geometry (the importer Morton-sorts it)
    pos, rgb, normal_axis = pos[order], rgb[order], normal_axis[order]

    # log-space scales: flat disks lying in their face (thin along the face normal)
    sigma = np.full((n, 3), 0.011)
    sigma[np.arange(n), normal_axis] = 0.003
    sigma *= rng.uniform(0.8, 1.25, size=(n, 1))
    scale = np.log(sigma)

    # opacity logits: ~30% +inf and a handful of -inf, as in real TripoSplat files
    opacity = rng.uniform(1.5, 5.0, size=n)
    opacity[rng.random(n) < 0.3] = np.inf
    opacity[rng.choice(n, 10, replace=False)] = -np.inf

    # rot_0..3 = (w, x, y, z), deliberately NOT unit length (readers must normalize)
    rot = np.zeros((n, 4))
    rot[:, 0] = 1.0
    rot[:, 1:] = rng.normal(0.0, 0.04, size=(n, 3))
    rot *= rng.uniform(0.5, 2.0, size=(n, 1))

    f_dc = (rgb - 0.5) / SH_C0
    normals = np.zeros((n, 3))
    data = np.concatenate([pos, normals, f_dc, opacity[:, None], scale, rot], axis=1).astype("<f4")
    assert data.shape == (n, 17)

    props = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
             "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    header = "ply\nformat binary_little_endian 1.0\nelement vertex %d\n" % n
    header += "".join("property float %s\n" % p for p in props)
    header += "end_header\n"
    with open(path, "wb") as f:
        f.write(header.encode("ascii"))
        f.write(data.tobytes())


# ------------------------------------------------------------------------------------------
# 2D: room + chair images

def box_px(box_norm, w, h):
    x0, y0, x1, y1 = box_norm
    return int(round(x0 * w)), int(round(y0 * h)), int(round(x1 * w)), int(round(y1 * h))


def chair_shapes(x0, y0, x1, y1):
    """Front-view chair silhouette inside the pixel rect: list of (rect, shade)."""
    cw, ch = x1 - x0, y1 - y0
    leg = max(2, int(round(cw * 0.07)))
    seat_top = y0 + int(round(ch * 0.55))
    seat_bot = y0 + int(round(ch * 0.63))
    inset = max(1, int(round(cw * 0.06)))
    return [
        ((x0 + inset, y0, x1 - inset - 1, seat_top), 1.0),                          # backrest
        ((x0 + inset + leg, seat_bot, x0 + inset + 2 * leg, y1 - 1 - ch // 20), 0.55),  # back legs (behind)
        ((x1 - inset - 2 * leg, seat_bot, x1 - inset - leg, y1 - 1 - ch // 20), 0.55),
        ((x0, seat_top, x1 - 1, seat_bot), 0.85),                                     # seat
        ((x0, seat_bot, x0 + leg, y1 - 1), 0.7),                                      # front legs
        ((x1 - 1 - leg, seat_bot, x1 - 1, y1 - 1), 0.7),
    ]


def draw_chair(img, mask, rect, base=(190, 30, 35)):
    x0, y0, x1, y1 = rect
    d = ImageDraw.Draw(img)
    md = ImageDraw.Draw(mask) if mask is not None else None
    for (r, shade) in chair_shapes(x0, y0, x1, y1):
        col = tuple(int(c * shade) for c in base)
        d.rectangle(r, fill=col)
        if md is not None:
            md.rectangle(r, fill=255)
    # a highlight stripe on the backrest so the object is not a flat blob
    bx0, by0, bx1, by1 = chair_shapes(x0, y0, x1, y1)[0][0]
    d.rectangle((bx0 + (bx1 - bx0) // 6, by0 + (by1 - by0) // 8, bx0 + (bx1 - bx0) // 4, by1 - (by1 - by0) // 8),
                fill=tuple(min(255, int(c * 1.25) + 20) for c in base))


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def make_room(rng):
    img = Image.new("RGB", (W, H))
    px = np.zeros((H, W, 3), dtype=np.float32)
    ys = np.arange(H, dtype=np.float32)[:, None]
    xs = np.arange(W, dtype=np.float32)[None, :]
    # back wall
    wall_t = ys / HORIZON_Y
    px[..., 0] = 232 - 18 * wall_t
    px[..., 1] = 222 - 18 * wall_t
    px[..., 2] = 204 - 20 * wall_t
    # floor (darker toward the horizon) with perspective plank lines
    floor = ys >= HORIZON_Y
    ft = np.clip((ys - HORIZON_Y) / (H - HORIZON_Y), 0, 1)
    fr, fg, fb = 120 + 60 * ft, 82 + 40 * ft, 50 + 25 * ft
    vx = W * 0.5
    plank = (np.abs(((xs - vx) / np.maximum(ys - HORIZON_Y + 30, 1)) * 6.0) % 1.0) < 0.06
    px[..., 0] = np.where(floor, np.where(plank, fr * 0.8, fr), px[..., 0])
    px[..., 1] = np.where(floor, np.where(plank, fg * 0.8, fg), px[..., 1])
    px[..., 2] = np.where(floor, np.where(plank, fb * 0.8, fb), px[..., 2])
    px += rng.normal(0, 2.0, size=px.shape)  # a little sensor-like noise
    img = Image.fromarray(np.clip(px, 0, 255).astype(np.uint8), "RGB")

    d = ImageDraw.Draw(img)
    # left side wall
    d.polygon([(0, 0), (200, 40), (200, HORIZON_Y), (0, 560)], fill=(196, 186, 168))
    d.polygon([(0, 560), (200, HORIZON_Y), (200, HORIZON_Y + 4), (0, 566)], fill=(150, 140, 125))
    # window with a sky gradient
    for y in range(110, 300):
        d.line([(430, y), (600, y)], fill=lerp((150, 190, 230), (205, 225, 240), (y - 110) / 190))
    d.rectangle((424, 104, 606, 306), outline=(245, 245, 240), width=6)
    d.line([(515, 104), (515, 306)], fill=(245, 245, 240), width=4)
    # table (right) with a contact shadow
    d.ellipse((790, 548, 1080, 578), fill=(95, 64, 40))
    d.rectangle((800, 428, 1060, 448), fill=(110, 70, 40))
    d.rectangle((800, 448, 1060, 456), fill=(80, 50, 30))
    for lx in (812, 1036):
        d.rectangle((lx, 456, lx + 12, 562), fill=(90, 58, 34))
    # a plant pot on the left
    d.rectangle((250, 470, 310, 540), fill=(170, 90, 60))
    d.ellipse((230, 380, 330, 480), fill=(60, 120, 60))
    return img


def make_edited_and_cutout(rng, out):
    room = make_room(rng)
    rect = box_px(CHAIR_BOX_NORM, W, H)
    # soft contact shadow under the chair (NOT part of the mask, like a real edit)
    shadow = Image.new("L", (W, H), 0)
    ImageDraw.Draw(shadow).ellipse((rect[0] - 12, rect[3] - 10, rect[2] + 12, rect[3] + 8), fill=110)
    shadow = shadow.filter(ImageFilter.GaussianBlur(6))
    room = Image.composite(Image.new("RGB", (W, H), (40, 28, 20)), room, shadow)

    mask = Image.new("L", (W, H), 0)
    draw_chair(room, mask, rect)
    room.save(os.path.join(out, "edited.jpg"), "JPEG", quality=85, optimize=True)

    edited = Image.open(os.path.join(out, "edited.jpg")).convert("RGB")
    cutout = edited.convert("RGBA")
    cutout.putalpha(mask)
    # fully transparent pixels carry no color (keeps the PNG small and matches masked outputs)
    arr = np.array(cutout)
    arr[arr[..., 3] == 0] = 0
    Image.fromarray(arr, "RGBA").save(os.path.join(out, "cutout.png"), "PNG", optimize=True)

    m = np.array(mask) > 0
    rows, cols = np.where(m.any(axis=1))[0], np.where(m.any(axis=0))[0]
    return {
        "x": int(cols.min()), "y": int(rows.min()),
        "w": int(cols.max() - cols.min() + 1), "h": int(rows.max() - rows.min() + 1),
    }, rect


def make_depth(out, chair_rect):
    """Relative inverse depth (near = bright) consistent with the room layout of edited.jpg."""
    ys = np.arange(H, dtype=np.float32)[:, None].repeat(W, axis=1)
    depth = np.full((H, W), 60.0, dtype=np.float32)  # back wall
    floor = ys >= HORIZON_Y
    depth[floor] = 60.0 + (ys[floor] - HORIZON_Y) / (H - HORIZON_Y) * 195.0
    img = Image.fromarray(np.clip(depth, 0, 255).astype(np.uint8), "L").convert("RGB")
    d = ImageDraw.Draw(img)
    d.polygon([(0, 0), (200, 40), (200, HORIZON_Y), (0, 560)], fill=(95, 95, 95))  # side wall is nearer
    table_v = int(60 + (562 - HORIZON_Y) / (H - HORIZON_Y) * 195)
    d.rectangle((800, 428, 1060, 562), fill=(table_v,) * 3)
    x0, y0, x1, y1 = chair_rect
    chair_v = int(60 + (y1 - HORIZON_Y) / (H - HORIZON_Y) * 195)  # same disparity as the floor it stands on
    shapes = Image.new("RGB", (W, H))
    draw_chair(shapes, None, chair_rect, base=(255, 255, 255))
    chair_mask = np.array(shapes.convert("L")) > 0
    arr = np.array(img)
    arr[chair_mask] = chair_v
    img = Image.fromarray(arr, "RGB").filter(ImageFilter.GaussianBlur(1.5))
    img.save(os.path.join(out, "depth.png"), "PNG", optimize=True)


def make_object_image(path, base, bg=(255, 255, 255)):
    img = Image.new("RGB", (512, 512), bg)
    d = ImageDraw.Draw(img)
    d.ellipse((150, 440, 362, 470), fill=(225, 225, 225))  # soft contact shadow
    draw_chair(img, None, (166, 70, 346, 456), base=base)
    img = img.filter(ImageFilter.SMOOTH)
    img.save(path, "PNG", optimize=True)


# ------------------------------------------------------------------------------------------
# glTF binary unit cube

def make_glb(path):
    positions, normals, indices = [], [], []
    for axis in range(3):
        for sign in (1.0, -1.0):
            n = [0.0, 0.0, 0.0]
            n[axis] = sign
            u = [0.0, 0.0, 0.0]
            v = [0.0, 0.0, 0.0]
            u[(axis + 1) % 3] = 1.0
            v[(axis + 2) % 3] = 1.0
            if sign < 0:
                u, v = v, u  # keep u x v == n so the winding stays counter-clockwise seen from outside
            base = len(positions)
            for su, sv in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                positions.append([0.5 * (n[i] + su * u[i] + sv * v[i]) for i in range(3)])
                normals.append(list(n))
            indices += [base, base + 1, base + 2, base, base + 2, base + 3]

    idx = struct.pack("<%dH" % len(indices), *indices)
    idx += b"\0" * ((4 - len(idx) % 4) % 4)
    pos = b"".join(struct.pack("<3f", *p) for p in positions)
    nrm = b"".join(struct.pack("<3f", *p) for p in normals)
    binary = idx + pos + nrm
    gltf = {
        "asset": {"version": "2.0", "generator": "SplatPresso make_fixtures.py"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "Cube"}],
        "meshes": [{"name": "Cube", "primitives": [{"attributes": {"POSITION": 1, "NORMAL": 2}, "indices": 0, "material": 0}]}],
        "materials": [{"name": "Red", "pbrMetallicRoughness": {"baseColorFactor": [0.8, 0.1, 0.1, 1.0],
                                                               "metallicFactor": 0.0, "roughnessFactor": 0.8}}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(indices) * 2, "target": 34963},
            {"buffer": 0, "byteOffset": len(idx), "byteLength": len(pos), "target": 34962},
            {"buffer": 0, "byteOffset": len(idx) + len(pos), "byteLength": len(nrm), "target": 34962},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5123, "count": len(indices), "type": "SCALAR"},
            {"bufferView": 1, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": [-0.5, -0.5, -0.5], "max": [0.5, 0.5, 0.5]},
            {"bufferView": 2, "componentType": 5126, "count": len(normals), "type": "VEC3"},
        ],
    }
    js = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    js += b" " * ((4 - len(js) % 4) % 4)
    total = 12 + 8 + len(js) + 8 + len(binary)
    with open(path, "wb") as f:
        f.write(struct.pack("<4sII", b"glTF", 2, total))
        f.write(struct.pack("<I4s", len(js), b"JSON"))
        f.write(js)
        f.write(struct.pack("<I4s", len(binary), b"BIN\0"))
        f.write(binary)


# ------------------------------------------------------------------------------------------
# JSON results

def round4(v):
    return [round(float(x), 4) for x in v]


def write_json(path, obj):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")


def make_json(out, bounds):
    x, y, w, h = bounds["x"] / W, bounds["y"] / H, bounds["w"] / W, bounds["h"] / H
    bbox = round4([x, y, w, h])
    decision = {
        "scene_summary": "A living room with a wooden floor, a beige back wall with a window, a small wooden table on the right and a potted plant on the left.",
        "feasible": True,
        "infeasible_reason": None,
        "objects": [{
            "id": 1,
            "name": "red chair",
            "description_for_image_edit": "a red wooden chair with a tall solid backrest, matte paint, lit by the soft window light",
            "description_for_segmentation": "red chair",
            "target_bbox_norm": bbox,
            "size_hint_m": 0.9,
            "resting_surface": "ground",
            "against_wall": "no",
        }],
        "edit_prompt": ("Add a red wooden chair with a tall solid backrest standing on the floor in the middle of the room, "
                        "left of the wooden table. Keep everything else exactly the same: same camera angle, same framing, "
                        "same lighting, same photographic style. Do not modify or remove any existing content."),
    }
    verification = {
        "camera_unchanged": True,
        "unexpected_changes": "",
        "objects": [{
            "id": 1,
            "name": "red chair",
            "found": True,
            "bbox_norm": bbox,
            "fully_visible": True,
            "support": "floor",
            "back_against_wall": "no",
            "front_faces": "toward_viewer",
            "notes": "",
        }],
    }
    write_json(os.path.join(out, "decision.json"), decision)
    write_json(os.path.join(out, "verification.json"), verification)
    return bbox


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=os.path.join(here, "..", "Tests", "Runtime", "Fixtures"))
    args = ap.parse_args()
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)

    rng = np.random.default_rng(SEED)
    make_ply(os.path.join(out, "object.ply"), rng)
    bounds, rect = make_edited_and_cutout(rng, out)
    make_depth(out, rect)
    make_object_image(os.path.join(out, "enhanced.png"), base=(190, 30, 35))
    make_object_image(os.path.join(out, "generated.png"), base=(205, 45, 30))
    make_glb(os.path.join(out, "object.glb"))
    bbox = make_json(out, bounds)

    names = ["object.ply", "edited.jpg", "cutout.png", "enhanced.png", "generated.png", "depth.png",
             "object.glb", "decision.json", "verification.json"]
    total = 0
    for n in names:
        size = os.path.getsize(os.path.join(out, n))
        total += size
        print("%-18s %8d bytes" % (n, size))
    print("%-18s %8d bytes" % ("total", total))
    print("chair bbox (x, y, w, h normalized):", bbox)
    assert total < 1_500_000, "fixtures must stay under 1.5 MB"


if __name__ == "__main__":
    main()
