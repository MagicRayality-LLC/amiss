using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Amiss.Shell.Evaluation;
using Amiss.Shell.Ports;
using UnityEngine;
using UnityEngine.Networking;

namespace Amiss.Shell.Tutor
{
    /// <summary>
    /// The device binding behind <see cref="TutorTransportPort"/>. It composes
    /// the evaluation transport with whole-reply admission and keeps the last
    /// admitted provenance for the panel trace line, so the visible state names
    /// the model, the mode, and the request id of every admitted reply.
    ///
    /// The policy envelope is bundled with this build. The evaluation key is
    /// the person's own credential and exists only once they save it in the
    /// device settings; the live path stays closed with a readable typed reason
    /// until both are present, and no request leaves the device before that.
    ///
    /// A live request is awaited, never waited on: the socket hands the frame
    /// back so the Trial, the hub, and rendering keep running, and its total
    /// deadline is measured against real elapsed time (design 06 and 08
    /// section 7). One request may be in flight; a second ask is refused here
    /// with a typed local reason instead of opening a second connection.
    /// </summary>
    public sealed class DeviceTutorTransport : TutorTransportPort
    {
        /// <summary>
        /// The short panel line. The trace card has one line per slot, so the
        /// full explanation lives in <see cref="FailClosedDetail"/> and the
        /// captured evaluation evidence instead of overrunning the subtitle.
        /// </summary>
        public const string NoPolicyReadable =
            "closed — no approved instructor policy is loaded";

        public const string NoKeyReadable =
            "closed — no Meta model key is held";

        /// <summary>The complete readable reason, for the evidence record.</summary>
        public const string NoPolicyDetail =
            "Evaluation help is closed: no approved instructor policy is loaded, " +
            "so no model request is built. Recorded play and the default path are " +
            "unaffected.";

        public const string NoKeyDetail =
            "Evaluation help is closed: no Meta model key is held. " +
            "Recorded play and the default path are unaffected.";

        /// <summary>
        /// The typed local refusal of a second ask while one is in flight. The
        /// design allows one outstanding model request and no queue (06 and 08
        /// section 7), so the second ask sends nothing.
        /// </summary>
        public const string InFlightReadable =
            "closed for this turn — one model request is already in flight, so no " +
            "second request was sent. The panel path continues.";

        readonly ModelKeyHandle key;
        readonly IModelKeyStore keyStore;
        readonly PolicyEnvelope policy;
        readonly IModelSender sender;
        readonly ProposalValidator validator;
        readonly ModelReplyAdmission admission;

        TransportOutcome lastOutcome;
        AdmissionResult lastAdmission;
        IReadOnlyDictionary<string, object> lastProvenance;
        IReadOnlyList<string> lastSpeech = Array.Empty<string>();
        bool inFlight;

        public DeviceTutorTransport(ProposalValidator validator, IModelSender sender = null,
            ModelKeyHandle key = null, PolicyEnvelope policy = null,
            IModelKeyStore keyStore = null)
        {
            this.validator = validator;
            this.sender = sender;
            this.key = key ?? new ModelKeyHandle();
            this.policy = policy;
            this.keyStore = keyStore;
            admission = new ModelReplyAdmission(validator);
        }

        /// <summary>
        /// Bound once a policy envelope exists. Without it the port stays
        /// closed, which is the honest state of this build.
        /// </summary>
        public bool Available => policy != null;

        public bool KeyPresent => keyStore != null ? keyStore.Present : key.Present;

        public ModelKeyHandle Key => key;

        /// <summary>The masked tail of the stored key, or empty when none is held.</summary>
        public string MaskedTail => keyStore != null
            ? keyStore.MaskedTail
            : key.Present ? "••••" : "";

        /// <summary>The exact admitted speech text of the last reply, or empty.</summary>
        public IReadOnlyList<string> LastSpeech => lastSpeech;

        /// <summary>The one visible trace line of 10 section 4.</summary>
        public string TraceLine => lastProvenance == null
            ? FailClosedLine
            : ModelProvenance.TraceLine(lastProvenance);

        public string FailClosedLine
        {
            get
            {
                if (policy == null)
                    return "Model: " + NoPolicyReadable;
                if (!KeyPresent)
                    return "Model: " + NoKeyReadable;
                return "Model: ready; no reply admitted yet.";
            }
        }

