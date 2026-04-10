"""
Acoustic signal processing pipeline.

Processing stages:
1. Compute 2D Gaussian beam-pattern weights per sub-beam
2. For each beam, aggregate sub-beam hits into range bins via sonar equation
3. Apply acoustic shadows (zero beyond first hit per beam)
4. Apply noise model
5. dB conversion with fixed dynamic range → 0-1 output
"""

import numpy as np
from .protocol import PingHeader
from .materials import MaterialTable


def compute_beam_weights(sub_beams_h: int, sub_beams_v: int) -> np.ndarray:
    h = np.linspace(-1, 1, sub_beams_h)
    v = np.linspace(-1, 1, sub_beams_v)
    hh, vv = np.meshgrid(h, v)
    sigma = 0.5
    weights = np.exp(-(hh**2 + vv**2) / (2 * sigma**2))
    weights = weights.ravel().astype(np.float32)
    weights /= weights.sum()
    return weights


def process_ping(header: PingHeader, rays: np.ndarray,
                 materials: MaterialTable) -> np.ndarray:
    nb = header.num_beams
    nr = header.num_range_bins
    max_r = header.max_range
    bin_width = max_r / nr
    sl_db = header.source_level_db
    speed = header.speed_of_sound
    pulse_len = header.pulse_length_s
    pulse_range = speed * pulse_len / 2.0

    beam_weights = compute_beam_weights(header.sub_beams_h, header.sub_beams_v)

    image = np.zeros((nb, nr), dtype=np.float64)

    # --- Vectorised sonar equation per ray ---
    hit_mask = rays["did_hit"].astype(bool)
    distances = rays["hit_distance"]
    mat_types = rays["material_type"]
    beam_indices = rays["beam_index"]
    sub_indices = rays["sub_beam_index"]

    # Grazing angle
    dot = np.abs(np.sum(rays["ray_dir"] * rays["hit_normal"], axis=1))
    dot = np.clip(dot, 1e-6, 1.0)

    # Material lookup
    safe_mat = np.clip(mat_types, 0, len(materials.reflectivity) - 1)
    refl = materials.reflectivity[safe_mat]
    scat = materials.scattering[safe_mat]

    # Sonar equation in dB
    ts_linear = refl * dot + scat
    ts_linear = np.maximum(ts_linear, 1e-12)
    ts_db = 10.0 * np.log10(ts_linear)

    safe_dist = np.maximum(distances, 0.1)
    absorption = materials.absorption[safe_mat]
    tl_db = 20.0 * np.log10(safe_dist) + absorption * safe_dist
    el_db = sl_db - 2.0 * tl_db + ts_db

    intensity = np.power(10.0, el_db / 10.0)
    intensity = np.where(hit_mask, intensity, 0.0)

    # Weight by beam pattern
    weights = beam_weights[sub_indices]
    weighted = intensity * weights

    # --- Range binning with Gaussian spread ---
    range_bins = np.clip((distances / bin_width).astype(int), 0, nr - 1)

    spread_bins = 8
    for offset in range(-spread_bins, spread_bins + 1):
        falloff = np.exp(-0.5 * (offset * offset))
        target_bins = np.clip(range_bins + offset, 0, nr - 1)
        valid = hit_mask
        np.maximum.at(image, (beam_indices[valid], target_bins[valid]),
                      weighted[valid] * falloff)

    # --- Acoustic shadows ---
    first_hit_bin = np.full(nb, nr, dtype=int)
    hit_beams = beam_indices[hit_mask]
    hit_bins = range_bins[hit_mask]
    np.minimum.at(first_hit_bin, hit_beams, hit_bins)

    shadow_offset = 12
    for b in range(nb):
        shadow_start = first_hit_bin[b] + shadow_offset
        if shadow_start < nr:
            image[b, shadow_start:] = 0.0

    # --- Convert to dB with fixed dynamic range ---
    # This replaces per-ping normalisation so nearby strong targets
    # don't crush weaker returns
    dynamic_range_db = 60.0  # display window in dB
    image_db = np.where(image > 0, 10.0 * np.log10(np.maximum(image, 1e-20)), -200.0)
    ceiling_db = np.max(image_db[image_db > -200.0]) if np.any(image_db > -200.0) else 0.0
    floor_db = ceiling_db - dynamic_range_db
    image_norm = (image_db - floor_db) / dynamic_range_db
    image_norm = np.clip(image_norm, 0.0, 1.0)

    # --- Noise model (applied in normalised 0-1 space) ---
    rng = np.random.default_rng()

    # Build a mask of active region (before shadow) per beam
    active_mask = np.zeros((nb, nr), dtype=bool)
    for b in range(nb):
        shadow_start = first_hit_bin[b] + shadow_offset
        active_mask[b, :min(shadow_start, nr)] = True

    # Volume scattering before first hit only
    for b in range(nb):
        fhb = first_hit_bin[b]
        if fhb > 0:
            range_ratio = np.arange(fhb, dtype=float) / nr
            vol_scat = rng.uniform(0.02, 0.08, size=fhb) * (1.0 - 0.3 * range_ratio)
            image_norm[b, :fhb] += vol_scat

    # Band noise, receiver noise, gaussian — active region only
    ping_offset = rng.uniform(0, 1000)
    noise_layer = np.zeros((nb, nr), dtype=np.float64)
    for _ in range(materials.noise.band_noise_num_bands):
        freq_mult = rng.uniform(1.0, 14.0)
        phase = rng.uniform(0, 100)
        range_t = np.arange(nr) * (bin_width / max_r) * 100.0 + ping_offset
        band = materials.noise.band_noise_amplitude * np.abs(
            np.sin(range_t * freq_mult + phase)
        )
        noise_layer += band[np.newaxis, :]

    noise_layer += rng.uniform(0.01, 0.04, size=(nb, nr))
    noise_layer += rng.normal(0, 0.02, size=(nb, nr))

    speckle_mask = rng.random(size=(nb, nr)) < 0.003
    noise_layer += speckle_mask * rng.uniform(0.08, 0.2, size=(nb, nr))

    # Apply noise only in active region
    image_norm[active_mask] += noise_layer[active_mask]

    # Faint noise in shadow zones only
    shadow_mask = ~active_mask
    image_norm[shadow_mask] += rng.uniform(0, 0.005, size=int(shadow_mask.sum()))

    np.clip(image_norm, 0.0, 1.0, out=image_norm)

    return image_norm.astype(np.float32)