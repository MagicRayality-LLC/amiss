using Amiss.Contracts;
using UnityEngine;

namespace Amiss.Shell
{
    /// <summary>
    /// Build and contract identity for logs and Trial identity areas. Every
    /// log line produced through this type names the app build version and all
    /// applicable framework contract versions; it never records device
    /// serials, account names, or room-identifiable information.
    /// </summary>
    public static class BuildIdentity
    {
        public const string CompanyName = "MagicRayality";
        public const string ProductName = "Amiss";

        public static string Compose(string appVersion, string unityVersion) =>
            $"app={appVersion} product={ProductName} company={CompanyName} " +
            $"unity={unityVersion} " +
            $"framework-api={FrameworkApiVersions.FrameworkApi} " +
            $"snapshot={FrameworkApiVersions.SnapshotEnvelope} " +
            $"tool={FrameworkApiVersions.ToolSchema} " +
            $"save={FrameworkApiVersions.SaveFormat} " +
            $"stage={FrameworkApiVersions.StageDataFormat} " +
            $"context={FrameworkApiVersions.ContextPayload} " +
            $"fixture={FrameworkApiVersions.FixtureFormat} " +
            $"provenance={FrameworkApiVersions.ProvenanceContract} " +
            $"shell-capabilities={ShellCapabilityManifest.ThisBuild().ManifestVersion}";

        public static void Log()
        {
            Debug.Log("[Amiss] build-identity " + Compose(Application.version, Application.unityVersion));
        }
    }
}
