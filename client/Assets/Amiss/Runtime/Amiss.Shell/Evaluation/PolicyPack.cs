using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// One verified public instructor policy pack, read from the bytes bundled
    /// with the build.
    ///
    /// The pack has three files: <c>pack.json</c> (version, file inventory with
    /// one SHA-256 per file, pack digest), <c>stage-data.json</c> (the existing
    /// mechanical envelope, with its own content digest), and
    /// <c>instructor-policy.json</c> (the instructor supply). This type checks
    /// exactly what the headless loader checks and nothing more: version
    /// support, per-file and content digests, cross-file identity, and the
    /// coupling between the prose supply and the mechanical envelope.
    /// Membership in a stage, an action set, or a question list is decided by
    /// the existing guide; it is not re-implemented here.
    ///
    /// A missing, corrupt, edited, or incompatible pack produces every reason
    /// and no pack. There is no second supply and no default.
    /// </summary>
    public sealed class PolicyPack
    {
        public const string PackFile = "pack.json";
        public const string StageDataFile = "stage-data.json";
        public const string InstructorFile = "instructor-policy.json";

        /// <summary>
        /// A defensive bound, not an approved design number. The pack is
        /// refused rather than sent when the assembled text exceeds it.
        /// </summary>
        public const int MaxSystemBytes = 64 * 1024;

        static readonly int[] SupportedPackMajors = { 1 };
        static readonly int[] SupportedStageDataMajors = { 1 };

        static readonly IReadOnlyList<string> PackFields = Array.AsReadOnly(new[]
        {
            "pack_format_version", "pack_id", "policy_version", "episode_id",
            "stage_data_format_version", "stage_data_file", "instructor_policy_file",
            "files", "integrity"
        });

        PolicyPack(string packId, string policyVersion, string episodeId,
            string packFormatVersion, string stageDataFormatVersion,
            IReadOnlyDictionary<string, object> stageData,
            IReadOnlyDictionary<string, object> instructor,
            string system, string packDigest, string stageDataDigest,
            IReadOnlyList<KeyValuePair<string, string>> fileDigests,
            IReadOnlyList<string> errors, bool loaded)
        {
            PackId = packId ?? "";
            PolicyVersion = policyVersion ?? "";
            EpisodeId = episodeId ?? "";
            PackFormatVersion = packFormatVersion ?? "";
            StageDataFormatVersion = stageDataFormatVersion ?? "";
            StageData = stageData;
            Instructor = instructor;
            System = system ?? "";
            PackDigest = packDigest ?? "";
            StageDataDigest = stageDataDigest ?? "";
            FileDigests = fileDigests ?? Array.Empty<KeyValuePair<string, string>>();
            Errors = errors ?? Array.Empty<string>();
            Loaded = loaded;
        }

        public bool Loaded { get; }
        public IReadOnlyList<string> Errors { get; }
        public string PackId { get; }
        public string PolicyVersion { get; }
        public string EpisodeId { get; }
        public string PackFormatVersion { get; }
        public string StageDataFormatVersion { get; }
        public IReadOnlyDictionary<string, object> StageData { get; }
        public IReadOnlyDictionary<string, object> Instructor { get; }
        public string System { get; }
        public string PackDigest { get; }
        public string StageDataDigest { get; }
        public IReadOnlyList<KeyValuePair<string, string>> FileDigests { get; }

        public int SystemBytes => Encoding.UTF8.GetByteCount(System);

        /// <summary>The transport-side envelope: version and system text only.</summary>
        public PolicyEnvelope Envelope() => Loaded
            ? new PolicyEnvelope(PolicyVersion, System)
            : throw new EvaluationContractException("no_policy",
                "No verified instructor policy pack is loaded, so no envelope exists.");

        /// <summary>One bounded line for the panel trace and evidence records.</summary>
        public string Summary => Loaded
            ? "policy=" + PolicyVersion + " pack=" + PackId + " stage-data=" +
              StageDataFormatVersion + " system-bytes=" +
              SystemBytes.ToString(CultureInfo.InvariantCulture) + " digest=" +
              Short(PackDigest)
            : "policy=refused";

        public string ReadableFailure => Loaded
            ? ""
            : "The bundled instructor policy pack was refused: " + string.Join("; ", Errors);

        static string Short(string digest) =>
            string.IsNullOrEmpty(digest) || digest.Length <= 12 ? digest : digest.Substring(0, 12);

        /// <summary>One refused pack carrying every reason.</summary>
        public static PolicyPack Refused(IEnumerable<string> errors) =>
            new(null, null, null, null, null, null, null, null, null, null, null,
                errors as IReadOnlyList<string> ?? errors?.ToList() ?? new List<string>(), false);

        /// <summary>
        /// Verify one pack from its three texts. Nothing is repaired: a failed
        /// check produces a refused pack with every reason found.
        /// </summary>
        public static PolicyPack Load(string packText, string stageDataText, string instructorText)
        {
            var errors = new List<string>();
            var pack = ParseObject(packText, PackFile, errors);
            var stageData = ParseObject(stageDataText, StageDataFile, errors);
            var instructor = ParseObject(instructorText, InstructorFile, errors);
            if (errors.Count > 0)
                return Refused(errors);

            foreach (var field in PackFields)
                if (!pack.ContainsKey(field))
                    errors.Add(PackFile + ": missing field " + field);
            foreach (var key in pack.Keys)
                if (!PackFields.Contains(key))
                    errors.Add(PackFile + ": unknown field " + key);
            if (errors.Count > 0)
                return Refused(errors);

            var packFormat = Text(pack, "pack_format_version");
            if (!Supported(packFormat, SupportedPackMajors))
                errors.Add(PackFile + ": unsupported pack format major " + packFormat);

            var integrity = Map(pack, "integrity");
            if (Text(integrity, "algorithm") != "sha256")
                errors.Add(PackFile + ": integrity algorithm is not sha256");
            if (Text(integrity, "status") != "verified")
                errors.Add(PackFile + ": integrity status is not verified");
            if (Text(integrity, "digest") != Digest(Without(pack, "integrity")))
                errors.Add(PackFile + ": integrity digest does not match the canonical payload");

            var digestByFile = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in List(pack, "files"))
            {
                var entry = item as IReadOnlyDictionary<string, object>;
                if (entry == null)
                {
                    errors.Add(PackFile + ": a file entry is not an object");
                    continue;
                }
                digestByFile[Text(entry, "path")] = Text(entry, "sha256");
            }
            CheckFile(StageDataFile, StageDataFile, stageDataText, digestByFile, errors);
            CheckFile(InstructorFile, InstructorFile, instructorText, digestByFile, errors);

            var declared = new[] { Text(pack, "stage_data_file"), Text(pack, "instructor_policy_file") };
            if (!declared.OrderBy(item => item, StringComparer.Ordinal)
                    .SequenceEqual(digestByFile.Keys.OrderBy(item => item, StringComparer.Ordinal),
                        StringComparer.Ordinal))
                errors.Add(PackFile + ": the file inventory does not match the declared files");

            var stageIntegrity = Map(stageData, "integrity");
            if (Text(stageIntegrity, "status") != "verified")
                errors.Add(StageDataFile + ": integrity status is not verified");
            var stageContentDigest = Digest(Without(stageData, "integrity"));
            if (Text(stageIntegrity, "digest") != stageContentDigest)
                errors.Add(StageDataFile + ": content digest does not match the canonical payload");

            var stageFormat = Text(stageData, "stage_data_format_version");
            if (stageFormat != Text(pack, "stage_data_format_version"))
                errors.Add(StageDataFile + ": format version differs from the pack manifest");
            if (!Supported(stageFormat, SupportedStageDataMajors))
                errors.Add(StageDataFile + ": unsupported format major " + stageFormat);
            if (Text(stageData, "policy_version") != Text(pack, "policy_version"))
                errors.Add(StageDataFile + ": policy version differs from the pack manifest");
            if (Text(stageData, "episode_id") != Text(pack, "episode_id"))
                errors.Add(StageDataFile + ": episode id differs from the pack manifest");
            if (Text(instructor, "policy_version") != Text(pack, "policy_version"))
                errors.Add(InstructorFile + ": policy version differs from the pack manifest");
            if (Text(instructor, "episode_id") != Text(pack, "episode_id"))
                errors.Add(InstructorFile + ": episode id differs from the pack manifest");

            CheckCoupling(stageData, instructor, errors);
            if (errors.Count > 0)
                return Refused(errors);

            var system = AssembleSystem(instructor);
            if (system.Trim().Length == 0)
                errors.Add(InstructorFile + ": the assembled system text is empty");
            if (Encoding.UTF8.GetByteCount(system) > MaxSystemBytes)
                errors.Add(InstructorFile + ": the assembled system text exceeds " +
                           MaxSystemBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
            if (errors.Count > 0)
                return Refused(errors);

            return new PolicyPack(
                Text(pack, "pack_id"), Text(pack, "policy_version"), Text(pack, "episode_id"),
                packFormat, stageFormat, stageData, instructor, system,
                Text(integrity, "digest"), stageContentDigest,
                digestByFile.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList(),
                Array.Empty<string>(), true);
        }

        static void CheckFile(string label, string path, string text,
            IReadOnlyDictionary<string, string> digestByFile, List<string> errors)
        {
            if (!digestByFile.TryGetValue(path, out var expected))
            {
                errors.Add(PackFile + ": listed file " + path + " is absent");
                return;
            }
            if (expected != Digest(text))
                errors.Add(path + ": file digest does not match the manifest");
        }

        /// <summary>The prose supply cannot drift away from the envelope.</summary>
        static void CheckCoupling(IReadOnlyDictionary<string, object> stageData,
            IReadOnlyDictionary<string, object> instructor, List<string> errors)
        {
            var graph = Map(stageData, "stage_graph");
            var stageIds = List(graph, "nodes")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(node => Text(node, "stage_id"))
                .ToList();
            var ruleStages = List(instructor, "stage_rules")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(rule => Text(rule, "stage_id"))
                .ToList();
            if (!stageIds.SequenceEqual(ruleStages, StringComparer.Ordinal))
                errors.Add(InstructorFile + ": stage rules do not name the stage graph in order");

            var questionIds = List(instructor, "next_questions")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "question_id"))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var rule in List(instructor, "stage_rules"))
                if (rule is IReadOnlyDictionary<string, object> ruleMap &&
                    !questionIds.Contains(Text(ruleMap, "question_id")))
                    errors.Add(InstructorFile + ": " + Text(ruleMap, "stage_id") +
                               " names an undefined question");
            foreach (var question in List(instructor, "next_questions"))
                if (question is IReadOnlyDictionary<string, object> questionMap &&
                    !stageIds.Contains(Text(questionMap, "stage_id")))
                    errors.Add(InstructorFile + ": question " + Text(questionMap, "question_id") +
                               " names an unknown stage");

            var declaredActions = List(Map(stageData, "allowed_action_set"), "action_ids")
                .Cast<string>().ToList();
            var catalogued = List(Map(instructor, "allowed_actions"), "actions")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "action_id"))
                .ToList();
            if (!declaredActions.SequenceEqual(catalogued, StringComparer.Ordinal))
                errors.Add(InstructorFile +
                           ": the allowed-action catalogue differs from the envelope");

            var catalogue = List(instructor, "tool_catalogue");
            if (catalogue.Count == 0)
                errors.Add(InstructorFile + ": the tool catalogue is absent or empty");
            var toolIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in catalogue)
            {
                if (entry is not IReadOnlyDictionary<string, object> tool)
                {
                    errors.Add(InstructorFile +
                               ": a tool catalogue entry is not an object");
                    continue;
                }
                foreach (var field in new[] { "tool_id", "name", "usage" })
                    if (string.IsNullOrWhiteSpace(Text(tool, field)))
                        errors.Add(InstructorFile + ": a tool entry is missing " + field);
                var toolId = Text(tool, "tool_id");
                if (!toolIds.Add(toolId))
                    errors.Add(InstructorFile + ": duplicate tool id " + toolId);
            }

            var wordingIds = List(instructor, "wording")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "wording_id"))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var category in List(Map(instructor, "mistake_classification"), "categories"))
                if (category is IReadOnlyDictionary<string, object> categoryMap &&
                    categoryMap.TryGetValue("wording_id", out var wording) && wording != null &&
                    !wordingIds.Contains(wording as string ?? ""))
                    errors.Add(InstructorFile + ": undefined wording ids");
            var scope = Map(instructor, "scope");
            if (scope.TryGetValue("out_of_scope_handling_id", out var scopeWording) &&
                scopeWording != null && !wordingIds.Contains(scopeWording as string ?? ""))
                errors.Add(InstructorFile + ": undefined wording ids");

            foreach (var threshold in List(instructor, "thresholds"))
            {
                if (threshold is not IReadOnlyDictionary<string, object> item)
                    continue;
                if (item.TryGetValue("value", out var value) && value != null)
                    errors.Add(InstructorFile + ": threshold " + Text(item, "threshold_id") +
                               " claims a value that this pack has not measured");
                if (item.TryGetValue("stage_id", out var stage) && stage != null &&
                    !stageIds.Contains(stage as string ?? ""))
                    errors.Add(InstructorFile + ": threshold " + Text(item, "threshold_id") +
                               " names an unknown stage");
            }
        }

        /// <summary>
        /// The one system text a judge path assembles at the send boundary.
        /// Order is fixed so the text and its hash are reproducible.
        /// </summary>
        public static string AssembleSystem(IReadOnlyDictionary<string, object> instructor)
        {
            var lines = new List<string>();

            void Block(string title, IEnumerable<string> items)
            {
                var list = items.ToList();
                if (list.Count == 0)
                    return;
                lines.Add(title + ":");
                lines.AddRange(list.Select(item => "  " + item));
            }

            Block("Instructions", Strings(Map(instructor, "system_instructions"), "text"));
            Block("Series rules", Rules(instructor, "series_rules"));
            Block("Episode rules", Rules(instructor, "episode_rules"));
            Block("Tools and controls", List(instructor, "tool_catalogue")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "tool_id") + " (" + Text(item, "name") +
                                "): " + Text(item, "usage")));

            var questions = List(instructor, "next_questions")
                .Cast<IReadOnlyDictionary<string, object>>()
                .ToDictionary(item => Text(item, "stage_id"), item => Text(item, "text"),
                    StringComparer.Ordinal);
            var stageLines = new List<string>();
            foreach (var rule in List(instructor, "stage_rules").Cast<IReadOnlyDictionary<string, object>>())
            {
                var stageId = Text(rule, "stage_id");
                var props = Strings(rule, "allowed_props").ToList();
                stageLines.Add(stageId + " (" + Text(rule, "title") + "): " +
                               Text(rule, "intent") + " Props: " +
                               (props.Count == 0 ? "none" : string.Join(", ", props)) +
                               ". Proposals: " + Text(rule, "proposal_expectation") +
                               ". Prediction: " + Text(rule, "prediction") + ". Question: " +
                               questions.GetValueOrDefault(stageId, ""));
            }
            Block("Stages", stageLines);

            Block("Allowed actions", List(Map(instructor, "allowed_actions"), "actions")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "action_id") + " (" + Text(item, "tool") + " " +
                                Text(item, "operation") + "): " + Text(item, "purpose")));

            Block("Reply classification",
                List(Map(instructor, "mistake_classification"), "categories")
                    .Cast<IReadOnlyDictionary<string, object>>()
                    .Select(item => Text(item, "category_id") + ": " + Text(item, "trigger") +
                                    " -> " + Text(item, "instructor_response")));

            Block("Wording", List(instructor, "wording")
                .Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "wording_id") + ": " + Text(item, "text")));

            return string.Join("\n", lines).Trim();
        }

        static IEnumerable<string> Rules(IReadOnlyDictionary<string, object> source, string field) =>
            List(source, field).Cast<IReadOnlyDictionary<string, object>>()
                .Select(item => Text(item, "rule_id") + ": " + Text(item, "text"));

        static bool Supported(string version, int[] majors)
        {
            var head = (version ?? "").Split('.')[0];
            return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture,
                       out var major) && majors.Contains(major);
        }

        static IReadOnlyDictionary<string, object> ParseObject(string text, string label,
            List<string> errors)
        {
            try
            {
                return CanonicalJsonReader.Parse(text) as IReadOnlyDictionary<string, object>
                       ?? throw new EvaluationContractException("malformed_parameters",
                           label + ": not an object");
            }
            catch (EvaluationContractException error)
            {
                errors.Add(label + ": " + error.Message);
                return null;
            }
        }

        public static string Digest(string text) =>
            CanonicalJson.Sha256Hex(Encoding.UTF8.GetBytes(text ?? ""));

        public static string Digest(IReadOnlyDictionary<string, object> value) =>
            CanonicalJson.Sha256Hex(value);

        static IReadOnlyDictionary<string, object> Without(IReadOnlyDictionary<string, object> value,
            string key)
        {
            var copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in value)
                if (pair.Key != key)
                    copy[pair.Key] = pair.Value;
            return copy;
        }

        public static IReadOnlyDictionary<string, object> Map(IReadOnlyDictionary<string, object> source,
            string key) => source != null && source.TryGetValue(key, out var value) &&
                        value is IReadOnlyDictionary<string, object> map
            ? map
            : new Dictionary<string, object>(StringComparer.Ordinal);

        public static IReadOnlyList<object> List(IReadOnlyDictionary<string, object> source,
            string key) => source != null && source.TryGetValue(key, out var value) &&
                        value is IReadOnlyList<object> list
            ? list
            : Array.Empty<object>();

        public static string Text(IReadOnlyDictionary<string, object> source, string key) =>
            source != null && source.TryGetValue(key, out var value) ? value as string ?? "" : "";

        static IEnumerable<string> Strings(IReadOnlyDictionary<string, object> source, string key) =>
            List(source, key).Cast<string>();
    }
}

