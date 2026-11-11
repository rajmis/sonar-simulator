"""
Visualise a depth/material frame and the sonar image it collapses to.

The top two panels are exactly what Unity sends over TCP (the parsed
range and material planes); the intensity panel is exactly what Python
sends back. The fan view is a reconstruction of Unity's display.

Two modes:

  # Offline — synthetic monopile fixture, no Unity needed
  python tools/visualize_frame.py --fixture

  # Live — frames dumped by the processor while Unity is running
  #   (start it with: python -m processor.src.main --dump-dir /tmp/sonar_dump)
  python tools/visualize_frame.py --dump-dir /tmp/sonar_dump
  python tools/visualize_frame.py --dump-dir /tmp/sonar_dump --watch

Add --out overview.png to save instead of opening a window.
"""

import argparse
import json
import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.colors import LinearSegmentedColormap, to_rgba
from matplotlib.patches import Patch

REPO = Path(__file__).parent.parent
sys.path.insert(0, str(REPO))

from processor.src.protocol import FrameHeader
from processor.src.materials import MaterialTable

DEPTH_CMAP = LinearSegmentedColormap.from_list(
    "depth", ["#eaf1fb", "#9dbfea", "#4a7fc1", "#1d4e89", "#0d2b52"])
DEPTH_CMAP.set_bad("#ececef")
SONAR_CMAP = LinearSegmentedColormap.from_list(
    "sonar", ["#000000", "#3d2400", "#8a5a00", "#d99a1e", "#ffd977", "#fff3cf"])
MATERIAL_COLORS = {0: "#b8b0ac", 1: "#5778a4", 2: "#9aa2ab", 3: "#e49444",
                   4: "#a87c5f", 5: "#6a6e73", 6: "#c58f6d", 7: "#3a3d40",
                   8: "#7fc7e0"}
NO_RETURN = "#ececef"
MUTED = "#6b6b76"


def load_fixture(config_path: Path):
    from processor.src.protocol import parse_frame
    from processor.src.processing import process_frame
    from processor.tests.generate_fixture import generate_frame_payload

    header, ranges, materials = parse_frame(generate_frame_payload())
    image = process_frame(header, ranges, materials, MaterialTable(config_path))
    return header, ranges, materials, image


def load_dump(dump_dir: Path):
    header = FrameHeader(**json.loads((dump_dir / "header.json").read_text()))
    return (header,
            np.load(dump_dir / "ranges.npy"),
            np.load(dump_dir / "materials.npy"),
            np.load(dump_dir / "intensity.npy"))


def _mark_swath(ax, header):
    """Dashed lines where the nominal swath/vFOV ends and padding begins."""
    for x in (-header.swath_angle_deg / 2, header.swath_angle_deg / 2):
        ax.axvline(x, color="#888890", lw=0.8, ls="--", alpha=0.7)
    for y in (-header.vertical_fov_deg / 2, header.vertical_fov_deg / 2):
        ax.axhline(y, color="#888890", lw=0.8, ls="--", alpha=0.5)


