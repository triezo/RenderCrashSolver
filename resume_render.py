"""
resume_render.py - resume animation render after a Blender crash.

Called by RenderCrashSolver.exe or run_render.bat via:
blender -b file.blend -P resume_render.py [-- --output-dir DIR] [--prefix NAME]
blender -b file.blend -P resume_render.py -- --info   (only print SCENE_* lines, no render)

Logic:
- Optionally overrides the output path (folder and/or file name prefix) of the scene
- Scans the output folder for already-rendered frame files with that prefix
- Broken frames (empty / truncated JPG or PNG) are renamed to *.broken and re-rendered
- Renders the missing frames; existing frames are skipped (no overwrite),
  so holes in the middle of the range do not cause re-rendering of finished frames
- Prints ALL_FRAMES_DONE when every frame of the range is on disk

Machine-readable lines for the GUI (all start with "[resume_render] "):
  OUTPUT_EXT <ext>
  OUTPUT_PATH <scene.render.filepath, absolute, after override>
  OUTPUT_DIR <path>
  STATUS total=<n> done=<n> missing=<n>
  FRAME_START <frame>
  FRAME_DONE <frame> <seconds> <saved file path>
  (--info mode: SCENE_PATH, SCENE_FRAMES, SCENE_EXT, STATUS)
  ALL_FRAMES_DONE ...
"""

import argparse
import bpy
import os
import re
import sys
import time

# ─── SETTINGS ──────────────────────────────────────────────────
# None = use scene.render.filepath's directory (or --output-dir)
OUTPUT_DIR = None

# File extension of rendered frames (png, exr, jpg...). None = auto-detect from scene settings
FILE_EXT = None

# Override frame range if needed. None = use scene.frame_start / scene.frame_end
FRAME_START_OVERRIDE = None
FRAME_END_OVERRIDE = None
# ────────────────────────────────────────────────────────────

TAG = "[resume_render]"


def log(msg):
    # flush so the GUI sees progress immediately even when stdout is a pipe
    print(f"{TAG} {msg}", flush=True)


def parse_args():
    # Blender ignores everything after "--", so our own options go there
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(prog="resume_render.py")
    parser.add_argument("--output-dir", help="folder for rendered frames (overrides the scene)")
    parser.add_argument("--prefix", help="file name prefix, e.g. shot_ (overrides the scene)")
    parser.add_argument("--info", action="store_true", help="only print the scene output settings, do not render")
    return parser.parse_args(argv)


def apply_output_override(scene, args):
    if not args.output_dir and args.prefix is None:
        return
    current = bpy.path.abspath(scene.render.filepath)
    folder = args.output_dir or os.path.dirname(current)
    prefix = args.prefix if args.prefix is not None else os.path.basename(current)
    scene.render.filepath = os.path.join(folder, prefix)
    log(f"Output path overridden: {scene.render.filepath}")


def get_output_dir(scene):
    if OUTPUT_DIR:
        return bpy.path.abspath(OUTPUT_DIR)
    return bpy.path.abspath(os.path.dirname(scene.render.filepath))


def get_file_ext(scene):
    if FILE_EXT:
        return FILE_EXT.lower().lstrip(".")
    fmt = scene.render.image_settings.file_format
    ext_map = {
        "PNG": "png", "OPEN_EXR": "exr", "OPEN_EXR_MULTILAYER": "exr",
        "JPEG": "jpg", "TIFF": "tif", "TARGA": "tga", "BMP": "bmp",
        "FFMPEG": None,
    }
    return ext_map.get(fmt, "png")


def get_frame_pattern(scene, ext):
    """Regex for this scene's frame files: 'shot_' -> shot_0001.jpg, 'shot_####_v2' -> shot_0001_v2.jpg."""
    base = os.path.basename(bpy.path.abspath(scene.render.filepath))
    if "#" in base:
        prefix, suffix = base[:base.index("#")], base[base.rindex("#") + 1:]
    else:
        prefix, suffix = base, ""
    return re.compile(
        "^" + re.escape(prefix) + r"(\d+)" + re.escape(suffix) + r"\." + re.escape(ext) + "$",
        re.IGNORECASE,
    )


