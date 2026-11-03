using System.Collections.Generic;
using UnityEngine;

namespace Amiss.Shell.Evaluation
{
    /// <summary>
    /// Reads the public instructor policy pack that the build stages into
    /// <c>Resources/PolicyPack</c> from the repository's <c>policy/</c>
    /// directory.
    ///
    /// The staging step copies the pack's bytes unchanged, so the digests the
    /// pack carries still verify here. This type never edits, repairs, or
    /// substitutes content: an absent or refused pack leaves the tutor port
    /// closed with a readable reason, and the default path continues without
    /// dynamic wording.
    /// </summary>
    public static class BundledPolicyPack
    {
        public const string ResourceRoot = "PolicyPack";

        /// <summary>
        /// The staged pack, or a refused pack carrying every reason. Unity
        /// strips the file extension when it imports a text asset, so the
        /// resource name is the file name without `.json`.
        /// </summary>
        public static PolicyPack Load()
        {
            var pack = Resources.Load<TextAsset>(ResourceRoot + "/" + ResourceName(PolicyPack.PackFile));
            var stageData = Resources.Load<TextAsset>(
                ResourceRoot + "/" + ResourceName(PolicyPack.StageDataFile));
            var instructor = Resources.Load<TextAsset>(
                ResourceRoot + "/" + ResourceName(PolicyPack.InstructorFile));

            var missing = new List<string>();
            if (pack == null)
                missing.Add("the bundled pack manifest is absent");
            if (stageData == null)
                missing.Add("the bundled stage data is absent");
            if (instructor == null)
                missing.Add("the bundled instructor supply is absent");
            if (missing.Count > 0)
                return PolicyPack.Refused(missing);

            return PolicyPack.Load(pack.text, stageData.text, instructor.text);
        }

        /// <summary>The resource name Unity assigns to a staged text asset.</summary>
        public static string ResourceName(string fileName) =>
            fileName.EndsWith(".json")
                ? fileName.Substring(0, fileName.Length - ".json".Length)
                : fileName;

        /// <summary>
        /// The one-line panel state for the pack itself, so a refused or absent
        /// pack is visible as its own reason rather than as a silent default.
        /// </summary>
        public static string TraceLine(PolicyPack pack) =>
            pack != null && pack.Loaded
                ? "Policy: " + pack.Summary
                : "Policy: closed - " + (pack?.ReadableFailure ?? "no pack");
    }
}
