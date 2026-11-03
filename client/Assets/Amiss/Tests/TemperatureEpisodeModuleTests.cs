using System;
using System.Collections.Generic;
using Amiss.Contracts;
using Amiss.Shell;
using Amiss.Shell.Episodes;
using NUnit.Framework;

namespace Amiss.Tests
{
    public class TemperatureEpisodeModuleTests
    {
        static WhatIsTemperatureEpisodeModule UnboundModule() => new(
            new UnboundSimulationRuntime(),
            new UnboundGuideRunner(),
            new UnboundTutorTransport(),
            new UnboundPersistenceAdapter());

        static EpisodeModuleHost HostWith(IEpisodeModule module) =>
            new(new Dictionary<string, Func<IEpisodeModule>>
            {
                [WhatIsTemperatureEpisode.EpisodeId] = () => module
            });

        [Test]
        public void ProductLibraryListsTheTemperatureEpisode()
        {
            var source = new WhatIsTemperatureEpisode.DeclarationSource();
            Assert.AreEqual(1, source.Declarations.Count);
            Assert.AreEqual(WhatIsTemperatureEpisode.EpisodeId, source.Declarations[0].EpisodeId);
            Assert.AreEqual(
                "What determines the temperature of a group of balls?",
                source.Declarations[0].CardQuestion);
            Assert.IsFalse(WhatIsTemperatureEpisode.ResolveCriticalAsset("fake-discrete-asset"));
            Assert.IsTrue(WhatIsTemperatureEpisode.ResolveCriticalAsset(
                WhatIsTemperatureEpisode.BatAssetId));

            var session = new HeadsetShellSession(
                source,
                ShellCapabilityManifest.ThisBuild(),
                new EpisodeModuleHost(),
                WhatIsTemperatureEpisode.ResolveCriticalAsset);
            Assert.AreEqual(ShellSessionState.Library, session.State);
            session.SelectCard(0);
            Assert.AreEqual(ShellSessionState.StartReady, session.State);
            Assert.IsTrue(session.SelectedVerdict.Runnable);
        }

        [Test]
        public void MountsTheBundledTemperatureEpisode()
        {
            var host = HostWith(UnboundModule());
            var result = host.Mount(WhatIsTemperatureEpisode.Declaration());

            Assert.IsTrue(result.Success, result.ReadableReason);
            Assert.IsInstanceOf<WhatIsTemperatureEpisodeModule>(host.Current);
            Assert.That(host.Residue, Is.Empty);
        }

        [Test]
        public void MountedModuleKeepsEveryRuntimeFaceUnbound()
        {
            var module = UnboundModule();
            var host = HostWith(module);
            Assert.IsTrue(host.Mount(WhatIsTemperatureEpisode.Declaration()).Success);

            Assert.IsFalse(module.RuntimeBound);
            Assert.IsFalse(module.TutorBound);
            Assert.IsFalse(module.PersistenceBound);

            var start = module.RequestTrialStart();
            Assert.IsFalse(start.Success);
            Assert.AreEqual(
                WhatIsTemperatureEpisodeModule.SimulationRuntimeUnbound,
                start.ReasonCategory);
            StringAssert.Contains("does not bind", start.ReadableReason);
        }

        [Test]
        public void UnloadLeavesNoResidue()
        {
            var host = HostWith(UnboundModule());
            Assert.IsTrue(host.Mount(WhatIsTemperatureEpisode.Declaration()).Success);

            var result = host.Unmount();
            Assert.IsTrue(result.Success);
            Assert.IsNull(host.Current);
            Assert.That(host.Residue, Is.Empty);
            Assert.IsTrue(host.Unmount().Success);
        }

        [Test]
        public void HostRefusesAModuleDeclaringAnotherEpisode()
        {
            var host = HostWith(new ForeignModule());
            var result = host.Mount(WhatIsTemperatureEpisode.Declaration());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("episode_module_identity_mismatch", result.ReasonCategory);
            Assert.IsNull(host.Current);
        }

        [Test]
        public void HostStillFailsClosedForAnUnregisteredEpisode()
        {
            var host = new EpisodeModuleHost();
            var result = host.Mount(WhatIsTemperatureEpisode.Declaration());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("episode_module_not_available", result.ReasonCategory);
            Assert.IsNull(host.Current);
        }

        sealed class ForeignModule : IEpisodeModule
        {
            public EpisodeDeclaration Declaration => new()
            {
                EpisodeId = "another-episode",
                Availability = EpisodeAvailability.Bundled
            };

            public LifecycleResult Load() => LifecycleResult.Ok();

            public LifecycleResult Unload(out string[] residue)
            {
                residue = Array.Empty<string>();
                return LifecycleResult.Ok();
            }
        }
    }
}
