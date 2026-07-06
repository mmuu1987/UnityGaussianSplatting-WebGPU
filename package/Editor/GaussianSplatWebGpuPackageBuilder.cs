// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatWebGpuPackageBuilder
    {
        const string kBundleAssetsFolder = "Assets/bundleAssets";

        [MenuItem("Tools/Gaussian WebGPU/资源包/一键构建选中Renderer资源包并配置")]
        static void BuildSelectedRendererAssetsToStreamingAssetsAndPrepareMenu()
        {
            BuildSelectedRendererAssetsToStreamingAssetsAndPrepare();
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/一键构建选中Renderer资源包并配置", true)]
        static bool CanBuildSelectedRendererAssetsToStreamingAssetsAndPrepareMenu()
        {
            return GetSelectedRenderers().Count != 0;
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到bundleAssets")]
        static void BuildSelectedGaussianSplatAssetBundleToBundleAssets()
        {
            if (!TryGetSelectedGaussianSplatAsset(out var asset, out var assetPath))
                return;

            string packageId = MakeSafePackageId(asset.name);
            string outputFolder = GetBundleAssetsPackageFolder(packageId);
            if (!ClearBundleAssetsPackageFolder(packageId))
                return;

            BuildGaussianSplatAssetBundle(asset, assetPath, outputFolder, packageId);
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到bundleAssets", true)]
        static bool CanBuildSelectedGaussianSplatAssetBundleToBundleAssets()
        {
            return Selection.activeObject is GaussianSplatAsset;
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到自定义目录")]
        static void BuildSelectedGaussianSplatAssetBundle()
        {
            if (!TryGetSelectedGaussianSplatAsset(out var asset, out var assetPath))
                return;

            string outputFolder = EditorUtility.SaveFolderPanel(
                "选择Gaussian WebGPU资源包输出目录",
                Directory.Exists(kBundleAssetsFolder) ? kBundleAssetsFolder : "Assets",
                "");
            if (string.IsNullOrEmpty(outputFolder))
                return;

            string packageId = MakeSafePackageId(asset.name);
            BuildGaussianSplatAssetBundle(asset, assetPath, outputFolder, packageId);
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到自定义目录", true)]
        static bool CanBuildSelectedGaussianSplatAssetBundle()
        {
            return Selection.activeObject is GaussianSplatAsset;
        }

        static bool BuildGaussianSplatAssetBundle(GaussianSplatAsset asset, string assetPath, string outputFolder, string packageId)
        {
            if (!EnsureWebGlBuildTarget())
                return false;

            Directory.CreateDirectory(outputFolder);
            string bundleName = packageId + ".bundle";
            var build = new AssetBundleBuild
            {
                assetBundleName = bundleName,
                assetNames = new[] { assetPath }
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                outputFolder,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.WebGL);

            if (manifest == null)
            {
                EditorUtility.DisplayDialog("构建Gaussian WebGPU资源包", "AssetBundle构建失败，请查看Unity Console里的详细错误。", "确定");
                return false;
            }

            AssetDatabase.Refresh();
            string bundlePath = Path.Combine(outputFolder, bundleName);
            Debug.Log(BuildSizeReport(asset, bundlePath), asset);
            return true;
        }

        static bool EnsureWebGlBuildTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                return true;

            bool switchTarget = EditorUtility.DisplayDialog(
                "构建Gaussian WebGPU资源包",
                "当前Build Target不是WebGL。WebGPU资源包必须使用WebGL目标平台构建，否则浏览器能下载但无法打开AssetBundle。\n\n是否现在切换到WebGL？",
                "切换到WebGL",
                "取消");
            if (!switchTarget)
                return false;

            return EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        }

        static bool TryGetSelectedGaussianSplatAsset(out GaussianSplatAsset asset, out string assetPath)
        {
            asset = Selection.activeObject as GaussianSplatAsset;
            assetPath = AssetDatabase.GetAssetPath(asset);
            if (asset != null && !string.IsNullOrEmpty(assetPath))
                return true;

            EditorUtility.DisplayDialog("构建Gaussian WebGPU资源包", "请先在Project窗口里选中一个GaussianSplatAsset。", "确定");
            return false;
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/仅配置选中Renderer从资源包加载")]
        static void PrepareSelectedRenderersForBundleLoading()
        {
            List<GaussianSplatRenderer> renderers = GetSelectedRenderers();
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog("配置Gaussian WebGPU资源包加载", "请先在Hierarchy里选中一个或多个带GaussianSplatRenderer的物体。", "确定");
                return;
            }

            int prepared = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                GaussianSplatAsset asset = renderer.asset;
                var loader = renderer.GetComponent<GaussianSplatAssetBundleLoader>();
                string packageId = GetRendererPackageId(renderer, loader, asset);

                if (loader == null)
                    loader = Undo.AddComponent<GaussianSplatAssetBundleLoader>(renderer.gameObject);
                else
                    Undo.RecordObject(loader, "配置Gaussian WebGPU资源包加载器");

                ConfigureLoader(loader, renderer, asset, packageId);

                EditorUtility.SetDirty(loader);
                ++prepared;
            }

            Debug.Log($"已配置 {prepared} 个GaussianSplatRenderer从WebGPU资源包加载。资源包源目录：{kBundleAssetsFolder}");
        }

        static void BuildSelectedRendererAssetsToStreamingAssetsAndPrepare()
        {
            List<GaussianSplatRenderer> renderers = GetSelectedRenderers();
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog("构建Gaussian WebGPU资源包", "请先在Hierarchy里选中一个或多个带GaussianSplatRenderer的物体。", "确定");
                return;
            }

            int built = 0;
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                GaussianSplatAsset asset = renderer.asset;
                string assetPath = AssetDatabase.GetAssetPath(asset);
                if (asset == null || string.IsNullOrEmpty(assetPath))
                {
                    Debug.LogWarning($"已跳过 {renderer.name}：GaussianSplatRenderer.Asset为空，没有可构建的源资源。", renderer);
                    continue;
                }

                var loader = renderer.GetComponent<GaussianSplatAssetBundleLoader>();
                string packageId = GetRendererPackageId(renderer, loader, asset);
                string outputFolder = GetBundleAssetsPackageFolder(packageId);
                if (!ClearBundleAssetsPackageFolder(packageId))
                    continue;

                if (!BuildGaussianSplatAssetBundle(asset, assetPath, outputFolder, packageId))
                    continue;

                if (loader == null)
                    loader = Undo.AddComponent<GaussianSplatAssetBundleLoader>(renderer.gameObject);
                else
                    Undo.RecordObject(loader, "配置Gaussian WebGPU资源包加载器");

                ConfigureLoader(loader, renderer, asset, packageId);

                EditorUtility.SetDirty(loader);
                ++built;
            }

            Debug.Log($"已构建并配置 {built} 个GaussianSplatRenderer资源包。输出目录：{kBundleAssetsFolder}");
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/仅配置选中Renderer从资源包加载", true)]
        static bool CanPrepareSelectedRenderersForBundleLoading()
        {
            return GetSelectedRenderers().Count != 0;
        }

        static List<GaussianSplatRenderer> GetSelectedRenderers()
        {
            var result = new List<GaussianSplatRenderer>();
            var seen = new HashSet<GaussianSplatRenderer>();
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null)
                    continue;

                var renderers = go.GetComponentsInChildren<GaussianSplatRenderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null && seen.Add(renderer))
                        result.Add(renderer);
                }
            }
            return result;
        }

        static string GetBundleAssetsPackageFolder(string packageId)
        {
            return $"{kBundleAssetsFolder}/{packageId}";
        }

        static bool ClearBundleAssetsPackageFolder(string packageId)
        {
            string packageFolderAssetPath = GetBundleAssetsPackageFolder(packageId);
            string packageFolder = Path.GetFullPath(packageFolderAssetPath);
            string bundleAssetsFolder = Path.GetFullPath(kBundleAssetsFolder);
            if (!IsChildPath(bundleAssetsFolder, packageFolder))
            {
                Debug.LogError($"拒绝清理 bundleAssets 之外的 Gaussian WebGPU 资源包目录：{packageFolder}");
                return false;
            }

            if (AssetDatabase.IsValidFolder(packageFolderAssetPath))
            {
                AssetDatabase.DeleteAsset(packageFolderAssetPath);
                AssetDatabase.Refresh();
            }
            else if (Directory.Exists(packageFolder))
            {
                Directory.Delete(packageFolder, true);
                string metaPath = packageFolder + ".meta";
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                AssetDatabase.Refresh();
            }

            Directory.CreateDirectory(packageFolder);
            AssetDatabase.Refresh();
            Debug.Log($"已清理同名 Gaussian WebGPU 资源包缓存：{packageFolderAssetPath}");
            return true;
        }

        static string GetStreamingAssetsPackageRelativeBundlePath(string packageId)
        {
            return $"{packageId}/{packageId}.bundle";
        }

        static string GetRendererPackageId(GaussianSplatRenderer renderer, GaussianSplatAssetBundleLoader loader, GaussianSplatAsset asset)
        {
            if (loader != null && !string.IsNullOrWhiteSpace(loader.m_PackageId))
                return MakeSafePackageId(loader.m_PackageId);
            return MakeSafePackageId(asset ? asset.name : renderer.name);
        }

        static void ConfigureLoader(GaussianSplatAssetBundleLoader loader, GaussianSplatRenderer renderer, GaussianSplatAsset asset, string packageId)
        {
            loader.m_Target = renderer;
            loader.m_PackageId = packageId;
            loader.m_BaseUrl = string.Empty;
            loader.m_BundleUrl = string.Empty;
            loader.m_BundleFileName = GetStreamingAssetsPackageRelativeBundlePath(packageId);
            loader.m_AssetName = asset ? asset.name : string.Empty;
            loader.m_ClearRendererAssetBeforeLoad = true;
        }

        static string MakeSafePackageId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "gaussian_splat";

            char[] chars = value.Trim().ToLowerInvariant().ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
            {
                char c = chars[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                chars[i] = valid ? c : '_';
            }
            return new string(chars);
        }

        static bool IsChildPath(string parentPath, string childPath)
        {
            string safeParentPath = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string safeChildPath = Path.GetFullPath(childPath);
            return safeChildPath.StartsWith(safeParentPath, System.StringComparison.OrdinalIgnoreCase);
        }

        static string BuildSizeReport(GaussianSplatAsset asset, string bundlePath)
        {
            long posBytes = GetTextAssetSize(asset.posData);
            long otherBytes = GetTextAssetSize(asset.otherData);
            long colorBytes = GetTextAssetSize(asset.colorData);
            long shBytes = GetTextAssetSize(asset.shData);
            long chunkBytes = GetTextAssetSize(asset.chunkData);
            long sourceBytes = posBytes + otherBytes + colorBytes + shBytes + chunkBytes;
            long bundleBytes = File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0;
            double bundlePercent = sourceBytes > 0 ? bundleBytes * 100.0 / sourceBytes : 0;

            return
                "Gaussian WebGPU资源包构建完成\n" +
                $"路径：{bundlePath}\n" +
                $"构建目标：{BuildTarget.WebGL}\n" +
                $"Splat数量：{asset.splatCount:N0}\n" +
                $"Bundle大小：{FormatBytes(bundleBytes)}\n" +
                $"原始Gaussian数据：{FormatBytes(sourceBytes)}\n" +
                $"Bundle/原始数据：{bundlePercent:0.0}%\n" +
                $"  位置数据：{FormatBytes(posBytes)}\n" +
                $"  变换数据：{FormatBytes(otherBytes)}\n" +
                $"  颜色数据：{FormatBytes(colorBytes)}\n" +
                $"  SH数据：{FormatBytes(shBytes)}\n" +
                $"  分块数据：{FormatBytes(chunkBytes)}\n" +
                $"格式：pos={asset.posFormat}, scale={asset.scaleFormat}, color={asset.colorFormat}, sh={asset.shFormat}\n" +
                "压缩：LZ4分块压缩AssetBundle";
        }

        static long GetTextAssetSize(TextAsset asset)
        {
            return asset != null ? asset.dataSize : 0;
        }

        static string FormatBytes(long bytes)
        {
            const double kb = 1024.0;
            const double mb = kb * 1024.0;
            const double gb = mb * 1024.0;

            if (bytes >= gb)
                return $"{bytes / gb:0.00} GB ({bytes:N0} bytes)";
            if (bytes >= mb)
                return $"{bytes / mb:0.00} MB ({bytes:N0} bytes)";
            if (bytes >= kb)
                return $"{bytes / kb:0.00} KB ({bytes:N0} bytes)";
            return $"{bytes:N0} bytes";
        }
    }
}
