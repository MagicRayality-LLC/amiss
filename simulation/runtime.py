from contextlib import contextmanager
from copy import deepcopy
from dataclasses import dataclass, field
import hashlib
import json
import math

from .kernel import KernelError, TERMINAL_PHASES, TrialKernel


CONTROL_OPERATIONS = frozenset({"pause", "resume", "cancel_batch"})
FAILURE_LAYERS = frozenset({
    "scene_permission",
    "input_tracking",
    "numerical_scientific",
    "persistence_quota",
    "network_service",
    "service_quota",
    "voice_tts",
    "performance_thermal",
    "export_deletion",
})
SUPPORTED_COLLISION_FORMAT_VERSIONS = ("1",)
PRIMARY_REPRESENTATION_FORMS = frozenset({"plane", "bounding_volume", "global_mesh"})
BOUNDARY_PROVENANCE = frozenset({"scanned_confirmed", "virtual_supplement"})
INTERACTION_FORMS = frozenset({"swept", "state"})
RESULT_GRANULARITIES = frozenset({"aggregate", "per_sample_summary"})
DEGRADATION_ACTIONS = {
    "replay": "paused",
    "batch": "deferred",
    "presentation": "reduced",
    "resolution": "reduced",
}


class RuntimeContractError(ValueError):
    def __init__(self, reason_code, message=None):
        super().__init__(message or reason_code)
        self.reason_code = reason_code


class CollisionContractError(ValueError):
    def __init__(self, reason_code, message=None):
        super().__init__(message or reason_code)
        self.reason_code = reason_code


def _finite_number(value):
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
    )


def _finite_vector(value):
    return (
        isinstance(value, (list, tuple))
        and len(value) == 3
        and all(_finite_number(item) for item in value)
    )


@dataclass(frozen=True)
class ModelDeterminismDeclaration:
    fixed_delta_time_id: str = "fixed-dt-0.05"
    delta_time_s: float = 0.05
    rng_algorithm_id: str = "sha256-counter-v1"
    seed_expansion_id: str = "stable-stream-name-v1"
    stream_layout_version: str = "1.0.0"
    numeric_convention_id: str = "ieee754-double-v1"
    writer_configuration_id: str = "headless-elastic-balls-v1"
    contact_iteration_bound: int = 4096
    tie_break_id: str = "stable-entity-id-v1"
    model_version: str = "1.0.0"
    calculation_version: str = "1.0.0"
    statistical_minimum_free_count: int = 10
    measurement_tolerance: float = 1e-9
    energy_tolerance: float = 1e-9
    elasticity_tolerance: float = 1e-9
    conversion_tolerance: float = 1e-12
    replay_tolerance: float = 1e-9

    def identity(self):
        return {
            "fixed_delta_time_id": self.fixed_delta_time_id,
            "delta_time_s": self.delta_time_s,
            "rng_algorithm_id": self.rng_algorithm_id,
            "seed_expansion_id": self.seed_expansion_id,
            "stream_layout_version": self.stream_layout_version,
            "numeric_convention_id": self.numeric_convention_id,
            "writer_configuration_id": self.writer_configuration_id,
            "contact_iteration_bound": self.contact_iteration_bound,
            "tie_break_id": self.tie_break_id,
            "model_version": self.model_version,
            "calculation_version": self.calculation_version,
            "statistical_minimum_free_count": self.statistical_minimum_free_count,
        }

    def tolerances(self):
        return {
            "measurement": self.measurement_tolerance,
            "energy": self.energy_tolerance,
            "elasticity": self.elasticity_tolerance,
            "conversion": self.conversion_tolerance,
            "replay": self.replay_tolerance,
        }


class NamedRandomStreams:
    def __init__(self, root_seed, expansion_id="stable-stream-name-v1", layout_version="1.0.0"):
        self.root_seed = int(root_seed)
        self.expansion_id = expansion_id
        self.layout_version = layout_version
        self._counts = {}

    def draw_uniform(self, stream_name):
        index = self._counts.get(stream_name, 0)
        material = "\0".join(
            (
                self.expansion_id,
                self.layout_version,
                str(self.root_seed),
                stream_name,
                str(index),
            )
        ).encode("utf-8")
        digest = hashlib.sha256(material).digest()
        self._counts[stream_name] = index + 1
        return int.from_bytes(digest[:8], "big") / 18446744073709551616.0

    def draw(self, stream_name, count=1):
        return [self.draw_uniform(stream_name) for _ in range(count)]

    def draw_count(self, stream_name):
        return self._counts.get(stream_name, 0)

    def counts(self):
        return dict(self._counts)

    def scientific_counts(self):
        return {
            name: count for name, count in self._counts.items()
            if not name.startswith("presentation")
        }


