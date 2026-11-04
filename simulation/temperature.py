"""Pure temperature-episode model for approved design 07.

This module owns space/count sizing (07 section 2), one immutable
temperature measurement (07 section 3.2), and the Trial energy ledger
(07 section 4.1). It reads simulation views through the existing
``kinetic_energies`` and ``kinetic_temperature`` contracts and computes no
device result. Values that 07 section 11 leaves to a later measurement are
declared here with an uncalibrated label.
"""

from copy import deepcopy
from dataclasses import dataclass, replace
import math

from .runtime import DEFAULT_UNIT_CONVENTION, kinetic_energies, kinetic_temperature


UNCALIBRATED = "uncalibrated_declared_not_measured"


def _finite_number(value):
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
    )


@dataclass(frozen=True)
class TemperatureModelDeclaration:
    """Episode model parameters; 07 section 11 values stay candidates."""

    model_version: str = "1.0.0"
    calculation_version: str = "1.0.0"
    dof_formula: str = "3N_free"
    v_min_m3: float = 3.0
    target_number_density_per_m3: float = 8.0
    minimum_ball_count: int = 36
    maximum_ball_count: int = 72
    large_room_warning_m3: float = 12.0
    large_room_subspace_m3: float = 9.0
    evidence_status: str = UNCALIBRATED

    def __post_init__(self):
        for name in (
            "v_min_m3",
            "target_number_density_per_m3",
            "large_room_warning_m3",
            "large_room_subspace_m3",
        ):
            value = getattr(self, name)
            if not _finite_number(value) or value <= 0.0:
                raise ValueError(f"{name}_invalid")
        if self.minimum_ball_count <= 0:
            raise ValueError("minimum_ball_count_invalid")
        if not self.minimum_ball_count <= self.maximum_ball_count:
            raise ValueError("ball_count_bounds_invalid")
        if self.minimum_ball_count % 2 != 0 or self.maximum_ball_count % 2 != 0:
            raise ValueError("ball_count_bounds_must_be_even")
        if not self.large_room_subspace_m3 <= self.large_room_warning_m3:
            raise ValueError("large_room_subspace_invalid")


TEMPERATURE_MODEL = TemperatureModelDeclaration()


def even_ball_count(value):
    """``even`` from 07 section 2: move a rounded odd result down by one."""

    return value - 1 if value % 2 else value


@dataclass(frozen=True)
class SpaceSizing:
    """Immutable ``SpaceSizing`` record from 07 section 1.2."""

    status: str
    v_eff_m3: float
    v_min_m3: float
    furniture_deduction_mode: str
    target_number_density_per_m3: float
    computed_n: int | None
    is_opening_override_n: bool
    large_room_subspace_applied: bool
    large_room_warning: bool
    counted_volume_m3: float | None
    reason_code: str = ""

    @property
    def actual_number_density_per_m3(self):
        if self.computed_n is None or not self.v_eff_m3:
            return None
        return self.computed_n / self.v_eff_m3


