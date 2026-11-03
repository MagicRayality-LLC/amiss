"""Frozen-writer solver-gate classifier for approved design 02 section 3.1.

This module classifies one or more device gate records written by the
development-only measurement harness. It computes no device result: it
recomputes the energy envelope from the recorded per-Tick kinetic-energy
series, compares the elasticity residuals with the tolerances each record
declares, and returns ``pass``, ``fail``, or ``incomplete``.

The gate criteria are fixed here, not read from the records:

- ``E_rel = max |E(t) - E(0)| / E(0) <= 0.001`` for every seed, over the
  60-second full run;
- at least three seeds at the design case (9 m3 effective volume, 72 balls);
- valid ball-ball and ball-boundary collision samples in every run;
- head-on two-ball, oblique two-ball, and stationary-boundary rebound
  momentum, kinetic-energy, and restitution residuals inside the declared
  episode tolerances;
- two render cadences producing the same measured outcome.

A record that declares a weaker energy tolerance than 02 section 3.1 is
never a pass. A run without valid collision samples is incomplete evidence,
never a pass. A failed criterion outranks missing evidence: the writer is
rejected even when the rest of the record is incomplete.
"""

from dataclasses import dataclass
import math

RECORD_VERSION = "1.0.0"
GATE_ENERGY_RELATIVE_TOLERANCE = 1e-3
GATE_MINIMUM_SEEDS = 3
GATE_FIXED_DELTA_TIME_S = 0.05
GATE_MODEL_SECONDS = 60.0
GATE_EXECUTED_TICKS = 1200
GATE_BALL_COUNT = 72
GATE_EFFECTIVE_VOLUME_M3 = 9.0
GATE_RENDER_CADENCE_TOLERANCE = 1e-9
GATE_REPORT_AGREEMENT_TOLERANCE = 1e-6
ELASTICITY_CASES = (
    "two_ball_head_on",
    "two_ball_oblique",
    "static_boundary_rebound",
)
ELASTICITY_TOLERANCE_KEYS = (
    "momentum_relative",
    "kinetic_energy_relative",
    "restitution_residual",
)

PASS = "pass"
FAIL = "fail"
INCOMPLETE = "incomplete"


@dataclass(frozen=True)
class GateClassification:
    """Outcome of the whole gate over every harvested run record."""

    status: str
    failures: tuple = ()
    incomplete: tuple = ()
    seeds: tuple = ()
    energy_relative: tuple = ()
    render_rate_delta: float | None = None

    @property
    def passed(self):
        return self.status == PASS

    def as_dict(self):
        return {
            "status": self.status,
            "failures": list(self.failures),
            "incomplete": list(self.incomplete),
            "seeds": list(self.seeds),
            "energy_relative": [
                {"seed_id": seed_id, "e_rel": value}
                for seed_id, value in self.energy_relative
            ],
            "render_rate_delta": self.render_rate_delta,
        }


def energy_relative_envelope(series_kt_j):
    """``max |K(t) - K(0)| / K(0)`` with the worst index.

    ``series_kt_j`` is the per-Tick total kinetic-energy series of one free
    run with zero gravity and no potential energy, so ``E = K_total``.
    """

    if not series_kt_j or len(series_kt_j) < 2:
        raise ValueError("energy_series_too_short")
    initial = series_kt_j[0]
    if not _finite(initial) or initial <= 0.0:
        raise ValueError("energy_initial_invalid")
    worst = 0.0
    worst_index = 0
    for index, value in enumerate(series_kt_j):
        if not _finite(value):
            raise ValueError("energy_sample_invalid")
        deviation = abs(value - initial) / initial
        if deviation > worst:
            worst = deviation
            worst_index = index
    return worst, worst_index


def momentum_delta(before, after):
    """Absolute change of total momentum in kg m/s.

    ``before`` and ``after`` are sequences of ``(mass_kg, (vx, vy, vz))``.
    """

    delta = [0.0, 0.0, 0.0]
    for (mass, velocity), (_, _) in zip(before, after):
        for axis in range(3):
            delta[axis] += mass * velocity[axis]
    for mass, velocity in after:
        for axis in range(3):
            delta[axis] -= mass * velocity[axis]
    return math.sqrt(sum(component * component for component in delta))


def momentum_scale(states):
    """``sum m |v|``; the natural denominator for a momentum residual."""

    return math.fsum(
        mass * math.sqrt(sum(component * component for component in velocity))
        for mass, velocity in states
    )


def kinetic_energy_relative_change(before, after):
    """``(K_after - K_before) / K_before`` for one contact."""

    initial = _kinetic(before)
    final = _kinetic(after)
    if initial <= 0.0:
        raise ValueError("kinetic_energy_initial_invalid")
    return (final - initial) / initial


