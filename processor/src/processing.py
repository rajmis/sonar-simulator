"""
Acoustic signal processing: collapse a polar depth/material frame into a
sonar intensity image. Runs on GPU (CUDA) via PyTorch when available.

Processing stages:
1. Reconstruct 3D points from the range grid (sonar local frame)
2. Estimate surface normals from depth gradients → incidence angle
3. Per-pixel sonar equation using material properties
4. Beam-pattern weighting (Gaussian across elevation and within-beam azimuth)
5. Max-bin into (beam, range bin) with Gaussian range spread
6. dB conversion with fixed dynamic range → 0–1
7. Noise model (noise floor everywhere, volume scattering before the
   first return per beam)

Acoustic shadows are not an explicit stage: the depth map only contains
the first surface along each ray, so occluded regions simply contribute
no returns and come out dark.
"""

import math

import numpy as np
import torch

from .protocol import FrameHeader
from .materials import MaterialTable

DEVICE = torch.device("cuda" if torch.cuda.is_available() else "cpu")

_MAX_FOOTPRINT_BINS = 32    # cap on a pixel's half-extent across range bins
_MAX_SPLAT_BINS = 36        # footprint cap + Gaussian pulse tail
_DYNAMIC_RANGE_DB = 60.0    # display window in dB
_BEAM_PATTERN_SIGMA = 0.5   # Gaussian sigma in normalised beam coordinates


class _FrameGeometry:
    """Grid quantities that only depend on the frame header, cached across pings."""

    def __init__(self, header: FrameHeader):
        w, h = header.grid_width, header.grid_height
        az_span = header.swath_angle_deg + 2.0 * header.padding_deg
        el_span = header.vertical_fov_deg + 2.0 * header.padding_deg

        az_deg = -az_span / 2.0 + (torch.arange(w, device=DEVICE) + 0.5) * az_span / w
        el_deg = -el_span / 2.0 + (torch.arange(h, device=DEVICE) + 0.5) * el_span / h

        # Unity ray direction: AngleAxis(az, Z+) · AngleAxis(el + tilt, X+) · down
        # → dir = (cos t · sin az, −cos t · cos az, −sin t), t = el + tilt
        t = torch.deg2rad(el_deg + header.mount_tilt_deg)
        a = torch.deg2rad(az_deg)
        tt, aa = torch.meshgrid(t, a, indexing="ij")
        self.dirs = torch.stack(
            [torch.cos(tt) * torch.sin(aa),
             -torch.cos(tt) * torch.cos(aa),
             -torch.sin(tt)], dim=-1)                      # (H, W, 3)

        # Column → output beam, by azimuth value (padding columns excluded)
        beam_step = header.swath_angle_deg / header.num_beams
        az_pos = (az_deg + header.swath_angle_deg / 2.0) / beam_step
        beam = torch.floor(az_pos).long()
        self.in_swath = (beam >= 0) & (beam < header.num_beams)  # (W,)
        self.beam = beam.clamp(0, header.num_beams - 1)          # (W,)

        # Beam-pattern weight: Gaussian across elevation (normalised to vFOV)
        # and across azimuth within each beam — same shape as the legacy
        # sub-beam weighting.
        el_norm = el_deg / (header.vertical_fov_deg / 2.0)       # (H,)
        az_norm = 2.0 * (az_pos - torch.floor(az_pos) - 0.5)     # (W,) in [-1, 1]
        en, an = torch.meshgrid(el_norm, az_norm, indexing="ij")
        self.weight = torch.exp(-(en**2 + an**2) / (2.0 * _BEAM_PATTERN_SIGMA**2))


_geom_cache: dict[tuple, _FrameGeometry] = {}
_mat_cache: dict[int, tuple] = {}


def _geometry(header: FrameHeader) -> _FrameGeometry:
    key = (header.grid_width, header.grid_height, header.swath_angle_deg,
           header.vertical_fov_deg, header.padding_deg, header.mount_tilt_deg,
           header.num_beams)
    if key not in _geom_cache:
        _geom_cache.clear()  # header geometry rarely changes; keep one entry
        _geom_cache[key] = _FrameGeometry(header)
    return _geom_cache[key]