def compute_space_sizing(
    v_eff_m3,
    *,
    model=None,
    furniture_deduction_mode="selected_bounding_volumes",
    is_opening_override_n=False,
    opening_ball_count=None,
    apply_large_room_subspace=False,
):
    """Compute ``N`` from an effective volume per 07 section 2.

    ``V_eff`` is the confirmed closed-region volume already reduced by the
    selected furniture bounding volumes. A below-minimum or invalid volume
    refuses with no balls. The large-room warning is reported and an explicit
    virtual subspace choice is recorded; it never changes ``V_eff`` itself.
    """

    model = model or TEMPERATURE_MODEL
    if not isinstance(furniture_deduction_mode, str) or not furniture_deduction_mode:
        raise ValueError("furniture_deduction_mode_missing")
    if not _finite_number(v_eff_m3):
        return SpaceSizing(
            status="refused",
            v_eff_m3=0.0,
            v_min_m3=model.v_min_m3,
            furniture_deduction_mode=furniture_deduction_mode,
            target_number_density_per_m3=model.target_number_density_per_m3,
            computed_n=None,
            is_opening_override_n=bool(is_opening_override_n),
            large_room_subspace_applied=False,
            large_room_warning=False,
            counted_volume_m3=None,
            reason_code="invalid_volume",
        )
    if v_eff_m3 < model.v_min_m3:
        return SpaceSizing(
            status="refused",
            v_eff_m3=float(v_eff_m3),
            v_min_m3=model.v_min_m3,
            furniture_deduction_mode=furniture_deduction_mode,
            target_number_density_per_m3=model.target_number_density_per_m3,
            computed_n=None,
            is_opening_override_n=bool(is_opening_override_n),
            large_room_subspace_applied=False,
            large_room_warning=False,
            counted_volume_m3=None,
            reason_code="below_minimum_volume",
        )

    large_room_warning = v_eff_m3 >= model.large_room_warning_m3
    counted_volume = v_eff_m3
    if large_room_warning and apply_large_room_subspace:
        counted_volume = min(v_eff_m3, model.large_room_subspace_m3)

    if is_opening_override_n:
        if (
            not isinstance(opening_ball_count, int)
            or isinstance(opening_ball_count, bool)
            or opening_ball_count <= 0
        ):
            raise ValueError("opening_ball_count_required")
        computed_n = opening_ball_count
    else:
        rounded = round(model.target_number_density_per_m3 * counted_volume)
        clamped = min(
            max(rounded, model.minimum_ball_count), model.maximum_ball_count
        )
        computed_n = even_ball_count(clamped)

    return SpaceSizing(
        status="valid",
        v_eff_m3=float(v_eff_m3),
        v_min_m3=model.v_min_m3,
        furniture_deduction_mode=furniture_deduction_mode,
        target_number_density_per_m3=model.target_number_density_per_m3,
        computed_n=computed_n,
        is_opening_override_n=bool(is_opening_override_n),
        large_room_subspace_applied=bool(
            large_room_warning and apply_large_room_subspace
        ),
        large_room_warning=large_room_warning,
        counted_volume_m3=float(counted_volume),
    )


@dataclass(frozen=True)
class MeasurementWindow:
    """Identity and extent of the declared measurement window."""

    window_id: str
    start_tick: int
    end_tick: int
    duration_seconds: float
    sample_count: int

    def __post_init__(self):
        if not self.window_id:
            raise ValueError("window_id_missing")
        if self.start_tick < 0 or self.end_tick < self.start_tick:
            raise ValueError("window_ticks_invalid")
        if not _finite_number(self.duration_seconds) or self.duration_seconds < 0.0:
            raise ValueError("window_duration_invalid")
        if self.sample_count < 0:
            raise ValueError("window_sample_count_invalid")


@dataclass(frozen=True)
class TemperatureDisplayTuple:
    """Producer-owned display tuple from 07 section 7.1; K is primary."""

    kelvin: int
    celsius: int
    fahrenheit: int
    kelvin_ratio_baseline: float | None


def _display_tuple(t_k, initial_t_k):
    # Display ratio is T_K / T_K_initial; lead decision pending founder confirmation.
    t_c = t_k - 273.15
    t_f = t_c * 9.0 / 5.0 + 32.0
    ratio = (
        None
        if initial_t_k is None or initial_t_k <= 0.0
        else round(t_k / initial_t_k, 1)
    )
    return TemperatureDisplayTuple(
        kelvin=int(round(t_k)),
        celsius=int(round(t_c)),
        fahrenheit=int(round(t_f)),
        kelvin_ratio_baseline=ratio,
    )


@dataclass(frozen=True)
class TemperatureMeasurement:
    """One immutable measurement from the free-ball set (07 section 3.2)."""

    measurement_id: str
    window: MeasurementWindow
    status: str
    n_total: int
    n_free: int
    n_held: int
    k_total_j: float
    k_thermal_j: float
    k_drift_j: float
    k_held_j: float
    v_cm_m_per_s: tuple
    mean_speed_m_per_s: float
    v_rms_m_per_s: float
    t_k: float | None
    initial_t_k: float | None
    equilibrium_sufficient: bool
    display_tuple: TemperatureDisplayTuple | None
    dof_formula: str
    statistical_minimum_free_count: int
    calculation_version: str
    model_version: str

    def __post_init__(self):
        if self.status not in {"valid", "not_applicable"}:
            raise ValueError("measurement_status_invalid")
        if self.n_total != self.n_free + self.n_held:
            raise ValueError("measurement_counts_inconsistent")
        if self.status == "valid":
            if self.t_k is None or self.display_tuple is None:
                raise ValueError("valid_measurement_missing_temperature")
            if self.display_tuple.kelvin_ratio_baseline is None:
                raise ValueError("valid_measurement_missing_baseline_ratio")
        elif self.t_k is not None or self.display_tuple is not None:
            raise ValueError("not_applicable_measurement_has_temperature")
        if self.equilibrium_sufficient and (
            self.status != "valid" or self.window.sample_count < 2
        ):
            raise ValueError("equilibrium_requires_a_window")