        public AdmissionResult LastAdmission => lastAdmission;

        public IReadOnlyDictionary<string, object> LastProvenance => lastProvenance;

        public TransportFailure LastFailure => lastOutcome?.Failure;

        /// <summary>The complete fail-closed reason, for the evidence record.</summary>
        public string FailClosedDetail =>
            policy == null ? NoPolicyDetail : !KeyPresent ? NoKeyDetail : "";

        /// <summary>
        /// True while this socket holds one model request. The design bounds the
        /// live path to one outstanding request per conversation and forbids a
        /// queue (06 and 08 section 7), so the hub may show this state and a
        /// second ask is refused rather than sent.
        /// </summary>
        public bool InFlight => inFlight;

        /// <summary>
        /// Sends one reply request, awaits it without holding the frame, and
        /// admits the reply as a whole. A failure at any stage leaves the
        /// committed model state and the trace unchanged except for the typed
        /// failure that explains it.
        /// </summary>
        public async Task<AdmissionResult> Request(IReadOnlyDictionary<string, object> contextPayload,
            string requestId, string turnId, string origin,
            CancellationToken cancellation = default)
        {
            if (policy == null)
            {
                Debug.Log("[Amiss] model transport closed: no policy envelope (request " +
                    requestId + ")");
                lastOutcome = TransportOutcome.Failed(EvaluationDirectTransport.Mode, requestId,
                    new TransportFailure("no_policy", "no_policy", NoPolicyDetail, requestId));
                lastAdmission = AdmissionResult.Rejected(lastOutcome.Failure,
                    EvaluationDirectTransport.Mode);
                return lastAdmission;
            }

            IReadOnlyDictionary<string, object> request;
            try
            {
                request = EvaluationRequestBuilder.Build(contextPayload, policy, requestId,
                    turnId, origin);
            }
            catch (EvaluationContractException error)
            {
                Debug.Log("[Amiss] model request build failed code=" + error.ReasonCode +
                    " (request " + requestId + ")");
                lastOutcome = TransportOutcome.Failed(EvaluationDirectTransport.Mode, requestId,
                    new TransportFailure(error.ReasonCode, error.ReasonCode, error.Message,
                        requestId));
                lastAdmission = AdmissionResult.Rejected(lastOutcome.Failure,
                    EvaluationDirectTransport.Mode);
                return lastAdmission;
            }

            if (inFlight)
            {
                Debug.Log("[Amiss] model request refused: one already in flight (request " +
                    requestId + ")");
                lastSpeech = Array.Empty<string>();
                lastOutcome = TransportOutcome.Failed(EvaluationDirectTransport.Mode, requestId,
                    new TransportFailure("request_in_flight", "request_in_flight",
                        InFlightReadable, requestId));
                lastAdmission = AdmissionResult.Rejected(lastOutcome.Failure,
                    EvaluationDirectTransport.Mode);
                return lastAdmission;
            }

            inFlight = true;
            try
            {
                var transport = new EvaluationDirectTransport(
                    sender ?? new UnityModelSender(), keyStore?.OpenHandle() ?? key, policy);
                Debug.Log("[Amiss] model send start (request " + requestId + ")");
                lastOutcome = await transport.Send(request, cancellation);
            }
            finally
            {
                inFlight = false;
            }

            if (!lastOutcome.Ok)
            {
                Debug.Log("[Amiss] model send failed code=" + lastOutcome.Failure.ReasonCode +
                    " (request " + requestId + ")");
                lastSpeech = Array.Empty<string>();
                lastAdmission = AdmissionResult.Rejected(lastOutcome.Failure, lastOutcome.Mode);
                return lastAdmission;
            }
            lastAdmission = admission.Admit(request, lastOutcome.Response, lastOutcome.Provenance,
                lastOutcome.Mode);
            lastSpeech = lastAdmission.Admitted
                ? lastAdmission.AcceptedSpeech
                : Array.Empty<string>();
            Debug.Log("[Amiss] model send ok; admitted=" + lastAdmission.Admitted +
                " speech=" + lastSpeech.Count + " (request " + requestId + ")");
            if (lastAdmission.Admitted && lastAdmission.Provenance != null)
                lastProvenance = lastAdmission.Provenance;
            return lastAdmission;
        }

