using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Amiss.EditorTools
{
    /// <summary>
    /// Command-line Android build. Re-asserts the frozen build identity
    /// settings before every build so the APK cannot drift from the locked
    /// project configuration.
    /// </summary>
    public static class AmissBuild
    {
        const string ScenePath = "Assets/Amiss/Shell.unity";
        const string ApkPath = "Builds/Amiss.apk";
        const string DebugApkPath = "Builds/Amiss-debug.apk";

        public static void ConfigureAndBuild()
        {
            Configure();
            Build(ApkPath, BuildOptions.None);
        }

        public static void ConfigureAndBuildDevelopment()
        {
            Configure();
            Build(ApkPath, BuildOptions.Development);
        }

        public static void ConfigureAndBuildDebug()
        {
            PlayerSettings.Android.useCustomKeystore = false;
            Configure();
            Build(DebugApkPath, BuildOptions.Development);
        }

        static void Configure()
        {
            if (!File.Exists(ScenePath))
                throw new System.Exception($"{ScenePath} missing — run AmissProjectSetup.Setup first");

            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.magicrayality.amiss");
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel34;

            OVRManifestPreprocessor.GenerateOrUpdateAndroidManifest(true);
            AmissProjectSetup.ScrubBuildMachineAddress();

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        static void Build(string apkPath, BuildOptions options)
        {
            Directory.CreateDirectory("Builds");
            var report = BuildPipeline.BuildPlayer(
                EditorBuildSettings.scenes, apkPath, BuildTarget.Android, options);
            Debug.Log($"[Amiss] Build result: {report.summary.result}, " +
                      $"errors={report.summary.totalErrors}, size={report.summary.totalSize}");
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}