def measure_temperature(
    balls,
    *,
    measurement_id,
    window,
    statistical_minimum_free_count,
    unit_convention=None,
    model=None,
    initial_t_k=None,
    equilibrium_sufficient=False,
):
    """Build one measurement from one immutable free-ball view.

    ``kinetic_energies`` performs the centered direct sum; a single-precision
    ``K_total - K_drift`` subtraction is never used. Below the statistical
    minimum, including zero and one free ball, the status is
    ``not_applicable`` and every temperature and display field is null.
    """

    model = model or TEMPERATURE_MODEL
    unit_convention = unit_convention or DEFAULT_UNIT_CONVENTION
    energies = kinetic_energies(balls)
    temperature = kinetic_temperature(
        energies, unit_convention, statistical_minimum_free_count
    )
    status = temperature["applicability"]
    t_k = temperature["value"] if status == "valid" else None
    if status == "valid" and (
        not _finite_number(initial_t_k) or initial_t_k <= 0.0
    ):
        raise ValueError("initial_temperature_required")

    free = [ball for ball in balls.values() if ball["membership"] == "free"]
    speeds = [
        math.sqrt(math.fsum(component * component for component in ball["velocity_m_per_s"]))
        for ball in free
    ]
    mean_speed = math.fsum(speeds) / len(speeds) if speeds else 0.0
    v_rms = (
        math.sqrt(2.0 * energies["k_total_j"] / energies["total_mass_kg"])
        if energies["total_mass_kg"] > 0.0
        else 0.0
    )

    return TemperatureMeasurement(
        measurement_id=measurement_id,
        window=window,
        status=status,
        n_total=energies["free_count"] + energies["held_count"],
        n_free=energies["free_count"],
        n_held=energies["held_count"],
        k_total_j=energies["k_total_j"],
        k_thermal_j=energies["k_thermal_j"],
        k_drift_j=energies["k_drift_j"],
        k_held_j=energies["k_held_j"],
        v_cm_m_per_s=tuple(energies["v_cm_m_per_s"]),
        mean_speed_m_per_s=mean_speed,
        v_rms_m_per_s=v_rms,
        t_k=t_k,
        initial_t_k=initial_t_k,
        equilibrium_sufficient=bool(equilibrium_sufficient)
        and status == "valid"
        and window.sample_count >= 2,
        display_tuple=_display_tuple(t_k, initial_t_k) if t_k is not None else None,
        dof_formula=model.dof_formula,
        statistical_minimum_free_count=statistical_minimum_free_count,
        calculation_version=model.calculation_version,
        model_version=model.model_version,
    )


@dataclass(frozen=True)
class EnergyContactEntry:
    """One closed external prop contact with signed measured work."""

    contact_id: str
    prop_id: str
    target_ball_id: str
    start_tick: int
    end_tick: int
    work_j: float
    closure_reason: str

    def __post_init__(self):
        for name in ("contact_id", "prop_id", "target_ball_id", "closure_reason"):
            if not getattr(self, name):
                raise ValueError(f"{name}_missing")
        if self.start_tick < 0 or self.end_tick < self.start_tick:
            raise ValueError("contact_ticks_invalid")
        if not _finite_number(self.work_j):
            raise ValueError("contact_work_invalid")