        /// <summary>
        /// Replays one recorded fixture through the same admission path. The
        /// provenance keeps <c>mode = recorded</c>, so a replay is visibly
        /// recorded wherever its wording appears.
        /// </summary>
        public AdmissionResult Replay(IReadOnlyDictionary<string, object> contextPayload,
            string requestId, string turnId, string origin,
            string scenarioId, IEnumerable<IReadOnlyDictionary<string, object>> fixtures)
        {
            var request = EvaluationRequestBuilder.Build(contextPayload, policy, requestId, turnId,
                origin);
            var transport = new RecordedFixtureTransport(fixtures);
            lastOutcome = transport.Send(request, scenarioId);
            if (!lastOutcome.Ok)
            {
                lastAdmission = AdmissionResult.Rejected(lastOutcome.Failure, lastOutcome.Mode);
                return lastAdmission;
            }
            lastAdmission = admission.Admit(request, lastOutcome.Response, lastOutcome.Provenance,
                lastOutcome.Mode);
            lastSpeech = lastAdmission.Admitted
                ? lastAdmission.AcceptedSpeech
                : Array.Empty<string>();
            if (lastAdmission.Admitted && lastAdmission.Provenance != null)
                lastProvenance = lastAdmission.Provenance;
            return lastAdmission;
        }

        /// <summary>
        /// Accepts one reply the runtime already produced (a scripted or
        /// recorded supply) and admits it through the same boundary. Used where
        /// the transport is a caller-owned adapter rather than this socket.
        /// </summary>
        public AdmissionResult Admit(IReadOnlyDictionary<string, object> request,
            IReadOnlyDictionary<string, object> response,
            IReadOnlyDictionary<string, object> provenance, string mode)
        {
            lastAdmission = admission.Admit(request, response, provenance, mode);
            lastSpeech = lastAdmission.Admitted
                ? lastAdmission.AcceptedSpeech
                : Array.Empty<string>();
            if (lastAdmission.Admitted && lastAdmission.Provenance != null)
                lastProvenance = lastAdmission.Provenance;
            return lastAdmission;
        }

        public void ClearKey()
        {
            key.Clear();
            keyStore?.Clear();
        }
    }

    /// <summary>
    /// The frame clock of one live model request. It reads real elapsed time,
    /// not frame time: a frame delta does not advance within a frame, so a
    /// wait that spends its budget in frame deltas aborts before any reply can
    /// arrive. Each pending step costs the caller exactly one frame, so the
    /// wait never spins and the shell keeps rendering while it waits.
    /// </summary>
    public sealed class ModelRequestWait
    {
        public enum State
        {
            Pending,
            Done,
            Expired,
            Cancelled
        }

        readonly Func<bool> finished;
        readonly Func<double> elapsedSeconds;
        readonly Func<bool> cancelled;
        readonly double deadlineSeconds;

        public ModelRequestWait(Func<bool> finished, Func<double> elapsedSeconds,
            double deadlineSeconds, Func<bool> cancelled = null)
        {
            this.finished = finished ?? throw new ArgumentNullException(nameof(finished));
            this.elapsedSeconds = elapsedSeconds ??
                                  throw new ArgumentNullException(nameof(elapsedSeconds));
            if (deadlineSeconds <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(deadlineSeconds));
            this.deadlineSeconds = deadlineSeconds;
            this.cancelled = cancelled;
        }

        /// <summary>The total deadline this wait measures, in seconds.</summary>
        public double DeadlineSeconds => deadlineSeconds;

        /// <summary>
        /// One step: a finished operation wins, then cancellation, then the
        /// real-time deadline. At or past the deadline the response is late.
        /// </summary>
        public State Step()
        {
            if (finished())
                return State.Done;
            if (cancelled != null && cancelled())
                return State.Cancelled;
            return elapsedSeconds() >= deadlineSeconds ? State.Expired : State.Pending;
        }

