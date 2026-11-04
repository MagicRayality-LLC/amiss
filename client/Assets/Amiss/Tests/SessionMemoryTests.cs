using System.Collections.Generic;
using System.Linq;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Tutor;
using NUnit.Framework;
using UnityEngine;

namespace Amiss.Tests
{
    public class SessionMemoryTests
    {
        static TrialReference Ref(int revision = 1) => new("trial-live", revision);

        static ContextPayloadSource Source(SessionMemory memory) => new()
        {
            EpisodeId = "fake-discrete",
            TrialId = "trial-live",
            TrialRevision = 1,
            Tick = 10,
            CollisionRepresentationId = "collision-test",
            RegionSizeM = new Vector3(2f, 2.5f, 2f),
            EffectiveVolumeM3 = 10.0,
            SessionMemory = memory,
            PolicyTokens = new PolicyTokenSet
            {
                EpisodeId = "fake-discrete",
                StageId = "stage-a",
                PolicyVersion = "synthetic-policy-v1"
            }
        };

        [Test]
        public void MemoryRetainsTheWholeSessionAndOnlyHitsItsSafetyCeiling()
        {
            var memory = new SessionMemory();
            var total = SessionMemory.MaxRecentExchanges + 2;
            for (var index = 0; index < total; index++)
                memory.RecordExchange("turn-" + index, "question " + index,
                    new[] { "answer " + index }, new[] { Ref() }, "stage-a", "policy-a");
            for (var index = 0; index < SessionMemory.MaxSessionEvents + 2; index++)
                memory.RecordEvent("effective_action", index, "trial-live", 1);

            Assert.AreEqual(SessionMemory.MaxRecentExchanges, memory.RecentExchanges.Count);
            Assert.AreEqual("turn-2", memory.RecentExchanges.First().TurnId);
            Assert.AreEqual("turn-" + (total - 1), memory.RecentExchanges.Last().TurnId);
            Assert.AreEqual(SessionMemory.MaxSessionEvents, memory.SessionEvents.Count);
            Assert.AreEqual("event-000003", memory.SessionEvents.First().EventId);
            Assert.AreEqual("event-" + (SessionMemory.MaxSessionEvents + 2).ToString("D6"),
                memory.SessionEvents.Last().EventId);

            memory.SetTranscriptRetention(false);
            Assert.IsEmpty(memory.RecentExchanges);
            Assert.AreEqual(SessionMemory.MaxSessionEvents, memory.SessionEvents.Count);
            memory.RecordExchange("turn-new", "not retained", new[] { "not retained" },
                new[] { Ref() }, "stage-a", "policy-a");
            Assert.IsEmpty(memory.RecentExchanges);

            memory.Clear();
            Assert.IsEmpty(memory.SessionEvents);
        }

        [Test]
        public void PayloadKeepsTheSessionChatAcrossContactRevisions()
        {
            var memory = new SessionMemory();
            memory.RecordExchange("turn-earlier", "an earlier question",
                new[] { "an earlier answer" }, new[] { Ref(1) }, "stage-a", "policy-a");
            memory.RecordExchange("turn-later", "a later question",
                new[] { "a later answer" }, new[] { Ref(9) }, "stage-a", "policy-a");
            memory.RecordEvent("trial_started", 0, "trial-live", 1);
            memory.RecordEvent("bat_hit", 2, "trial-live", 9);

            var payload = ContextPayloadBuilder.Build(Source(memory));
            var context = (IReadOnlyDictionary<string, object>)payload["compressed_session_context"];
            var exchanges = ((IEnumerable<object>)context["recent_exchanges"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();
            var events = ((IEnumerable<object>)context["session_events"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();

            // A contact commit advances the Trial revision during one
            // conversation; that must not erase what was said before it, and
            // the prop-named hit event must survive into the payload.
            Assert.AreEqual(2, exchanges.Length);
            Assert.AreEqual("turn-earlier", exchanges[0]["turn_id"]);
            Assert.AreEqual("turn-later", exchanges[1]["turn_id"]);
            Assert.AreEqual(2, events.Length);
            Assert.AreEqual("trial_started", events[0]["kind"]);
            Assert.AreEqual("bat_hit", events[1]["kind"]);
        }

        [Test]
        public void PayloadOmitsMemoryFromAnotherTrial()
        {
            var memory = new SessionMemory();
            memory.RecordExchange("turn-current", "current question", new[] { "current answer" },
                new[] { Ref() }, "stage-a", "policy-a");
            memory.RecordExchange("turn-other", "other question", new[] { "other answer" },
                new[] { new TrialReference("trial-other", 2) }, "stage-a", "policy-a");
            memory.RecordEvent("trial_started", 0, "trial-live", 1);
            memory.RecordEvent("effective_action", 2, "trial-other", 2);

            var payload = ContextPayloadBuilder.Build(Source(memory));
            var context = (IReadOnlyDictionary<string, object>)payload["compressed_session_context"];
            var exchanges = ((IEnumerable<object>)context["recent_exchanges"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();
            var events = ((IEnumerable<object>)context["session_events"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();

            Assert.AreEqual(1, exchanges.Length);
            Assert.AreEqual("turn-current", exchanges[0]["turn_id"]);
            Assert.AreEqual(1, events.Length);
            Assert.AreEqual("trial_started", events[0]["kind"]);
            StringAssert.DoesNotContain("other answer", CanonicalJson.Text(payload));
        }

        [Test]
        public void PayloadEvictsWholeExchangesToMeetTheCompressedBound()
        {
            var memory = new SessionMemory();
            const int recorded = 8;
            var userText = new string('u', ContextPayloadBuilder.MaxUtteranceBytes);
            var agentText = new string('a', 4 * 1024);
            for (var index = 0; index < recorded; index++)
                memory.RecordExchange("turn-" + index, userText, new[] { agentText, agentText },
                    new[] { Ref() }, "stage-a", "policy-a");

            var payload = ContextPayloadBuilder.Build(Source(memory));
            var context = (IReadOnlyDictionary<string, object>)payload["compressed_session_context"];
            var exchanges = ((IEnumerable<object>)context["recent_exchanges"]).Cast<
                IReadOnlyDictionary<string, object>>().ToArray();

            Assert.LessOrEqual(CanonicalJson.Bytes(context).Length,
                ContextPayloadBuilder.MaxCompressedContextBytes);
            Assert.Greater(exchanges.Length, 0);
            Assert.Less(exchanges.Length, recorded);
            Assert.AreEqual("turn-" + (recorded - 1), exchanges.Last()["turn_id"]);
        }

        [Test]
        public void StageObservationIsOneMechanicalEventPerTrialAndStage()
        {
            var memory = new SessionMemory();

            memory.ObserveStage("stage-a", 2, "trial-live", 1);
            memory.ObserveStage("stage-a", 3, "trial-live", 1);
            memory.ObserveStage("unguided", 3, "trial-live", 1);

            Assert.AreEqual(1, memory.SessionEvents.Count);
            Assert.AreEqual("stage_entered", memory.SessionEvents[0].Kind);
            Assert.AreEqual(2, memory.SessionEvents[0].Tick);
        }
    }
}
