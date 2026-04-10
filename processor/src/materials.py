"""Load material acoustic properties from YAML config."""

from pathlib import Path
from dataclasses import dataclass

import numpy as np
import yaml


@dataclass
class MaterialProps:
    name: str
    reflectivity: float
    scattering_coefficient: float
    absorption_db_per_m: float


@dataclass
class NoiseConfig:
    receiver_noise_floor_db: float
    volume_scattering_db_per_m: float
    speckle_std: float
    band_noise_num_bands: int
    band_noise_amplitude: float


class MaterialTable:
    """Lookup table indexed by materialType enum value."""

    def __init__(self, config_path: Path):
        with open(config_path) as f:
            cfg = yaml.safe_load(f)

        self._materials: dict[int, MaterialProps] = {}
        for idx_str, props in cfg["materials"].items():
            idx = int(idx_str)
            self._materials[idx] = MaterialProps(
                name=props["name"],
                reflectivity=props["reflectivity"],
                scattering_coefficient=props["scattering_coefficient"],
                absorption_db_per_m=props["absorption_db_per_m"],
            )

        nc = cfg["noise"]
        self.noise = NoiseConfig(
            receiver_noise_floor_db=nc["receiver_noise_floor_db"],
            volume_scattering_db_per_m=nc["volume_scattering_db_per_m"],
            speckle_std=nc["speckle_std"],
            band_noise_num_bands=nc["band_noise_num_bands"],
            band_noise_amplitude=nc["band_noise_amplitude"],
        )

        # Pre-build numpy lookup arrays for vectorised access
        max_idx = max(self._materials.keys()) + 1
        self.reflectivity = np.zeros(max_idx, dtype=np.float32)
        self.scattering = np.zeros(max_idx, dtype=np.float32)
        self.absorption = np.zeros(max_idx, dtype=np.float32)
        for idx, m in self._materials.items():
            self.reflectivity[idx] = m.reflectivity
            self.scattering[idx] = m.scattering_coefficient
            self.absorption[idx] = m.absorption_db_per_m

    def get(self, material_type: int) -> MaterialProps:
        return self._materials.get(material_type, self._materials[0])
