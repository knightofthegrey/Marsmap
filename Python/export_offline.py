"""
Martian Map - export the offline bundle for the headset (no laptop or Wi-Fi needed).

Writes exactly what server.py + feeder.py would send to Unity into
Assets/StreamingAssets/Marsmap/, which Unity packs into the app. In offline mode
(Assets/Resources/marsmap_config.json "mode", or "auto" when no server answers),
OfflineReplay.cs feeds these messages through the same code path as live ones.

  manifest.json      list of terrain message files, in sending order
  NN_*.json / .bin   the terrain reply: site info, heightmap (HMAP or TILE), images (TEXL/TIMG)
  replay.json        the EVA replay: every telemetry / event / status message with its EVA time

Usage (from the Marsmap folder):
  python3 Python/export_offline.py                 # the HiRISE site (Python/site/), like server.py --site
  python3 Python/export_offline.py --demo          # the fake demo terrain
  python3 Python/export_offline.py export.csv 1025 # a JMARS CSV
  python3 Python/export_offline.py --polygon ...   # a polygon, like server.py (big: one file per tile)
  --speed / --tick / --eva-min / --samples / --events / --source as in feeder.py

The site is the right choice for a Quest: one 513 x 513 terrain and three 2048 px images
(about 5 MB). A tiled polygon can be hundreds of MB and too heavy for a Quest 1.
Re-run this after changing Python/site/, sol_samples.csv or events.json.
"""
import os
import sys
from pathlib import Path

# Same .venv relaunch as server.py / feeder.py
_VENV_PY = Path(__file__).resolve().parent.parent / ".venv" / (
    "Scripts/python.exe" if os.name == "nt" else "bin/python")
if (sys.prefix == sys.base_prefix and _VENV_PY.exists()
        and not os.environ.get("MARSMAP_VENV_RELAUNCH")):
    os.environ["MARSMAP_VENV_RELAUNCH"] = "1"
    if os.name == "nt":
        import subprocess
        sys.exit(subprocess.call([str(_VENV_PY), *sys.argv]))
    os.execv(str(_VENV_PY), [str(_VENV_PY), *sys.argv])

import argparse
import json
import shutil

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import feeder  # noqa: E402  (after the venv relaunch)
import server  # noqa: E402

DEFAULT_OUT = HERE.parent / "Assets" / "StreamingAssets" / "Marsmap"


def kind_of(msg):
    """Short file-name tag for a terrain message."""
    if isinstance(msg, str):
        try:
            return json.loads(msg).get("type", "json")
        except ValueError:
            return "text"
    tag = msg[:4].decode("ascii", "replace")
    if tag == "TEXL":
        n = int.from_bytes(msg[4:8], "little")
        return "layer_" + msg[8:8 + n].decode("utf-8", "replace")
    return tag.lower()


def main():
    ap = argparse.ArgumentParser(description="Export the headset's offline bundle")
    ap.add_argument("csv", nargs="?", help="lat/lon/elevation CSV (default: the HiRISE site)")
    ap.add_argument("res", nargs="?", type=int, default=None, help="heightmap resolution, 2^n + 1")
    ap.add_argument("--demo", action="store_true")
    ap.add_argument("--polygon", metavar="POINTS")
    ap.add_argument("--polygon-file", metavar="FILE")
    ap.add_argument("--max-tiles", type=int, default=16)
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    ap.add_argument("--samples", default=str(HERE / "sol_samples.csv"))
    ap.add_argument("--events", default=str(HERE / "events.json"))
    ap.add_argument("--speed", type=float, default=300.0)
    ap.add_argument("--tick", type=float, default=0.5)
    ap.add_argument("--eva-min", type=float, default=240.0)
    ap.add_argument("--source")
    # Polygon points without quotes, as in server.py
    argv, i = sys.argv[1:], 0
    while i < len(argv):
        if argv[i] == "--polygon":
            j = i + 1
            while j < len(argv) and "," in argv[j]:
                j += 1
            argv[i:j] = ["--polygon=" + " ".join(argv[i + 1:j])]
        i += 1
    a = ap.parse_args(argv)
    if (a.polygon or a.polygon_file) and a.csv:
        if a.csv.isdigit() and a.res is None:   # --polygon ... 1025, as server.py accepts
            a.res, a.csv = int(a.csv), None
        else:
            ap.error(f"--polygon builds its own terrain; don't also give a CSV ({a.csv})")

    # --- Terrain reply, built by server.py's own code ---
    if a.polygon or a.polygon_file:
        messages = server.prepare_polygon(a.polygon, a.polygon_file, a.res, a.max_tiles)
    else:
        csv_path = a.csv or (None if a.demo else str(server.SITE_CSV))
        messages = server.prepare_messages(csv_path, a.res or 513, a.demo)

    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)

    files, total = [], 0
    for n, msg in enumerate(messages, 1):
        name = f"{n:02d}_{kind_of(msg)}" + (".json" if isinstance(msg, str) else ".bin")
        data = msg.encode("utf-8") if isinstance(msg, str) else msg
        (out / name).write_bytes(data)
        files.append(name)
        total += len(data)
    # Remove files from an earlier export that aren't part of this one (and Unity's .meta for
    # them). Files that are rewritten keep their .meta, so Unity's IDs don't change.
    keep = set(files) | {"replay.json", "manifest.json"}
    for old in out.iterdir():
        if old.is_file() and old.suffix in (".json", ".bin") and old.name not in keep:
            old.unlink()
            Path(str(old) + ".meta").unlink(missing_ok=True)

    # --- EVA replay, built by feeder.py's own code ---
    samples = feeder.load_samples(a.samples)
    with open(a.events) as f:
        events = json.load(f)
    source = feeder.load_source(a.samples, a.source)
    steps = feeder.timeline(samples, events, a.speed, a.tick, a.eva_min, source)
    replay = {"version": 1, "speed": a.speed, "tick_s": a.tick, "eva_min": a.eva_min, "source": source,
              "messages": [{"t_eva_min": round(t, 6), "json": feeder.encode(m)} for t, m in steps]}
    (out / "replay.json").write_text(json.dumps(replay, indent=1))
    manifest = {"version": 1, "terrain": files, "replay": "replay.json"}
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2))

    print(f"Offline bundle -> {out}")
    print(f"  terrain: {len(files)} message(s), {total / 1e6:.1f} MB ({server.describe_terrain(messages)})")
    print(f"  replay: {len(steps)} messages, {a.eva_min * 60 / a.speed:.0f} s at {a.speed:g}x "
          f"({sum(1 for _, m in steps if m['type'] in ('event', 'earth_note'))} events)")
    if total > 50e6:
        print("  WARNING: over 50 MB of terrain; that's heavy for a Quest app (the HiRISE site is ~5 MB)")


if __name__ == "__main__":
    main()
