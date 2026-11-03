#if DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Amiss.Shell.Episodes;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// Development-only measurement harness for the scientific writer of
    /// approved design 02 section 3.1. It runs whichever writer the runtime
    /// binds — after T4B that is the custom hard-sphere writer — at the
    /// sizing-derived ball count of a declared test volume, three seeds by 60
    /// uninterrupted model seconds, plus the two-ball and static-boundary
    /// elasticity cases, and writes one JSON gate record per run. The rejected
    /// frozen Unity configuration is measured beside it as an archived
    /// comparison; it is never bound to the Trial.
    ///
    /// No product path reaches this file: it is compiled only into
    /// DEVELOPMENT_BUILD, it starts only when a request file is present, and
    /// it only reads published views. It never writes ball state, changes a
    /// tolerance, or alters a reading.
    /// </summary>
    public sealed class SolverGateHarness : MonoBehaviour
    {
        public const string RequestFileName = "solver-gate-request.txt";
        public const string CompleteFileName = "solver-gate-complete.txt";
        public const string RecordVersion = "1.0.0";
        public const string FixtureLabel = "design_table_E-07-01_9m3_72_ball_case";
        const float FixedDelta = FrozenWriterConfiguration.FixedDeltaTimeSeconds;
        const float GateModelSeconds = 60f;
        const int GateTicks = 1200;
        const float WallBudgetSeconds = 240f;
        const int ElasticityTicks = 60;

        sealed class RunPlan
        {
            public string SeedId;
            public string TrialId;
            public int TicksPerFrame;
        }

        readonly RunPlan[] plan =
        {
            new RunPlan { SeedId = "seed-a", TrialId = "solver-gate-a", TicksPerFrame = 1 },
            new RunPlan { SeedId = "seed-b", TrialId = "solver-gate-b", TicksPerFrame = 1 },
            new RunPlan { SeedId = "seed-c", TrialId = "solver-gate-c", TicksPerFrame = 1 },
            new RunPlan { SeedId = "seed-a", TrialId = "solver-gate-a", TicksPerFrame = 2 }
        };

        readonly List<string> written = new();
        bool running;

        /// <summary>
        /// The declared 9 m3 test volume, lifted clear of the room and of any
        /// presentation collider so the measurement contains only the frozen
        /// writer's own bodies. The collider census in each record proves it.
        /// </summary>
        static Bounds FixtureRegion => new(new Vector3(0f, 101.125f, 0f),
            new Vector3(2f, 2.25f, 2f));

        static Vector3 FixtureUserPosition => new(0f, 100f, 0f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            var host = new GameObject("AmissSolverGateHarness");
            host.AddComponent<SolverGateHarness>();
            DontDestroyOnLoad(host);
        }

        void Start()
        {
            StartCoroutine(WaitForRequest());
        }

        IEnumerator WaitForRequest()
        {
            while (!running)
            {
                if (File.Exists(RequestPath))
                {
                    running = true;
                    yield return RunAll();
                    yield break;
                }
                yield return new WaitForSeconds(1f);
            }
        }

        static string RequestPath =>
            Path.Combine(Application.persistentDataPath, RequestFileName);

        static string CompletePath =>
            Path.Combine(Application.persistentDataPath, CompleteFileName);

        IEnumerator RunAll()
        {
            Debug.Log("[Amiss] solver-gate: start");
            var services = FindFirstObjectByType<ShellServices>();
            var runtime = services?.SimulationRuntime as DeviceSimulationRuntime;
            var controller = services?.Room;
            if (runtime == null || controller == null)
            {
                Finish("harness_failed:shell_services_missing");
                yield break;
            }

            var driver = services.GetComponent<SimulationTickDriver>();
            if (driver != null)
                driver.enabled = false;
            var presentation = new BallPresentation();
            yield return null;

            if (!PrepareFixture(controller, out var fixtureReason))
            {
                Finish("harness_failed:" + fixtureReason);
                yield break;
            }

            var confirmation = controller.Preparation.Confirmation;
            foreach (var run in plan)
            {
                yield return RunSeed(runtime, controller, presentation, confirmation,
                    run.SeedId, run.TrialId, run.TicksPerFrame);
            }

            presentation.Clear();
            if (driver != null)
                driver.enabled = true;
            Finish("complete:" + written.Count.ToString(CultureInfo.InvariantCulture));
        }

        void Finish(string outcome)
        {
            Debug.Log("[Amiss] solver-gate: " + outcome);
            try
            {
                File.WriteAllText(CompletePath, outcome + "\n" + string.Join("\n", written));
            }
            catch (Exception error)
            {
                Debug.LogError("[Amiss] solver-gate: marker write failed " + error.Message);
            }
            running = false;
        }

        static bool PrepareFixture(RoomPreparationController controller, out string reason)
        {
            reason = "";
            var preparation = controller.Preparation;
            var recovery = controller.Recovery;
            var id = preparation.Begin(TemperatureModelDeclaration.VMinM3);
            if (!preparation.RequestPermission(id))
            {
                reason = "permission_refused";
                return false;
            }
            preparation.CompletePermission(id, true);
            var draft = RoomDraft.CreateManual(FixtureRegion, FixtureUserPosition,
                Vector3.forward);
            preparation.CompleteSceneLoad(id, draft);
            if (!preparation.Confirm(id))
            {
                reason = "confirm_refused:" + preparation.Failure;
                return false;
            }
            if (!recovery.ConfirmRoom(preparation))
            {
                reason = "recovery_confirm_refused";
                return false;
            }
            return true;
        }

        IEnumerator RunSeed(DeviceSimulationRuntime runtime, RoomPreparationController controller,
            BallPresentation presentation, RoomConfirmation confirmation, string seedId,
            string trialId, int ticksPerFrame)
        {
            runtime.StopTrial("solver_gate_reset");
            yield return null;

            var recovery = controller.Recovery;
            var pin = recovery.AcceptTrialSetup(trialId, confirmation.CollisionChecksum);
            if (pin == null)
            {
                Finish("harness_failed:collision_pin_refused:" + trialId);
                yield break;
            }

            measuredElasticity = new SolverGateElasticity();
            archivedElasticity = new SolverGateElasticity();
            yield return MeasureElasticity(confirmation, pin, () => runtime.CreateWriter(),
                measuredElasticity);
            yield return MeasureElasticity(confirmation, pin, () => new UnityRigidbodyWriter(),
                archivedElasticity);
            var elasticity = measuredElasticity;
            var request = TemperatureTrialSetup.CreateTrialStart(trialId, confirmation, pin,
                out var sizing);
            var outcome = runtime.TryStartTrial(request);
            var record = new SolverGateRecord
            {
                record_version = RecordVersion,
                run_id = RunId(seedId, ticksPerFrame),
                generated_utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                writer_configuration_id = outcome?.WriterConfigurationId ??
                                          CustomHardSphereWriter.ConfigurationIdValue,
                writer_disposition = WriterDisposition.ProductTrial.ToString(),
                archived_writer_configuration_id =
                    FrozenWriterConfiguration.ArchivedUnityConfigurationId,
                writer_configuration = ToArray(outcome?.WriterConfigurationRecord),
                build = BuildIdentityRecord(),
                fixture = Fixture(confirmation, sizing, outcome),
                seed = new SolverGateSeed
                {
                    seed_id = seedId,
                    trial_id = trialId,
                    root_seed = request.RootSeed.ToString(CultureInfo.InvariantCulture),
                    seed_stream_id = request.SeedStreamId
                },
                schedule = new SolverGateSchedule
                {
                    fixed_delta_time_s = FixedDelta,
                    ticks_per_frame = ticksPerFrame,
                    execution = "harness_fixed_tick"
                },
                elasticity = elasticity,
                archived_elasticity = archivedElasticity,
                tolerances = Tolerances()
            };

            if (!outcome.Started)
            {
                record.schedule.interrupted = true;
                record.schedule.interruption_reason = "trial_start_refused:" + outcome.ReasonCode;
                record.energy = new SolverGateEnergy { status = "not_measured" };
                record.collisions = new SolverGateCollisions();
                Write(record);
                yield break;
            }

            record.fixture.collider_census = ColliderCensus(FixtureRegion.center, 4f);
            record.collisions = new SolverGateCollisions
            {
                spawn_contact_overlaps = SpawnOverlaps(pin, runtime.CurrentView)
            };
            var probes = AttachProbes(runtime, trialId);
            var factWriter = runtime.WriterBindingFor(trialId)?.Writer as IContactFactWriter;
            var tracker = new SolverGateContactTracker();
            var startWall = Time.realtimeSinceStartup;
            var series = new List<double>(GateTicks + 1) { Kinetic(runtime.CurrentView) };
            var executed = 0;
            var frames = 0;
            var paused = 0;
            var interrupted = false;
            var interruptionReason = "";
            var writerWallContacts = 0;
            var writerPairContacts = 0;
            var writerBoundExhausted = false;

            while (executed < GateTicks)
            {
                if (runtime.PauseRequested || !recovery.Focused || !recovery.SafetyValid)
                {
                    interrupted = true;
                    interruptionReason = runtime.PauseRequested
                        ? "runtime_paused"
                        : !recovery.Focused ? "focus_lost" : "safety_hold";
                    break;
                }
                for (var slice = 0; slice < ticksPerFrame && executed < GateTicks; slice++)
                {
                    SolverGateContactProbe.CurrentTick = executed;
                    var step = runtime.Step(1);
                    if (factWriter != null)
                    {
                        var facts = factWriter.LastContactFacts;
                        writerWallContacts += facts.WallContacts;
                        writerPairContacts += facts.PairContacts;
                        writerBoundExhausted |= facts.IterationBoundExhausted;
                    }
                    if (step.Executed != 1)
                    {
                        paused++;
                        interrupted = true;
                        interruptionReason = "tick_not_executed";
                        break;
                    }
                    executed++;
                    series.Add(Kinetic(runtime.CurrentView));
                    tracker.Sample(runtime.CurrentView.Balls,
                        pin.Representation.Surfaces, executed);
                }
                frames++;
                presentation.Sync(runtime.PreviousView, runtime.CurrentView, 1f);
                if (Time.realtimeSinceStartup - startWall > WallBudgetSeconds)
                {
                    interrupted = true;
                    interruptionReason = "wall_budget_exhausted";
                    break;
                }
                if (interrupted)
                    break;
                yield return null;
            }

            var corroboration = GatherProbes(probes);
            var collisions = new SolverGateCollisions
            {
                ball_ball_samples = tracker.BallBallSamples,
                ball_boundary_samples = tracker.BallBoundarySamples,
                contact_ticks = tracker.ContactTicks.Count,
                probe_ball_ball_samples = corroboration.ball_ball_samples,
                probe_ball_boundary_samples = corroboration.ball_boundary_samples,
                spawn_contact_overlaps = record.collisions.spawn_contact_overlaps,
                writer_wall_contacts = writerWallContacts,
                writer_pair_contacts = writerPairContacts,
                iteration_bound_exhausted = writerBoundExhausted,
                physx_ball_bodies = PhysxBallBodies()
            };
            var wallSeconds = Time.realtimeSinceStartup - startWall;
            runtime.StopTrial("solver_gate_complete");

            record.collisions = collisions;
            record.schedule.frames = frames;
            record.schedule.executed_ticks = executed;
            record.schedule.model_seconds = executed / 20d;
            record.schedule.wall_seconds = wallSeconds;
            record.schedule.paused_ticks = paused;
            record.schedule.interrupted = interrupted;
            record.schedule.interruption_reason = interruptionReason;
            record.energy = Energy(series, executed);
            Write(record);
            yield return null;
        }

        static string RunId(string seedId, int ticksPerFrame) =>
            seedId + "-" + ticksPerFrame.ToString(CultureInfo.InvariantCulture) + "tpf";

        static SolverGateBuildIdentity BuildIdentityRecord() => new()
        {
            application_version = Application.version,
            unity_version = Application.unityVersion,
            development_build = Debug.isDebugBuild,
            device_model = SystemInfo.deviceModel,
            operating_system = SystemInfo.operatingSystem,
            product_name = BuildIdentity.ProductName
        };

        static SolverGateFixture Fixture(RoomConfirmation confirmation, SpaceSizing sizing,
            TrialStartOutcome outcome) => new()
        {
            label = FixtureLabel,
            effective_volume_m3 = confirmation?.EffectiveVolumeM3 ?? 0f,
            region_size_m = confirmation == null
                ? Array.Empty<float>()
                : new[] { confirmation.Region.size.x, confirmation.Region.size.y,
                    confirmation.Region.size.z },
            region_center_m = confirmation == null
                ? Array.Empty<float>()
                : new[] { confirmation.Region.center.x, confirmation.Region.center.y,
                    confirmation.Region.center.z },
            boundary_label = confirmation?.BoundaryLabel ?? "",
            collision_representation_id = outcome?.CollisionRepresentationId ?? "",
            collision_checksum = outcome?.CollisionChecksum ?? "",
            collision_format_version = CollisionRepresentation.CurrentFormatVersion,
            ball_count = outcome?.BallCount ?? 0,
            sizing_computed_n = sizing?.ComputedN ?? 0,
            sizing_status = sizing?.Status ?? "",
            sizing_is_opening_override = sizing?.IsOpeningOverrideN ?? false
        };

        /// <summary>
        /// The declared tolerance set. 02 section 3.1 fixes the energy envelope
        /// at 0.1 %. The elasticity, momentum, and restitution tolerance ids
        /// required by 02 section 2.3 and 07 section 11 have no declared value
        /// in any approved artefact, so they are recorded as undeclared instead
        /// of being invented here.
        /// </summary>
        static SolverGateTolerances Tolerances() => new()
        {
            energy_relative = 0.001,
            energy_relative_source = "design_02_section_3_1",
            elasticity_tolerance_id = "episode_declared_elasticity_tolerance",
            elasticity_tolerance_declared = false,
            elasticity_momentum_relative = -1d,
            elasticity_kinetic_energy_relative = -1d,
            elasticity_restitution_residual = -1d,
            elasticity_tolerance_source = "not_declared_in_approved_artefacts"
        };

        List<SolverGateContactProbe> AttachProbes(DeviceSimulationRuntime runtime, string trialId)
        {
            var probes = new List<SolverGateContactProbe>();
            if (!runtime.HasTrial)
                return probes;
            var root = "AmissSimulationTrial-" + trialId;
            foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (body.GetComponent<SphereCollider>() == null ||
                    body.transform.root.name != root)
                    continue;
                probes.Add(body.gameObject.AddComponent<SolverGateContactProbe>());
            }
            return probes;
        }

        static SolverGateCollisions GatherProbes(List<SolverGateContactProbe> probes)
        {
            var collisions = new SolverGateCollisions();
            var ticks = new HashSet<int>();
            foreach (var probe in probes)
            {
                if (probe == null)
                    continue;
                collisions.ball_ball_samples += probe.BallBall;
                collisions.ball_boundary_samples += probe.BallBoundary;
                ticks.UnionWith(probe.ContactTicks);
                Destroy(probe);
            }
            collisions.contact_ticks = ticks.Count;
            return collisions;
        }

        static int SpawnOverlaps(CollisionPin pin, TrialStateView view)
        {
            if (pin?.Representation == null || view == null)
                return 0;
            var reach = FrozenWriterConfiguration.BallRadiusM -
                        FrozenWriterConfiguration.ContactOffsetM;
            var limit = reach * reach;
            var count = 0;
            foreach (var ball in view.Balls)
            {
                foreach (var surface in pin.Representation.Surfaces)
                    if (surface.WorldBounds.SqrDistance(ball.PositionM) < limit)
                    {
                        count++;
                        break;
                    }
            }
            return count;
        }

        static double Kinetic(TrialStateView view)
        {
            if (view == null)
                return 0d;
            var total = 0d;
            foreach (var ball in view.Balls)
            {
                double x = ball.VelocityMPerS.x;
                double y = ball.VelocityMPerS.y;
                double z = ball.VelocityMPerS.z;
                total += 0.5 * ball.MassKg * (x * x + y * y + z * z);
            }
            return total;
        }

        static SolverGateEnergy Energy(List<double> series, int executed)
        {
            var energy = new SolverGateEnergy
            {
                status = "not_measured",
                samples = series.Count,
                series_kt_j = series.ToArray()
            };
            if (series.Count < 2 || !(series[0] > 0d))
                return energy;
            energy.status = "valid";
            energy.k_initial_j = series[0];
            var min = double.MaxValue;
            var max = double.MinValue;
            var worst = 0d;
            var worstTick = 0;
            var windows = new List<SolverGateEnergyWindow>();
            for (var index = 0; index < series.Count; index++)
            {
                var value = series[index];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
                var deviation = Math.Abs(value - series[0]) / series[0];
                if (deviation > worst)
                {
                    worst = deviation;
                    worstTick = index;
                }
                if (index % 200 == 0 && index > 0)
                    windows.Add(new SolverGateEnergyWindow
                    {
                        tick = index,
                        model_seconds = index / 20d,
                        e_rel = deviation
                    });
            }
            energy.k_min_j = min;
            energy.k_max_j = max;
            energy.e_rel = worst;
            energy.worst_tick = worstTick;
            energy.windows = windows.ToArray();
            return energy;
        }

        SolverGateElasticity measuredElasticity;
        SolverGateElasticity archivedElasticity;

        IEnumerator MeasureElasticity(RoomConfirmation confirmation, CollisionPin pin,
            Func<IScientificWriter> writerFactory, SolverGateElasticity target)
        {
            yield return null;
            if (!CollisionBridge.TryBind(confirmation, pin, out var pinned, out _))
                yield break;
            var center = confirmation.Region.center;
            // The two-ball fixtures use the discrete-writer parity geometry
            // (client CustomHardSphereWriterTests / simulation
            // HardSphereOracleParityTestCase): ±0.28 m spacing so the fixed
            // 0.05 s step never lands both centres on one point (an exact
            // centre coincidence is skipped by the declared contact rule), and
            // a ±0.03 m oblique offset so the pair genuinely contacts instead
            // of grazing the exact 0.10 m reach. Both writers - measured and
            // archived - run the same fixtures.
            target.two_ball_head_on = TwoBallCase(pinned, writerFactory, "two_ball_head_on",
                center + new Vector3(-0.28f, 0f, 0f), new Vector3(1f, 0f, 0f),
                center + new Vector3(0.28f, 0f, 0f), new Vector3(-1f, 0f, 0f));
            yield return null;
            target.two_ball_oblique = TwoBallCase(pinned, writerFactory, "two_ball_oblique",
                center + new Vector3(-0.28f, 0f, -0.03f), new Vector3(1f, 0f, 0f),
                center + new Vector3(0.28f, 0f, 0.03f), new Vector3(-1f, 0f, 0f));
            yield return null;
            target.static_boundary_rebound = BoundaryCase(pinned, writerFactory,
                center + new Vector3(-0.20f, 0f, 0f));
            yield return null;
        }

        sealed class Snapshot
        {
            public Vector3[] Positions;
            public Vector3[] Velocities;
        }

        static List<Snapshot> RunFixture(PinnedCollision collision,
            Func<IScientificWriter> writerFactory, string caseId,
            Vector3[] positions, Vector3[] velocities, SolverGateElasticityCase record)
        {
            var balls = new BallState[positions.Length];
            for (var index = 0; index < positions.Length; index++)
                balls[index] = new BallState(
                    "ball-" + (index + 1).ToString("D4", CultureInfo.InvariantCulture),
                    positions[index], velocities[index],
                    FrozenWriterConfiguration.BallMassKg,
                    FrozenWriterConfiguration.BallRadiusM);
            var configuration = new FrozenWriterConfiguration(collision.RepresentationId,
                collision.Checksum, collision.FormatVersion, 0, "elasticity-fixture");
            var writer = writerFactory?.Invoke();
            var trace = new List<Snapshot>(ElasticityTicks + 1) { Capture(balls) };
            if (writer == null)
                return trace;
            try
            {
                writer.Setup(new SimulationTrialSetup("solver-gate-" + caseId, balls,
                    collision, configuration));
                record.writer_configuration_id = writer.ConfigurationId;
                record.gravity_m_per_s2 = new[] { Physics.gravity.x, Physics.gravity.y,
                    Physics.gravity.z };
                record.simulation_mode = Physics.simulationMode.ToString();
                record.scene_colliders = FindObjectsByType<Collider>(
                    FindObjectsSortMode.None).Length;
                record.nearby_colliders = ColliderCensus(FixtureRegion.center, 3f);
                for (var tick = 1; tick <= ElasticityTicks; tick++)
                {
                    writer.Advance(tick, FixedDelta);
                    trace.Add(Capture(balls));
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Amiss] solver-gate elasticity " + caseId + ": " +
                                 error.Message);
                return trace;
            }
            finally
            {
                writer.Teardown();
            }
            return trace;
        }

        static Snapshot Capture(IReadOnlyList<BallState> balls)
        {
            var snapshot = new Snapshot
            {
                Positions = new Vector3[balls.Count],
                Velocities = new Vector3[balls.Count]
            };
            for (var index = 0; index < balls.Count; index++)
            {
                snapshot.Positions[index] = balls[index].PositionM;
                snapshot.Velocities[index] = balls[index].VelocityMPerS;
            }
            return snapshot;
        }

        SolverGateElasticityCase TwoBallCase(PinnedCollision collision,
            Func<IScientificWriter> writerFactory, string caseId,
            Vector3 positionA, Vector3 velocityA, Vector3 positionB, Vector3 velocityB)
        {
            var record = new SolverGateElasticityCase
            {
                case_id = caseId,
                ball_count = 2,
                momentum_basis = "pair_total_momentum"
            };
            var trace = RunFixture(collision, writerFactory, caseId,
                new[] { positionA, positionB }, new[] { velocityA, velocityB }, record);
            record.ticks = trace.Count - 1;
            Trace(record, trace);
            var contactReach = 2f * FrozenWriterConfiguration.BallRadiusM +
                               FrozenWriterConfiguration.ContactOffsetM;
            var contact = -1;
            for (var tick = 1; tick < trace.Count; tick++)
                if ((trace[tick].Positions[1] - trace[tick].Positions[0]).magnitude <=
                    contactReach)
                {
                    contact = tick;
                    break;
                }
            if (contact < 1)
                return record;
            var pre = trace[contact - 1];
            var separation = pre.Positions[1] - pre.Positions[0];
            if (separation.magnitude <= 0f)
                return record;
            var normal = separation.normalized;
            if (Vector3.Dot(pre.Velocities[1] - pre.Velocities[0], normal) >= 0f)
                return record;
            for (var tick = contact; tick < trace.Count; tick++)
            {
                if (Vector3.Dot(trace[tick].Velocities[1] - trace[tick].Velocities[0],
                        normal) <= 0f)
                    continue;
                record.contact_tick = contact;
                record.reversal_tick = tick;
                FillContact(record, tick, normal, pre, trace[tick]);
                record.post_stable = tick + 1 >= trace.Count ||
                                     Mathf.Abs(Vector3.Dot(
                                         trace[tick + 1].Velocities[1] -
                                         trace[tick + 1].Velocities[0], normal) -
                                               Vector3.Dot(trace[tick].Velocities[1] -
                                                   trace[tick].Velocities[0], normal)) < 1e-6f;
                return record;
            }
            return record;
        }

        SolverGateElasticityCase BoundaryCase(PinnedCollision collision,
            Func<IScientificWriter> writerFactory, Vector3 position)
        {
            var record = new SolverGateElasticityCase
            {
                case_id = "static_boundary_rebound",
                ball_count = 1,
                momentum_basis = "normal_component_reversal"
            };
            var trace = RunFixture(collision, writerFactory, "static_boundary_rebound",
                new[] { position }, new[] { new Vector3(-1f, 0f, 0f) }, record);
            record.ticks = trace.Count - 1;
            Trace(record, trace);
            var normal = Vector3.right;
            var reach = FrozenWriterConfiguration.BallRadiusM +
                        FrozenWriterConfiguration.ContactOffsetM;
            var contact = -1;
            for (var tick = 1; tick < trace.Count; tick++)
            {
                foreach (var surface in collision.Surfaces)
                    if (surface.WorldBounds.SqrDistance(trace[tick].Positions[0]) <= reach * reach)
                    {
                        contact = tick;
                        break;
                    }
                if (contact > 0)
                    break;
            }
            if (contact < 1)
                return record;
            var pre = trace[contact - 1];
            if (Vector3.Dot(pre.Velocities[0], normal) >= 0f)
                return record;
            for (var tick = contact; tick < trace.Count; tick++)
            {
                if (Vector3.Dot(trace[tick].Velocities[0], normal) <= 0f)
                    continue;
                record.contact_tick = contact;
                record.reversal_tick = tick;
                FillContact(record, tick, normal, pre, trace[tick]);
                record.post_stable = tick + 1 >= trace.Count ||
                                     Mathf.Abs(Vector3.Dot(trace[tick + 1].Velocities[0],
                                         normal) - Vector3.Dot(trace[tick].Velocities[0],
                                         normal)) < 1e-6f;
                return record;
            }
            return record;
        }

        /// <summary>
        /// Ball bodies PhysX currently holds for the fixture. The custom writer
        /// creates none, so a non-zero count is evidence of a second writer or
        /// a stale scene.
        /// </summary>
        static int PhysxBallBodies()
        {
            var count = 0;
            foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (body.GetComponent<SphereCollider>() == null)
                    continue;
                if (!body.transform.root.name.StartsWith("AmissSimulationTrial-",
                        StringComparison.Ordinal))
                    continue;
                count++;
            }
            return count;
        }

        static string[] ColliderCensus(Vector3 centre, float radius)
        {
            var names = new List<string>();
            var limit = radius * radius;
            foreach (var collider in FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if ((collider.bounds.center - centre).sqrMagnitude > limit)
                    continue;
                names.Add(collider.gameObject.name + "|" + collider.GetType().Name + "|" +
                          collider.transform.root.name);
                if (names.Count >= 64)
                    break;
            }
            return names.ToArray();
        }

        static void Trace(SolverGateElasticityCase record, List<Snapshot> trace)
        {
            var positions = new List<float>(trace.Count * trace[0].Positions.Length * 3);
            var velocities = new List<float>(trace.Count * trace[0].Positions.Length * 3);
            foreach (var snapshot in trace)
            {
                positions.AddRange(Flatten(snapshot.Positions));
                velocities.AddRange(Flatten(snapshot.Velocities));
            }
            record.trace_positions_m = positions.ToArray();
            record.trace_velocities_m_per_s = velocities.ToArray();
            record.trace_balls = trace[0].Positions.Length;
        }

        void FillContact(SolverGateElasticityCase record, int tick, Vector3 normal,
            Snapshot pre, Snapshot post)
        {
            var count = pre.Velocities.Length;
            var before = new List<(double mass, Vector3 velocity)>(count);
            var after = new List<(double mass, Vector3 velocity)>(count);
            var preNormal = 0d;
            var postNormal = 0d;
            for (var index = 0; index < count; index++)
            {
                before.Add((FrozenWriterConfiguration.BallMassKg, pre.Velocities[index]));
                after.Add((FrozenWriterConfiguration.BallMassKg, post.Velocities[index]));
            }
            if (count == 1)
            {
                preNormal = Vector3.Dot(pre.Velocities[0], normal);
                postNormal = Vector3.Dot(post.Velocities[0], normal);
            }
            else
            {
                preNormal = Vector3.Dot(pre.Velocities[1] - pre.Velocities[0], normal);
                postNormal = Vector3.Dot(post.Velocities[1] - post.Velocities[0], normal);
            }

            var momentum = 0d;
            for (var axis = 0; axis < 3; axis++)
            {
                var delta = 0d;
                for (var index = 0; index < count; index++)
                    delta += FrozenWriterConfiguration.BallMassKg *
                             (before[index].velocity[axis] - after[index].velocity[axis]);
                momentum += delta * delta;
            }
            momentum = Math.Sqrt(momentum);
            var scale = 0d;
            foreach (var state in before)
                scale += state.mass * state.velocity.magnitude;
            if (count == 1)
                momentum = Math.Abs(FrozenWriterConfiguration.BallMassKg *
                                    (preNormal + postNormal));

            var preEnergy = 0d;
            var postEnergy = 0d;
            foreach (var state in before)
                preEnergy += 0.5 * state.mass * state.velocity.sqrMagnitude;
            foreach (var state in after)
                postEnergy += 0.5 * state.mass * state.velocity.sqrMagnitude;

            record.contact_observed = true;
            record.contact_normal = new[] { normal.x, normal.y, normal.z };
            record.pre_separation_m = count == 2
                ? (pre.Positions[1] - pre.Positions[0]).magnitude
                : 0f;
            record.pre_velocities = Flatten(pre.Velocities);
            record.post_velocities = Flatten(post.Velocities);
            record.pre_normal_velocity_m_per_s = preNormal;
            record.post_normal_velocity_m_per_s = postNormal;
            record.momentum_delta_kg_m_per_s = momentum;
            record.momentum_residual_relative = scale > 0d ? momentum / scale : 0d;
            record.kinetic_energy_residual_relative =
                preEnergy > 0d ? (postEnergy - preEnergy) / preEnergy : 0d;
            record.restitution = -postNormal / preNormal;
            record.restitution_residual = Math.Abs(record.restitution - 1d);
        }

        static float[] Flatten(IReadOnlyList<Vector3> values)
        {
            var flat = new float[values.Count * 3];
            for (var index = 0; index < values.Count; index++)
            {
                flat[index * 3] = values[index].x;
                flat[index * 3 + 1] = values[index].y;
                flat[index * 3 + 2] = values[index].z;
            }
            return flat;
        }

        static string[] ToArray(IReadOnlyList<string> values)
        {
            if (values == null)
                return Array.Empty<string>();
            var copy = new string[values.Count];
            for (var index = 0; index < values.Count; index++)
                copy[index] = values[index] ?? "";
            return copy;
        }

        void Write(SolverGateRecord record)
        {
            var path = Path.Combine(Application.persistentDataPath,
                "solver-gate-" + record.run_id + ".json");
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(record, true));
                written.Add(record.run_id);
                var envelope = record.energy == null
                    ? "none"
                    : record.energy.e_rel.ToString("R", CultureInfo.InvariantCulture);
                Debug.Log("[Amiss] solver-gate: wrote " + record.run_id + " e_rel=" +
                          envelope);
            }
            catch (Exception error)
            {
                Debug.LogError("[Amiss] solver-gate: write failed " + error.Message);
            }
        }
    }

    /// <summary>
    /// Contact samples counted from the published per-Tick view against the
    /// pinned surfaces. A sample is a fresh contact: a ball pair or a
    /// ball-surface pair that is within the declared contact offset and was
    /// not in contact on the previous Tick. It reads views only.
    /// </summary>
    sealed class SolverGateContactTracker
    {
        readonly HashSet<long> ballBall = new();
        readonly HashSet<long> ballSurface = new();

        public int BallBallSamples { get; private set; }
        public int BallBoundarySamples { get; private set; }
        public readonly HashSet<int> ContactTicks = new();

        public void Sample(IReadOnlyList<PublishedBall> balls,
            IReadOnlyList<CollisionSurface> surfaces, int tick)
        {
            var ballReach = 2f * FrozenWriterConfiguration.BallRadiusM +
                            FrozenWriterConfiguration.ContactOffsetM;
            var ballReachSquared = ballReach * ballReach;
            var nextBallBall = new HashSet<long>();
            for (var first = 0; first < balls.Count; first++)
                for (var second = first + 1; second < balls.Count; second++)
                {
                    if ((balls[second].PositionM - balls[first].PositionM).sqrMagnitude >
                        ballReachSquared)
                        continue;
                    var key = ((long)first << 32) | (uint)second;
                    nextBallBall.Add(key);
                    if (!ballBall.Contains(key))
                    {
                        BallBallSamples++;
                        ContactTicks.Add(tick);
                    }
                }
            ballBall.Clear();
            ballBall.UnionWith(nextBallBall);

            var boundaryReach = FrozenWriterConfiguration.BallRadiusM +
                                FrozenWriterConfiguration.ContactOffsetM;
            var boundaryReachSquared = boundaryReach * boundaryReach;
            var nextBallSurface = new HashSet<long>();
            for (var ball = 0; ball < balls.Count; ball++)
                for (var surface = 0; surface < surfaces.Count; surface++)
                {
                    if (surfaces[surface].WorldBounds.SqrDistance(balls[ball].PositionM) >
                        boundaryReachSquared)
                        continue;
                    var key = ((long)ball << 32) | (uint)surface;
                    nextBallSurface.Add(key);
                    if (!ballSurface.Contains(key))
                    {
                        BallBoundarySamples++;
                        ContactTicks.Add(tick);
                    }
                }
            ballSurface.Clear();
            ballSurface.UnionWith(nextBallSurface);
        }
    }

    /// <summary>
    /// Read-only contact sample counter. It is attached to a writer-owned ball
    /// only for the duration of a measurement run and never writes pose or
    /// velocity, so it cannot become a second writer.
    /// </summary>
    sealed class SolverGateContactProbe : MonoBehaviour
    {
        public static int CurrentTick;

        public int BallBall { get; private set; }
        public int BallBoundary { get; private set; }
        public readonly HashSet<int> ContactTicks = new();

        void OnCollisionEnter(Collision collision)
        {
            var other = collision.collider;
            if (other == null)
                return;
            var body = other.attachedRigidbody;
            if (body == null)
                BallBoundary++;
            else if (string.CompareOrdinal(gameObject.name, body.gameObject.name) < 0)
                BallBall++;
            else
                return;
            ContactTicks.Add(CurrentTick);
        }
    }

    [Serializable]
    public sealed class SolverGateRecord
    {
        public string record_version;
        public string run_id;
        public string generated_utc;
        public string writer_configuration_id;
        public string writer_disposition;
        public string archived_writer_configuration_id;
        public string[] writer_configuration;
        public SolverGateBuildIdentity build;
        public SolverGateFixture fixture;
        public SolverGateSeed seed;
        public SolverGateSchedule schedule;
        public SolverGateEnergy energy;
        public SolverGateCollisions collisions;
        public SolverGateElasticity elasticity;
        public SolverGateElasticity archived_elasticity;
        public SolverGateTolerances tolerances;
    }

    [Serializable]
    public sealed class SolverGateBuildIdentity
    {
        public string application_version;
        public string unity_version;
        public bool development_build;
        public string device_model;
        public string operating_system;
        public string product_name;
    }

    [Serializable]
    public sealed class SolverGateFixture
    {
        public string label;
        public float effective_volume_m3;
        public float[] region_size_m;
        public float[] region_center_m;
        public string boundary_label;
        public string collision_representation_id;
        public string collision_checksum;
        public string collision_format_version;
        public int ball_count;
        public int sizing_computed_n;
        public string sizing_status;
        public bool sizing_is_opening_override;
        public string[] collider_census;
    }

    [Serializable]
    public sealed class SolverGateSeed
    {
        public string seed_id;
        public string trial_id;
        public string root_seed;
        public string seed_stream_id;
    }

    [Serializable]
    public sealed class SolverGateSchedule
    {
        public float fixed_delta_time_s;
        public int ticks_per_frame;
        public string execution;
        public int frames;
        public int executed_ticks;
        public double model_seconds;
        public float wall_seconds;
        public int paused_ticks;
        public bool interrupted;
        public string interruption_reason;
    }

    [Serializable]
    public sealed class SolverGateEnergyWindow
    {
        public int tick;
        public double model_seconds;
        public double e_rel;
    }

    [Serializable]
    public sealed class SolverGateEnergy
    {
        public string status;
        public double k_initial_j;
        public double k_min_j;
        public double k_max_j;
        public double e_rel;
        public int worst_tick;
        public int samples;
        public double[] series_kt_j;
        public SolverGateEnergyWindow[] windows;
    }

    [Serializable]
    public sealed class SolverGateCollisions
    {
        public int ball_ball_samples;
        public int ball_boundary_samples;
        public int contact_ticks;
        public int probe_ball_ball_samples;
        public int probe_ball_boundary_samples;
        public int spawn_contact_overlaps;
        public int writer_wall_contacts;
        public int writer_pair_contacts;
        public bool iteration_bound_exhausted;
        public int physx_ball_bodies;
    }

    [Serializable]
    public sealed class SolverGateElasticity
    {
        public SolverGateElasticityCase two_ball_head_on;
        public SolverGateElasticityCase two_ball_oblique;
        public SolverGateElasticityCase static_boundary_rebound;
    }

    [Serializable]
    public sealed class SolverGateElasticityCase
    {
        public string case_id;
        public int ball_count;
        public int ticks;
        public string writer_configuration_id;
        public string momentum_basis;
        public bool contact_observed;
        public int contact_tick;
        public int reversal_tick;
        public bool post_stable;
        public float[] contact_normal;
        public float pre_separation_m;
        public float[] pre_velocities;
        public float[] post_velocities;
        public double pre_normal_velocity_m_per_s;
        public double post_normal_velocity_m_per_s;
        public double momentum_delta_kg_m_per_s;
        public double momentum_residual_relative;
        public double kinetic_energy_residual_relative;
        public double restitution;
        public double restitution_residual;
        public float[] trace_positions_m;
        public float[] trace_velocities_m_per_s;
        public int trace_balls;
        public float[] gravity_m_per_s2;
        public string simulation_mode;
        public int scene_colliders;
        public string[] nearby_colliders;
    }

    [Serializable]
    public sealed class SolverGateTolerances
    {
        public double energy_relative;
        public string energy_relative_source;
        public string elasticity_tolerance_id;
        public bool elasticity_tolerance_declared;
        public double elasticity_momentum_relative;
        public double elasticity_kinetic_energy_relative;
        public double elasticity_restitution_residual;
        public string elasticity_tolerance_source;
    }
}
#endif