def plot(fig, header, ranges, materials, image, table: MaterialTable):
    fig.clf()
    az_half = header.swath_angle_deg / 2 + header.padding_deg
    el_half = header.vertical_fov_deg / 2 + header.padding_deg
    extent = [-az_half, az_half, -el_half, el_half]
    hit = ranges > 0

    gs = fig.add_gridspec(3, 2, height_ratios=[1, 1, 2.3], hspace=0.62,
                          wspace=0.25, left=0.07, right=0.97,
                          top=0.88, bottom=0.07)

    # --- Range plane (Unity → Python) ---
    ax = fig.add_subplot(gs[0, :])
    im = ax.imshow(np.where(hit, ranges, np.nan), origin="lower",
                   extent=extent, aspect="auto", cmap=DEPTH_CMAP)
    ax.set_title("Unity → Python · range plane — grey = no return, "
                 "dashes = swath/vFOV edge (padding beyond)",
                 loc="left", fontsize=10)
    ax.set_xlabel("azimuth (°)  —  columns map to beams")
    ax.set_ylabel("elevation (°)")
    _mark_swath(ax, header)
    fig.colorbar(im, ax=ax, pad=0.01).set_label("range (m)", fontsize=8)

    # --- Material plane (Unity → Python) ---
    ax = fig.add_subplot(gs[1, :])
    lut = np.array([to_rgba(MATERIAL_COLORS.get(i, "#d05ce3")) for i in range(256)])
    rgba = lut[materials]
    rgba[~hit] = to_rgba(NO_RETURN)
    ax.imshow(rgba, origin="lower", extent=extent, aspect="auto")
    ax.set_title("Unity → Python · material plane", loc="left", fontsize=10)
    ax.set_xlabel("azimuth (°)"); ax.set_ylabel("elevation (°)")
    _mark_swath(ax, header)
    present = sorted(np.unique(materials[hit]).tolist()) if hit.any() else []
    handles = [Patch(facecolor=MATERIAL_COLORS.get(m, "#d05ce3"),
                     label=f"{m} · {table.get(int(m)).name} "
                           f"({100 * (materials[hit] == m).mean():.0f}%)")
               for m in present]
    handles.append(Patch(facecolor=NO_RETURN,
                         label=f"no return ({100 * (~hit).mean():.0f}%)"))
    ax.legend(handles=handles, loc="upper left", fontsize=8, framealpha=0.92)

    # --- Intensity image (Python → Unity) ---
    ax = fig.add_subplot(gs[2, 0])
    im = ax.imshow(image.T, origin="lower", aspect="auto", cmap=SONAR_CMAP,
                   vmin=0, vmax=1,
                   extent=[0, header.num_beams, 0, header.max_range])
    ax.set_title("Python → Unity · intensity image "
                 f"({header.num_beams} beams × {header.num_range_bins} bins)",
                 loc="left", fontsize=10)
    ax.set_xlabel("beam index"); ax.set_ylabel("range (m)")
    beam_step = header.swath_angle_deg / header.num_beams
    secax = ax.secondary_xaxis(
        "top", functions=(lambda b: -header.swath_angle_deg / 2 + b * beam_step,
                          lambda a: (a + header.swath_angle_deg / 2) / beam_step))
    secax.set_xlabel("azimuth (°)", fontsize=8)
    secax.tick_params(labelsize=8)
    fig.colorbar(im, ax=ax, pad=0.02).set_label("normalised intensity (60 dB window)",
                                                fontsize=8)

    # --- Fan view (what Unity displays) ---
    ax = fig.add_subplot(gs[2, 1])
    theta = np.radians(np.linspace(-header.swath_angle_deg / 2,
                                   header.swath_angle_deg / 2,
                                   header.num_beams + 1))
    r_edges = np.linspace(0, header.max_range, header.num_range_bins + 1)
    th, rr = np.meshgrid(theta, r_edges)
    ax.pcolormesh(rr * np.sin(th), rr * np.cos(th), image.T,
                  cmap=SONAR_CMAP, vmin=0, vmax=1, shading="flat")
    ring_step = 5 if header.max_range <= 30 else (10 if header.max_range <= 100 else 25)
    tt = np.radians(np.linspace(-header.swath_angle_deg / 2,
                                header.swath_angle_deg / 2, 180))
    for ring in np.arange(ring_step, header.max_range + 0.1, ring_step):
        ax.plot(ring * np.sin(tt), ring * np.cos(tt),
                color="#55555c", lw=0.5, alpha=0.7)
        ax.annotate(f"{ring:.0f} m", xy=(0, ring), fontsize=6.5, color="#9a9aa2",
                    ha="center", va="bottom")
    for spoke in (-60, -30, 0, 30, 60):
        if abs(spoke) <= header.swath_angle_deg / 2:
            s = np.radians(spoke)
            ax.plot([0, header.max_range * np.sin(s)],
                    [0, header.max_range * np.cos(s)],
                    color="#55555c", lw=0.5, alpha=0.7)
            ax.annotate(f"{spoke:+d}°" if spoke else "0°",
                        xy=(1.04 * header.max_range * np.sin(s),
                            1.04 * header.max_range * np.cos(s)),
                        fontsize=6.5, color="#9a9aa2", ha="center")
    ax.set_title("fan view (as displayed in Unity)", loc="left", fontsize=10)
    ax.set_xlabel("across-track (m)"); ax.set_ylabel("down-range (m)")
    ax.set_aspect("equal")

    # --- Header/status line ---
    px, py, pz = header.sonar_pos
    water = "IN WATER" if header.in_water else "DRY — noise only"
    stats = (f"returns {100 * hit.mean():.0f}%"
             + (f", range {ranges[hit].min():.1f}–{ranges[hit].max():.1f} m"
                if hit.any() else ""))
    fig.suptitle(
        f"ping {header.ping_id}   ·   t = {header.timestamp:.2f} s   ·   "
        f"pos ({px:.1f}, {py:.1f}, {pz:.1f})   ·   {water}\n"
        f"grid {header.grid_width}×{header.grid_height}   ·   "
        f"swath {header.swath_angle_deg:.0f}° + 2×{header.padding_deg:.0f}° pad   ·   "
        f"vFOV {header.vertical_fov_deg:.0f}°   ·   "
        f"tilt {header.mount_tilt_deg:.0f}°   ·   "
        f"max range {header.max_range:.0f} m   ·   {stats}",
        x=0.07, ha="left", fontsize=10.5, fontweight="bold")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    src = parser.add_mutually_exclusive_group(required=True)
    src.add_argument("--fixture", action="store_true",
                     help="run the synthetic monopile fixture through the processor")
    src.add_argument("--dump-dir", type=Path,
                     help="directory written by `processor.src.main --dump-dir`")
    parser.add_argument("--watch", action="store_true",
                        help="with --dump-dir: refresh every second (live view)")
    parser.add_argument("--config", type=Path,
                        default=REPO / "processor" / "config" / "materials.yaml",
                        help="materials.yaml (for material names in the legend)")
    parser.add_argument("--out", type=Path, default=None,
                        help="save PNG instead of opening a window")
    args = parser.parse_args()

    table = MaterialTable(args.config)
    fig = plt.figure(figsize=(12.5, 9.5))

    if args.fixture:
        plot(fig, *load_fixture(args.config), table)
    elif args.watch:
        plt.ion()
        while plt.fignum_exists(fig.number):
            plot(fig, *load_dump(args.dump_dir), table)
            plt.pause(1.0)
        return
    else:
        plot(fig, *load_dump(args.dump_dir), table)

    if args.out:
        fig.savefig(args.out, dpi=150)
        print(f"saved {args.out}")
    else:
        plt.show()


if __name__ == "__main__":
    main()
