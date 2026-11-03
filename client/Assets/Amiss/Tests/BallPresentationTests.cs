using System;
using System.Collections.Generic;
using System.Linq;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using Amiss.Shell.Ports;
using Amiss.Shell.Room;
using Amiss.Shell.Simulation;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Pins the presentation path the wearer actually sees. A Trial that is
    /// running is not evidence by itself: every spawned ball must get one
    /// mesh, on a layer the eye cameras keep, with a material built from a
    /// shader the player build ships.
    /// </summary>
    public class BallPresentationTests
    {
        const float RoomXZ = 4f;
        const float RoomHeight = 2.5f;

        static (DeviceSimulationRuntime runtime, RoomConfirmation confirmation, int ballCount)
            StartedTrial(string trialId)
        {
            var manual = RoomDraft.CreateManual(
                new Bounds(new Vector3(0f, RoomHeight * 0.5f, 0f),
                    new Vector3(RoomXZ, RoomHeight, RoomXZ)),
                Vector3.zero, Vector3.forward);
            var room = new RoomPreparation();
            var id = room.Begin(1.0);
            room.RequestPermission(id);
            room.CompletePermission(id, true);
            room.CompleteSceneLoad(id, manual);
            Assert.IsTrue(room.Confirm(id), room.Failure.ToString());

            var recovery = new ShellRecovery();
            Assert.IsTrue(recovery.ConfirmRoom(room));
            var pin = recovery.AcceptTrialSetup(trialId, room.Confirmation.CollisionChecksum);
            Assert.IsNotNull(pin);

            var runtime = new DeviceSimulationRuntime();
            var request = TemperatureTrialSetup.CreateTrialStart(trialId,
                room.Confirmation, pin, out var sizing);
            var outcome = runtime.TryStartTrial(request);
            Assert.IsTrue(outcome.Started, outcome.ReasonCode);
            Assert.AreEqual(sizing.ComputedN, outcome.BallCount);
            return (runtime, room.Confirmation, outcome.BallCount);
        }

        static void End(DeviceSimulationRuntime runtime, BallPresentation presentation)
        {
            presentation.Clear();
            runtime.StopTrial("test_end");
        }

        [Test]
        public void EverySpawnedBallGetsOneMeshOnALayerTheEyeCamerasKeep()
        {
            var started = StartedTrial("trial-ball-meshes");
            var presentation = new BallPresentation();
            try
            {
                presentation.Sync(null, started.runtime.CurrentView, 1f);

                var spawned = started.runtime.CurrentView.Balls.Count;
                Assert.AreEqual(started.ballCount, spawned);
                Assert.AreEqual(72, spawned, "the 40 m3 fixture room sizes to 72 balls");
                Assert.AreEqual(spawned, presentation.BallCount);

                var root = presentation.Root;
                Assert.IsNotNull(root, "the presentation root exists");
                Assert.AreEqual(spawned, root.transform.childCount,
                    "one mesh per spawned ball, not a subset");

                var eye = new GameObject("BallEyeProbe").AddComponent<Camera>();
                eye.cullingMask = ~0;
                EpisodeHub.ExcludeFlatSource(eye);

                for (var index = 0; index < root.transform.childCount; index++)
                {
                    var ball = root.transform.GetChild(index);
                    var filter = ball.GetComponent<MeshFilter>();
                    var renderer = ball.GetComponent<MeshRenderer>();
                    Assert.IsNotNull(filter, ball.name + " filter");
                    Assert.IsNotNull(renderer, ball.name + " renderer");
                    Assert.IsNotNull(filter.sharedMesh, ball.name + " mesh");
                    Assert.IsNotNull(renderer.sharedMaterial, ball.name + " material");
                    Assert.AreEqual(0, ball.gameObject.layer, ball.name + " stays on Default");
                    Assert.AreNotEqual(EpisodeHub.OverlaySourceLayer, ball.gameObject.layer,
                        ball.name + " must not sit on the overlay source layer");
                    Assert.AreNotEqual(0, eye.cullingMask & (1 << ball.gameObject.layer),
                        ball.name + " must survive the eye culling mask");
                }

                UnityEngine.Object.DestroyImmediate(eye.gameObject);
            }
            finally
            {
                End(started.runtime, presentation);
            }
        }

        [Test]
        public void BallMaterialComesFromAShaderThePlayerBuildShips()
        {
            var started = StartedTrial("trial-ball-shader");
            var presentation = new BallPresentation();
            try
            {
                presentation.Sync(null, started.runtime.CurrentView, 1f);
                var material = presentation.Root.transform.GetChild(0)
                    .GetComponent<MeshRenderer>().sharedMaterial;

                Assert.IsNotNull(material, "the ball material exists");
                Assert.IsNotNull(material.shader, "the ball shader resolved");
                Assert.AreEqual(BallPresentation.BallShaderName, material.shader.name);

                // A player build only keeps a shader that an asset references.
                // The imported instrument models are that reference, so the
                // shader they pull in is the one a Shader.Find can still reach
                // on device; a runtime-only name is stripped and resolves to
                // null instead.
                Assert.IsTrue(ShippedShaderDependencies().Contains(material.shader),
                    "no shipped asset keeps " + material.shader.name +
                    "; a player build would strip it");
            }
            finally
            {
                End(started.runtime, presentation);
            }
        }

        [Test]
        public void StoppingTheTrialLeavesNoBallsInTheRoom()
        {
            var started = StartedTrial("trial-ball-stop");
            var presentation = new BallPresentation();
            try
            {
                presentation.Sync(null, started.runtime.CurrentView, 1f);
                Assert.Greater(presentation.BallCount, 0);

                started.runtime.StopTrial("knife_switch_open");
                presentation.Sync(started.runtime.PreviousView, started.runtime.CurrentView, 0f);

                Assert.AreEqual(0, presentation.BallCount, "the balls leave with the trial");
                Assert.IsFalse(presentation.IsBuilt);
                Assert.IsNull(started.runtime.Readout);
            }
            finally
            {
                End(started.runtime, presentation);
            }
        }

        [Test]
        public void RoomHighlightMaterialUsesAShaderThePlayerBuildShips()
        {
            var material = RoomHighlights.CreateHighlightMaterial(Color.red);
            Assert.IsNotNull(material, "the highlight material exists");
            Assert.IsNotNull(material.shader);
            Assert.AreEqual(RoomHighlights.HighlightShaderName, material.shader.name);

            Assert.IsTrue(ShippedShaderDependencies().Contains(material.shader),
                "no shipped asset keeps " + material.shader.name +
                "; a player build would strip it");
            UnityEngine.Object.DestroyImmediate(material);
        }

        [Test]
        public void SpawnedBallsFillTheConfirmedRegionAroundTheWearer()
        {
            var started = StartedTrial("trial-ball-spread");
            try
            {
                var view = started.runtime.CurrentView;
                var region = started.confirmation.Region;
                var wearer = started.confirmation.ConfirmedUserPosition;

                var heightBands = new int[3];
                var distances = new List<float>(view.Balls.Count);
                foreach (var ball in view.Balls)
                {
                    Assert.IsTrue(region.Contains(ball.PositionM),
                        ball.EntityId + " spawned outside the confirmed region");
                    Assert.AreEqual(FrozenWriterConfiguration.BallRadiusM, ball.RadiusM, 0.000001f);
                    var band = Mathf.Clamp(
                        Mathf.FloorToInt((ball.PositionM.y - region.min.y) / region.size.y * 3f),
                        0, 2);
                    heightBands[band]++;
                    distances.Add(Vector3.Distance(ball.PositionM, wearer));
                }

                distances.Sort();
                Debug.Log("[Amiss] ball spread balls=" + view.Balls.Count +
                          " height_bands=" + string.Join("/", heightBands) +
                          " region=" + region.size.x.ToString("F2") + "x" +
                          region.size.y.ToString("F2") + "x" + region.size.z.ToString("F2") +
                          " distance_min=" + distances[0].ToString("F2") +
                          " median=" + distances[distances.Count / 2].ToString("F2") +
                          " max=" + distances[distances.Count - 1].ToString("F2"));

                Assert.That(heightBands, Has.All.GreaterThan(0),
                    "each height band holds balls");
                Assert.Less(distances[0], 2f,
                    "at least one ball is spawned within arm's length of the wearer");
            }
            finally
            {
                started.runtime.StopTrial("test_end");
            }
        }

        /// <summary>
        /// The shaders a player build keeps because an asset references them.
        /// A model import keeps the shader its embedded materials use, so that
        /// is the reachable set a runtime <see cref="Shader.Find"/> can hit on
        /// device; a name no asset references is stripped from the build.
        /// </summary>
        static HashSet<Shader> ShippedShaderDependencies()
        {
            var shaders = new HashSet<Shader>();
            var assets = new[] { "t:Model", "t:Prefab", "t:Material" }
                .SelectMany(filter => AssetDatabase.FindAssets(filter, new[] { "Assets/Amiss" }))
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct(StringComparer.Ordinal);
            foreach (var path in assets)
            {
                foreach (var dependency in AssetDatabase.GetDependencies(path, true))
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(dependency);
                    if (shader != null)
                        shaders.Add(shader);
                }
                foreach (var representation in AssetDatabase.LoadAllAssetsAtPath(path)
                             .Concat(AssetDatabase.LoadAllAssetRepresentationsAtPath(path)))
                    if (representation is Material material && material.shader != null)
                        shaders.Add(material.shader);
            }
            return shaders;
        }
    }
}
