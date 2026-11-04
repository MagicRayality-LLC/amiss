namespace Amiss.Contracts
{
    /// <summary>
    /// Framework contract versions. These mirror the independent version axes
    /// recorded in schema/versions.json at the repository root; the edit-mode
    /// test suite fails if the two drift apart.
    /// </summary>
    public static class FrameworkApiVersions
    {
        public const string FrameworkApi = "1.0.0";
        public const string SnapshotEnvelope = "1.0.0";
        public const string ToolSchema = "1.0.0";
        public const string SaveFormat = "1.0.0";
        public const string StageDataFormat = "1.0.0";
        public const string ContextPayload = "1.2.0";
        public const string FixtureFormat = "1.0.0";
        public const string ProvenanceContract = "1.0.0";
    }
}
