using UnityEditor;

namespace Amiss.EditorTools
{
    /// <summary>
    /// Imports the wall display in meters, without cameras, lights, or
    /// embedded materials. Normals stay linear. Called again from the
    /// Android build so a fresh checkout does not bake the first default.
    /// </summary>
    public sealed class WallDashboardImport : AssetPostprocessor
    {
        const string Folder = "/WallDashboard/";
        const string KnifeFolder = "/KnifeSwitch/";
        const string BatFolder = "/BaseballBat/";

        public static void EnsureImported()
        {
            AssetDatabase.ImportAsset(
                "Assets/Amiss/Art/WallDashboard",
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(
                "Assets/Amiss/Art/KnifeSwitch",
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(
                "Assets/Amiss/Art/BaseballBat",
                ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
        }

        void OnPreprocessModel()
        {
            var wall = assetPath.IndexOf(Folder, System.StringComparison.Ordinal) >= 0;
            var knife = assetPath.IndexOf(KnifeFolder, System.StringComparison.Ordinal) >= 0;
            var bat = assetPath.IndexOf(BatFolder, System.StringComparison.Ordinal) >= 0;
            if (!wall && !knife && !bat)
                return;
            var importer = (ModelImporter)assetImporter;
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = wall || bat;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.meshCompression = ModelImporterMeshCompression.Off;
        }

        void OnPreprocessTexture()
        {
            if (assetPath.IndexOf(Folder, System.StringComparison.Ordinal) < 0 &&
                assetPath.IndexOf(KnifeFolder, System.StringComparison.Ordinal) < 0 &&
                assetPath.IndexOf(BatFolder, System.StringComparison.Ordinal) < 0)
                return;
            var importer = (TextureImporter)assetImporter;
            var name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            var normal = name.EndsWith("_Normal", System.StringComparison.Ordinal);
            var data = name.EndsWith("_Roughness", System.StringComparison.Ordinal) ||
                       name.EndsWith("_Metallic", System.StringComparison.Ordinal) ||
                       name.EndsWith("_Emission", System.StringComparison.Ordinal);
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = !normal && !data;
            importer.mipmapEnabled = true;
            importer.textureCompression = TextureImporterCompression.Compressed;
            var small = name.StartsWith("LED_", System.StringComparison.Ordinal) ||
                        name.StartsWith("Teal_", System.StringComparison.Ordinal);
            importer.maxTextureSize = small ? 64 : name.StartsWith("Label_", System.StringComparison.Ordinal) ? 256 : 1024;
        }
    }
}