def normal_restitution(pre_normal_velocity, post_normal_velocity):
    """``e = -v_n' / v_n`` for one contact along the contact normal."""

    if not _finite(pre_normal_velocity) or pre_normal_velocity == 0.0:
        raise ValueError("pre_contact_normal_velocity_invalid")
    if not _finite(post_normal_velocity):
        raise ValueError("post_contact_normal_velocity_invalid")
    return -post_normal_velocity / pre_normal_velocity


def restitution_residual(measured):
    """Distance from a perfectly elastic rebound."""

    if not _finite(measured):
        raise ValueError("restitution_invalid")
    return abs(measured - 1.0)


def classify_gate(records):
    """Classify every harvested run record as one gate outcome."""

    failures = []
    incomplete = []
    seeds = []
    energy_relative = []

    if not records:
        return GateClassification(
            status=INCOMPLETE, incomplete=("no_gate_records",)
        )

    cadences = {}
    for index, record in enumerate(records):
        record_failures, record_incomplete, seed_id, e_rel, cadence = (
            _classify_record(record, index)
        )
        failures.extend(record_failures)
        incomplete.extend(record_incomplete)
        if seed_id:
            seeds.append(seed_id)
            cadences.setdefault(seed_id, []).append((cadence, e_rel))
        if seed_id and _finite(e_rel):
            energy_relative.append((seed_id, e_rel))

    distinct = tuple(dict.fromkeys(seeds))
    if len(distinct) < GATE_MINIMUM_SEEDS:
        incomplete.append(
            f"fewer_than_{GATE_MINIMUM_SEEDS}_seeds:{len(distinct)}"
        )

    render_delta, render_incomplete = _render_rate_delta(cadences)
    incomplete.extend(render_incomplete)
    if render_delta is not None and render_delta > GATE_RENDER_CADENCE_TOLERANCE:
        failures.append(f"render_rate_changed_outcome:{render_delta!r}")

    if failures:
        status = FAIL
    elif incomplete:
        status = INCOMPLETE
    else:
        status = PASS
    return GateClassification(
        status=status,
        failures=tuple(failures),
        incomplete=tuple(incomplete),
        seeds=distinct,
        energy_relative=tuple(energy_relative),
        render_rate_delta=render_delta,
    )


def _classify_record(record, index):
    failures = []
    incomplete = []
    label = f"record{index}"
    if not isinstance(record, dict):
        return failures, [f"{label}:record_invalid"], None, None, None

    if record.get("record_version") != RECORD_VERSION:
        incomplete.append(f"{label}:record_version_unsupported")

    build = record.get("build") or {}
    if not isinstance(build, dict) or not build.get("application_version") or not (
        build.get("unity_version")
    ):
        incomplete.append(f"{label}:build_identity_missing")
    elif build.get("development_build") is not True:
        incomplete.append(f"{label}:not_a_development_harness_build")

    configuration = record.get("writer_configuration") or []
    if not isinstance(configuration, list) or len(configuration) < 20:
        incomplete.append(f"{label}:writer_configuration_record_incomplete")

    fixture = record.get("fixture") or {}
    if not isinstance(fixture, dict) or (
        fixture.get("ball_count") != GATE_BALL_COUNT
        or fixture.get("sizing_computed_n") != GATE_BALL_COUNT
        or not _close(fixture.get("effective_volume_m3"), GATE_EFFECTIVE_VOLUME_M3)
    ):
        incomplete.append(f"{label}:fixture_not_the_72_ball_design_case")

    seed = record.get("seed") or {}
    seed_id = seed.get("seed_id") if isinstance(seed, dict) else None
    if not isinstance(seed_id, str) or not seed_id:
        incomplete.append(f"{label}:seed_identity_missing")
        seed_id = None

    schedule = record.get("schedule") or {}
    cadence = schedule.get("ticks_per_frame") if isinstance(schedule, dict) else None
    if not isinstance(schedule, dict):
        schedule = {}
        incomplete.append(f"{label}:schedule_missing")
    if not _close(schedule.get("fixed_delta_time_s"), GATE_FIXED_DELTA_TIME_S):
        incomplete.append(f"{label}:fixed_delta_time_not_the_frozen_tick")
    if schedule.get("executed_ticks") != GATE_EXECUTED_TICKS:
        incomplete.append(
            f"{label}:run_did_not_complete_{GATE_EXECUTED_TICKS}_ticks"
        )
    if not _close(schedule.get("model_seconds"), GATE_MODEL_SECONDS):
        incomplete.append(f"{label}:model_duration_not_60_seconds")
    if schedule.get("interrupted") is not False:
        incomplete.append(f"{label}:run_interrupted")

    collisions = record.get("collisions") or {}
    ball_ball = collisions.get("ball_ball_samples")
    ball_boundary = collisions.get("ball_boundary_samples")
    if not _positive_int(ball_ball):
        incomplete.append(f"{label}:missing_collision_samples:ball_ball")
    if not _positive_int(ball_boundary):
        incomplete.append(f"{label}:missing_collision_samples:ball_boundary")

    tolerances = record.get("tolerances") or {}
    declared_energy = tolerances.get("energy_relative")
    if not _finite(declared_energy) or declared_energy <= 0.0:
        incomplete.append(f"{label}:energy_tolerance_missing")
    elif declared_energy > GATE_ENERGY_RELATIVE_TOLERANCE:
        incomplete.append(f"{label}:energy_tolerance_weakened")

    e_rel = None
    energy = record.get("energy") or {}
    series = energy.get("series_kt_j") if isinstance(energy, dict) else None
    try:
        recomputed, worst_index = energy_relative_envelope(series)
    except ValueError as error:
        incomplete.append(f"{label}:{error}")
    else:
        reported = energy.get("e_rel")
        if not _finite(reported):
            incomplete.append(f"{label}:reported_energy_envelope_missing")
        elif abs(reported - recomputed) > GATE_REPORT_AGREEMENT_TOLERANCE:
            incomplete.append(f"{label}:reported_energy_envelope_mismatch")
        if energy.get("worst_tick") != worst_index:
            incomplete.append(f"{label}:reported_worst_tick_mismatch")
        e_rel = recomputed
        tolerance = (
            declared_energy
            if _finite(declared_energy) and declared_energy > 0.0
            else GATE_ENERGY_RELATIVE_TOLERANCE
        )
        if recomputed > min(tolerance, GATE_ENERGY_RELATIVE_TOLERANCE):
            failures.append(
                f"{label}:energy_envelope_exceeded:{seed_id or 'unseeded'}:"
                f"{recomputed!r}"
            )

    elasticity_failures, elasticity_incomplete = _classify_elasticity(
        record.get("elasticity"), tolerances, label
    )
    failures.extend(elasticity_failures)
    incomplete.extend(elasticity_incomplete)
    return failures, incomplete, seed_id, e_rel, cadence