        /// <summary>
        /// Steps once per frame until the request finishes, expires, or is
        /// cancelled. Between steps the caller's frame loop runs, so the Trial,
        /// the hub, and rendering continue while one request is in flight.
        /// </summary>
        public static async Task<State> AwaitAsync(ModelRequestWait wait, Func<Task> yieldFrame)
        {
            if (wait == null)
                throw new ArgumentNullException(nameof(wait));
            var frame = yieldFrame;
            if (frame == null)
                frame = YieldToNextFrame;
            State state;
            while ((state = wait.Step()) == State.Pending)
                await frame();
            return state;
        }

        static async Task YieldToNextFrame()
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// The narrow socket binding behind <see cref="IModelSender"/>. It performs
    /// the real call and is the only place the evaluation key is handed to the
    /// platform. The evaluation comment sits at this single call site.
    ///
    /// The request is awaited on the frame loop: pending work returns the frame
    /// to Unity instead of blocking it. The total deadline is a real 30 s from
    /// a monotonic clock, and expiry or cancellation aborts the request.
    /// </summary>
    public sealed class UnityModelSender : IModelSender
    {
        public async Task<ModelReply> Send(string endpointAddress, byte[] body,
            string authorization, double connectDeadlineSeconds, double deadlineSeconds,
            CancellationToken cancellation = default)
        {
            IReadOnlyDictionary<string, object> canonicalRequest;
            try
            {
                canonicalRequest = CanonicalJsonReader.Parse(System.Text.Encoding.UTF8.GetString(body))
                    as IReadOnlyDictionary<string, object>;
            }
            catch (Exception)
            {
                throw new SenderRefusedException("malformed_request");
            }
            if (canonicalRequest == null)
                throw new SenderRefusedException("malformed_request");
            var wireBody = MetaChatCompletionCodec.RequestBytes(canonicalRequest);
            Debug.Log("[Amiss] model http start " + endpointAddress +
                " bytes=" + wireBody.Length);

            // Hackathon evaluation only. The person saves a Meta Model API key in
            // the device settings. The product sends the trial to the company
            // endpoint first. That endpoint calls the model.
            var started = DateTime.UtcNow;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var request = new UnityWebRequest(endpointAddress,
                UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(wireBody);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + authorization);
            request.timeout = (int)Math.Ceiling(deadlineSeconds);

            var operation = request.SendWebRequest();
            var wait = new ModelRequestWait(() => operation.isDone,
                () => clock.Elapsed.TotalSeconds, deadlineSeconds,
                () => cancellation.IsCancellationRequested);
            var state = await ModelRequestWait.AwaitAsync(wait, null);
            if (state == ModelRequestWait.State.Cancelled)
            {
                Debug.Log("[Amiss] model http cancelled");
                request.Abort();
                throw new OperationCanceledException(cancellation);
            }
            if (state != ModelRequestWait.State.Done)
            {
                Debug.Log("[Amiss] model http deadline exceeded after " +
                    clock.Elapsed.TotalSeconds.ToString("0.0") + "s");
                request.Abort();
                throw new TimeoutException("model_deadline_exceeded");
            }

            var elapsed = clock.Elapsed.TotalSeconds;
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                Debug.Log("[Amiss] model http connection error: " + request.error);
                throw new SenderUnavailableException(request.error);
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.Log("[Amiss] model http refused status=" + request.responseCode +
                    " error=" + request.error);
                throw new SenderRefusedException(request.error);
            }

            IReadOnlyDictionary<string, object> completion;
            try
            {
                completion = CanonicalJsonReader.Parse(request.downloadHandler.text)
                    as IReadOnlyDictionary<string, object>;
            }
            catch (Exception)
            {
                throw new SenderRefusedException("malformed_response");
            }
            if (completion == null)
                throw new SenderRefusedException("malformed_response");
            var response = MetaChatCompletionCodec.Response(completion);
            var usage = MetaChatCompletionCodec.UsageTokens(completion);
            Debug.Log("[Amiss] model http ok status=" + request.responseCode +
                " elapsed=" + elapsed.ToString("0.0") + "s" +
                " bytes=" + (request.downloadHandler != null
                    ? request.downloadedBytes
                    : 0));
            return new ModelReply(response, elapsed,
                UtcStamp(started), UtcStamp(started.AddSeconds(elapsed)),
                usage.input, usage.output);
        }

        static string UtcStamp(DateTime value) =>
            value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
