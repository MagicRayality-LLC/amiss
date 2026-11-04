using System.Collections.Generic;
using Amiss.Shell.Cooling;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    /// <summary>
    /// Palm cooling: the field art and molasses damping depend on the gesture
    /// actually opening when two open palms face each other. These pin the
    /// handedness-dependent normal and the enter/exit decision without a
    /// headset, so a wrong normal sign cannot silently hide the tool again.
    /// </summary>
    public class PalmCoolingTests
    {
        readonly List<GameObject> created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) Object.DestroyImmediate(go);
            created.Clear();
        }

        GameObject Track(GameObject go)
        {
            created.Add(go);
            return go;
        }

        TransformPalmSource Palm(string name, Vector3 position, Vector3 normal,
            PalmHandSource.Side side)
        {
            var go = Track(new GameObject(name));
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);
            var source = go.AddComponent<TransformPalmSource>();
            source.side = side;
            return source;
        }

        [Test]
        public void RightPalmNormalPointsOutOfThePalm()
        {
            // Right hand, fingers forward, palm facing left (toward -X):
            // index knuckle up, pinky knuckle down. The palm normal is -X.
            var sample = PalmGeometry.FromJoints(PalmHandSource.Side.Right,
                wrist: Vector3.zero,
                palm: new Vector3(0f, 0f, 0.05f),
                indexProximal: new Vector3(0f, 0.03f, 0.09f),
                middleProximal: new Vector3(0f, 0f, 0.10f),
                littleProximal: new Vector3(0f, -0.03f, 0.09f),
                indexTip: new Vector3(0f, 0.03f, 0.16f),
                middleTip: new Vector3(0f, 0f, 0.17f),
                ringTip: new Vector3(0f, -0.02f, 0.16f),
                pinkyTip: new Vector3(0f, -0.03f, 0.14f),
                thumbTip: new Vector3(0f, 0.04f, 0.06f));

            Assert.IsTrue(sample.tracked);
            Assert.Greater(Vector3.Dot(sample.normal, Vector3.left), 0.99f,
                "right palm normal should point out of the palm, toward -X");
            Assert.Greater(sample.openness, 0.9f);
        }

        [Test]
        public void LeftPalmNormalPointsOutOfThePalm()
        {
            // Mirror pose: left hand, fingers forward, palm facing right (+X).
            var sample = PalmGeometry.FromJoints(PalmHandSource.Side.Left,
                wrist: Vector3.zero,
                palm: new Vector3(0f, 0f, 0.05f),
                indexProximal: new Vector3(0f, 0.03f, 0.09f),
                middleProximal: new Vector3(0f, 0f, 0.10f),
                littleProximal: new Vector3(0f, -0.03f, 0.09f),
                indexTip: new Vector3(0f, 0.03f, 0.16f),
                middleTip: new Vector3(0f, 0f, 0.17f),
                ringTip: new Vector3(0f, -0.02f, 0.16f),
                pinkyTip: new Vector3(0f, -0.03f, 0.14f),
                thumbTip: new Vector3(0f, 0.04f, 0.06f));

            Assert.IsTrue(sample.tracked);
            Assert.Greater(Vector3.Dot(sample.normal, Vector3.right), 0.99f,
                "left palm normal should point out of the palm, toward +X");
        }

        [Test]
        public void FacingOpenPalmsOpenTheField()
        {
            var left = Palm("Palm_L", new Vector3(-0.15f, 1.2f, 0.4f), Vector3.right,
                PalmHandSource.Side.Left);
            var right = Palm("Palm_R", new Vector3(0.15f, 1.2f, 0.4f), Vector3.left,
                PalmHandSource.Side.Right);
            var gesture = Track(new GameObject("PalmCoolingGesture"))
                .AddComponent<PalmCoolingGesture>();
            gesture.leftHand = left;
            gesture.rightHand = right;
            gesture.poseSmoothing = 0f;
            gesture.enterHoldTime = 0.10f;

            gesture.Step(0.06f);
            gesture.Step(0.06f);

            Assert.IsTrue(gesture.IsActive);
            Assert.Greater(gesture.Strength, 0f);
            Assert.Greater(gesture.Closeness, 0f);
        }

        [Test]
        public void PalmsFacingTheSameWayDoNotOpenTheField()
        {
            var left = Palm("Palm_L", new Vector3(-0.15f, 1.2f, 0.4f), Vector3.forward,
                PalmHandSource.Side.Left);
            var right = Palm("Palm_R", new Vector3(0.15f, 1.2f, 0.4f), Vector3.forward,
                PalmHandSource.Side.Right);
            var gesture = Track(new GameObject("PalmCoolingGesture"))
                .AddComponent<PalmCoolingGesture>();
            gesture.leftHand = left;
            gesture.rightHand = right;
            gesture.poseSmoothing = 0f;

            for (var i = 0; i < 10; i++)
                gesture.Step(0.05f);
            Assert.IsFalse(gesture.IsActive);
            Assert.AreEqual(0f, gesture.Strength);
        }
    }
}
