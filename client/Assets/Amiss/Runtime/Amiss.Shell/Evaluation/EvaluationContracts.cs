using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Amiss.Contracts;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// The frozen hackathon evaluation endpoint and model. A direct call is
    /// evaluation-only; the product path sends the same Context Payload to the
    /// company endpoint, which holds the credential.
    /// </summary>
    public static class EvaluationContract
    {
        public const string EndpointAddress = "https://api.meta.ai/v1";
        public const string EndpointHost = "api.meta.ai";
        public const string ModelId = "muse-spark-1.3";
        public const string RequestContractVersion = "1.0.0";
        public const string ResponseContractVersion = "1.0.0";

        /// <summary>08 section 7: connection establishment.</summary>
        public const double ConnectDeadlineSeconds = 5.0;

        /// <summary>08 section 7: the complete response.</summary>
        public const double ModelDeadlineSeconds = 30.0;

        /// <summary>08 section 4.1: complete response admission bound.</summary>
        public const int MaxResponseBytes = 64 * 1024;

        public const int MaxProposals = 32;
        public const int MaxSpeechUnits = 2;
        public const int MaxSpeechTextBytes = 4 * 1024;

        public static readonly IReadOnlyList<string> Origins =
            Array.AsReadOnly(new[] { "answer", "proactive", "guide_default" });

        /// <summary>
        /// The exact three-line evaluation comment the decision fixes next to
        /// the single direct call site.
        /// </summary>
        public const string CallSiteComment =
            "Hackathon evaluation only. The person saves a Meta Model API key in\n" +
            "the device settings. The product sends the trial to the company\n" +
            "endpoint first. That endpoint calls the model.";
    }

    /// <summary>Typed, readable transport failure. It never carries the key.</summary>
    public sealed class TransportFailure
    {
        public TransportFailure(string kind, string reasonCode, string readable,
            string requestId = "", double deadlineSeconds = EvaluationContract.ModelDeadlineSeconds)
        {
            Kind = kind ?? "";
            ReasonCode = reasonCode ?? "";
            Readable = readable ?? "";
            RequestId = requestId ?? "";
            DeadlineSeconds = deadlineSeconds;
        }

        public string Kind { get; }
        public string ReasonCode { get; }
        public string Readable { get; }
        public string RequestId { get; }
        public double DeadlineSeconds { get; }

        public bool Offline => Kind == "offline" || Kind == "timeout" || Kind == "remote_refusal";

        public IReadOnlyDictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["kind"] = Kind,
            ["reason_code"] = ReasonCode,
            ["readable"] = Readable,
            ["request_id"] = RequestId,
            ["deadline_seconds"] = DeadlineSeconds
        };
    }

    /// <summary>
    /// The person's evaluation key for this slice only. The value is held in
    /// memory, is never written to disk, never serialized, and never rendered:
    /// <see cref="ToString"/> reports presence only. <see cref="Use"/> returns
    /// it once at the send boundary.
    /// </summary>
    public sealed class ModelKeyHandle
    {
        string key;

        public ModelKeyHandle(string key = null)
        {
            this.key = string.IsNullOrEmpty(key) ? null : key;
        }

        public bool Present => key != null;

        public string Use()
        {
            if (key == null)
                throw new InvalidOperationException("no_credential");
            return key;
        }

        public void Clear() => key = null;

        public string Redacted => Present ? "[key present]" : "[key absent]";

        public override string ToString() => "ModelKeyHandle(" + Redacted + ")";
    }

    /// <summary>
    /// The instructor supply, assembled at the send boundary only. It never
    /// enters a snapshot, ledger, log, export, or fixture body.
    /// </summary>
    public sealed class PolicyEnvelope
    {
        public PolicyEnvelope(string policyVersion, string system)
        {
            if (string.IsNullOrEmpty(policyVersion))
                throw new ArgumentException("no_policy", nameof(policyVersion));
            if (string.IsNullOrEmpty(system) || system.Trim().Length == 0)
                throw new ArgumentException("no_policy", nameof(system));
            PolicyVersion = policyVersion;
            System = system;
        }

        public string PolicyVersion { get; }
        public string System { get; }

        public IReadOnlyDictionary<string, object> ToRecord() => new Dictionary<string, object>
        {
            ["policy_version"] = PolicyVersion,
            ["system"] = System
        };

        public string Digest() => CanonicalJson.Sha256Hex(ToRecord());
    }

    /// <summary>Typed outcome of one transport send: response or failure, never both.</summary>
    public sealed class TransportOutcome
    {
        TransportOutcome(string mode, string requestId, IReadOnlyDictionary<string, object> response,
            IReadOnlyDictionary<string, object> provenance, TransportFailure failure)
        {
            Mode = mode;
            RequestId = requestId;
            Response = response;
            Provenance = provenance;
            Failure = failure;
        }

        public string Mode { get; }
        public string RequestId { get; }
        public IReadOnlyDictionary<string, object> Response { get; }
        public IReadOnlyDictionary<string, object> Provenance { get; }
        public TransportFailure Failure { get; }
        public bool Ok => Failure == null && Response != null;

        public static TransportOutcome Sent(string mode, string requestId,
            IReadOnlyDictionary<string, object> response,
            IReadOnlyDictionary<string, object> provenance) =>
            new(mode, requestId ?? "", response, provenance, null);

        public static TransportOutcome Failed(string mode, string requestId, TransportFailure failure) =>
            new(mode, requestId ?? "", null, null, failure);
    }

    /// <summary>
    /// One bounded model reply as returned by a transport. The body is the
    /// parsed structured response; the clock facts belong to the sender.
    /// </summary>
    public sealed class ModelReply
    {
        public ModelReply(IReadOnlyDictionary<string, object> body, double elapsedSeconds,
            string tsStart, string tsEnd, int? inputTokens, int? outputTokens,
            string providerRequestId = "")
        {
            Body = body;
            ElapsedSeconds = elapsedSeconds;
            TsStart = tsStart ?? "";
            TsEnd = tsEnd ?? "";
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
            ProviderRequestId = providerRequestId ?? "";
        }

        public IReadOnlyDictionary<string, object> Body { get; }
        public double ElapsedSeconds { get; }
        public string TsStart { get; }
        public string TsEnd { get; }
        public int? InputTokens { get; }
        public int? OutputTokens { get; }
        public string ProviderRequestId { get; }
    }

    /// <summary>
    /// The narrow socket seam. A direct sender performs the real call; the
    /// headless fixtures supply deterministic replies, so no suite opens a
    /// network connection and no live model result is claimed by a test.
    /// The call is awaited: a live sender hands the frame back to the shell
    /// while the request is in flight, so the Trial, hub, and rendering keep
    /// running, and it completes only with a reply or one typed failure
    /// (offline, refusal, malformed body, or the total deadline).
    /// </summary>
    public interface IModelSender
    {
        /// <summary>Sends one canonical request and returns its bounded reply.</summary>
        Task<ModelReply> Send(string endpointAddress, byte[] body, string authorization,
            double connectDeadlineSeconds, double deadlineSeconds,
            CancellationToken cancellation = default);
    }

    /// <summary>Raised when no network path exists.</summary>
    public sealed class SenderUnavailableException : Exception
    {
        public SenderUnavailableException(string message = "offline") : base(message)
        {
        }
    }

    /// <summary>Raised when the model service refuses the request.</summary>
    public sealed class SenderRefusedException : Exception
    {
        public SenderRefusedException(string message = "remote_refusal") : base(message)
        {
        }
    }
}
