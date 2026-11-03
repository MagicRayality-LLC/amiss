using System;
using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Simulation
{
    /// <summary>
    /// The frozen Unity rigidbody writer of approved design 02 section 3.1.
    /// It was the measured candidate and failed the locked gate; per 02
    /// section 3.1 both outcomes stay archived, so its code and complete
    /// configuration record are retained as an evaluation artifact only.
    /// <see cref="Disposition"/> is <see cref="WriterDisposition.ArchivedOnly"/>
    /// and <see cref="ScientificWriterSlot"/> refuses it, so it can never be
    /// bound to a product Trial.
    ///
    /// The writer needs play mode because PhysX only steps a live scene. A
    /// start outside play mode fails closed instead of pretending to simulate.
    /// </summary>
    public sealed class UnityRigidbodyWriter : IScientificWriter
    {
        const string MaterialName = "amiss-elastic-frozen-v1";

        GameObject root;
        GameObject statics;
        PhysicsMaterial material;
        Rigidbody[] bodies;
        BallState[] balls;
        FrozenWriterConfiguration configuration;
        bool configured;

        SimulationMode savedSimulationMode;
        Vector3 savedGravity;
        int savedSolverIterations;
        int savedSolverVelocityIterations;
        float savedBounceThreshold;
        float savedContactOffset;

        public string WriterId => FrozenWriterConfiguration.ArchivedUnityConfigurationId;
        public string ConfigurationId => FrozenWriterConfiguration.ArchivedUnityConfigurationId;
        public WriterDisposition Disposition => WriterDisposition.ArchivedOnly;
        public IReadOnlyList<string> ConfigurationRecord =>
            configuration?.Record() ?? Array.Empty<string>();
        public int BodyCount => bodies?.Length ?? 0;
        public bool IsConfigured => configured;

        public void Setup(SimulationTrialSetup setup)
        {
            if (!Application.isPlaying)
                throw new InvalidOperationException("writer_requires_play_mode");
            if (setup == null || setup.Collision == null)
                throw new ArgumentNullException(nameof(setup));
            if (configured)
                throw new InvalidOperationException("writer_already_setup");

            savedSimulationMode = Physics.simulationMode;
            savedGravity = Physics.gravity;
            savedSolverIterations = Physics.defaultSolverIterations;
            savedSolverVelocityIterations = Physics.defaultSolverVelocityIterations;
            savedBounceThreshold = Physics.bounceThreshold;
            savedContactOffset = Physics.defaultContactOffset;

            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = FrozenWriterConfiguration.Gravity;
            Physics.defaultSolverIterations = FrozenWriterConfiguration.SolverIterations;
            Physics.defaultSolverVelocityIterations =
                FrozenWriterConfiguration.SolverVelocityIterations;
            Physics.bounceThreshold = FrozenWriterConfiguration.BounceThresholdMPerS;
            Physics.defaultContactOffset = FrozenWriterConfiguration.ContactOffsetM;

            material = new PhysicsMaterial(MaterialName)
            {
                dynamicFriction = FrozenWriterConfiguration.DynamicFriction,
                staticFriction = FrozenWriterConfiguration.StaticFriction,
                bounciness = FrozenWriterConfiguration.Bounciness,
                frictionCombine = FrozenWriterConfiguration.FrictionCombine,
                bounceCombine = FrozenWriterConfiguration.BounceCombine
            };

            root = new GameObject("AmissSimulationTrial-" + setup.TrialId);
            statics = CollisionBridge.InstallStatics(setup.Collision, root.transform);
            configuration = setup.Configuration;

            balls = new BallState[setup.Balls.Count];
            bodies = new Rigidbody[setup.Balls.Count];
            for (var index = 0; index < setup.Balls.Count; index++)
            {
                var ball = setup.Balls[index];
                balls[index] = ball;
                bodies[index] = CreateBody(ball);
            }
            configured = true;
        }

        Rigidbody CreateBody(BallState ball)
        {
            var bodyObject = new GameObject(ball.EntityId);
            bodyObject.transform.SetParent(root.transform, false);
            bodyObject.transform.SetPositionAndRotation(ball.PositionM, Quaternion.identity);

            var collider = bodyObject.AddComponent<SphereCollider>();
            collider.radius = ball.RadiusM;
            collider.material = material;

            var body = bodyObject.AddComponent<Rigidbody>();
            body.mass = ball.MassKg;
            body.useGravity = false;
            body.linearDamping = FrozenWriterConfiguration.LinearDamping;
            body.angularDamping = FrozenWriterConfiguration.AngularDamping;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.collisionDetectionMode = FrozenWriterConfiguration.DetectionMode;
            body.interpolation = FrozenWriterConfiguration.Interpolation;
            body.sleepThreshold = 0f;
            body.linearVelocity = ball.VelocityMPerS;
            return body;
        }

        public void Advance(int tick, float deltaTimeSeconds)
        {
            if (!configured)
                throw new InvalidOperationException("writer_not_setup");
            Physics.Simulate(deltaTimeSeconds);
            for (var index = 0; index < bodies.Length; index++)
            {
                balls[index].PositionM = bodies[index].position;
                balls[index].VelocityMPerS = bodies[index].linearVelocity;
            }
        }

        public void Teardown()
        {
            if (configured)
            {
                Physics.simulationMode = savedSimulationMode;
                Physics.gravity = savedGravity;
                Physics.defaultSolverIterations = savedSolverIterations;
                Physics.defaultSolverVelocityIterations = savedSolverVelocityIterations;
                Physics.bounceThreshold = savedBounceThreshold;
                Physics.defaultContactOffset = savedContactOffset;
            }
            configured = false;
            bodies = null;
            balls = null;
            configuration = null;

            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
            statics = null;
            if (material != null)
            {
                UnityEngine.Object.Destroy(material);
                material = null;
            }
        }
    }
}