def _material_tensors(materials: MaterialTable):
    key = id(materials)
    if key not in _mat_cache:
        _mat_cache[key] = (
            torch.from_numpy(materials.reflectivity).to(DEVICE),
            torch.from_numpy(materials.scattering).to(DEVICE),
            torch.from_numpy(materials.absorption).to(DEVICE),
        )
    return _mat_cache[key]


def _shift(x: torch.Tensor, dim: int, step: int) -> torch.Tensor:
    """Shift along dim with edge replication (no wrap-around)."""
    idx = torch.clamp(torch.arange(x.shape[dim], device=x.device) + step,
                      0, x.shape[dim] - 1)
    return x.index_select(dim, idx)


def _surface_normals(points: torch.Tensor, hit: torch.Tensor):
    """Central-difference normals on the polar grid.

    Returns (normals (H,W,3), reliable (H,W)) — normals are only reliable
    where the pixel and its 4-neighbours all have returns.
    """
    d_az = _shift(points, 1, 1) - _shift(points, 1, -1)
    d_el = _shift(points, 0, 1) - _shift(points, 0, -1)
    n = torch.cross(d_az, d_el, dim=-1)
    n = n / torch.linalg.norm(n, dim=-1, keepdim=True).clamp_min(1e-12)

    reliable = (hit
                & _shift(hit, 1, 1) & _shift(hit, 1, -1)
                & _shift(hit, 0, 1) & _shift(hit, 0, -1))
    return n, reliable


