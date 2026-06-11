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
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatWebGpuPlayerBuilder
    {
        const string kDefaultBuildFolder = "Builds/WebGPU";
        const string kDefaultPackageFolder = "Assets/StreamingAssets/GaussianSplatPackages";
        const int kDefaultServerPort = 8080;
        const int kMaxServerPort = 8099;

        static HttpListener s_LocalServer;
        static CancellationTokenSource s_LocalServerCancel;
        static int s_LocalServerPort;

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

            string[] scenes = GetBuildScenes();
            if (scenes.Length == 0)
                return;

            if (!PreflightPackageSetup())
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

                if (renderer.asset != null)
                {
                    warnings.AppendLine($"- {renderer.name}：GaussianSplatRenderer.Asset仍然有直接引用，可能会把大点云塞进Web主包。");
                }

                var loader = renderer.GetComponent<GaussianSplatAssetBundleLoader>();
                if (loader == null)
                {
                    warnings.AppendLine($"- {renderer.name}：没有找到GaussianSplatAssetBundleLoader。");
                    continue;
                }

                if (!loader.m_LoadOnStart)
                    warnings.AppendLine($"- {renderer.name}：Loader的Load On Start没有开启。");

                if (!HasExternalUrl(loader) && !LocalBundleExists(loader))
                    warnings.AppendLine($"- {renderer.name}：没有找到本地StreamingAssets资源包：{GetLocalBundlePath(loader)}");
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

            string fileName = !string.IsNullOrWhiteSpace(loader.m_BundleFileName)
                ? loader.m_BundleFileName
                : !string.IsNullOrWhiteSpace(loader.m_PackageId)
                    ? loader.m_PackageId + ".bundle"
                    : string.Empty;
            if (string.IsNullOrEmpty(fileName))
                return string.Empty;

            string projectRoot = Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(projectRoot, kDefaultPackageFolder, NormalizeRelativePath(fileName)));
        }

        static string NormalizeRelativePath(string path)
        {
            return path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
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
