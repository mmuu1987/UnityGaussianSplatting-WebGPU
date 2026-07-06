// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatWebGpuPlayerBuilder
    {
        const string kDefaultBuildFolder = "Builds/WebGPU";
        const string kBundleAssetsFolder = "Assets/bundleAssets";
        const string kStreamingAssetsPackageFolder = "Assets/StreamingAssets/GaussianSplatPackages";
        const string kFullscreenTemplate = "PROJECT:GaussianFullscreen";
        const int kDefaultServerPort = 8080;
        const int kMaxServerPort = 8099;

        static HttpListener s_LocalServer;
        static CancellationTokenSource s_LocalServerCancel;
        static int s_LocalServerPort;

        internal static bool ClearRendererAssetsDuringBuild { get; private set; }

        [MenuItem("Tools/Gaussian WebGPU/发布/构建WebGPU版本")]
        static void BuildWebGpuPlayer()
        {
            BuildWebGpuPlayer(false);
        }

        [MenuItem("Tools/Gaussian WebGPU/发布/构建并运行WebGPU版本")]
        static void BuildAndRunWebGpuPlayer()
        {
            BuildWebGpuPlayer(true);
        }

        [MenuItem("Tools/Gaussian WebGPU/发布/运行上一次WebGPU构建")]
        static void RunLastWebGpuBuild()
        {
            string root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), kDefaultBuildFolder));
            string indexPath = Path.Combine(root, "index.html");
            if (!File.Exists(indexPath))
            {
                EditorUtility.DisplayDialog(
                    "运行Gaussian WebGPU构建",
                    $"没有找到上一次WebGPU构建：\n{indexPath}\n\n请先执行“构建WebGPU版本”或“构建并运行WebGPU版本”。",
                    "确定");
                return;
            }

            if (!StartLocalServer(root, out string url))
                return;

            Application.OpenURL(url);
            Debug.Log($"Gaussian WebGPU本地服务器已启动：{url}\n根目录：{root}");
        }

        [MenuItem("Tools/Gaussian WebGPU/发布/停止本地WebGPU服务器")]
        static void StopLocalWebGpuServer()
        {
            StopLocalServer();
        }

        static void BuildWebGpuPlayer(bool autoRun)
        {
            if (!EnsureWebGlBuildTarget())
                return;

            TrySetWebGpuOnlyGraphicsApi();
            TrySetFullscreenTemplate();

            string[] scenes = GetBuildScenes();
            if (scenes.Length == 0)
                return;

            if (!PreflightPackageSetup())
                return;

            if (!StageBundleAssetsForBuild(scenes))
                return;

            Directory.CreateDirectory(kDefaultBuildFolder);
            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = kDefaultBuildFolder,
                target = BuildTarget.WebGL,
                options = autoRun ? BuildOptions.AutoRunPlayer : BuildOptions.None
            };

            if (EditorUserBuildSettings.development)
                options.options |= BuildOptions.Development;
            else
                Debug.Log("Development Build未开启。仍然可以在URL后加 ?gsPanel=1 打开运行时调参面板。");

            ClearRendererAssetsDuringBuild = true;
            try
            {
                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;
                Debug.Log(
                    "Gaussian WebGPU版本构建完成\n" +
                    $"结果：{summary.result}\n" +
                    $"输出目录：{summary.outputPath}\n" +
                    $"总大小：{FormatBytes((long)summary.totalSize)}\n" +
                    $"场景：{string.Join(", ", scenes)}\n" +
                    $"Development Build：{EditorUserBuildSettings.development}\n" +
                    $"自动运行：{autoRun}");
            }
            finally
            {
                ClearRendererAssetsDuringBuild = false;
                ClearStagedStreamingAssetsPackageFolder();
            }
        }

        static bool StartLocalServer(string root, out string url)
        {
            StopLocalServer();

            for (int port = kDefaultServerPort; port <= kMaxServerPort; ++port)
            {
                var listener = new HttpListener();
                string prefix = $"http://localhost:{port}/";
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    s_LocalServer = listener;
                    s_LocalServerCancel = new CancellationTokenSource();
                    s_LocalServerPort = port;
                    EditorApplication.quitting -= StopLocalServer;
                    EditorApplication.quitting += StopLocalServer;
                    _ = Task.Run(() => ServeLocalBuildAsync(listener, root, s_LocalServerCancel.Token));
                    url = prefix;
                    return true;
                }
                catch
                {
                    listener.Close();
                }
            }

            url = string.Empty;
            EditorUtility.DisplayDialog(
                "运行Gaussian WebGPU构建",
                $"无法在端口 {kDefaultServerPort}-{kMaxServerPort} 启动本地HTTP服务器。可能端口被占用，或Windows阻止了HttpListener。",
                "确定");
            return false;
        }

        static void StopLocalServer()
        {
            s_LocalServerCancel?.Cancel();
            s_LocalServerCancel?.Dispose();
            s_LocalServerCancel = null;

            if (s_LocalServer != null)
            {
                try
                {
                    s_LocalServer.Stop();
                    s_LocalServer.Close();
                }
                catch
                {
                    // ignored
                }
            }

            if (s_LocalServer != null)
                Debug.Log($"已停止Gaussian WebGPU本地服务器，端口：{s_LocalServerPort}。");

            s_LocalServer = null;
            s_LocalServerPort = 0;
            EditorApplication.quitting -= StopLocalServer;
        }

        static async Task ServeLocalBuildAsync(HttpListener listener, string root, CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                        Debug.LogWarning("Gaussian WebGPU本地服务器意外停止。");
                    break;
                }

                _ = Task.Run(() => ServeLocalFile(context, root), token);
            }
        }

        static void ServeLocalFile(HttpListenerContext context, string root)
        {
            try
            {
                string relativePath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
                if (string.IsNullOrEmpty(relativePath))
                    relativePath = "index.html";

                relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
                string filePath = Path.GetFullPath(Path.Combine(root, relativePath));
                string safeRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!filePath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                {
                    context.Response.StatusCode = 404;
                    WriteText(context.Response, "404 Not Found");
                    return;
                }

                byte[] bytes = File.ReadAllBytes(filePath);
                SetResponseHeaders(context.Response, filePath);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    WriteText(context.Response, ex.Message);
                }
                catch
                {
                    // ignored
                }
            }
            finally
            {
                try
                {
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // ignored
                }
            }
        }

        static void SetResponseHeaders(HttpListenerResponse response, string filePath)
        {
            string path = filePath.Replace('\\', '/').ToLowerInvariant();
            if (path.EndsWith(".br"))
            {
                response.AddHeader("Content-Encoding", "br");
                path = path.Substring(0, path.Length - 3);
            }
            else if (path.EndsWith(".gz"))
            {
                response.AddHeader("Content-Encoding", "gzip");
                path = path.Substring(0, path.Length - 3);
            }

            response.ContentType = GetContentType(path);
            response.AddHeader("Access-Control-Allow-Origin", "*");
        }

        static string GetContentType(string path)
        {
            if (path.EndsWith(".html")) return "text/html";
            if (path.EndsWith(".js")) return "application/javascript";
            if (path.EndsWith(".wasm")) return "application/wasm";
            if (path.EndsWith(".json")) return "application/json";
            if (path.EndsWith(".css")) return "text/css";
            if (path.EndsWith(".png")) return "image/png";
            if (path.EndsWith(".jpg") || path.EndsWith(".jpeg")) return "image/jpeg";
            if (path.EndsWith(".svg")) return "image/svg+xml";
            if (path.EndsWith(".bundle")) return "application/octet-stream";
            if (path.EndsWith(".data")) return "application/octet-stream";
            return "application/octet-stream";
        }

        static void WriteText(HttpListenerResponse response, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            response.ContentType = "text/plain";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static bool EnsureWebGlBuildTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL)
                return true;

            bool switchTarget = EditorUtility.DisplayDialog(
                "构建Gaussian WebGPU版本",
                "当前Build Target不是WebGL。是否现在切换到WebGL？",
                "切换到WebGL",
                "取消");
            if (!switchTarget)
                return false;

            return EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
        }

        static void TrySetWebGpuOnlyGraphicsApi()
        {
            try
            {
                var webGpu = (GraphicsDeviceType)Enum.Parse(typeof(GraphicsDeviceType), "WebGPU");
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, new[] { webGpu });
                Debug.Log("已将WebGL图形API设置为仅使用WebGPU。");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"无法自动设置WebGPU图形API。请在Player Settings里确认Graphics API使用WebGPU。详情：{ex.Message}");
            }
        }

        static void TrySetFullscreenTemplate()
        {
            try
            {
                PlayerSettings.WebGL.template = kFullscreenTemplate;
                Debug.Log($"已将WebGL模板设置为：{kFullscreenTemplate}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"无法自动设置WebGL全屏模板。请在Player Settings > Web > Resolution and Presentation中选择GaussianFullscreen。详情：{ex.Message}");
            }
        }

        static string[] GetBuildScenes()
        {
            var scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled && !string.IsNullOrEmpty(scene.path))
                    scenes.Add(scene.path);
            }

            if (scenes.Count != 0)
                return scenes.ToArray();

            var activeScene = EditorSceneManager.GetActiveScene();
            if (!activeScene.IsValid() || string.IsNullOrEmpty(activeScene.path))
            {
                EditorUtility.DisplayDialog(
                    "构建Gaussian WebGPU版本",
                    "Build Settings里没有启用的场景，而且当前活动场景还没有保存。",
                    "确定");
                return Array.Empty<string>();
            }

            bool useActiveScene = EditorUtility.DisplayDialog(
                "构建Gaussian WebGPU版本",
                $"Build Settings里没有启用的场景。是否构建当前活动场景？\n\n{activeScene.path}",
                "使用当前场景",
                "取消");
            return useActiveScene ? new[] { activeScene.path } : Array.Empty<string>();
        }

        static bool PreflightPackageSetup()
        {
            var warnings = new StringBuilder();
            GaussianSplatRenderer[] renderers = Object.FindObjectsOfType<GaussianSplatRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || EditorUtility.IsPersistent(renderer) || !renderer.gameObject.scene.IsValid())
                    continue;

                var loader = renderer.GetComponent<GaussianSplatAssetBundleLoader>();
                if (loader == null)
                {
                    warnings.AppendLine($"- {renderer.name}：没有找到GaussianSplatAssetBundleLoader。");
                    continue;
                }

                if (!loader.m_LoadOnStart)
                    warnings.AppendLine($"- {renderer.name}：Loader的Load On Start没有开启。");

                if (!HasExternalUrl(loader) && !LocalBundleExists(loader))
                    warnings.AppendLine($"- {renderer.name}：没有找到本地bundleAssets资源包：{GetLocalBundlePath(loader)}");
            }

            if (warnings.Length == 0)
            {
                Debug.Log("Gaussian WebGPU发布前检查通过。");
                return true;
            }

            return EditorUtility.DisplayDialog(
                "Gaussian WebGPU发布前检查警告",
                "可以继续构建，但下面这些问题可能导致Web主包过大，或者运行时加载失败：\n\n" + warnings,
                "仍然构建",
                "取消");
        }

        static bool StageBundleAssetsForBuild(string[] scenes)
        {
            if (!ClearStagedStreamingAssetsPackageFolder())
                return false;

            string sourceRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), kBundleAssetsFolder));
            string targetRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), kStreamingAssetsPackageFolder));
            HashSet<string> requiredBundleFiles = CollectRequiredLocalBundleFiles(scenes);
            if (requiredBundleFiles.Count == 0)
            {
                Debug.Log("没有发现需要从bundleAssets暂存的Gaussian WebGPU本地资源包。");
                return true;
            }

            if (!Directory.Exists(sourceRoot))
            {
                EditorUtility.DisplayDialog(
                    "构建Gaussian WebGPU版本",
                    $"没有找到Gaussian WebGPU资源包源目录：{kBundleAssetsFolder}。\n\n请先构建资源包，或检查Loader是否配置为外部URL。",
                    "确定");
                return false;
            }

            Directory.CreateDirectory(targetRoot);
            int copied = 0;
            var missing = new StringBuilder();
            foreach (string requiredBundleFile in requiredBundleFiles)
            {
                string sourceBundlePath = Path.GetFullPath(Path.Combine(sourceRoot, NormalizeRelativePath(requiredBundleFile)));
                if (!IsChildPath(sourceRoot, sourceBundlePath))
                {
                    Debug.LogError($"拒绝从 bundleAssets 之外暂存资源包文件：{sourceBundlePath}");
                    return false;
                }

                if (!File.Exists(sourceBundlePath))
                {
                    missing.AppendLine($"- {requiredBundleFile}");
                    continue;
                }

                copied += CopyPackageDirectory(sourceRoot, targetRoot, sourceBundlePath);
            }

            if (missing.Length != 0)
            {
                EditorUtility.DisplayDialog(
                    "构建Gaussian WebGPU版本",
                    $"bundleAssets里缺少下面这些Loader引用的资源包：\n\n{missing}\n请先构建对应资源包。",
                    "确定");
                ClearStagedStreamingAssetsPackageFolder();
                return false;
            }

            AssetDatabase.Refresh();
            Debug.Log($"已从 {kBundleAssetsFolder} 暂存 {copied} 个Gaussian WebGPU资源文件到 {kStreamingAssetsPackageFolder}。");
            return true;
        }

        static HashSet<string> CollectRequiredLocalBundleFiles(string[] scenes)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            GaussianSplatAssetBundleLoader[] loadedLoaders = Object.FindObjectsOfType<GaussianSplatAssetBundleLoader>(true);
            foreach (var loader in loadedLoaders)
            {
                if (loader == null || EditorUtility.IsPersistent(loader) || !loader.gameObject.scene.IsValid() || HasExternalUrl(loader))
                    continue;

                AddRequiredBundleFile(result, GetLoaderBundleFileName(loader));
            }

            foreach (string scenePath in scenes)
                CollectRequiredLocalBundleFilesFromSceneAsset(scenePath, result);

            return result;
        }

        static void CollectRequiredLocalBundleFilesFromSceneAsset(string scenePath, HashSet<string> result)
        {
            if (string.IsNullOrWhiteSpace(scenePath) || !File.Exists(scenePath))
                return;

            foreach (string line in File.ReadLines(scenePath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("m_BundleFileName:", StringComparison.Ordinal))
                    continue;

                string value = trimmed.Substring("m_BundleFileName:".Length).Trim().Trim('"');
                AddRequiredBundleFile(result, value);
            }
        }

        static void AddRequiredBundleFile(HashSet<string> result, string bundleFileName)
        {
            if (string.IsNullOrWhiteSpace(bundleFileName))
                return;

            string normalized = bundleFileName.Trim().Trim('"').Replace('\\', '/').TrimStart('/');
            if (normalized.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(normalized) || normalized.Contains("../", StringComparison.Ordinal))
                return;

            result.Add(normalized);
        }

        static int CopyPackageDirectory(string sourceRoot, string targetRoot, string sourceBundlePath)
        {
            string sourceDirectory = Path.GetDirectoryName(sourceBundlePath);
            if (string.IsNullOrEmpty(sourceDirectory))
                return 0;

            int copied = 0;
            foreach (string sourcePath in Directory.GetFiles(sourceDirectory))
            {
                if (sourcePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;

                string relativePath = GetRelativePath(sourceRoot, sourcePath);
                string targetPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
                if (!IsChildPath(targetRoot, targetPath))
                {
                    Debug.LogError($"拒绝暂存 StreamingAssets 目录外的资源包文件：{targetPath}");
                    continue;
                }

                string targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);
                File.Copy(sourcePath, targetPath, true);
                ++copied;
            }

            return copied;
        }

        static bool ClearStagedStreamingAssetsPackageFolder()
        {
            string packageFolder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), kStreamingAssetsPackageFolder));
            string streamingAssetsFolder = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Assets/StreamingAssets"));
            if (!IsChildPath(streamingAssetsFolder, packageFolder))
            {
                Debug.LogError($"拒绝清理 StreamingAssets 之外的 Gaussian WebGPU 暂存目录：{packageFolder}");
                return false;
            }

            if (AssetDatabase.IsValidFolder(kStreamingAssetsPackageFolder))
            {
                AssetDatabase.DeleteAsset(kStreamingAssetsPackageFolder);
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

            return true;
        }

        static bool HasExternalUrl(GaussianSplatAssetBundleLoader loader)
        {
            return loader != null &&
                   (!string.IsNullOrWhiteSpace(loader.m_BundleUrl) ||
                    !string.IsNullOrWhiteSpace(loader.m_BaseUrl));
        }

        static bool LocalBundleExists(GaussianSplatAssetBundleLoader loader)
        {
            string path = GetLocalBundlePath(loader);
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        static string GetLocalBundlePath(GaussianSplatAssetBundleLoader loader)
        {
            if (loader == null)
                return string.Empty;

            string fileName = GetLoaderBundleFileName(loader);
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            string projectRoot = Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(projectRoot, kBundleAssetsFolder, NormalizeRelativePath(fileName)));
        }

        static string GetLoaderBundleFileName(GaussianSplatAssetBundleLoader loader)
        {
            if (loader == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(loader.m_BundleFileName))
                return loader.m_BundleFileName;

            return !string.IsNullOrWhiteSpace(loader.m_PackageId)
                ? loader.m_PackageId + ".bundle"
                : string.Empty;
        }

        static string NormalizeRelativePath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        }

        static bool IsChildPath(string parentPath, string childPath)
        {
            string safeParentPath = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string safeChildPath = Path.GetFullPath(childPath);
            return safeChildPath.StartsWith(safeParentPath, StringComparison.OrdinalIgnoreCase);
        }

        static string GetRelativePath(string rootPath, string filePath)
        {
            string safeRootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string safeFilePath = Path.GetFullPath(filePath);
            if (!safeFilePath.StartsWith(safeRootPath, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(filePath);
            return safeFilePath.Substring(safeRootPath.Length);
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

    sealed class GaussianSplatWebGpuSceneBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!GaussianSplatWebGpuPlayerBuilder.ClearRendererAssetsDuringBuild)
                return;
            if (report != null && report.summary.platform != BuildTarget.WebGL)
                return;

            int cleared = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                GaussianSplatRenderer[] renderers = root.GetComponentsInChildren<GaussianSplatRenderer>(true);
                foreach (GaussianSplatRenderer renderer in renderers)
                {
                    if (renderer == null || renderer.m_Asset == null)
                        continue;
                    if (renderer.GetComponent<GaussianSplatAssetBundleLoader>() == null)
                        continue;

                    renderer.m_Asset = null;
                    ++cleared;
                }
            }

            if (cleared != 0)
                Debug.Log($"Gaussian WebGPU构建场景副本已清空 {cleared} 个GaussianSplatRenderer.Asset直接引用：{scene.path}");
        }
    }
}
