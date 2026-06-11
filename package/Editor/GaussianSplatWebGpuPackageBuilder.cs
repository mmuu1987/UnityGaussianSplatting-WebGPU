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
        const string kDefaultPackageFolder = "Assets/StreamingAssets/GaussianSplatPackages";

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

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到StreamingAssets")]
        static void BuildSelectedGaussianSplatAssetBundleToStreamingAssets()
        {
            if (!TryGetSelectedGaussianSplatAsset(out var asset, out var assetPath))
                return;

            string packageId = MakeSafePackageId(asset.name);
            string outputFolder = GetStreamingAssetsPackageFolder(packageId);
            BuildGaussianSplatAssetBundle(asset, assetPath, outputFolder, packageId);
        }

        [MenuItem("Tools/Gaussian WebGPU/资源包/构建选中的GaussianSplatAsset到StreamingAssets", true)]
        static bool CanBuildSelectedGaussianSplatAssetBundleToStreamingAssets()
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
                Directory.Exists(kDefaultPackageFolder) ? kDefaultPackageFolder : "Assets",
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

        static void BuildGaussianSplatAssetBundle(GaussianSplatAsset asset, string assetPath, string outputFolder, string packageId)
        {
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
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                EditorUtility.DisplayDialog("构建Gaussian WebGPU资源包", "AssetBundle构建失败，请查看Unity Console里的详细错误。", "确定");
                return;
            }

            AssetDatabase.Refresh();
            string bundlePath = Path.Combine(outputFolder, bundleName);
            Debug.Log(BuildSizeReport(asset, bundlePath), asset);
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

                Undo.RecordObject(renderer, "清空直接GaussianSplatAsset引用");
                renderer.m_Asset = null;
                EditorUtility.SetDirty(loader);
                EditorUtility.SetDirty(renderer);
                ++prepared;
            }

            Debug.Log($"已配置 {prepared} 个GaussianSplatRenderer从WebGPU资源包加载。默认资源包目录：{kDefaultPackageFolder}");
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
                string outputFolder = GetStreamingAssetsPackageFolder(packageId);
                BuildGaussianSplatAssetBundle(asset, assetPath, outputFolder, packageId);

                if (loader == null)
                    loader = Undo.AddComponent<GaussianSplatAssetBundleLoader>(renderer.gameObject);
                else
                    Undo.RecordObject(loader, "配置Gaussian WebGPU资源包加载器");

                ConfigureLoader(loader, renderer, asset, packageId);

                Undo.RecordObject(renderer, "清空直接GaussianSplatAsset引用");
                renderer.m_Asset = null;
                EditorUtility.SetDirty(loader);
                EditorUtility.SetDirty(renderer);
                ++built;
            }

            Debug.Log($"已构建并配置 {built} 个GaussianSplatRenderer资源包。输出目录：{kDefaultPackageFolder}");
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

        static string GetStreamingAssetsPackageFolder(string packageId)
        {
            return $"{kDefaultPackageFolder}/{packageId}";
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
                $"构建目标：{EditorUserBuildSettings.activeBuildTarget}\n" +
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