def process_frame(header: FrameHeader, ranges: np.ndarray,
                  material_ids: np.ndarray,
                  materials: MaterialTable) -> np.ndarray:
    """Collapse one depth/material frame into a (num_beams, num_range_bins)
    float32 intensity image, normalised 0–1."""
    nb = header.num_beams
    nr = header.num_range_bins
    max_r = header.max_range
    bin_width = max_r / nr
    sl_db = header.source_level_db

    geom = _geometry(header)
    refl_t, scat_t, absorp_t = _material_tensors(materials)

    # .copy(): frames arrive as read-only views of the socket buffer
    r = torch.from_numpy(ranges.copy()).to(DEVICE)                         # (H, W)
    mat = torch.from_numpy(material_ids.astype(np.int64)).to(DEVICE)       # (H, W)
    hit = r > 0
    if not header.in_water:
        # Dry transducer: no acoustic returns, only the noise floor
        hit = torch.zeros_like(hit)

    # --- Geometry: points, normals, incidence ---
    points = r.unsqueeze(-1) * geom.dirs
    normals, reliable = _surface_normals(points, hit)
    incidence = torch.abs(torch.sum(geom.dirs * normals, dim=-1))
    # Silhouette edges / isolated pixels: assume head-on return
    incidence = torch.where(reliable, incidence, torch.ones_like(incidence))
    incidence = incidence.clamp(1e-6, 1.0)

    # --- Sonar equation (same as legacy ray pipeline) ---
    safe_mat = mat.clamp(0, refl_t.shape[0] - 1)
    refl = refl_t[safe_mat]
    scat = scat_t[safe_mat]
    absorption = absorp_t[safe_mat]

    ts_linear = (refl * incidence + scat).clamp_min(1e-12)
    ts_db = 10.0 * torch.log10(ts_linear)

    safe_dist = r.clamp_min(0.1)
    tl_db = 20.0 * torch.log10(safe_dist) + absorption * safe_dist
    el_db = sl_db - 2.0 * tl_db + ts_db
    intensity = torch.pow(10.0, el_db / 10.0) * geom.weight

    # --- Max-bin into (beam, range bin) with footprint-aware splatting ---
    # A grid pixel covers a patch of surface spanning a range extent that
    # grows with grazing angle (one elevation row can cross many range bins
    # on a flat seabed). Splat each return across its own footprint, plus a
    # Gaussian pulse tail. One-sided differences keep footprints from
    # bridging depth discontinuities (silhouettes, shadows).
    def _local_step(x, h, dim, step):
        d = (_shift(x, dim, step) - x).abs()
        return torch.where(_shift(h, dim, step), d, torch.full_like(d, torch.inf))

    d_el = torch.minimum(_local_step(r, hit, 0, 1), _local_step(r, hit, 0, -1))
    d_az = torch.minimum(_local_step(r, hit, 1, 1), _local_step(r, hit, 1, -1))
    half_extent = 0.5 * torch.maximum(d_el, d_az)
    half_extent = torch.where(torch.isfinite(half_extent), half_extent,
                              torch.zeros_like(half_extent))
    extent_bins = (half_extent / bin_width).clamp(max=_MAX_FOOTPRINT_BINS)

    sel = hit & geom.in_swath.unsqueeze(0)
    beam_grid = geom.beam.unsqueeze(0).expand_as(r)
    rbin = (r / bin_width).long().clamp(0, nr - 1)

    src = intensity[sel]
    beam_sel = beam_grid[sel]
    rbin_sel = rbin[sel]
    extent_sel = extent_bins[sel]

    image = torch.zeros(nb * nr, dtype=torch.float64, device=DEVICE)
    for offset in range(-_MAX_SPLAT_BINS, _MAX_SPLAT_BINS + 1):
        # Flat inside the pixel footprint, Gaussian tail (σ = 1 bin) beyond
        d = (abs(offset) - extent_sel).clamp_min(0.0)
        falloff = torch.exp(-0.5 * d * d)
        target = (rbin_sel + offset).clamp(0, nr - 1)
        image.scatter_reduce_(0, beam_sel * nr + target,
                              (src * falloff).to(torch.float64),
                              reduce="amax", include_self=True)
    image = image.view(nb, nr)

    # No explicit shadow stage: occlusion is inherent in the depth map.
    # First-return bins are still needed to place volume scattering.
    bins = torch.arange(nr, device=DEVICE)
    active = image > 0
    first_hit_bin = torch.where(active, bins.unsqueeze(0), nr).min(dim=1).values

    # --- dB conversion with fixed dynamic range ---
    image_db = torch.where(image > 0,
                           10.0 * torch.log10(image.clamp_min(1e-20)),
                           torch.full_like(image, -200.0))
    positive = image_db > -200.0
    ceiling_db = image_db[positive].max() if positive.any() else torch.tensor(0.0, device=DEVICE)
    floor_db = ceiling_db - _DYNAMIC_RANGE_DB
    image_norm = ((image_db - floor_db) / _DYNAMIC_RANGE_DB).clamp(0.0, 1.0)

    # --- Noise model (same components as legacy, in 0–1 space) ---
    before_first = bins.unsqueeze(0) < first_hit_bin.unsqueeze(1)

    # Volume scattering before the first return, fading with range
    range_ratio = (bins.to(torch.float64) / nr).unsqueeze(0)
    vol_scat = (0.02 + 0.06 * torch.rand(nb, nr, device=DEVICE, dtype=torch.float64)) \
        * (1.0 - 0.3 * range_ratio)
    image_norm = image_norm + vol_scat * before_first

    # Range-periodic band noise
    ping_offset = torch.rand(1, device=DEVICE, dtype=torch.float64) * 1000.0
    range_t = bins.to(torch.float64) * (bin_width / max_r) * 100.0 + ping_offset
    noise_layer = torch.zeros(nb, nr, device=DEVICE, dtype=torch.float64)
    for _ in range(materials.noise.band_noise_num_bands):
        freq_mult = 1.0 + 13.0 * torch.rand(1, device=DEVICE, dtype=torch.float64)
        phase = torch.rand(1, device=DEVICE, dtype=torch.float64) * 100.0
        noise_layer += materials.noise.band_noise_amplitude \
            * torch.abs(torch.sin(range_t * freq_mult + phase)).unsqueeze(0)

    # Receiver noise + gaussian + sparse speckle
    noise_layer += 0.01 + 0.03 * torch.rand(nb, nr, device=DEVICE, dtype=torch.float64)
    noise_layer += 0.02 * torch.randn(nb, nr, device=DEVICE, dtype=torch.float64)
    speckle = (torch.rand(nb, nr, device=DEVICE) < 0.003) \
        * (0.08 + 0.12 * torch.rand(nb, nr, device=DEVICE, dtype=torch.float64))
    noise_layer += speckle

    image_norm = image_norm + noise_layer

    image_norm = image_norm.clamp(0.0, 1.0)
    return image_norm.to(torch.float32).cpu().numpy()