@dataclass(frozen=True)
class TemperatureEnergyLedger:
    """Trial-local energy accounts from 07 section 4.1.

    ``free_total_ke_j`` is the measured free-ball ``K_total``. The
    ``contacts`` tuple holds external work events only, so
    ``work_events_cumulative_ke_j`` is their signed algebraic sum. Sandbag
    absorption is the separate cumulative ``e_sandbag_j`` account, and a
    derived Trial's initial-energy difference is
    ``setup_energy_diff_from_source_j``, never a work event.
    """

    initial_total_ke_j: float
    free_total_ke_j: float
    k_thermal_j: float
    k_drift_j: float
    k_held_j: float = 0.0
    e_sandbag_j: float = 0.0
    contacts: tuple = ()
    setup_energy_diff_from_source_j: float | None = None
    numerical_diagnostics: tuple = ()

    def __post_init__(self):
        for name in (
            "initial_total_ke_j",
            "free_total_ke_j",
            "k_thermal_j",
            "k_drift_j",
            "k_held_j",
            "e_sandbag_j",
        ):
            value = getattr(self, name)
            if not _finite_number(value) or value < 0.0:
                raise ValueError(f"{name}_invalid")
        if self.setup_energy_diff_from_source_j is not None and not _finite_number(
            self.setup_energy_diff_from_source_j
        ):
            raise ValueError("setup_energy_diff_invalid")
        for entry in self.contacts:
            if not isinstance(entry, EnergyContactEntry):
                raise ValueError("contact_entry_invalid")

    @property
    def k_total_j(self):
        return self.k_thermal_j + self.k_drift_j

    @property
    def e_accounted_j(self):
        return self.k_thermal_j + self.k_drift_j + self.k_held_j + self.e_sandbag_j

    @property
    def work_events_cumulative_ke_j(self):
        return math.fsum(entry.work_j for entry in self.contacts)

    @property
    def closure_residual_j(self):
        return self.free_total_ke_j - self.k_total_j

    def with_contact(self, entry):
        if not isinstance(entry, EnergyContactEntry):
            raise ValueError("contact_entry_invalid")
        return replace(self, contacts=self.contacts + (entry,))


# Teacher-operated distribution parameters (founder decision 2026-11-04,
# option A, distribution-preserving). The teacher sets group-level scalars
# only — count, uniform mass, random-component speed scale, and one drift
# vector — and local deterministic sampling produces the balls. Every key is
# a scalar, so the public ``TargetParameters`` scalar-only contract, the
# one-input delta, and the per-object state-vector rejection all stand.
# ``teacher-controlled-group`` is a hackathon-build episode parametrization
# recorded in ``private/design/next.md``; it does not rewrite the 07 section
# 5.1 families. Declared bounds are candidates (``UNCALIBRATED``): count
# spans the single opening ball to the model maximum, speeds and drift span
# the device held-input limit, and uniform mass spans quarter to four times
# the engine ball, covering the P1 1x/4x contrast without per-ball masses.

TEACHER_GROUP_FAMILY_ID = "teacher-controlled-group"

TEACHER_GROUP_DEFAULT_PARAMETERS = {
    "ball_count": 8,
    "initial_speed_scale": 1.0,
    "ball_mass_kg": 1.0,
    "drift_vx_m_per_s": 0.0,
    "drift_vy_m_per_s": 0.0,
    "drift_vz_m_per_s": 0.0,
}

TEACHER_GROUP_CONTROLLABLE_INPUTS = {
    "ball_count": {"minimum": 1, "maximum": 72},
    "initial_speed_scale": {"minimum": 0.0, "maximum": 6.0},
    "ball_mass_kg": {"minimum": 0.25, "maximum": 4.0},
    "drift_vx_m_per_s": {"minimum": -6.0, "maximum": 6.0},
    "drift_vy_m_per_s": {"minimum": -6.0, "maximum": 6.0},
    "drift_vz_m_per_s": {"minimum": -6.0, "maximum": 6.0},
}

TEACHER_GROUP_DOWNSTREAM_EFFECT_IDS = ("initial-conditions",)

TEACHER_GROUP_INVARIANT_IDS = ("finite-state",)


def teacher_group_declarations():
    """Tool declarations for the teacher's create/derive proposals.

    Both operations share one setup family, one closed scalar parameter set,
    and one downstream effect. Action ids match the public stage-data
    catalogue so guide tokens admit them unchanged.
    """
    from .validator import ToolDeclaration

    return [
        ToolDeclaration(
            action_id=f"action-trial.propose-{operation}",
            tool="trial.propose",
            operation=operation,
            setup_family_id=TEACHER_GROUP_FAMILY_ID,
            controllable_inputs=deepcopy(TEACHER_GROUP_CONTROLLABLE_INPUTS),
            default_parameters=dict(TEACHER_GROUP_DEFAULT_PARAMETERS),
            downstream_effect_ids=TEACHER_GROUP_DOWNSTREAM_EFFECT_IDS,
            invariant_ids=TEACHER_GROUP_INVARIANT_IDS,
        )
        for operation in ("create", "derive")
    ]