class CollisionRepresentation:
    def __init__(self, representation_id, format_version, coordinate_frame_id, surfaces):
        self.representation_id = representation_id
        self.format_version = str(format_version)
        self.coordinate_frame_id = coordinate_frame_id
        self.surfaces = deepcopy(list(surfaces))
        self._bounds = None

    def to_dict(self):
        return {
            "representation_id": self.representation_id,
            "format_version": self.format_version,
            "coordinate_frame_id": self.coordinate_frame_id,
            "surfaces": deepcopy(self.surfaces),
        }

    @classmethod
    def from_dict(cls, value):
        return cls(
            value["representation_id"],
            value["format_version"],
            value["coordinate_frame_id"],
            value["surfaces"],
        )

    def canonical_surface_bytes(self):
        ordered = sorted(self.surfaces, key=lambda surface: surface["surface_id"])
        return json.dumps(
            ordered, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")

    def checksum(self):
        return hashlib.sha256(self.canonical_surface_bytes()).hexdigest()

    def _surface_extent(self, surface):
        primary = surface["primary_representation"]
        geometry = surface[primary]
        if primary == "plane":
            normal = geometry["normal"]
            axis = max(range(3), key=lambda item: abs(normal[item]))
            offset = geometry["offset"]
            point = [0.0, 0.0, 0.0]
            point[axis] = offset
            return point, deepcopy(point)
        if primary == "bounding_volume":
            center = geometry["center"]
            half = geometry["half_extent"]
            lower = [center[axis] - half for axis in range(3)]
            upper = [center[axis] + half for axis in range(3)]
            return lower, upper
        vertices = geometry["vertices"]
        lower = [min(vertex[axis] for vertex in vertices) for axis in range(3)]
        upper = [max(vertex[axis] for vertex in vertices) for axis in range(3)]
        return lower, upper

    def validate(self):
        if self.format_version not in SUPPORTED_COLLISION_FORMAT_VERSIONS:
            raise CollisionContractError("unsupported_format_version")
        surface_ids = [surface.get("surface_id") for surface in self.surfaces]
        if any(not isinstance(item, str) or not item for item in surface_ids):
            raise CollisionContractError("surface_id_missing")
        if len(set(surface_ids)) != len(surface_ids):
            raise CollisionContractError("duplicate_surface_id")
        lower = None
        upper = None
        for surface in self.surfaces:
            if surface.get("provenance") not in BOUNDARY_PROVENANCE:
                raise CollisionContractError("provenance_missing")
            primary = surface.get("primary_representation")
            if primary not in PRIMARY_REPRESENTATION_FORMS:
                raise CollisionContractError("invalid_primary_representation")
            present = [
                key for key in surface if key in PRIMARY_REPRESENTATION_FORMS
            ]
            if len(present) != 1 or present[0] != primary:
                raise CollisionContractError("duplicate_representation")
            geometry = surface[primary]
            valid_geometry = (
                _finite_vector(geometry.get("normal")) and _finite_number(geometry.get("offset"))
                if primary == "plane"
                else _finite_vector(geometry.get("center")) and _finite_number(geometry.get("half_extent"))
                if primary == "bounding_volume"
                else isinstance(geometry.get("vertices"), list)
                and len(geometry["vertices"]) > 0
                and all(_finite_vector(vertex) for vertex in geometry["vertices"])
            )
            if not valid_geometry:
                raise CollisionContractError("non_finite_geometry")
            if primary == "plane":
                normal = geometry["normal"]
                norm = math.sqrt(sum(component * component for component in normal))
                if abs(norm - 1.0) > 1e-9:
                    raise CollisionContractError("plane_normal_not_unit")
            surface_lower, surface_upper = self._surface_extent(surface)
            if lower is None:
                lower = list(surface_lower)
                upper = list(surface_upper)
            else:
                for axis in range(3):
                    lower[axis] = min(lower[axis], surface_lower[axis])
                    upper[axis] = max(upper[axis], surface_upper[axis])
        if lower is None:
            raise CollisionContractError("missing_bounds")
        for axis in range(3):
            if not (_finite_number(lower[axis]) and _finite_number(upper[axis])):
                raise CollisionContractError("non_finite_geometry")
            if not upper[axis] > lower[axis]:
                raise CollisionContractError("degenerate_bounds")
        self._bounds = {"min": lower, "max": upper}
        return {
            "status": "valid",
            "surface_count": len(self.surfaces),
            "checksum": self.checksum(),
            "bounds": deepcopy(self._bounds),
        }

    def bounds(self):
        if self._bounds is None:
            self.validate()
        return deepcopy(self._bounds)


def box_representation(
    representation_id,
    dimensions_m,
    *,
    format_version="1",
    coordinate_frame_id="room-frame-v1",
    virtual=True,
):
    origin = [0.0, 0.0, 0.0]
    surfaces = []
    for axis, axis_name in enumerate("xyz"):
        for side, sign in (("min", -1.0), ("max", 1.0)):
            normal = [0.0, 0.0, 0.0]
            normal[axis] = sign
            offset = origin[axis] + (0.0 if side == "min" else dimensions_m[axis_name])
            surfaces.append(
                {
                    "surface_id": f"wall-{axis_name}-{side}",
                    "primary_representation": "plane",
                    "provenance": "virtual_supplement" if virtual else "scanned_confirmed",
                    "plane": {"normal": normal, "offset": offset},
                }
            )
    return CollisionRepresentation(
        representation_id, format_version, coordinate_frame_id, surfaces
    )


@dataclass(frozen=True)
class RuntimeBudgetProfile:
    max_ticks_per_scheduling_slice: int = 2
    input_admission_per_tick: int = 64
    held_speed_limit_m_per_s: float = 6.0
    batch_samples_per_slice: int = 1
    sustained_pressure_slices: int = 3
    degradation_order: tuple = ("replay", "batch", "presentation", "resolution", "hold")

    def identity(self):
        return {
            "evidence_status": "declared_initial_limits_not_measured",
            "max_ticks_per_scheduling_slice": self.max_ticks_per_scheduling_slice,
            "input_admission_per_tick": self.input_admission_per_tick,
            "held_speed_limit_m_per_s": self.held_speed_limit_m_per_s,
            "batch_samples_per_slice": self.batch_samples_per_slice,
            "sustained_pressure_slices": self.sustained_pressure_slices,
            "degradation_order": list(self.degradation_order),
        }


def failure_report(
    layer,
    reason,
    *,
    affected_operation=None,
    trial_ref=None,
    tick=None,
    containment_result=None,
    permitted_recovery_actions=(),
    diagnostic_sequence=0,
):
    if layer not in FAILURE_LAYERS:
        raise ValueError("unknown failure layer")
    return {
        "layer": layer,
        "reason": reason,
        "affected_operation": affected_operation,
        "trial_ref": deepcopy(trial_ref),
        "tick": tick,
        "diagnostic_sequence": diagnostic_sequence,
        "containment_result": containment_result,
        "permitted_recovery_actions": list(permitted_recovery_actions),
    }


# The 07 section 3.2 prototype scale point in the code's internal units: an
# equal-mass free set of the declared engine ball mass at v_rms = 1.1 m/s
# measures 300 K, so the declared model constant is
# k_B_eff = m_ball * v_rms^2 / (3 * T) = 1.3444...e-3 J/K. It is a model
# constant, not the physical Boltzmann constant: the design prototype ball is
# m_0 = 1e-20 kg and this engine mass is 1e20 times heavier in the same units.
DECLARED_BALL_MASS_KG = 1.0
SCALE_POINT_V_RMS_M_PER_S = 1.1
SCALE_POINT_T_K = 300.0
EFFECTIVE_K_B_J_PER_K = (
    DECLARED_BALL_MASS_KG
    * SCALE_POINT_V_RMS_M_PER_S ** 2
    / (3.0 * SCALE_POINT_T_K)
)

# Founder energy balance unit (2026-11-04), mirrored by the device tools. One
# unit is a declared uncalibrated candidate: a fist hit adds 1, a bat strike
# adds 10, the cloth bag absorbs 10, and a ball below the palm capture speed is
# trapped instead of passing through. A tool slower than the minimum strike
# speed adds nothing, so a held-still hand cannot warm the balls. Every applied
# change is still measured by the writer, so the ledger keeps real signed work.
ENERGY_QUANTUM_J = 0.05
FIST_ENERGY_DELTA_J = ENERGY_QUANTUM_J
BAT_ENERGY_DELTA_J = 10.0 * ENERGY_QUANTUM_J
SANDBAG_ENERGY_DELTA_J = 10.0 * ENERGY_QUANTUM_J
PALM_CAPTURE_SPEED_M_PER_S = 0.5
PALM_PASS_ENERGY_DELTA_J = ENERGY_QUANTUM_J
MINIMUM_STRIKE_SPEED_M_PER_S = 1.0


DEFAULT_UNIT_CONVENTION = {
    "unit_convention_id": "si-model-v1",
    "internal_units": {
        "mass": "kg",
        "length": "m",
        "time": "s",
        "energy": "J",
        "temperature": "K_model",
    },
    "display_conversions": {
        "length": {"display_unit_id": "cm", "factor": 100.0},
        "energy": {"display_unit_id": "mJ", "factor": 1000.0},
        "mass": {"display_unit_id": "g", "factor": 1000.0},
    },
    "conversion_tolerance": 1e-12,
    "scale_constants": {"effective_kB": EFFECTIVE_K_B_J_PER_K},
    "degrees_of_freedom": 3,
}


def round_trip_units(value, axis_id, unit_convention):
    conversion = unit_convention["display_conversions"][axis_id]
    display = value * conversion["factor"]
    restored = display / conversion["factor"]
    tolerance = unit_convention["conversion_tolerance"] * max(1.0, abs(value))
    if not math.isfinite(display) or abs(restored - value) > tolerance:
        raise RuntimeContractError("conversion_out_of_tolerance")
    return {
        "axis_id": axis_id,
        "internal_value": value,
        "display_value": display,
        "display_unit_id": conversion["display_unit_id"],
        "restored_internal_value": restored,
        "tolerance": unit_convention["conversion_tolerance"],
    }


def kinetic_energies(balls):
    free = [ball for ball in balls.values() if ball["membership"] == "free"]
    held = [ball for ball in balls.values() if ball["membership"] == "held"]
    total_mass = math.fsum(ball["mass_kg"] for ball in free)
    if total_mass > 0.0:
        v_cm = [
            math.fsum(ball["velocity_m_per_s"][axis] * ball["mass_kg"] for ball in free)
            / total_mass
            for axis in range(3)
        ]
    else:
        v_cm = [0.0, 0.0, 0.0]
    k_total = math.fsum(
        0.5 * ball["mass_kg"] * math.fsum(v * v for v in ball["velocity_m_per_s"])
        for ball in free
    )
    k_drift = 0.5 * total_mass * math.fsum(v * v for v in v_cm)
    k_thermal = math.fsum(
        0.5
        * ball["mass_kg"]
        * math.fsum(
            (ball["velocity_m_per_s"][axis] - v_cm[axis]) ** 2 for axis in range(3)
        )
        for ball in free
    )
    k_held = math.fsum(
        0.5 * ball["mass_kg"] * math.fsum(v * v for v in ball["velocity_m_per_s"])
        for ball in held
    )
    return {
        "free_count": len(free),
        "held_count": len(held),
        "total_mass_kg": total_mass,
        "k_total_j": k_total,
        "k_drift_j": k_drift,
        "k_thermal_j": k_thermal,
        "k_held_j": k_held,
        "v_cm_m_per_s": v_cm,
    }


def kinetic_temperature(energies, unit_convention, statistical_minimum_free_count):
    record = {
        "computation_path_id": "direct-relative-summation-v1",
        "free_count": energies["free_count"],
        "statistical_minimum_free_count": statistical_minimum_free_count,
    }
    values = (energies["k_total_j"], energies["k_thermal_j"], energies["k_drift_j"])
    if any(not math.isfinite(value) for value in values):
        raise RuntimeContractError("non_finite_measurement_state")
    if energies["free_count"] < statistical_minimum_free_count:
        record.update({"applicability": "not_applicable", "value": None})
        return record
    k_b = unit_convention["scale_constants"]["effective_kB"]
    degrees_of_freedom = unit_convention["degrees_of_freedom"]
    record.update(
        {
            "applicability": "valid",
            "value": 2.0
            * energies["k_thermal_j"]
            / (energies["free_count"] * degrees_of_freedom * k_b),
            "unit_id": unit_convention["internal_units"]["temperature"],
        }
    )
    return record


class HeadlessBallWriter:
    writer_id = "headless-elastic-balls-v1"

    def __init__(self):
        # Persistent optical-trap-style cooling field between two palms.
        # None when the field art is off; otherwise a dict with endpoints,
        # waist/mouth radii, the molasses damping coefficient beta (1/s), and
        # the capture speed (m/s) below which a ball is held at the centre.
        self.palm_field = None
        self._palm_trapped = set()
        self._palm_inside = set()
        # One strike per ball per open tool contact: a swing may submit a
        # sample every Tick, but the founder's balance unit is per hit.
        self._contact_sweep_hits = {}
        self._active_sweep_contact = ""

    def setup(self, streams, setup_parameters, collision):
        # Teacher distribution knobs (founder decision 2026-11-04, option A):
        # count, uniform mass, random-component speed scale, and one group
        # drift vector. All are scalars, so the tool-schema scalar-only rule
        # stands: no per-ball list ever reaches the writer. The drift is added
        # after the named-stream draws, so zero drift reproduces the exact
        # pre-drift state bit for bit, and zero spread plus drift gives one
        # ball its exact specified velocity (the opening_single_ball line).
        count = int(setup_parameters.get("ball_count", 8))
        if count < 0:
            raise RuntimeContractError("invalid_ball_count")
        radius = float(setup_parameters.get("ball_radius_m", 0.05))
        if not _finite_number(radius) or radius <= 0.0:
            raise RuntimeContractError("invalid_ball_radius")
        mass = float(setup_parameters.get("ball_mass_kg", DECLARED_BALL_MASS_KG))
        if not _finite_number(mass) or mass <= 0.0:
            raise RuntimeContractError("invalid_ball_mass")
        speed_scale = float(setup_parameters.get("initial_speed_scale", 1.0))
        if not _finite_number(speed_scale) or speed_scale < 0.0:
            raise RuntimeContractError("invalid_speed_scale")
        drift = [
            float(setup_parameters.get(f"drift_v{axis}_m_per_s", 0.0))
            for axis in ("x", "y", "z")
        ]
        if any(not _finite_number(value) for value in drift):
            raise RuntimeContractError("non_finite_input")
        bounds = collision.bounds()
        balls = {}
        for index in range(count):
            draws = streams.draw("initial-setup", 6)
            entity_id = f"ball-{index + 1:04d}"
            span = [
                max(0.0, bounds["max"][axis] - bounds["min"][axis] - 2.0 * radius)
                for axis in range(3)
            ]
            position = [
                bounds["min"][axis] + radius + draws[axis] * span[axis]
                for axis in range(3)
            ]
            velocity = [
                (draws[3 + axis] * 2.0 - 1.0) * speed_scale + drift[axis]
                for axis in range(3)
            ]
            balls[entity_id] = {
                "entity_id": entity_id,
                "position_m": position,
                "velocity_m_per_s": velocity,
                "mass_kg": mass,
                "radius_m": radius,
                "membership": "free",
            }
        self._palm_trapped = set()
        self._palm_inside = set()
        self._contact_sweep_hits = {}
        self._active_sweep_contact = ""
        return {"balls": balls}

    @staticmethod
    def _ball_energy(ball):
        return 0.5 * ball["mass_kg"] * math.fsum(
            v * v for v in ball["velocity_m_per_s"]
        )

    def set_palm_field(self, endpoint_a_m, endpoint_b_m, *, waist_radius_m,
                       mouth_radius_m,
                       pass_energy_delta_j=PALM_PASS_ENERGY_DELTA_J,
                       capture_speed_m_per_s=0.0):
        """Arm the hourglass field between two open palms.

        The tube is wide at the palms (mouth_radius_m) and pinched at the
        waist (waist_radius_m), like the optical trap used to cool atoms. A
        ball crossing the tube loses one declared energy unit on each new
        pass, clamped to its own kinetic energy. A ball slower than
        capture_speed_m_per_s is trapped instead: it leaves the free set and
        is held at the field centre with its captured velocity until the
        field clears, then rejoins the free set at rest (the founder's
        2026-11-04 release requirement), so the trap cools the ball it lets
        go.
        """
        for value in list(endpoint_a_m) + list(endpoint_b_m):
            if not _finite_number(value):
                raise RuntimeContractError("non_finite_input")
        for value in (waist_radius_m, mouth_radius_m, pass_energy_delta_j,
                      capture_speed_m_per_s):
            if not _finite_number(value):
                raise RuntimeContractError("non_finite_input")
        if waist_radius_m <= 0.0 or mouth_radius_m <= 0.0:
            raise RuntimeContractError("invalid_palm_field")
        if pass_energy_delta_j <= 0.0 or capture_speed_m_per_s < 0.0:
            raise RuntimeContractError("invalid_palm_field")
        separation = math.sqrt(math.fsum(
            (endpoint_b_m[axis] - endpoint_a_m[axis]) ** 2 for axis in range(3)))
        if not math.isfinite(separation) or separation <= 0.0:
            raise RuntimeContractError("invalid_palm_field")
        self.palm_field = {
            "endpoint_a_m": [float(endpoint_a_m[axis]) for axis in range(3)],
            "endpoint_b_m": [float(endpoint_b_m[axis]) for axis in range(3)],
            "waist_radius_m": float(waist_radius_m),
            "mouth_radius_m": float(mouth_radius_m),
            "pass_energy_delta_j": float(pass_energy_delta_j),
            "capture_speed_m_per_s": float(capture_speed_m_per_s),
        }

    def clear_palm_field(self):
        self.palm_field = None
        self._palm_inside.clear()

    def _apply_palm_field(self, state, delta_s, facts):
        field = self.palm_field
        if field is None:
            if self._palm_trapped:
                # The palms dropped the field: release the trapped balls as
                # free and at rest, so the trap hands back a cooled ball and
                # not the speed it caught. The removed kinetic energy is
                # measured into the field's absorbed account.
                for entity_id in sorted(self._palm_trapped):
                    ball = state["balls"].get(entity_id)
                    if ball is None or ball["membership"] != "held":
                        continue
                    released = self._ball_energy(ball)
                    for axis in range(3):
                        ball["velocity_m_per_s"][axis] = 0.0
                    ball["membership"] = "free"
                    facts["applied_effects"].append({
                        "effect_kind": "palm_release",
                        "entity_id": entity_id,
                        "energy_delta_j": -released,
                    })
                    facts["palm_released_count"] = facts.get("palm_released_count", 0) + 1
                    if released > 0.0:
                        facts["palm_absorbed_j"] = (
                            facts.get("palm_absorbed_j", 0.0) + released
                        )
                self._palm_trapped.clear()
            return
        if delta_s <= 0.0:
            return
        axis = [field["endpoint_b_m"][i] - field["endpoint_a_m"][i] for i in range(3)]
        length_sq = math.fsum(component * component for component in axis)
        if length_sq <= 0.0:
            return
        pass_energy = field["pass_energy_delta_j"]
        capture_speed = field.get("capture_speed_m_per_s", 0.0)
        center = [
            0.5 * (field["endpoint_a_m"][i] + field["endpoint_b_m"][i])
            for i in range(3)
        ]
        waist = field["waist_radius_m"]
        mouth = field["mouth_radius_m"]

        # A trapped ball rides the moving field centre and keeps the velocity
        # it had at capture until release; the held account owns its energy
        # while it is pinned.
        for entity_id in sorted(self._palm_trapped):
            ball = state["balls"].get(entity_id)
            if ball is None or ball["membership"] != "held":
                self._palm_trapped.discard(entity_id)
                continue
            ball["position_m"] = list(center)

        for entity_id in sorted(state["balls"]):
            ball = state["balls"][entity_id]
            if ball["membership"] != "free":
                self._palm_inside.discard(entity_id)
                continue
            relative = [ball["position_m"][i] - field["endpoint_a_m"][i] for i in range(3)]
            fraction = math.fsum(relative[i] * axis[i] for i in range(3)) / length_sq
            inside = False
            if 0.0 <= fraction <= 1.0:
                closest = [field["endpoint_a_m"][i] + axis[i] * fraction for i in range(3)]
                distance = math.sqrt(math.fsum(
                    (ball["position_m"][i] - closest[i]) ** 2 for i in range(3)))
                tube_radius = waist + (mouth - waist) * abs(2.0 * fraction - 1.0) ** 1.5
                inside = distance <= tube_radius + 0.5 * ball["radius_m"]
            if not inside:
                self._palm_inside.discard(entity_id)
                continue
            speed = math.sqrt(math.fsum(
                v * v for v in ball["velocity_m_per_s"]))
            if capture_speed > 0.0 and speed <= capture_speed:
                ball["membership"] = "held"
                ball["position_m"] = list(center)
                self._palm_trapped.add(entity_id)
                self._palm_inside.discard(entity_id)
                facts["applied_effects"].append({
                    "effect_kind": "palm_trap",
                    "entity_id": entity_id,
                    "energy_delta_j": 0.0,
                })
                facts["palm_trapped_count"] = facts.get("palm_trapped_count", 0) + 1
                continue
            if entity_id in self._palm_inside:
                continue
            # A new pass through the tube removes exactly one balance unit.
            self._palm_inside.add(entity_id)
            before = self._ball_energy(ball)
            removed = min(pass_energy, before)
            if removed <= 0.0:
                continue
            factor = math.sqrt(max(0.0, 1.0 - removed / before))
            for i in range(3):
                ball["velocity_m_per_s"][i] *= factor
            facts["applied_effects"].append({
                "effect_kind": "palm_cooling",
                "entity_id": entity_id,
                "energy_delta_j": self._ball_energy(ball) - before,
            })
            facts["palm_cooled_count"] = facts.get("palm_cooled_count", 0) + 1
            facts["palm_absorbed_j"] = facts.get("palm_absorbed_j", 0.0) + (before - self._ball_energy(ball))

    def _apply_effect(self, state, effect, delta_s, facts):
        kind = effect.get("effect_kind")
        ball = state["balls"].get(effect.get("entity_id"))
        if kind == "held_state":
            if ball is not None and ball["membership"] == "held":
                ball["position_m"] = list(effect["position_m"])
                ball["velocity_m_per_s"] = list(effect["velocity_m_per_s"])
            return
        if kind == "sweep":
            self._apply_sweep(state, effect, delta_s, facts)
            return
        if ball is None or ball["membership"] != "free":
            return
        before = self._ball_energy(ball)
        if kind == "impulse":
            for axis in range(3):
                ball["velocity_m_per_s"][axis] += effect["delta_v_m_per_s"][axis]
        elif kind == "damping":
            if "energy_delta_j" in effect:
                removed = min(effect["energy_delta_j"], before)
                if removed <= 0.0:
                    return
                factor = math.sqrt(max(0.0, 1.0 - removed / before))
            else:
                factor = effect["factor"]
            for axis in range(3):
                ball["velocity_m_per_s"][axis] *= factor
        else:
            return
        applied = {
            "effect_kind": kind,
            "entity_id": effect["entity_id"],
            "energy_delta_j": self._ball_energy(ball) - before,
        }
        if "interaction_id" in effect:
            applied["interaction_id"] = effect["interaction_id"]
        if "prop_id" in effect:
            applied["prop_id"] = effect["prop_id"]
        facts["applied_effects"].append(applied)

    def _apply_sweep(self, state, effect, delta_s, facts):
        # The declared contact shape is a segment from origin to origin_b.
        # A point sweep omits origin_b and is the degenerate segment. Each ball
        # couples to the closest point on that segment and to the velocity
        # measured there, so a rotating bat's tip moves faster than its grip.
        interaction_id = effect.get("interaction_id", "")
        if interaction_id != self._active_sweep_contact:
            self._active_sweep_contact = interaction_id
            self._contact_sweep_hits.clear()
        hits = self._contact_sweep_hits.setdefault(interaction_id, set())
        origin = effect["origin_m"]
        end = effect.get("origin_b_m", origin)
        segment = [end[axis] - origin[axis] for axis in range(3)]
        segment_length_sq = math.fsum(component * component for component in segment)
        origin_velocity = effect["velocity_m_per_s"]
        end_velocity = effect.get("velocity_b_m_per_s", origin_velocity)
        for entity_id in sorted(state["balls"]):
            ball = state["balls"][entity_id]
            if ball["membership"] != "free":
                continue
            relative = [
                ball["position_m"][axis] - origin[axis] for axis in range(3)
            ]
            if segment_length_sq > 0.0:
                tool_parameter = max(
                    0.0,
                    min(
                        1.0,
                        math.fsum(
                            relative[axis] * segment[axis] for axis in range(3)
                        )
                        / segment_length_sq,
                    ),
                )
            else:
                tool_parameter = 0.0
            tool_origin = [
                origin[axis] + segment[axis] * tool_parameter for axis in range(3)
            ]
            velocity = [
                origin_velocity[axis]
                + (end_velocity[axis] - origin_velocity[axis]) * tool_parameter
                for axis in range(3)
            ]
            # A held-still tool adds no energy even if a contact stays open
            # or a ball drifts into it.
            tool_speed = math.sqrt(math.fsum(v * v for v in velocity))
            if tool_speed < MINIMUM_STRIKE_SPEED_M_PER_S:
                continue
            sweep_delta = [velocity[axis] * delta_s for axis in range(3)]
            length_sq = math.fsum(component * component for component in sweep_delta)
            relative = [
                ball["position_m"][axis] - tool_origin[axis] for axis in range(3)
            ]
            if length_sq > 0.0:
                parameter = max(
                    0.0,
                    min(
                        1.0,
                        math.fsum(
                            relative[axis] * sweep_delta[axis] for axis in range(3)
                        )
                        / length_sq,
                    ),
                )
            else:
                parameter = 0.0
            closest = [
                tool_origin[axis] + sweep_delta[axis] * parameter
                for axis in range(3)
            ]
            distance_vector = [
                ball["position_m"][axis] - closest[axis] for axis in range(3)
            ]
            distance = math.sqrt(
                math.fsum(component * component for component in distance_vector)
            )
            reach = effect["radius_m"] + ball["radius_m"]
            if distance > reach:
                continue
            if entity_id in hits:
                continue
            if distance <= 1e-9:
                # A ball on the swept path has no meaningful perpendicular;
                # push it along the tool's motion instead of a rounding noise
                # direction. A still tool keeps the stable +x fallback.
                speed = math.sqrt(math.fsum(component * component for component in velocity))
                if speed > 0.0:
                    direction = [component / speed for component in velocity]
                else:
                    direction = [1.0, 0.0, 0.0]
            else:
                direction = [component / distance for component in distance_vector]
            before = self._ball_energy(ball)
            if "energy_delta_j" in effect:
                # The founder balance mode adds a fixed kinetic energy along
                # the push direction: solve |v + p*n|^2 = |v|^2 + 2E/m.
                along_normal = math.fsum(
                    direction[axis] * ball["velocity_m_per_s"][axis]
                    for axis in range(3)
                )
                magnitude = -along_normal + math.sqrt(
                    along_normal * along_normal
                    + 2.0 * effect["energy_delta_j"] / ball["mass_kg"]
                )
            else:
                magnitude = effect["impulse_magnitude"]
            for axis in range(3):
                ball["velocity_m_per_s"][axis] += direction[axis] * magnitude
            hits.add(entity_id)
            facts["applied_effects"].append(
                {
                    "effect_kind": "sweep",
                    "interaction_id": effect["interaction_id"],
                    "entity_id": entity_id,
                    "energy_delta_j": self._ball_energy(ball) - before,
                }
            )

    @staticmethod
    def _sweep_planes(ball, start, collision, facts, resolutions, contact_iteration_bound):
        radius = ball["radius_m"]
        position = ball["position_m"]
        velocity = ball["velocity_m_per_s"]
        for surface in sorted(collision.surfaces, key=lambda item: item["surface_id"]):
            if surface["primary_representation"] != "plane":
                continue
            normal = surface["plane"]["normal"]
            geometry = surface["plane"]
            dominant_axis = max(range(3), key=lambda axis: abs(normal[axis]))
            signed_offset = normal[dominant_axis] * geometry["offset"]
            start_distance = signed_offset - math.fsum(
                normal[axis] * start[axis] for axis in range(3)
            )
            end_distance = signed_offset - math.fsum(
                normal[axis] * position[axis] for axis in range(3)
            )
            if end_distance >= radius or end_distance >= start_distance:
                continue
            fraction = (
                (start_distance - radius) / (start_distance - end_distance)
                if start_distance > radius
                else 0.0
            )
            contact = [
                start[axis] + (position[axis] - start[axis]) * fraction
                for axis in range(3)
            ]
            if resolutions >= contact_iteration_bound:
                facts["iteration_bound_exhausted"] = True
                return resolutions
            remaining = [position[axis] - contact[axis] for axis in range(3)]
            correction = radius - (
                signed_offset
                - math.fsum(normal[axis] * contact[axis] for axis in range(3))
            )
            for axis in range(3):
                contact[axis] -= normal[axis] * correction
            remaining_normal = math.fsum(
                normal[axis] * remaining[axis] for axis in range(3)
            )
            normal_velocity = math.fsum(
                normal[axis] * velocity[axis] for axis in range(3)
            )
            for axis in range(3):
                position[axis] = (
                    contact[axis]
                    + remaining[axis]
                    - 2.0 * remaining_normal * normal[axis]
                )
                velocity[axis] -= 2.0 * normal_velocity * normal[axis]
            facts["wall_contacts"] += 1
            resolutions += 1
        return resolutions

    def advance(self, state, delta_s, collision, streams, effects, contact_iteration_bound):
        facts = {
            "wall_contacts": 0,
            "pair_contacts": 0,
            "applied_effects": [],
        }
        for effect in effects:
            self._apply_effect(state, effect, delta_s, facts)
        bounds = collision.bounds()
        start_positions = {
            entity_id: list(ball["position_m"])
            for entity_id, ball in state["balls"].items()
        }
        for entity_id in sorted(state["balls"]):
            ball = state["balls"][entity_id]
            if ball["membership"] == "held":
                continue
            for axis in range(3):
                ball["position_m"][axis] += (
                    ball["velocity_m_per_s"][axis] * delta_s
                )
        resolutions = 0
        for entity_id in sorted(state["balls"]):
            ball = state["balls"][entity_id]
            if ball["membership"] == "held":
                continue
            radius = ball["radius_m"]
            for axis in range(3):
                lower = bounds["min"][axis] + radius
                upper = bounds["max"][axis] - radius
                while not lower <= ball["position_m"][axis] <= upper:
                    if resolutions >= contact_iteration_bound:
                        facts["iteration_bound_exhausted"] = True
                        break
                    if ball["position_m"][axis] < lower:
                        ball["position_m"][axis] = lower + (
                            lower - ball["position_m"][axis]
                        )
                        ball["velocity_m_per_s"][axis] = abs(
                            ball["velocity_m_per_s"][axis]
                        )
                    else:
                        ball["position_m"][axis] = upper - (
                            ball["position_m"][axis] - upper
                        )
                        ball["velocity_m_per_s"][axis] = -abs(
                            ball["velocity_m_per_s"][axis]
                        )
                    facts["wall_contacts"] += 1
                    resolutions += 1
            resolutions = self._sweep_planes(
                ball, start_positions[entity_id], collision, facts,
                resolutions, contact_iteration_bound
            )
        free_ids = sorted(
            entity_id
            for entity_id, ball in state["balls"].items()
            if ball["membership"] == "free"
        )
        for first_index in range(len(free_ids)):
            for second_index in range(first_index + 1, len(free_ids)):
                first = state["balls"][free_ids[first_index]]
                second = state["balls"][free_ids[second_index]]
                delta = [
                    second["position_m"][axis] - first["position_m"][axis]
                    for axis in range(3)
                ]
                distance_sq = math.fsum(
                    component * component for component in delta
                )
                reach = first["radius_m"] + second["radius_m"]
                if distance_sq >= reach * reach or distance_sq == 0.0:
                    continue
                approaching = math.fsum(
                    (first["velocity_m_per_s"][axis] - second["velocity_m_per_s"][axis])
                    * delta[axis]
                    for axis in range(3)
                )
                if approaching <= 0.0:
                    continue
                if resolutions >= contact_iteration_bound:
                    facts["iteration_bound_exhausted"] = True
                    continue
                distance = math.sqrt(distance_sq)
                normal = [component / distance for component in delta]
                total_mass = first["mass_kg"] + second["mass_kg"]
                impulse_scale = 2.0 * approaching / (total_mass * distance)
                for axis in range(3):
                    component = impulse_scale * normal[axis]
                    first["velocity_m_per_s"][axis] -= component * second["mass_kg"]
                    second["velocity_m_per_s"][axis] += component * first["mass_kg"]
                resolutions += 1
                facts["pair_contacts"] += 1
        self._apply_palm_field(state, delta_s, facts)
        return facts


class TrialMechanics:
    def __init__(self, state, collision, determinism, budget):
        self.state = state
        self.collision = collision
        self.determinism = determinism
        self.budget = budget
        self.held = {}
        self.props = {}
        self.contacts = {}
        self.contact_counter = 0

    def open_contact(self, form, target_ids, parameters=None, *, next_tick=0):
        if form not in INTERACTION_FORMS:
            raise RuntimeContractError("unknown_interaction_form")
        self.contact_counter += 1
        interaction_id = f"interaction-{next_tick:08d}-{self.contact_counter:04d}"
        self.contacts[interaction_id] = {
            "interaction_id": interaction_id,
            "form": form,
            "target_ids": list(target_ids),
            "parameters": deepcopy(parameters or {}),
            "start_tick": next_tick,
            "end_tick": None,
            "closure": None,
            "accumulated": {"energy_deltas_j": []},
        }
        return interaction_id

    def close_contact(self, interaction_id, reason, *, end_tick):
        contact = self.contacts.get(interaction_id)
        if contact is None:
            raise RuntimeContractError("unknown_interaction")
        if contact["closure"] is not None:
            raise RuntimeContractError("contact_already_closed")
        work = math.fsum(contact["accumulated"]["energy_deltas_j"])
        contact["end_tick"] = end_tick
        contact["closure"] = {
            "closure_reason": reason,
            "work_j": work,
            "absorbed_energy_j": -work if work < 0.0 else 0.0,
            "duration_ticks": end_tick - contact["start_tick"] + 1,
        }
        return deepcopy(contact["closure"])

    def register_prop(self, prop_id, *, damping_factor=0.5, energy_delta_j=None):
        if prop_id in self.props:
            raise RuntimeContractError("duplicate_prop_id")
        if energy_delta_j is not None and (
            not _finite_number(energy_delta_j) or energy_delta_j <= 0.0
        ):
            raise RuntimeContractError("invalid_prop_energy_delta")
        self.props[prop_id] = {
            "prop_id": prop_id,
            "kind": "one_shot_consumable",
            "state": "unused",
            "damping_factor": float(damping_factor),
            "energy_delta_j": (
                None if energy_delta_j is None else float(energy_delta_j)
            ),
            "absorbed_energy_j": 0.0,
        }
        return deepcopy(self.props[prop_id])

    def _trigger_prop(self, prop_id):
        prop = self.props.get(prop_id)
        if prop is None:
            raise RuntimeContractError("unknown_prop")
        if prop["state"] == "consumed":
            raise RuntimeContractError("prop_consumed")
        if prop["state"] != "unused":
            return None
        prop["state"] = "active"
        return prop

    def grab(self, entity_id, *, next_tick=0):
        ball = self.state["balls"].get(entity_id)
        if ball is None or ball["membership"] != "free":
            raise RuntimeContractError("entity_not_grabbable")
        ball["membership"] = "held"
        interaction_id = self.open_contact(
            "state", [entity_id], {"kind": "held_sequence"}, next_tick=next_tick
        )
        self.held[entity_id] = {
            "entity_id": entity_id,
            "position_m": list(ball["position_m"]),
            "velocity_m_per_s": list(ball["velocity_m_per_s"]),
            "tracking_valid": True,
            "interaction_id": interaction_id,
            "kinetic_energy_at_grab_j": HeadlessBallWriter._ball_energy(ball),
        }
        return interaction_id

    def held_target(self, parameters):
        entity_id = parameters["entity_id"]
        held = self.held.get(entity_id)
        if held is None:
            raise RuntimeContractError("entity_not_held")
        if not parameters.get("tracking_valid", True):
            held["tracking_valid"] = False
            return None
        held["tracking_valid"] = True
        position = list(parameters["target_position_m"])
        velocity = list(parameters["target_velocity_m_per_s"])
        if not all(_finite_number(value) for value in position + velocity):
            return {"invalid": "non_finite_input"}
        bounds = self.collision.bounds()
        radius = self.state["balls"][entity_id]["radius_m"]
        limit = self.budget.held_speed_limit_m_per_s
        for axis in range(3):
            position[axis] = min(
                max(position[axis], bounds["min"][axis] + radius),
                bounds["max"][axis] - radius,
            )
            velocity[axis] = max(-limit, min(limit, velocity[axis]))
        held["position_m"] = position
        held["velocity_m_per_s"] = velocity
        return {
            "effect_kind": "held_state",
            "entity_id": entity_id,
            "position_m": position,
            "velocity_m_per_s": velocity,
        }

    def release(self, entity_id, *, end_tick=0):
        ball = self.state["balls"].get(entity_id)
        held = self.held.get(entity_id)
        if ball is None or held is None or ball["membership"] != "held":
            raise RuntimeContractError("entity_not_held")
        if not held["tracking_valid"]:
            raise RuntimeContractError("tracking_lost_release_refused")
        ball["membership"] = "free"
        ball["position_m"] = list(held["position_m"])
        ball["velocity_m_per_s"] = list(held["velocity_m_per_s"])
        del self.held[entity_id]
        net_work = HeadlessBallWriter._ball_energy(ball) - held[
            "kinetic_energy_at_grab_j"
        ]
        return self.close_contact(
            held["interaction_id"], "released", end_tick=end_tick
        ), net_work

    def command_effect(self, entry):
        operation = entry["operation"]
        parameters = entry["parameters"]
        if operation == "ball_impulse":
            return {
                "effect_kind": "impulse",
                "entity_id": parameters["entity_id"],
                "delta_v_m_per_s": list(parameters["delta_v_m_per_s"]),
            }
        if operation == "prop_trigger":
            prop = self._trigger_prop(parameters["prop_id"])
            if prop is None:
                return None
            effect = {
                "effect_kind": "damping",
                "entity_id": parameters["entity_id"],
                "factor": prop["damping_factor"],
                "prop_id": parameters["prop_id"],
            }
            if prop["energy_delta_j"] is not None:
                effect["energy_delta_j"] = prop["energy_delta_j"]
            return effect
        if operation == "grab":
            self.grab(parameters["entity_id"], next_tick=entry["target_tick"])
            return None
        if operation == "release":
            return self.release(parameters["entity_id"], end_tick=entry["target_tick"])
        if operation == "held_target":
            return self.held_target(parameters)
        if operation == "sweep_sample":
            contact = self.contacts.get(parameters["interaction_id"])
            if contact is None or contact["closure"] is not None:
                return None
            effect = {
                "effect_kind": "sweep",
                "interaction_id": parameters["interaction_id"],
                "origin_m": list(parameters["origin_m"]),
                "velocity_m_per_s": list(parameters["velocity_m_per_s"]),
                "radius_m": parameters["radius_m"],
            }
            if "energy_delta_j" in parameters:
                effect["energy_delta_j"] = parameters["energy_delta_j"]
            else:
                effect["impulse_magnitude"] = parameters["impulse_magnitude"]
            if "origin_b_m" in parameters:
                effect["origin_b_m"] = list(parameters["origin_b_m"])
                effect["velocity_b_m_per_s"] = list(
                    parameters["velocity_b_m_per_s"]
                )
            return effect
        raise RuntimeContractError("unknown_operation")

    def record_facts(self, facts):
        for applied in facts.get("applied_effects", []):
            interaction_id = applied.get("interaction_id")
            if interaction_id is not None:
                contact = self.contacts.get(interaction_id)
                if contact is not None and contact["closure"] is None:
                    contact["accumulated"]["energy_deltas_j"].append(
                        applied["energy_delta_j"]
                    )
            prop_id = applied.get("prop_id")
            if prop_id is not None and applied["effect_kind"] == "damping":
                prop = self.props[prop_id]
                prop["state"] = "consumed"
                prop["absorbed_energy_j"] += -applied["energy_delta_j"]


class ScientificWriterSlot:
    def __init__(self):
        self._bindings = {}

    def bind(self, trial_id, writer, writer_configuration_id):
        existing = self._bindings.get(trial_id)
        if existing is not None and existing[0] is not writer:
            raise RuntimeContractError("writer_already_bound")
        self._bindings[trial_id] = (writer, writer_configuration_id)

    def writer_for(self, trial_id):
        binding = self._bindings.get(trial_id)
        if binding is None:
            raise RuntimeContractError("writer_not_bound")
        return binding[0]

    def binding(self, trial_id):
        writer, configuration_id = self._bindings[trial_id]
        return {
            "writer_id": getattr(writer, "writer_id", writer.__class__.__name__),
            "writer_configuration_id": configuration_id,
        }


@dataclass
class PhysicalTrial:
    trial_id: str
    kernel_trial: object
    writer: object
    streams: NamedRandomStreams
    collision: CollisionRepresentation
    setup: dict
    mechanics: TrialMechanics
    completed_tick: int = 0
    command_log: list = field(default_factory=list)
    admission_counter: int = 0
    tick_measurements: list = field(default_factory=list)
    publications: list = field(default_factory=list)
    hold: dict = None
    pressure_streak: int = 0
    candidate: bool = False
    recompute_supported: bool = True
    recompute_reason: str = None
    playback_only: bool = False
    batch: dict = None
    checkpoints: dict = field(default_factory=dict)
    degradation_events: list = field(default_factory=list)


def _state_digest(state):
    return hashlib.sha256(
        json.dumps(state, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()


class SimulationRuntime:
    def __init__(
        self,
        kernel,
        determinism=None,
        collision=None,
        *,
        writer_factory=None,
        budget=None,
        unit_convention=None,
    ):
        if not isinstance(kernel, TrialKernel):
            raise TypeError("kernel must be a TrialKernel")
        self.kernel = kernel
        self.determinism = determinism or ModelDeterminismDeclaration()
        self.unit_convention = deepcopy(unit_convention or DEFAULT_UNIT_CONVENTION)
        self.collision_supply = collision
        if self.collision_supply is not None:
            self._validate_collision_supply(self.collision_supply)
        self.writer_factory = writer_factory or HeadlessBallWriter
        self.budget = budget or RuntimeBudgetProfile()
        self.physical = {}
        self.live_trial_id = None
        self.frozen_trial_ids = set()
        self.readonly_trial_ids = set()
        self.slot = ScientificWriterSlot()
        self.failure_reports = []
        self.diagnostic_sequence = 0
        self.degradation_events = []
        self.scene_events = []

    @contextmanager
    def _kernel_focus(self, trial_id):
        previous = self.kernel.current_trial_id
        self.kernel.current_trial_id = trial_id
        try:
            yield
        finally:
            self.kernel.current_trial_id = previous

    def _emit_failure(
        self,
        layer,
        reason,
        *,
        affected_operation=None,
        trial=None,
        containment_result=None,
        permitted_recovery_actions=(),
    ):
        self.diagnostic_sequence += 1
        report = failure_report(
            layer,
            reason,
            affected_operation=affected_operation,
            trial_ref=trial.kernel_trial.ref.to_dict() if trial is not None else None,
            tick=trial.completed_tick if trial is not None else None,
            containment_result=containment_result,
            permitted_recovery_actions=permitted_recovery_actions,
            diagnostic_sequence=self.diagnostic_sequence,
        )
        self.failure_reports.append(report)
        return report

    def _validate_collision_supply(self, representation):
        try:
            return representation.validate()
        except CollisionContractError as error:
            self._emit_failure(
                "scene_permission",
                error.reason_code,
                affected_operation="collision_validation",
                containment_result="trial_creation_refused",
                permitted_recovery_actions=["confirm_room"],
            )
            raise RuntimeContractError(error.reason_code) from error

    @staticmethod
    def _pin(representation):
        return {
            "representation_id": representation.representation_id,
            "format_version": representation.format_version,
            "content_checksum": representation.checksum(),
            "coordinate_frame_id": representation.coordinate_frame_id,
        }

    def _assert_finite_state(self, state, *, trial=None, context="tick"):
        for entity_id in sorted(state.get("balls", {})):
            ball = state["balls"][entity_id]
            values = list(ball["position_m"]) + list(ball["velocity_m_per_s"])
            if any(not _finite_number(value) for value in values):
                self._emit_failure(
                    "numerical_scientific",
                    "non_finite_state",
                    affected_operation=context,
                    trial=trial,
                    containment_result="publication_withheld_numerical_hold",
                    permitted_recovery_actions=["restart_trial"],
                )
                raise RuntimeContractError("non_finite_state")

    def _require_physical(self, trial_id):
        physical = self.physical.get(trial_id)
        if physical is None:
            raise RuntimeContractError("trial_not_prepared")
        return physical

    def prepare_trial(self, trial_id, *, setup_parameters=None, invariants=("finite-state",)):
        kernel_trial = self.kernel.trials.get(trial_id)
        if kernel_trial is None:
            raise RuntimeContractError("unknown_trial")
        if trial_id in self.physical:
            raise RuntimeContractError("trial_already_prepared")
        if self.collision_supply is None:
            self._emit_failure(
                "scene_permission",
                "collision_representation_missing",
                affected_operation="trial_preparation",
                containment_result="trial_creation_refused",
                permitted_recovery_actions=["confirm_room"],
            )
            raise RuntimeContractError("collision_representation_missing")
        self._validate_collision_supply(self.collision_supply)
        pinned = CollisionRepresentation.from_dict(self.collision_supply.to_dict())
        parameters = (
            deepcopy(setup_parameters)
            if setup_parameters is not None
            else deepcopy(kernel_trial.configuration["target_parameters"])
        )
        seed = kernel_trial.configuration["seed"]
        writer = self.writer_factory()
        self.slot.bind(trial_id, writer, self.determinism.writer_configuration_id)
        streams = NamedRandomStreams(
            seed,
            self.determinism.seed_expansion_id,
            self.determinism.stream_layout_version,
        )
        initial_state = writer.setup(streams, parameters, pinned)
        self._assert_finite_state(initial_state, context="initial_setup")
        mechanics = TrialMechanics(
            initial_state, pinned, self.determinism, self.budget
        )
        physical = PhysicalTrial(
            trial_id=trial_id,
            kernel_trial=kernel_trial,
            writer=writer,
            streams=streams,
            collision=pinned,
            setup={
                "seed": seed,
                "setup_parameters": parameters,
                "actual_initial_invariant_ids": list(invariants),
                "determinism_identity": self.determinism.identity(),
                "collision_pin": self._pin(pinned),
                "writer_binding": self.slot.binding(trial_id),
                "initial_state": deepcopy(initial_state),
            },
            mechanics=mechanics,
            candidate=kernel_trial.state["acceptance"] == "proposed",
        )
        self.physical[trial_id] = physical
        return deepcopy(physical.setup)

    def activate_live(self, trial_id):
        physical = self._require_physical(trial_id)
        kernel_trial = physical.kernel_trial
        if kernel_trial.state["acceptance"] != "accepted":
            raise RuntimeContractError("trial_not_accepted")
        if kernel_trial.state["phase"] in TERMINAL_PHASES or kernel_trial.state["phase"] == "setup":
            raise RuntimeContractError("trial_not_started")
        previous = self.live_trial_id
        if previous is not None and previous != trial_id:
            self.frozen_trial_ids.add(previous)
        physical.candidate = False
        self.frozen_trial_ids.discard(trial_id)
        self.readonly_trial_ids.discard(trial_id)
        self.live_trial_id = trial_id
        return {
            "live_trial_id": trial_id,
            "previous_live_trial_id": previous,
            "frozen_trial_ids": sorted(self.frozen_trial_ids),
        }

    def abandon_candidate(self, trial_id):
        physical = self._require_physical(trial_id)
        if not physical.candidate:
            raise RuntimeContractError("trial_not_proposed_candidate")
        physical.candidate = False
        self.readonly_trial_ids.add(trial_id)
        return {
            "abandoned_candidate": trial_id,
            "live_trial_id": self.live_trial_id,
        }

    def return_from_started(self, trial_id):
        physical = self._require_physical(trial_id)
        if self.live_trial_id != trial_id:
            raise RuntimeContractError("trial_not_live")
        kernel_trial = physical.kernel_trial
        with self._kernel_focus(trial_id):
            if kernel_trial.state["phase"] == "measured":
                self.kernel.archive(generation_committed=True)
                disposition = "archived"
            else:
                if kernel_trial.state["activity"] == "running":
                    self.kernel.pause()
                disposition = "paused"
        self.live_trial_id = None
        self.frozen_trial_ids.discard(trial_id)
        self.readonly_trial_ids.add(trial_id)
        result = {
            "returned_trial_id": trial_id,
            "disposition": disposition,
            "resumed_live_trial_id": None,
            "source_disposition": "not_applicable",
        }
        source_ref = kernel_trial.identity["source_trial_ref"]
        if source_ref is None:
            return result
        source_id = source_ref["trial_id"]
        source_kernel = self.kernel.trials.get(source_id)
        source_physical = self.physical.get(source_id)
        eligible = (
            source_kernel is not None
            and source_physical is not None
            and source_kernel.state["phase"] not in TERMINAL_PHASES
            and source_kernel.state["phase"] not in {"setup", "measured"}
        )
        if eligible:
            with self._kernel_focus(source_id):
                if source_kernel.state["activity"] == "paused":
                    self.kernel.resume()
            self.live_trial_id = source_id
            self.frozen_trial_ids.discard(source_id)
            self.readonly_trial_ids.discard(source_id)
            result["resumed_live_trial_id"] = source_id
            result["source_disposition"] = "resumed_live"
        else:
            if source_id in self.physical:
                self.readonly_trial_ids.add(source_id)
                self.frozen_trial_ids.discard(source_id)
            result["source_disposition"] = "read_only"
        return result

    @staticmethod
    def _normalize_parameters(parameters):
        invalid = object()

        def walk(value):
            if value is None or isinstance(value, (bool, str)):
                return value
            if isinstance(value, (int, float)):
                return value if math.isfinite(value) else invalid
            if isinstance(value, list):
                walked = [walk(item) for item in value]
                return invalid if invalid in walked else walked
            if isinstance(value, dict):
                walked = {key: walk(item) for key, item in value.items()}
                return invalid if invalid in walked.values() else walked
            return invalid

        result = walk(deepcopy(parameters))
        return None if result is invalid else result

    def _log_entry(self, physical, entry):
        physical.admission_counter += 1
        entry["admission_sequence"] = physical.admission_counter
        physical.command_log.append(entry)
        return entry

    def _log_invalid_input(self, physical, operation, parameters, reason_code):
        return self._log_entry(
            physical,
            {
                "kind": "invalid_input_fact",
                "operation": operation,
                "reason_code": reason_code,
                "target_tick": physical.completed_tick + 1,
                "intended_tick": None,
                "late_assignment_fact": None,
            },
        )

    def _admission_precondition(self, physical, operation, parameters):
        mechanics = physical.mechanics
        if operation in CONTROL_OPERATIONS:
            return None
        if operation == "ball_impulse" or operation == "grab":
            ball = mechanics.state["balls"].get(parameters.get("entity_id"))
            if ball is None:
                return "unknown_entity"
            if operation == "grab":
                return None if ball["membership"] == "free" else "entity_not_grabbable"
            return None if ball["membership"] == "free" else "entity_not_free"
        if operation in ("release", "held_target"):
            held = mechanics.held.get(parameters.get("entity_id"))
            if held is None:
                return "entity_not_held"
            if operation == "held_target":
                return None
            return None if held["tracking_valid"] else "tracking_lost_release_refused"
        if operation == "prop_trigger":
            prop = mechanics.props.get(parameters.get("prop_id"))
            if prop is None:
                return "unknown_prop"
            if prop["state"] == "consumed":
                return "prop_consumed"
            if prop["state"] != "unused":
                return "prop_inactive"
            return None
        if operation == "sweep_sample":
            contact = mechanics.contacts.get(parameters.get("interaction_id"))
            if contact is None or contact["closure"] is not None:
                return "contact_not_open"
            return None
        return "unknown_operation"

    def admit(
        self,
        trial_id,
        operation,
        parameters,
        *,
        source="user_input",
        command_id=None,
        basis_trial_ref=None,
        intended_tick=None,
    ):
        physical = self._require_physical(trial_id)
        if physical.candidate:
            raise RuntimeContractError("candidate_isolated")
        if trial_id in self.readonly_trial_ids:
            raise RuntimeContractError("trial_read_only")
        if physical.batch is not None and operation not in CONTROL_OPERATIONS:
            raise RuntimeContractError("batch_occupies_command_stream")
        if source == "accepted_proposal":
            if command_id is None:
                self._log_invalid_input(physical, operation, parameters, "missing_command_id")
                return {"admitted": False, "reason_code": "missing_command_id"}
            if basis_trial_ref is not None:
                try:
                    self.kernel.assert_current_refs([basis_trial_ref])
                except KernelError as error:
                    self._log_invalid_input(physical, operation, parameters, error.reason_code)
                    return {"admitted": False, "reason_code": error.reason_code}
        normalized = self._normalize_parameters(parameters)
        if normalized is None:
            self._log_invalid_input(physical, operation, parameters, "non_finite_input")
            return {"admitted": False, "reason_code": "non_finite_input"}
        precondition = self._admission_precondition(physical, operation, normalized)
        if precondition is not None:
            self._log_invalid_input(physical, operation, parameters, precondition)
            return {"admitted": False, "reason_code": precondition}
        target_tick = physical.completed_tick + 1
        admitted_for_target = sum(
            1
            for entry in physical.command_log
            if entry["kind"] == "command" and entry["target_tick"] == target_tick
        )
        if admitted_for_target >= self.budget.input_admission_per_tick:
            self._log_invalid_input(physical, operation, parameters, "admission_quota_exceeded")
            return {"admitted": False, "reason_code": "admission_quota_exceeded"}
        if command_id is not None:
            for entry in physical.command_log:
                if (
                    entry["kind"] == "command"
                    and entry.get("command_id") == command_id
                ):
                    return {
                        "admitted": False,
                        "reason_code": "duplicate_command_id",
                        "replay": True,
                    }
        late_fact = None
        if intended_tick is not None and intended_tick < target_tick:
            late_fact = {
                "intended_tick": intended_tick,
                "assigned_to_next_unstarted_tick": target_tick,
            }
        entry = self._log_entry(
            physical,
            {
                "kind": "command",
                "command_id": command_id,
                "source": source,
                "operation": operation,
                "parameters": normalized,
                "target_tick": target_tick,
                "intended_tick": intended_tick,
                "late_assignment_fact": late_fact,
                "executed": False,
            },
        )
        return {
            "admitted": True,
            "command_id": command_id,
            "target_tick": target_tick,
            "admission_sequence": entry["admission_sequence"],
            "late_assignment_fact": deepcopy(late_fact),
        }

    def _apply_degradation(self, physical):
        events = [
            {"step": step, "action": DEGRADATION_ACTIONS[step]}
            for step in self.budget.degradation_order
            if step != "hold"
        ]
        physical.degradation_events.extend(deepcopy(events))
        self.degradation_events.extend(
            {"trial_id": physical.trial_id, **event} for event in events
        )
        return events

    def request_slice(self, trial_id, demand):
        physical = self._require_physical(trial_id)
        if physical.hold is not None:
            return {"executed": 0, "hold": deepcopy(physical.hold)}
        if trial_id != self.live_trial_id:
            raise RuntimeContractError("trial_not_live")
        kernel_trial = physical.kernel_trial
        if (
            kernel_trial.state["activity"] != "running"
            or kernel_trial.state["phase"] in TERMINAL_PHASES
        ):
            return {
                "executed": 0,
                "paused": True,
                "activity": kernel_trial.state["activity"],
                "tick": physical.completed_tick,
            }
        if not isinstance(demand, int) or isinstance(demand, bool) or demand < 0:
            raise RuntimeContractError("invalid_tick_demand")
        shed_ticks = 0
        degradation = []
        if demand > self.budget.max_ticks_per_scheduling_slice:
            shed_ticks = demand - self.budget.max_ticks_per_scheduling_slice
            demand = self.budget.max_ticks_per_scheduling_slice
            physical.pressure_streak += 1
            degradation = self._apply_degradation(physical)
            if physical.pressure_streak >= self.budget.sustained_pressure_slices:
                physical.hold = {
                    "kind": "performance",
                    "reason_code": "sustained_tick_pressure",
                }
                self._emit_failure(
                    "performance_thermal",
                    "sustained_tick_pressure",
                    affected_operation="tick_scheduling",
                    trial=physical,
                    containment_result="performance_hold_no_tick_debt",
                    permitted_recovery_actions=["reduce_tick_demand", "clear_performance_hold"],
                )
                degradation = degradation + [
                    {"step": "hold", "action": "performance_hold"}
                ]
        else:
            physical.pressure_streak = 0
        publications = []
        executed = 0
        for _ in range(demand):
            if physical.hold is not None:
                break
            publications.append(self._run_tick(physical))
            executed += 1
        return {
            "executed": executed,
            "publications": publications,
            "shed_ticks": shed_ticks,
            "degradation": degradation,
            "tick": physical.completed_tick,
        }

    def _run_tick(self, physical):
        target_tick = physical.completed_tick + 1
        commands = [
            entry
            for entry in physical.command_log
            if entry["kind"] == "command" and entry["target_tick"] == target_tick
        ]
        effects = []
        semantic_closures = 0
        for entry in commands:
            effect = physical.mechanics.command_effect(entry)
            entry["executed"] = True
            if effect is None:
                entry["outcome"] = {"applied": False}
            elif isinstance(effect, tuple):
                closure, net_work = effect
                entry["outcome"] = {
                    "applied": False,
                    "semantic_closure": deepcopy(closure),
                    "net_external_work_j": net_work,
                }
                semantic_closures += 1
            elif effect.get("invalid"):
                entry["outcome"] = {"applied": False, "reason_code": effect["invalid"]}
            else:
                effects.append(effect)
                entry["outcome"] = {"applied": True}
        try:
            facts = physical.writer.advance(
                physical.mechanics.state,
                self.determinism.delta_time_s,
                physical.collision,
                physical.streams,
                effects,
                self.determinism.contact_iteration_bound,
            )
        except Exception as error:
            physical.hold = {"kind": "numerical", "reason_code": "writer_execution_failed"}
            self._emit_failure(
                "numerical_scientific",
                "writer_execution_failed",
                affected_operation="scientific_advance",
                trial=physical,
                containment_result="numerical_hold",
                permitted_recovery_actions=["restart_trial"],
            )
            raise RuntimeContractError("writer_execution_failed") from error
        if facts.get("iteration_bound_exhausted"):
            physical.hold = {
                "kind": "numerical",
                "reason_code": "contact_iteration_bound_exhausted",
            }
            self._emit_failure(
                "numerical_scientific",
                "contact_iteration_bound_exhausted",
                affected_operation="scientific_advance",
                trial=physical,
                containment_result="numerical_hold_no_tunnelling_fallback",
                permitted_recovery_actions=["restart_trial"],
            )
            raise RuntimeContractError("contact_iteration_bound_exhausted")
        try:
            self._assert_finite_state(
                physical.mechanics.state, trial=physical, context="tick_publication"
            )
            energies = kinetic_energies(physical.mechanics.state["balls"])
            temperature = kinetic_temperature(
                energies,
                self.unit_convention,
                self.determinism.statistical_minimum_free_count,
            )
        except RuntimeContractError:
            if physical.hold is None:
                physical.hold = {"kind": "numerical", "reason_code": "non_finite_state"}
            raise
        physical.mechanics.record_facts(facts)
        physical.tick_measurements.append(
            {
                "tick": target_tick,
                "kinetic": energies,
                "temperature": temperature,
                "facts": {
                    "wall_contacts": facts["wall_contacts"],
                    "pair_contacts": facts["pair_contacts"],
                },
                "effects": facts["applied_effects"],
            }
        )
        physical.completed_tick = target_tick
        physical.kernel_trial.state["tick"] = target_tick
        view = self.state_view(physical.trial_id)
        physical.publications.append(view["view_id"])
        for _ in range(semantic_closures):
            if not physical.candidate:
                with self._kernel_focus(physical.trial_id):
                    self.kernel.close_interaction(fact_kind="interaction_closed")
        return view

    def state_view(self, trial_id):
        physical = self._require_physical(trial_id)
        energies = kinetic_energies(physical.mechanics.state["balls"])
        entities = [
            {
                "entity_id": entity_id,
                "position_m": list(ball["position_m"]),
                "velocity_m_per_s": list(ball["velocity_m_per_s"]),
                "mass_kg": ball["mass_kg"],
                "membership": ball["membership"],
            }
            for entity_id, ball in sorted(physical.mechanics.state["balls"].items())
        ]
        return {
            "view_id": f"view-{trial_id}-{physical.completed_tick:08d}-{len(physical.publications) + 1:06d}",
            "trial_ref": physical.kernel_trial.ref.to_dict(),
            "tick": physical.completed_tick,
            "entities": entities,
            "energy": energies,
            "collision_pin": deepcopy(physical.setup["collision_pin"]),
            "determinism_identity": deepcopy(self.determinism.identity()),
        }

    def clock(self, trial_id):
        physical = self._require_physical(trial_id)
        return {
            "tick": physical.completed_tick,
            "fixed_delta_time_id": self.determinism.fixed_delta_time_id,
            "delta_time_s": self.determinism.delta_time_s,
            "activity": physical.kernel_trial.state["activity"],
            "hold": deepcopy(physical.hold),
        }

    def open_contact(self, trial_id, form, target_ids, parameters=None):
        physical = self._require_physical(trial_id)
        return physical.mechanics.open_contact(
            form,
            target_ids,
            parameters,
            next_tick=physical.completed_tick + 1,
        )

    def submit_sweep_sample(self, trial_id, interaction_id, sample):
        physical = self._require_physical(trial_id)
        contact = physical.mechanics.contacts.get(interaction_id)
        if contact is None or contact["form"] != "swept":
            raise RuntimeContractError("sweep_not_open")
        if contact["closure"] is not None:
            raise RuntimeContractError("contact_already_closed")
        if not sample.get("tracking_valid", True):
            self._log_invalid_input(
                physical, "sweep_sample", sample, "tracking_lost"
            )
            self._close_contact(trial_id, interaction_id, "tracking_lost_aborted")
            return {"accepted": False, "reason_code": "tracking_lost", "aborted": True}
        contact_parameters = contact["parameters"]
        parameters = {
            "interaction_id": interaction_id,
            "origin_m": sample["origin_m"],
            "velocity_m_per_s": sample["velocity_m_per_s"],
            "radius_m": sample["radius_m"],
        }
        if "energy_delta_j" in contact_parameters:
            parameters["energy_delta_j"] = contact_parameters["energy_delta_j"]
        else:
            parameters["impulse_magnitude"] = contact_parameters.get(
                "impulse_magnitude", 1.0
            )
        if "origin_b_m" in sample:
            parameters["origin_b_m"] = sample["origin_b_m"]
            parameters["velocity_b_m_per_s"] = sample.get(
                "velocity_b_m_per_s", sample["velocity_m_per_s"]
            )
        return self.admit(trial_id, "sweep_sample", parameters)

    def close_contact(self, trial_id, interaction_id, reason="completed"):
        return self._close_contact(trial_id, interaction_id, reason)

    def _close_contact(self, trial_id, interaction_id, reason):
        physical = self._require_physical(trial_id)
        closure = physical.mechanics.close_contact(
            interaction_id, reason, end_tick=physical.completed_tick
        )
        if not physical.candidate:
            with self._kernel_focus(trial_id):
                self.kernel.close_interaction(fact_kind="interaction_closed")
        return closure

    def register_prop(self, trial_id, prop_id, *, damping_factor=0.5,
                      energy_delta_j=None):
        physical = self._require_physical(trial_id)
        return physical.mechanics.register_prop(
            prop_id, damping_factor=damping_factor,
            energy_delta_j=energy_delta_j
        )

    def prop_states(self, trial_id):
        physical = self._require_physical(trial_id)
        return deepcopy(physical.mechanics.props)

    def begin_batch(self, trial_id, declaration):
        physical = self._require_physical(trial_id)
        if physical.batch is not None:
            raise RuntimeContractError("batch_already_active")
        if not {"batch_id", "sample_count", "input_delta", "result_granularity"} <= set(declaration):
            raise RuntimeContractError("batch_declaration_incomplete")
        if declaration["result_granularity"] not in RESULT_GRANULARITIES:
            raise RuntimeContractError("unknown_result_granularity")
        delta = declaration["input_delta"]
        if not {"parameter_id", "from_value", "to_value", "downstream_effect_ids"} <= set(delta):
            raise RuntimeContractError("input_delta_invalid")
        parameters = physical.setup["setup_parameters"]
        if (
            delta["parameter_id"] not in parameters
            or delta["from_value"] != parameters[delta["parameter_id"]]
        ):
            raise RuntimeContractError("input_delta_invalid")
        physical.batch = {
            "declaration": deepcopy(declaration),
            "status": "running",
            "samples": [],
            "aggregate": None,
            "reason_code": None,
            "semantic_measurement_id": None,
        }
        return deepcopy(physical.batch["declaration"])

    def _batch_sample_seed(self, physical, declaration, sample_index):
        stream_id = declaration.get(
            "sample_stream_id", f"batch-{declaration['batch_id']}"
        )
        material = (
            f"{physical.setup['seed']}\0{stream_id}\0{sample_index}"
        ).encode("utf-8")
        return int(hashlib.sha256(material).hexdigest()[:16], 16)

    def _run_batch_sample(self, physical, declaration, sample_index):
        delta = declaration["input_delta"]
        sample_parameters = deepcopy(physical.setup["setup_parameters"])
        sample_parameters[delta["parameter_id"]] = delta["to_value"]
        sample_seed = self._batch_sample_seed(physical, declaration, sample_index)
        sample_streams = NamedRandomStreams(
            sample_seed,
            self.determinism.seed_expansion_id,
            self.determinism.stream_layout_version,
        )
        writer = self.writer_factory()
        pinned = CollisionRepresentation.from_dict(physical.collision.to_dict())
        state = writer.setup(sample_streams, sample_parameters, pinned)
        self._assert_finite_state(state, context="batch_sample_setup")
        ticks = int(declaration.get("ticks_per_sample", 10))
        series = []
        for _ in range(ticks):
            facts = writer.advance(
                state,
                self.determinism.delta_time_s,
                pinned,
                sample_streams,
                [],
                self.determinism.contact_iteration_bound,
            )
            energies = kinetic_energies(state["balls"])
            series.append(
                {
                    "k_total_j": energies["k_total_j"],
                    "k_thermal_j": energies["k_thermal_j"],
                }
            )
        return {
            "sample_index": sample_index,
            "sample_seed": sample_seed,
            "final": series[-1],
            "series": series,
            "setup_parameters": sample_parameters,
        }

    def _complete_batch(self, physical, batch):
        declaration = batch["declaration"]
        finals = [sample["final"]["k_thermal_j"] for sample in batch["samples"]]
        statistics = {}
        statistic_ids = declaration.get(
            "aggregate_statistic_ids", ["mean_final_k_thermal"]
        )
        for statistic_id in statistic_ids:
            if statistic_id != "mean_final_k_thermal":
                raise RuntimeContractError("unknown_aggregate_statistic")
            statistics[statistic_id] = math.fsum(finals) / len(finals)
        batch["aggregate"] = {
            "statistics": statistics,
            "completed_samples": len(batch["samples"]),
            "requested_samples": declaration["sample_count"],
        }
        ticks = int(declaration.get("ticks_per_sample", 10))
        measurement = {
            "measurement_id": f"batch-{declaration['batch_id']}-aggregate",
            "value": statistics["mean_final_k_thermal"],
            "unit_id": "J",
            "coordinate_frame_id": "room-frame-v1",
            "quantity_convention_id": "kinetic-energy",
            "window": {
                "window_id": f"batch-{declaration['batch_id']}",
                "start_tick": 0,
                "end_tick": ticks,
                "duration_seconds": ticks * self.determinism.delta_time_s,
            },
            "sample_count": len(batch["samples"]),
            "uncertainty": None,
            "sufficient": True,
            "applicability": "valid",
            "calculation_version": self.determinism.calculation_version,
        }
        with self._kernel_focus(physical.trial_id):
            self.kernel.commit_measurement(measurement)
        batch["status"] = "complete"
        batch["semantic_measurement_id"] = measurement["measurement_id"]

    def step_batch(self, trial_id):
        physical = self._require_physical(trial_id)
        batch = physical.batch
        if batch is None:
            raise RuntimeContractError("no_active_batch")
        if batch["status"] != "running":
            raise RuntimeContractError("batch_not_running")
        if physical.hold is not None:
            return {"deferred": True, "reason_code": physical.hold["reason_code"]}
        if physical.pressure_streak > 0:
            return {"deferred": True, "reason_code": "batch_yields_to_live_work"}
        declaration = batch["declaration"]
        sample_index = len(batch["samples"])
        try:
            result = self._run_batch_sample(physical, declaration, sample_index)
        except Exception as error:
            batch["status"] = "incomplete"
            batch["reason_code"] = "sample_execution_failed"
            return {
                "status": "incomplete",
                "reason_code": "sample_execution_failed",
                "completed_samples": len(batch["samples"]),
                "error": str(error.__class__.__name__),
            }
        batch["samples"].append(result)
        if len(batch["samples"]) >= declaration["sample_count"]:
            self._complete_batch(physical, batch)
        return {
            "sample_index": sample_index,
            "result": deepcopy(result),
            "status": batch["status"],
        }

    def _finish_batch_incomplete(self, physical, reason_code):
        batch = physical.batch
        batch["status"] = "incomplete"
        batch["reason_code"] = reason_code
        return {
            "status": "incomplete",
            "reason_code": reason_code,
            "completed_samples": len(batch["samples"]),
            "requested_samples": batch["declaration"]["sample_count"],
            "results": deepcopy(batch["samples"]),
        }

    def cancel_batch(self, trial_id, reason_code="user_cancelled"):
        physical = self._require_physical(trial_id)
        if physical.batch is None or physical.batch["status"] != "running":
            raise RuntimeContractError("batch_not_running")
        return self._finish_batch_incomplete(physical, reason_code)

    def batch_evidence_status(self, trial_id):
        physical = self._require_physical(trial_id)
        batch = physical.batch
        if batch is None:
            raise RuntimeContractError("no_active_batch")
        return {
            "status": batch["status"],
            "satisfies_complete_measurement_rule": batch["status"] == "complete",
            "reason_code": batch["reason_code"],
            "completed_samples": len(batch["samples"]),
            "requested_samples": batch["declaration"]["sample_count"],
        }

    def capture_checkpoint(self, trial_id, *, verified=True):
        physical = self._require_physical(trial_id)
        checkpoint = {
            "tick": physical.completed_tick,
            "verified": bool(verified),
            "state": deepcopy(physical.mechanics.state),
            "command_log_length": len(physical.command_log),
            "state_digest": _state_digest(physical.mechanics.state),
        }
        physical.checkpoints[checkpoint["tick"]] = checkpoint
        return {
            "tick": checkpoint["tick"],
            "verified": checkpoint["verified"],
            "state_digest": checkpoint["state_digest"],
        }

    def build_replay_recipe(self, trial_id, to_tick=None, *, capability="recompute"):
        physical = self._require_physical(trial_id)
        if to_tick is None:
            to_tick = physical.completed_tick
        if to_tick < 0 or to_tick > physical.completed_tick:
            raise RuntimeContractError("recipe_tick_out_of_range")
        commands = [
            deepcopy(entry)
            for entry in physical.command_log
            if entry["kind"] == "command" and entry["target_tick"] <= to_tick
        ]
        for entry in commands:
            if not entry["executed"]:
                raise RuntimeContractError("partial_trailing_command")
        measurements = [
            {
                "tick": sample["tick"],
                "k_total_j": sample["kinetic"]["k_total_j"],
                "k_thermal_j": sample["kinetic"]["k_thermal_j"],
            }
            for sample in physical.tick_measurements
            if sample["tick"] <= to_tick
        ]
        checkpoints = [
            deepcopy(checkpoint)
            for tick, checkpoint in sorted(physical.checkpoints.items())
            if tick <= to_tick
        ]
        return {
            "recipe_format_version": "1",
            "capability": capability,
            "trial_id": trial_id,
            "to_tick": to_tick,
            "setup": deepcopy(physical.setup),
            "determinism_identity": self.determinism.identity(),
            "tolerances": self.determinism.tolerances(),
            "commands": commands,
            "collision": physical.collision.to_dict(),
            "collision_checksum": physical.setup["collision_pin"]["content_checksum"],
            "checkpoints": checkpoints,
            "measurement_series": measurements,
        }

    def mark_state_playback(self, trial_id):
        physical = self._require_physical(trial_id)
        physical.playback_only = True
        return self.build_replay_recipe(trial_id, capability="state_playback")

    @staticmethod
    def recompute(
        recipe,
        *,
        determinism=None,
        writer_factory=None,
        from_checkpoint_tick=None,
    ):
        if recipe.get("capability", "recompute") == "state_playback":
            return {
                "status": "refused",
                "reason_code": "state_playback_not_recompute",
                "recompute_supported": False,
            }
        collision_data = recipe.get("collision")
        if not collision_data:
            return {
                "status": "unsupported",
                "reason_code": "collision_data_missing",
                "recompute_supported": False,
            }
        try:
            collision = CollisionRepresentation.from_dict(collision_data)
            collision.validate()
        except CollisionContractError as error:
            return {
                "status": "unsupported",
                "reason_code": error.reason_code,
                "recompute_supported": False,
            }
        except (KeyError, TypeError, ValueError):
            return {
                "status": "unsupported",
                "reason_code": "collision_data_invalid",
                "recompute_supported": False,
            }
        if collision.checksum() != recipe.get("collision_checksum"):
            return {
                "status": "unsupported",
                "reason_code": "collision_checksum_mismatch",
                "recompute_supported": False,
            }
        determinism = determinism or ModelDeterminismDeclaration()
        if recipe["determinism_identity"] != determinism.identity():
            return {
                "status": "unsupported",
                "reason_code": "incompatible_model_version",
                "recompute_supported": False,
            }
        to_tick = recipe["to_tick"]
        for entry in recipe["commands"]:
            if entry["target_tick"] > to_tick:
                return {
                    "status": "refused",
                    "reason_code": "partial_trailing_command",
                    "recompute_supported": False,
                }
        setup = recipe["setup"]
        writer_factory = writer_factory or HeadlessBallWriter
        writer = writer_factory()
        streams = NamedRandomStreams(
            setup["seed"],
            determinism.seed_expansion_id,
            determinism.stream_layout_version,
        )
        mechanics = TrialMechanics(
            None, collision, determinism, RuntimeBudgetProfile()
        )
        if from_checkpoint_tick is not None:
            checkpoint = next(
                (
                    item
                    for item in recipe["checkpoints"]
                    if item["tick"] == from_checkpoint_tick
                ),
                None,
            )
            if checkpoint is None or not checkpoint.get("verified"):
                return {
                    "status": "refused",
                    "reason_code": "checkpoint_unverified",
                    "recompute_supported": False,
                }
            state = deepcopy(checkpoint["state"])
            start_tick = checkpoint["tick"]
        else:
            state = writer.setup(streams, setup["setup_parameters"], collision)
            start_tick = 0
        mechanics.state = state
        measurement_series = []
        for tick in range(start_tick + 1, to_tick + 1):
            commands = [
                entry
                for entry in recipe["commands"]
                if entry["target_tick"] == tick
            ]
            effects = []
            for entry in commands:
                effect = mechanics.command_effect(entry)
                if effect is None or isinstance(effect, tuple):
                    continue
                if effect.get("invalid"):
                    continue
                effects.append(effect)
            writer.advance(
                state,
                determinism.delta_time_s,
                collision,
                streams,
                effects,
                determinism.contact_iteration_bound,
            )
            energies = kinetic_energies(state["balls"])
            measurement_series.append(
                {
                    "tick": tick,
                    "k_total_j": energies["k_total_j"],
                    "k_thermal_j": energies["k_thermal_j"],
                }
            )
        expected = [
            item
            for item in recipe["measurement_series"]
            if item["tick"] > start_tick
        ]
        tolerance = recipe["tolerances"]["replay"]
        matches = len(measurement_series) == len(expected) and all(
            abs(record["k_total_j"] - item["k_total_j"])
            <= tolerance * max(1.0, abs(item["k_total_j"]))
            and abs(record["k_thermal_j"] - item["k_thermal_j"])
            <= tolerance * max(1.0, abs(item["k_thermal_j"]))
            for record, item in zip(measurement_series, expected)
        )
        return {
            "status": "recomputed",
            "final_tick": to_tick,
            "from_checkpoint_tick": start_tick if start_tick else None,
            "measurement_series": measurement_series,
            "measurements_match": matches,
            "recompute_supported": True,
        }

    def recompute_trial(
        self, trial_id, to_tick=None, *, from_checkpoint_tick=None, determinism=None
    ):
        physical = self._require_physical(trial_id)
        recipe = self.build_replay_recipe(trial_id, to_tick)
        result = self.recompute(
            recipe,
            determinism=determinism,
            writer_factory=self.writer_factory,
            from_checkpoint_tick=from_checkpoint_tick,
        )
        if not result.get("recompute_supported", False):
            physical.recompute_supported = False
            physical.recompute_reason = result.get("reason_code")
        return result

    def rescan_collision(self, representation):
        self._validate_collision_supply(representation)
        self.collision_supply = representation
        return {
            "adopted_representation_id": representation.representation_id,
            "existing_pins": {
                trial_id: deepcopy(physical.setup["collision_pin"])
                for trial_id, physical in self.physical.items()
            },
        }

    def notify_scene_event(self, event_id):
        self.scene_events.append({"event_id": event_id})
        result = {
            "event_recorded": event_id,
            "pins": {
                trial_id: deepcopy(physical.setup["collision_pin"])
                for trial_id, physical in self.physical.items()
            },
        }
        if event_id == "safety_boundary_lost" and self.live_trial_id is not None:
            physical = self.physical[self.live_trial_id]
            physical.hold = {"kind": "scene", "reason_code": "safety_boundary_lost"}
            self._emit_failure(
                "scene_permission",
                "safety_boundary_lost",
                affected_operation="live_advance",
                trial=physical,
                containment_result="explainable_pause_no_silent_swap",
                permitted_recovery_actions=["finish_and_rescan"],
            )
            result["paused_trial_id"] = physical.trial_id
        return result

    def recover_hold(self, trial_id, recovery_action):
        physical = self._require_physical(trial_id)
        hold = physical.hold
        if hold is None:
            return {"recovered": False, "reason_code": "no_hold"}
        if hold["kind"] == "performance" and recovery_action in (
            "reduce_tick_demand",
            "clear_performance_hold",
        ):
            physical.hold = None
            physical.pressure_streak = 0
            return {"recovered": True, "failure_fact_retained": True}
        return {
            "recovered": False,
            "reason_code": "recovery_action_not_permitted",
            "permitted": [
                report
                for report in self.failure_reports
                if report["trial_ref"] is not None
                and report["trial_ref"]["trial_id"] == trial_id
                and report["containment_result"]
            ][-1]["permitted_recovery_actions"]
            if any(
                report["trial_ref"] is not None
                and report["trial_ref"]["trial_id"] == trial_id
                for report in self.failure_reports
            )
            else [],
        }

    def controlled_comparison(self, trial_id, *, window_ticks=10):
        physical = self._require_physical(trial_id)
        kernel_trial = physical.kernel_trial
        delta = deepcopy(kernel_trial.configuration["source_input_delta"])
        if delta is None:
            raise RuntimeContractError("trial_not_derived")
        window = int(window_ticks)
        minimum = self.determinism.statistical_minimum_free_count
        samples = [
            sample
            for sample in physical.tick_measurements
            if sample["tick"] <= window
        ]
        actual_count = len(samples)
        settled = actual_count >= window
        sufficient = (
            actual_count > 0
            and samples[-1]["kinetic"]["free_count"] >= minimum
        )
        downstream = {}
        first_ball = next(
            iter(
                sorted(
                    physical.mechanics.state["balls"].values(),
                    key=lambda ball: ball["entity_id"],
                )
            ),
            None,
        )
        radius = first_ball["radius_m"] if first_ball is not None else None
        contacts_total = sum(
            sample["facts"]["pair_contacts"] for sample in samples
        )
        for effect_id in delta["downstream_effect_ids"]:
            if effect_id == "collision_cross_section":
                downstream[effect_id] = (
                    None
                    if radius is None
                    else {"value": float(radius) ** 2, "unit_id": "m2"}
                )
            elif effect_id == "mixing_rate":
                downstream[effect_id] = {
                    "value": contacts_total / window if window else 0.0,
                    "unit_id": "contacts_per_tick",
                }
            else:
                downstream[effect_id] = {"value": None, "unit_id": None}
        applicability = "valid" if settled and sufficient else "insufficient"
        record = {
            "record_kind": "controlled_comparison",
            "comparison_id": f"comparison-{trial_id}",
            "source_trial_ref": deepcopy(kernel_trial.identity["source_trial_ref"]),
            "input_delta": delta,
            "evidence_declaration": {
                "window_ticks": window,
                "convergence_criteria": "window_completed",
                "statistical_minimum_free_count": minimum,
                "error_tolerances": self.determinism.tolerances(),
            },
            "runtime_evidence": {
                "actual_sample_count": actual_count,
                "window": {
                    "start_tick": 1,
                    "end_tick": min(window, physical.completed_tick),
                },
                "applicability": applicability,
                "uncertainty": None if applicability == "valid" else "insufficient_window_or_sample",
                "sufficiency": sufficient,
                "downstream_effect_measurements": downstream,
            },
        }
        if applicability != "valid":
            record["no_conclusion_reason"] = "insufficient_window_or_sample"
        return record

    @staticmethod
    def project_comparison(record, projection):
        if projection not in {
            "trial_card",
            "snapshot",
            "validation_evidence",
            "explanation_citation",
        }:
            raise RuntimeContractError("unknown_comparison_projection")
        return {"projection": projection, "content": deepcopy(record)}

    def thermal_measurement(self, trial_id):
        physical = self._require_physical(trial_id)
        if not physical.tick_measurements:
            return {"applicability": "not_applicable", "reason_code": "no_samples"}
        return deepcopy(physical.tick_measurements[-1]["temperature"])