def is_complete(path, ext):
    """False for empty files and JPG/PNG files cut off mid-write."""
    try:
        size = os.path.getsize(path)
        if size == 0:
            return False
        if ext in ("jpg", "jpeg", "png"):
            with open(path, "rb") as f:
                f.seek(max(0, size - 32))
                tail = f.read()
            if ext == "png":
                return b"IEND" in tail
            return b"\xff\xd9" in tail
    except OSError:
        return False
    return True


def find_rendered_frames(output_dir, pattern, ext, fix_broken=True):
    if not os.path.isdir(output_dir):
        return set()

    rendered = set()
    for fname in os.listdir(output_dir):
        match = pattern.match(fname)
        if not match:
            continue
        path = os.path.join(output_dir, fname)
        if is_complete(path, ext):
            rendered.add(int(match.group(1)))
        elif fix_broken:
            broken = path + ".broken"
            try:
                if os.path.exists(broken):
                    os.remove(broken)
                os.rename(path, broken)
                log(f"WARN broken frame file renamed to {os.path.basename(broken)}, will re-render")
            except OSError as e:
                log(f"ERROR cannot rename broken frame {fname}: {e}")
    return rendered


_frame_t0 = [0.0]


def on_render_pre(scene, *args):
    _frame_t0[0] = time.time()
    log(f"FRAME_START {scene.frame_current}")


def on_render_write(scene, *args):
    path = bpy.path.abspath(scene.render.frame_path(frame=scene.frame_current))
    log(f"FRAME_DONE {scene.frame_current} {time.time() - _frame_t0[0]:.1f} {path}")


def main():
    args = parse_args()
    scene = bpy.context.scene

    frame_start = FRAME_START_OVERRIDE if FRAME_START_OVERRIDE is not None else scene.frame_start
    frame_end = FRAME_END_OVERRIDE if FRAME_END_OVERRIDE is not None else scene.frame_end

    full_range = set(range(frame_start, frame_end + 1))

    if args.info:
        # read-only query for the GUI: the scene's own output path, before any override
        ext = get_file_ext(scene)
        log(f"SCENE_PATH {bpy.path.abspath(scene.render.filepath)}")
        log(f"SCENE_FRAMES {frame_start} {frame_end}")
        log(f"SCENE_EXT {ext or 'FFMPEG'}")
        if ext:
            rendered = find_rendered_frames(get_output_dir(scene), get_frame_pattern(scene, ext), ext,
                                            fix_broken=False) & full_range
            log(f"STATUS total={len(full_range)} done={len(rendered)} missing={len(full_range) - len(rendered)}")
        return

    apply_output_override(scene, args)
    output_dir = get_output_dir(scene)
    ext = get_file_ext(scene)

    if ext is None:
        raise RuntimeError(
            "Output format is a video container (FFMPEG), not an image sequence. "
            "Cannot resume by frame number - switch Output to PNG/EXR."
        )

    pattern = get_frame_pattern(scene, ext)
    log(f"OUTPUT_EXT {ext}")
    log(f"OUTPUT_PATH {bpy.path.abspath(scene.render.filepath)}")
    log(f"OUTPUT_DIR {output_dir}")

    rendered = find_rendered_frames(output_dir, pattern, ext) & full_range
    missing = sorted(full_range - rendered)
    log(f"STATUS total={len(full_range)} done={len(rendered)} missing={len(missing)}")

    if not missing:
        log("ALL_FRAMES_DONE - all frames already rendered.")
        return

    resume_from = missing[0]
    log(f"Found {len(rendered)} finished frames. Missing: {len(missing)}. Resuming from frame {resume_from}.")

    scene.frame_start = resume_from
    scene.frame_end = frame_end
    # skip frames that already exist on disk instead of overwriting them
    scene.render.use_overwrite = False
    scene.render.use_placeholder = False

    bpy.app.handlers.render_pre.append(on_render_pre)
    bpy.app.handlers.render_write.append(on_render_write)

    bpy.ops.render.render(animation=True)

    still_missing = sorted(full_range - find_rendered_frames(output_dir, pattern, ext))
    if still_missing:
        log(f"Render finished but {len(still_missing)} frames are still missing (first: {still_missing[0]}).")
    else:
        log("ALL_FRAMES_DONE - render finished, all frames on disk.")


if __name__ == "__main__":
    main()
