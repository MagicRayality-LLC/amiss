using System;
using System.Collections.Generic;
using System.Linq;

namespace Amiss.Shell.Tutor
{
    public sealed class TrialReference
    {
        public TrialReference(string trialId, int revision)
        {
            TrialId = trialId ?? "";
            Revision = Math.Max(1, revision);
        }

        public string TrialId { get; }
        public int Revision { get; }
    }

    public sealed class RecentExchange
    {
        internal RecentExchange(string turnId, string userText, IEnumerable<string> agentSpeech,
            IEnumerable<TrialReference> refs, string stageId, string policyVersion)
        {
            TurnId = turnId ?? "";
            UserText = userText;
            AgentSpeech = (agentSpeech ?? Enumerable.Empty<string>()).ToArray();
            Refs = (refs ?? Enumerable.Empty<TrialReference>()).Select(Copy).ToArray();
            StageId = stageId ?? "";
            PolicyVersion = policyVersion ?? "";
        }

        public string TurnId { get; }
        public string UserText { get; }
        public IReadOnlyList<string> AgentSpeech { get; }
        public IReadOnlyList<TrialReference> Refs { get; }
        public string StageId { get; }
        public string PolicyVersion { get; }

        static TrialReference Copy(TrialReference reference) =>
            new(reference.TrialId, reference.Revision);
    }

    public sealed class SessionEvent
    {
        internal SessionEvent(string eventId, string kind, int tick,
            IEnumerable<TrialReference> refs)
        {
            EventId = eventId ?? "";
            Kind = kind ?? "";
            Tick = Math.Max(0, tick);
            Refs = (refs ?? Enumerable.Empty<TrialReference>()).Select(Copy).ToArray();
        }

        public string EventId { get; }
        public string Kind { get; }
        public int Tick { get; }
        public IReadOnlyList<TrialReference> Refs { get; }

        static TrialReference Copy(TrialReference reference) =>
            new(reference.TrialId, reference.Revision);
    }

    public sealed class SessionMemory
    {
        /// <summary>
        /// The whole admitted conversation of one session is retained (founder
        /// requirement, 2026-11-04). This count is a memory-safety ceiling far
        /// beyond a real session; the encoded 32 KiB compressed-context bound
        /// in <see cref="ContextPayloadBuilder"/> is the effective transport
        /// limit and evicts the oldest whole exchange first.
        /// </summary>
        public const int MaxRecentExchanges = 256;

        /// <summary>The mechanical session events retained alongside the chat.</summary>
        public const int MaxSessionEvents = 64;

        readonly List<RecentExchange> recentExchanges = new();
        readonly List<SessionEvent> sessionEvents = new();
        readonly HashSet<string> observedStages = new(StringComparer.Ordinal);
        int eventSequence;

        public IReadOnlyList<RecentExchange> RecentExchanges => recentExchanges;
        public IReadOnlyList<SessionEvent> SessionEvents => sessionEvents;
        public bool TranscriptRetentionEnabled { get; private set; } = true;

        public void RecordExchange(string turnId, string userText, IEnumerable<string> agentSpeech,
            IEnumerable<TrialReference> refs, string stageId, string policyVersion)
        {
            if (!TranscriptRetentionEnabled)
                return;
            var exchange = new RecentExchange(turnId, userText, agentSpeech, refs,
                stageId, policyVersion);
            if (exchange.AgentSpeech.Count == 0)
                return;
            recentExchanges.Add(exchange);
            while (recentExchanges.Count > MaxRecentExchanges)
                recentExchanges.RemoveAt(0);
        }

        public void RecordEvent(string kind, int tick, string trialId, int trialRevision)
        {
            RecordEvent(kind, tick, new[] { new TrialReference(trialId, trialRevision) });
        }

        public void RecordEvent(string kind, int tick, IEnumerable<TrialReference> refs)
        {
            sessionEvents.Add(new SessionEvent("event-" + (++eventSequence).ToString("D6"),
                kind, tick, refs));
            while (sessionEvents.Count > MaxSessionEvents)
                sessionEvents.RemoveAt(0);
        }

        public void ObserveStage(string stageId, int tick, string trialId, int trialRevision)
        {
            if (string.IsNullOrEmpty(stageId) || stageId == "unguided" ||
                string.IsNullOrEmpty(trialId))
                return;
            var key = trialId + "\0" + stageId;
            if (observedStages.Add(key))
                RecordEvent("stage_entered", tick, trialId, trialRevision);
        }

        public void SetTranscriptRetention(bool enabled)
        {
            TranscriptRetentionEnabled = enabled;
            if (!enabled)
                ClearDialogue();
        }

        public void ClearDialogue() => recentExchanges.Clear();

        public void Clear()
        {
            recentExchanges.Clear();
            sessionEvents.Clear();
            observedStages.Clear();
        }
    }
}
