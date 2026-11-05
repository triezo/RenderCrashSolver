"""
resume_render.py - resume animation render after a Blender crash.

Called by RenderSolver.exe (RenderCrashSolver.exe up to 0.5) or run_render.bat via:
blender -b file.blend -P resume_render.py [-- --output-dir DIR] [--prefix NAME] [--override-since UNIX_TS] [--frames SPEC]
blender -b file.blend -P resume_render.py -- --info [--frames SPEC] [--output-dir DIR] [--prefix NAME]
  (only print SCENE_* lines and STATUS, no render; SCENE_PATH is the scene's own path, STATUS counts the custom output)

Logic:
- Frames: the scene range with the scene's frame step, or --frames "1-100,120,150-200" (exactly those frames;
  each run of consecutive frames is rendered as its own animation render)
- Optionally renders to a different output folder and/or file name prefix (custom output)
- Scans the output folder for already-rendered frame files with that prefix
- Broken frames (empty / truncated JPG or PNG) are renamed to *.broken and re-rendered
- Renders the missing frames; existing frames are skipped (no overwrite),
  so holes in the middle of the range do not cause re-rendering of finished frames
- Override (--override-since): re-render everything. Frames saved before that time count as stale
  and are overwritten; frames saved after it belong to this re-render, so a crash resumes it
- Prints ALL_FRAMES_DONE when every selected frame is on disk

Machine-readable lines for the GUI (all start with "[resume_render] "):
  OUTPUT_EXT <ext>
  OUTPUT_PATH <scene.render.filepath, absolute, custom output applied>
  OUTPUT_DIR <path>
  STATUS total=<n> done=<n> missing=<n>        (counts of the selected frames)
  FRAME_START <frame>
  FRAME_DONE <frame> <seconds> <saved file path>
  (--info mode: SCENE_PATH, SCENE_FRAMES <start> <end> <step>, SCENE_EXT, STATUS)
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

# exit code when the render itself fails inside Blender (GPU/CUDA errors etc.);
# the GUI and run_render.bat restart Blender, same as after a crash
RENDER_ERROR_EXIT_CODE = 3


def log(msg):
    # flush so the GUI sees progress immediately even when stdout is a pipe
    print(f"{TAG} {msg}", flush=True)


def parse_args():
    # Blender ignores everything after "--", so our own options go there
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(prog="resume_render.py")
    parser.add_argument("--output-dir", help="folder for rendered frames (instead of the scene path)")
    parser.add_argument("--prefix", help="file name prefix, e.g. shot_ (instead of the scene name)")
    parser.add_argument("--override-since", type=float,
                        help="re-render all frames: files saved before this unix time are stale and get overwritten")
    parser.add_argument("--frames", help="render only these frames, e.g. 1-100,120,150-200 (instead of the scene range)")
    parser.add_argument("--info", action="store_true", help="only print the scene output settings, do not render")
    return parser.parse_args(argv)


# Blender's MAXFRAME
MAX_FRAME = 1048574


def parse_frames(spec):
    """'1-100, 120 150-200' -> set of frame numbers. ValueError for anything else."""
    frames = set()
    for part in re.split(r"[,;\s]+", spec.strip()):
        if not part:
            continue
        m = re.fullmatch(r"(\d+)(?:-(\d+))?", part)
        if not m:
            raise ValueError(f"cannot read frames {part!r}: use numbers and ranges like 1-100,120")
        first, last = int(m.group(1)), int(m.group(2) or m.group(1))
        if last < first or last > MAX_FRAME:
            raise ValueError(f"bad frame range {part!r}")
        frames.update(range(first, last + 1))
    if not frames:
        raise ValueError("no frames selected")
    return frames


def frame_segments(frames, step):
    """Runs of frames that are `step` apart: {1,2,3,7,8} with step 1 -> [(1, 3), (7, 8)]."""
    segments = []
    for f in sorted(frames):
        if segments and f == segments[-1][1] + step:
            segments[-1][1] = f
        else:
            segments.append([f, f])
    return [(a, b) for a, b in segments]


def apply_custom_output(scene, args):
    if not args.output_dir and args.prefix is None:
        return
    current = bpy.path.abspath(scene.render.filepath)
    folder = args.output_dir or os.path.dirname(current)
    prefix = args.prefix if args.prefix is not None else os.path.basename(current)
    scene.render.filepath = os.path.join(folder, prefix)
    log(f"Custom output path: {scene.render.filepath}")


def get_output_dir(scene):
    if OUTPUT_DIR:
        return bpy.path.abspath(OUTPUT_DIR)
    # abspath first: Blender stores relative paths as "//render\shot_", and ntpath.dirname
    # treats a leading "//" as a UNC share, returning the whole string as the "directory"
    return os.path.dirname(bpy.path.abspath(scene.render.filepath))


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


def find_rendered_frames(output_dir, pattern, ext, fix_broken=True, stale_before=None, stale=None):
    """Frame numbers with a complete file on disk.

    stale_before (Override): files saved before this unix time don't count as rendered;
    their frame numbers are collected into the optional `stale` set instead.
    """
    if not os.path.isdir(output_dir):
        return set()

    rendered = set()
    for fname in os.listdir(output_dir):
        match = pattern.match(fname)
        if not match:
            continue
        path = os.path.join(output_dir, fname)
        if is_complete(path, ext):
            if stale_before is not None and os.path.getmtime(path) < stale_before:
                if stale is not None:
                    stale.add(int(match.group(1)))
            else:
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


def describe_existing(output_dir, pattern, frames):
    """Log which files were counted as finished frames, so a wrong folder / stale renders are visible."""
    if not frames:
        return
    names = {}
    for fname in os.listdir(output_dir):
        match = pattern.match(fname)
        if match and int(match.group(1)) in frames:
            names[int(match.group(1))] = fname
    if not names:
        return
    newest = max(os.path.getmtime(os.path.join(output_dir, n)) for n in names.values())
    log(f"Existing frames counted as done: {len(names)} files in {output_dir} "
        f"({names[min(names)]} ... {names[max(names)]}), newest saved "
        f"{time.strftime('%Y-%m-%d %H:%M', time.localtime(newest))}")


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
    scene_step = max(1, scene.frame_step)

    # the frames to have on disk: the scene range (with the scene's step, which Blender honours too)
    # or exactly the frames given with --frames
    if args.frames:
        selection, step = parse_frames(args.frames), 1
    else:
        selection, step = set(range(frame_start, frame_end + 1, scene_step)), scene_step

    if args.info:
        # read-only query for the GUI: the scene's own output path, before a custom output is applied
        ext = get_file_ext(scene)
        log(f"SCENE_PATH {bpy.path.abspath(scene.render.filepath)}")
        log(f"SCENE_FRAMES {frame_start} {frame_end} {scene_step}")
        log(f"SCENE_EXT {ext or 'FFMPEG'}")
        if ext:
            # count the frames where they will actually be written (custom output applied)
            apply_custom_output(scene, args)
            rendered = find_rendered_frames(get_output_dir(scene), get_frame_pattern(scene, ext), ext,
                                            fix_broken=False) & selection
            log(f"STATUS total={len(selection)} done={len(rendered)} missing={len(selection) - len(rendered)}")
        return

    apply_custom_output(scene, args)
    output_dir = get_output_dir(scene)
    override_since = args.override_since
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

    stale = set()
    rendered = find_rendered_frames(output_dir, pattern, ext, stale_before=override_since, stale=stale) & selection
    missing = sorted(selection - rendered)
    log(f"STATUS total={len(selection)} done={len(rendered)} missing={len(missing)}")
    describe_existing(output_dir, pattern, rendered)
    if override_since is not None:
        since = time.strftime('%Y-%m-%d %H:%M', time.localtime(override_since))
        log(f"Override: re-rendering all frames; {len(stale & selection)} files saved before {since} "
            f"will be overwritten, {len(rendered)} already re-rendered")

    if not missing:
        log("ALL_FRAMES_DONE - all frames already rendered.")
        return

    log(f"Found {len(rendered)} finished frames. Missing: {len(missing)}. Resuming from frame {missing[0]}.")

    # normally: skip frames that already exist on disk instead of overwriting them.
    # Override: stale files must be overwritten. The re-render goes in order, so every frame from
    # the first missing one of a segment on is stale or missing and nothing re-rendered gets redone.
    scene.render.use_overwrite = override_since is not None
    scene.render.use_placeholder = False

    bpy.app.handlers.render_pre.append(on_render_pre)
    bpy.app.handlers.render_write.append(on_render_write)

    segments = frame_segments(selection, step)
    for seg_start, seg_end in segments:
        seg_missing = [f for f in missing if seg_start <= f <= seg_end]
        if not seg_missing:
            continue
        if len(segments) > 1:
            log(f"Rendering frames {seg_missing[0]}-{seg_end}" if seg_missing[0] != seg_end else f"Rendering frame {seg_end}")
        scene.frame_start = seg_missing[0]
        scene.frame_end = seg_end
        scene.frame_step = step
        try:
            bpy.ops.render.render(animation=True)
        except RuntimeError as e:
            # Cycles reports GPU failures (e.g. CUDA "Illegal address") as an exception, not a crash.
            # Exit hard with a dedicated code: the GPU context is broken, a fresh Blender has to continue.
            message = str(e).strip().splitlines()
            log(f"RENDER_ERROR {message[0] if message else repr(e)}")
            sys.stdout.flush()
            sys.stderr.flush()
            os._exit(RENDER_ERROR_EXIT_CODE)

    still_missing = sorted(selection - find_rendered_frames(output_dir, pattern, ext, stale_before=override_since))
    if still_missing:
        log(f"Render finished but {len(still_missing)} frames are still missing (first: {still_missing[0]}).")
    else:
        log("ALL_FRAMES_DONE - render finished, all frames on disk.")


if __name__ == "__main__":
    main()