def _classify_elasticity(elasticity, tolerances, label):
    failures = []
    incomplete = []
    if not isinstance(elasticity, dict):
        return failures, [f"{label}:elasticity_record_missing"]
    for case_id in ELASTICITY_CASES:
        case = elasticity.get(case_id)
        if not isinstance(case, dict):
            incomplete.append(f"{label}:elasticity_case_missing:{case_id}")
            continue
        if case.get("contact_observed") is not True:
            incomplete.append(f"{label}:elasticity_no_contact:{case_id}")
            continue
        for key in ELASTICITY_TOLERANCE_KEYS:
            declared = (
                tolerances.get(f"elasticity_{key}")
                if tolerances.get("elasticity_tolerance_declared") is True
                else None
            )
            if not _finite(declared) or declared < 0.0:
                incomplete.append(
                    f"{label}:elasticity_tolerance_undeclared:{case_id}:{key}"
                )
                continue
            measured = case.get(_residual_key(key))
            if not _finite(measured):
                incomplete.append(f"{label}:elasticity_residual_missing:{case_id}:{key}")
                continue
            if measured > declared:
                failures.append(
                    f"{label}:elasticity_exceeded:{case_id}:{key}:{measured!r}"
                )
    return failures, incomplete


def _residual_key(key):
    return {
        "momentum_relative": "momentum_residual_relative",
        "kinetic_energy_relative": "kinetic_energy_residual_relative",
        "restitution_residual": "restitution_residual",
    }[key]


def _render_rate_delta(cadences):
    incomplete = []
    delta = None
    compared = 0
    for seed_id, runs in cadences.items():
        cadence_values = {cadence for cadence, _ in runs if cadence is not None}
        values = [e_rel for _, e_rel in runs if _finite(e_rel)]
        if len(cadence_values) < 2 or len(values) < 2:
            continue
        compared += 1
        spread = max(values) - min(values)
        delta = spread if delta is None else max(delta, spread)
    if compared == 0:
        incomplete.append("render_rate_comparison_missing")
    return delta, incomplete


def _kinetic(states):
    return math.fsum(
        0.5 * mass * sum(component * component for component in velocity)
        for mass, velocity in states
    )


def _finite(value):
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(value)
    )


def _positive_int(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0


def _close(value, expected, tolerance=1e-6):
    return _finite(value) and abs(value - expected) <= tolerance
