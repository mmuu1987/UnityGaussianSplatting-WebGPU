// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Runtime tuning panel for WebGPU bring-up builds.
    /// It intentionally uses IMGUI so it works in Web builds without adding UI package dependencies.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class GaussianSplatRuntimeTuningPanel : MonoBehaviour
    {
        const string kPrefsPrefix = "GaussianSplatting.WebGpuRuntimeTuning.";
        const int kWindowId = 8273041;

        [Serializable]
        public class RuntimeTuningValues
        {
            public bool sortOnlyWhenCameraChanges;
            public float sortPositionThreshold;
            public float sortAngleThreshold;
            public int sortMode;
            public int bucketCount;
            public bool cachePositions;
            public bool chunkFrustumCulling;
            public float chunkCullPadding;
            public int maxVisibleSplats;
            public bool distanceLod;
            public float lodNearDistance;
            public float lodFarDistance;
            public int lodMidSampleStep;
            public int lodFarSampleStep;
            public int lodMaxSampleStep;
            public int lodFarReservePercent;
            public float splatScale;
            public float opacityScale;
        }

        [Serializable]
        public class RuntimeTuningExport
        {
            public int version = 1;
            public string scopeId;
            public string scopeKey;
            public string sceneName;
            public string scenePath;
            public string packageId;
            public string bundleFileName;
            public string assetName;
            public string rendererName;
            public string rendererPath;
            public RuntimeTuningValues values;
        }

        struct SettingsScope
        {
            public string id;
            public string key;
            public string sceneName;
            public string scenePath;
            public string packageId;
            public string bundleFileName;
            public string assetName;
            public string rendererPath;
        }

        public GaussianSplatRenderer m_Target;
        public bool m_ShowOnStart = true;
        public bool m_LoadSavedOnStart = true;
        public bool m_UnlockCursorWhileVisible = true;
        public KeyCode m_ToggleKey = KeyCode.F8;

        Rect m_WindowRect = new Rect(20, 20, 420, 720);
        Vector2 m_Scroll;
        bool m_Visible;
        bool m_CursorWasOverridden;
        bool m_PreviousCursorVisible;
        CursorLockMode m_PreviousLockState;
        float m_NextRendererRefreshTime;
        string m_ExportJson;
        string m_ExportStatus;
        readonly List<GaussianSplatRenderer> m_Renderers = new();
        readonly HashSet<GaussianSplatRenderer> m_AutoLoadedRenderers = new();

        public static bool BlocksCameraInput { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreateForDevelopmentBuilds()
        {
            if (!Debug.isDebugBuild && !Application.isEditor && !Application.absoluteURL.Contains("gsPanel=1"))
                return;
            if (FindAnyObjectByType<GaussianSplatRuntimeTuningPanel>() != null)
                return;

            var go = new GameObject("Gaussian Splat 运行时调参面板");
            DontDestroyOnLoad(go);
            go.AddComponent<GaussianSplatRuntimeTuningPanel>();
        }

        void Awake()
        {
            m_Visible = m_ShowOnStart;
            RefreshRenderers(true);
            if (m_Target == null && m_Renderers.Count != 0)
                m_Target = m_Renderers[0];
            AutoLoadSavedSettingsForKnownRenderers();
        }

        void Update()
        {
            if (Input.GetKeyDown(m_ToggleKey))
                m_Visible = !m_Visible;

            RefreshRenderers();
            AutoLoadSavedSettingsForKnownRenderers();
            UpdateCursorLock();
        }

        void OnGUI()
        {
            if (!m_Visible)
                return;

            m_WindowRect = GUILayout.Window(kWindowId, m_WindowRect, DrawWindow, "GS WebGPU 调参");
        }

        void OnDisable()
        {
            RestoreCursorLock();
        }

        void OnDestroy()
        {
            RestoreCursorLock();
        }

        void UpdateCursorLock()
        {
            BlocksCameraInput = m_Visible;
            if (m_Visible && m_UnlockCursorWhileVisible)
            {
                if (!m_CursorWasOverridden)
                {
                    m_PreviousLockState = Cursor.lockState;
                    m_PreviousCursorVisible = Cursor.visible;
                    m_CursorWasOverridden = true;
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                RestoreCursorLock();
            }
        }

        void RestoreCursorLock()
        {
            BlocksCameraInput = false;
            if (!m_CursorWasOverridden)
                return;

            Cursor.lockState = m_PreviousLockState;
            Cursor.visible = m_PreviousCursorVisible;
            m_CursorWasOverridden = false;
        }

        void DrawWindow(int id)
        {
            RefreshRenderers();
            if (m_Target == null && m_Renderers.Count != 0)
                m_Target = m_Renderers[0];

            GUILayout.BeginVertical();
            GUILayout.Label("F8：显示/隐藏面板。面板显示时会解锁鼠标视角。");
            GUILayout.Label("提示：先在这里调参，再用“导出”复制 JSON 到编辑器导入。");

            DrawTargetSelector();

            if (m_Target == null)
            {
                GUILayout.Label("没有找到 GaussianSplatRenderer。");
                GUILayout.EndVertical();
                GUI.DragWindow(new Rect(0, 0, 10000, 24));
                return;
            }

            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Width(400), GUILayout.Height(610));

            GUILayout.Label($"目标：{m_Target.name}");
            GUILayout.Label($"保存作用域：{GetSettingsScopeId(m_Target)}");
            GUILayout.Label($"已绘制点数 / 总点数：{m_Target.renderSplatCount:N0} / {m_Target.splatCount:N0}");
            GUILayout.Space(8);

            DrawBool("01 相机变化时才排序", ref m_Target.m_WebGpuCpuSortOnlyWhenCameraChanges,
                "只有相机移动或旋转超过阈值时才重新排序，性能更好。");
            DrawFloat("02 排序移动阈值", ref m_Target.m_WebGpuCpuSortPositionThreshold, 0, 0.2f,
                "数值越大 CPU 越省，但透明关系更新越慢。建议试 0.01-0.05。");
            DrawFloat("03 排序旋转阈值", ref m_Target.m_WebGpuCpuSortAngleThreshold, 0, 2.0f,
                "数值越大 CPU 越省。建议试 0.25-1。");

            GUILayout.Space(8);
            DrawSortMode();
            DrawInt("04 深度桶数量", ref m_Target.m_WebGpuCpuSortBucketCount, 256, 16384,
                "数量越多排序越好，但 CPU 开销越高。建议试 2048-8192。");
            DrawBool("05 缓存点位置", ref m_Target.m_WebGpuCpuSortCachePositions,
                "排序更快但内存占用明显增加。大 Web 场景通常关闭。");

            GUILayout.Space(8);
            DrawBool("06 分块视锥裁剪", ref m_Target.m_WebGpuChunkFrustumCulling,
                "只处理相机可见的分块，是主要性能开关。");
            DrawFloat("07 裁剪外扩距离", ref m_Target.m_WebGpuChunkCullPadding, 0, 10,
                "如果边缘点云闪烁或消失，可以适当增大。");
            DrawInt("08 可见点预算", ref m_Target.m_WebGpuMaxVisibleSplats, 0, 6000000,
                "每帧点数预算。越大质量越好但更慢，0 表示不限。");

            GUILayout.Space(8);
            DrawBool("09 距离 LOD", ref m_Target.m_WebGpuDistanceLod,
                "超过预算时，让中远距离点云变稀疏，而不是整块丢掉。");
            DrawFloat("10 近处全质量距离", ref m_Target.m_WebGpuLodNearDistance, 0, 300,
                "近距离保持完整密度。如果近中距离有空洞，可以增大。");
            DrawFloat("11 远处 LOD 起始距离", ref m_Target.m_WebGpuLodFarDistance, 0, 600,
                "超过该距离开始远处 LOD。如果远处太稀疏，可以增大。");
            DrawInt("12 中距离采样步长", ref m_Target.m_WebGpuLodMidSampleStep, 1, 8,
                "中距离每 N 个点取一个。1 表示不采样，2 表示约一半。");
            DrawInt("13 远距离采样步长", ref m_Target.m_WebGpuLodFarSampleStep, 1, 16,
                "远距离每 N 个点取一个。越大越快但越稀疏。");
            DrawInt("14 最大采样步长", ref m_Target.m_WebGpuLodMaxSampleStep, 1, 32,
                "预算仍不够时，最远分块最多可以稀疏到这个程度。");
            DrawInt("15 远景保留比例", ref m_Target.m_WebGpuLodFarReservePercent, 0, 50,
                "从可见点预算中保留一部分给远景。如果远景消失，可以增大。");

            GUILayout.Space(8);
            DrawFloat("16 点大小", ref m_Target.m_SplatScale, 0.5f, 2.0f,
                "略微增大可以填补小空洞。建议试 1.05-1.15。");
            DrawFloat("17 不透明度", ref m_Target.m_OpacityScale, 0.1f, 3.0f,
                "画面太透时可以增大，过高会发糊或发白。");

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("保存"))
                SaveSettings(m_Target);
            if (GUILayout.Button("加载"))
                LoadSettings(m_Target);
            if (GUILayout.Button("导出"))
                ExportSettingsToClipboard(m_Target);
            if (GUILayout.Button("打印"))
                LogSettings(m_Target);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(m_ExportStatus))
                GUILayout.Label(m_ExportStatus);
            if (!string.IsNullOrEmpty(m_ExportJson))
                m_ExportJson = GUILayout.TextArea(m_ExportJson, GUILayout.Height(90));

            m_Target.SanitizeWebGpuOptions();
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 24));
        }

        void DrawTargetSelector()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("目标", GUILayout.Width(48));
            if (GUILayout.Button("刷新", GUILayout.Width(70)))
                RefreshRenderers(true);

            if (m_Renderers.Count <= 1)
            {
                GUILayout.Label(m_Target ? m_Target.name : "无");
            }
            else
            {
                int index = Mathf.Max(0, m_Renderers.IndexOf(m_Target));
                if (GUILayout.Button("<", GUILayout.Width(28)))
                    index = (index + m_Renderers.Count - 1) % m_Renderers.Count;
                GUILayout.Label(m_Renderers[index].name);
                if (GUILayout.Button(">", GUILayout.Width(28)))
                    index = (index + 1) % m_Renderers.Count;
                m_Target = m_Renderers[index];
            }
            GUILayout.EndHorizontal();
        }

        void RefreshRenderers(bool force = false)
        {
            if (!force && Time.unscaledTime < m_NextRendererRefreshTime)
                return;

            m_NextRendererRefreshTime = Time.unscaledTime + 0.5f;
            m_Renderers.Clear();
            m_Renderers.AddRange(FindObjectsByType<GaussianSplatRenderer>());
            m_Renderers.RemoveAll(r => r == null);
        }

        void AutoLoadSavedSettingsForKnownRenderers()
        {
            if (!m_LoadSavedOnStart)
                return;

            foreach (var renderer in m_Renderers)
            {
                if (renderer == null)
                    continue;

                if (m_AutoLoadedRenderers.Contains(renderer))
                    continue;

                if (HasSavedSettings(renderer))
                    LoadSettings(renderer, false);
                m_AutoLoadedRenderers.Add(renderer);
            }
        }

        static void DrawBool(string label, ref bool value, string help)
        {
            value = GUILayout.Toggle(value, new GUIContent(label, help));
        }

        static void DrawFloat(string label, ref float value, float min, float max, string help)
        {
            GUILayout.Label(new GUIContent($"{label}: {value:0.###}", help));
            value = Mathf.Clamp(GUILayout.HorizontalSlider(value, min, max), min, max);
        }

        static void DrawInt(string label, ref int value, int min, int max, string help)
        {
            GUILayout.Label(new GUIContent($"{label}: {value:N0}", help));
            value = Mathf.Clamp(Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max)), min, max);
        }

        void DrawSortMode()
        {
            GUILayout.Label("CPU 排序模式");
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(m_Target.m_WebGpuCpuSortMode == GaussianSplatRenderer.WebGpuCpuSortMode.DepthBuckets, "快速", GUI.skin.button))
                m_Target.m_WebGpuCpuSortMode = GaussianSplatRenderer.WebGpuCpuSortMode.DepthBuckets;
            if (GUILayout.Toggle(m_Target.m_WebGpuCpuSortMode == GaussianSplatRenderer.WebGpuCpuSortMode.Exact, "精确 慢", GUI.skin.button))
                m_Target.m_WebGpuCpuSortMode = GaussianSplatRenderer.WebGpuCpuSortMode.Exact;
            GUILayout.EndHorizontal();
        }

        static void SaveSettings(GaussianSplatRenderer r)
        {
            if (r == null) return;
            r.SanitizeWebGpuOptions();
            PlayerPrefs.SetInt(Key(r, "SortOnChange"), r.m_WebGpuCpuSortOnlyWhenCameraChanges ? 1 : 0);
            PlayerPrefs.SetFloat(Key(r, "SortPosThreshold"), r.m_WebGpuCpuSortPositionThreshold);
            PlayerPrefs.SetFloat(Key(r, "SortAngleThreshold"), r.m_WebGpuCpuSortAngleThreshold);
            PlayerPrefs.SetInt(Key(r, "SortMode"), (int)r.m_WebGpuCpuSortMode);
            PlayerPrefs.SetInt(Key(r, "BucketCount"), r.m_WebGpuCpuSortBucketCount);
            PlayerPrefs.SetInt(Key(r, "CachePositions"), r.m_WebGpuCpuSortCachePositions ? 1 : 0);
            PlayerPrefs.SetInt(Key(r, "ChunkCulling"), r.m_WebGpuChunkFrustumCulling ? 1 : 0);
            PlayerPrefs.SetFloat(Key(r, "ChunkPadding"), r.m_WebGpuChunkCullPadding);
            PlayerPrefs.SetInt(Key(r, "MaxVisibleSplats"), r.m_WebGpuMaxVisibleSplats);
            PlayerPrefs.SetInt(Key(r, "DistanceLod"), r.m_WebGpuDistanceLod ? 1 : 0);
            PlayerPrefs.SetFloat(Key(r, "LodNear"), r.m_WebGpuLodNearDistance);
            PlayerPrefs.SetFloat(Key(r, "LodFar"), r.m_WebGpuLodFarDistance);
            PlayerPrefs.SetInt(Key(r, "LodMidStep"), r.m_WebGpuLodMidSampleStep);
            PlayerPrefs.SetInt(Key(r, "LodFarStep"), r.m_WebGpuLodFarSampleStep);
            PlayerPrefs.SetInt(Key(r, "LodMaxStep"), r.m_WebGpuLodMaxSampleStep);
            PlayerPrefs.SetInt(Key(r, "LodFarReserve"), r.m_WebGpuLodFarReservePercent);
            PlayerPrefs.SetFloat(Key(r, "SplatScale"), r.m_SplatScale);
            PlayerPrefs.SetFloat(Key(r, "OpacityScale"), r.m_OpacityScale);
            PlayerPrefs.Save();
            Debug.Log($"Gaussian WebGPU 运行时调参已保存。作用域：{GetSettingsScopeId(r)}");
        }

        static void LoadSettings(GaussianSplatRenderer r)
        {
            LoadSettings(r, true);
        }

        static void LoadSettings(GaussianSplatRenderer r, bool log)
        {
            if (r == null || !HasSavedSettings(r)) return;
            r.m_WebGpuCpuSortOnlyWhenCameraChanges = PlayerPrefs.GetInt(Key(r, "SortOnChange"), r.m_WebGpuCpuSortOnlyWhenCameraChanges ? 1 : 0) != 0;
            r.m_WebGpuCpuSortPositionThreshold = PlayerPrefs.GetFloat(Key(r, "SortPosThreshold"), r.m_WebGpuCpuSortPositionThreshold);
            r.m_WebGpuCpuSortAngleThreshold = PlayerPrefs.GetFloat(Key(r, "SortAngleThreshold"), r.m_WebGpuCpuSortAngleThreshold);
            r.m_WebGpuCpuSortMode = (GaussianSplatRenderer.WebGpuCpuSortMode)PlayerPrefs.GetInt(Key(r, "SortMode"), (int)r.m_WebGpuCpuSortMode);
            r.m_WebGpuCpuSortBucketCount = PlayerPrefs.GetInt(Key(r, "BucketCount"), r.m_WebGpuCpuSortBucketCount);
            r.m_WebGpuCpuSortCachePositions = PlayerPrefs.GetInt(Key(r, "CachePositions"), r.m_WebGpuCpuSortCachePositions ? 1 : 0) != 0;
            r.m_WebGpuChunkFrustumCulling = PlayerPrefs.GetInt(Key(r, "ChunkCulling"), r.m_WebGpuChunkFrustumCulling ? 1 : 0) != 0;
            r.m_WebGpuChunkCullPadding = PlayerPrefs.GetFloat(Key(r, "ChunkPadding"), r.m_WebGpuChunkCullPadding);
            r.m_WebGpuMaxVisibleSplats = PlayerPrefs.GetInt(Key(r, "MaxVisibleSplats"), r.m_WebGpuMaxVisibleSplats);
            r.m_WebGpuDistanceLod = PlayerPrefs.GetInt(Key(r, "DistanceLod"), r.m_WebGpuDistanceLod ? 1 : 0) != 0;
            r.m_WebGpuLodNearDistance = PlayerPrefs.GetFloat(Key(r, "LodNear"), r.m_WebGpuLodNearDistance);
            r.m_WebGpuLodFarDistance = PlayerPrefs.GetFloat(Key(r, "LodFar"), r.m_WebGpuLodFarDistance);
            r.m_WebGpuLodMidSampleStep = PlayerPrefs.GetInt(Key(r, "LodMidStep"), r.m_WebGpuLodMidSampleStep);
            r.m_WebGpuLodFarSampleStep = PlayerPrefs.GetInt(Key(r, "LodFarStep"), r.m_WebGpuLodFarSampleStep);
            r.m_WebGpuLodMaxSampleStep = PlayerPrefs.GetInt(Key(r, "LodMaxStep"), r.m_WebGpuLodMaxSampleStep);
            r.m_WebGpuLodFarReservePercent = PlayerPrefs.GetInt(Key(r, "LodFarReserve"), r.m_WebGpuLodFarReservePercent);
            r.m_SplatScale = PlayerPrefs.GetFloat(Key(r, "SplatScale"), r.m_SplatScale);
            r.m_OpacityScale = PlayerPrefs.GetFloat(Key(r, "OpacityScale"), r.m_OpacityScale);
            r.SanitizeWebGpuOptions();
            r.InvalidateWebGpuRuntimeState();
            if (log)
                Debug.Log($"Gaussian WebGPU 运行时调参已加载。作用域：{GetSettingsScopeId(r)}");
        }

        static void LogSettings(GaussianSplatRenderer r)
        {
            if (r == null) return;
            Debug.Log(
                $"Gaussian WebGPU 调参。作用域：{GetSettingsScopeId(r)}\n" +
                $"m_WebGpuMaxVisibleSplats = {r.m_WebGpuMaxVisibleSplats}\n" +
                $"m_WebGpuLodNearDistance = {r.m_WebGpuLodNearDistance}\n" +
                $"m_WebGpuLodFarDistance = {r.m_WebGpuLodFarDistance}\n" +
                $"m_WebGpuLodMidSampleStep = {r.m_WebGpuLodMidSampleStep}\n" +
                $"m_WebGpuLodFarSampleStep = {r.m_WebGpuLodFarSampleStep}\n" +
                $"m_WebGpuLodMaxSampleStep = {r.m_WebGpuLodMaxSampleStep}\n" +
                $"m_WebGpuLodFarReservePercent = {r.m_WebGpuLodFarReservePercent}\n" +
                $"m_WebGpuChunkCullPadding = {r.m_WebGpuChunkCullPadding}\n" +
                $"m_SplatScale = {r.m_SplatScale}\n" +
                $"m_OpacityScale = {r.m_OpacityScale}");
        }

        void ExportSettingsToClipboard(GaussianSplatRenderer r)
        {
            if (r == null)
                return;

            m_ExportJson = ExportSettingsJson(r);
            try
            {
                GUIUtility.systemCopyBuffer = m_ExportJson;
                m_ExportStatus = "导出 JSON 已复制。请在 Unity Editor 执行 Tools > Gaussian WebGPU > 调试 > 从剪贴板导入运行时调参JSON。";
            }
            catch (Exception exception)
            {
                m_ExportStatus = "无法自动复制。请手动复制下方 JSON。";
                Debug.LogWarning($"无法复制 Gaussian WebGPU 调参 JSON：{exception.Message}", this);
            }

            Debug.Log($"Gaussian WebGPU 运行时调参导出 JSON：\n{m_ExportJson}");
        }

        public static string ExportSettingsJson(GaussianSplatRenderer r)
        {
            return JsonUtility.ToJson(CaptureExportData(r), true);
        }

        public static RuntimeTuningExport CaptureExportData(GaussianSplatRenderer r)
        {
            SettingsScope scope = BuildSettingsScope(r);
            return new RuntimeTuningExport
            {
                version = 1,
                scopeId = scope.id,
                scopeKey = scope.key,
                sceneName = scope.sceneName,
                scenePath = scope.scenePath,
                packageId = scope.packageId,
                bundleFileName = scope.bundleFileName,
                assetName = scope.assetName,
                rendererName = r ? r.name : string.Empty,
                rendererPath = scope.rendererPath,
                values = CaptureValues(r)
            };
        }

        public static bool TryParseExportJson(string json, out RuntimeTuningExport data, out string error)
        {
            data = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "调参 JSON 为空。";
                return false;
            }

            try
            {
                data = JsonUtility.FromJson<RuntimeTuningExport>(json);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (data == null || data.values == null)
            {
                error = "调参 JSON 中没有运行时调参数据。";
                return false;
            }

            return true;
        }

        public static RuntimeTuningValues CaptureValues(GaussianSplatRenderer r)
        {
            if (r == null)
                return new RuntimeTuningValues();

            r.SanitizeWebGpuOptions();
            return new RuntimeTuningValues
            {
                sortOnlyWhenCameraChanges = r.m_WebGpuCpuSortOnlyWhenCameraChanges,
                sortPositionThreshold = r.m_WebGpuCpuSortPositionThreshold,
                sortAngleThreshold = r.m_WebGpuCpuSortAngleThreshold,
                sortMode = (int)r.m_WebGpuCpuSortMode,
                bucketCount = r.m_WebGpuCpuSortBucketCount,
                cachePositions = r.m_WebGpuCpuSortCachePositions,
                chunkFrustumCulling = r.m_WebGpuChunkFrustumCulling,
                chunkCullPadding = r.m_WebGpuChunkCullPadding,
                maxVisibleSplats = r.m_WebGpuMaxVisibleSplats,
                distanceLod = r.m_WebGpuDistanceLod,
                lodNearDistance = r.m_WebGpuLodNearDistance,
                lodFarDistance = r.m_WebGpuLodFarDistance,
                lodMidSampleStep = r.m_WebGpuLodMidSampleStep,
                lodFarSampleStep = r.m_WebGpuLodFarSampleStep,
                lodMaxSampleStep = r.m_WebGpuLodMaxSampleStep,
                lodFarReservePercent = r.m_WebGpuLodFarReservePercent,
                splatScale = r.m_SplatScale,
                opacityScale = r.m_OpacityScale
            };
        }

        public static void ApplyValues(GaussianSplatRenderer r, RuntimeTuningValues values)
        {
            if (r == null || values == null)
                return;

            r.m_WebGpuCpuSortOnlyWhenCameraChanges = values.sortOnlyWhenCameraChanges;
            r.m_WebGpuCpuSortPositionThreshold = values.sortPositionThreshold;
            r.m_WebGpuCpuSortAngleThreshold = values.sortAngleThreshold;
            r.m_WebGpuCpuSortMode = (GaussianSplatRenderer.WebGpuCpuSortMode)values.sortMode;
            r.m_WebGpuCpuSortBucketCount = values.bucketCount;
            r.m_WebGpuCpuSortCachePositions = values.cachePositions;
            r.m_WebGpuChunkFrustumCulling = values.chunkFrustumCulling;
            r.m_WebGpuChunkCullPadding = values.chunkCullPadding;
            r.m_WebGpuMaxVisibleSplats = values.maxVisibleSplats;
            r.m_WebGpuDistanceLod = values.distanceLod;
            r.m_WebGpuLodNearDistance = values.lodNearDistance;
            r.m_WebGpuLodFarDistance = values.lodFarDistance;
            r.m_WebGpuLodMidSampleStep = values.lodMidSampleStep;
            r.m_WebGpuLodFarSampleStep = values.lodFarSampleStep;
            r.m_WebGpuLodMaxSampleStep = values.lodMaxSampleStep;
            r.m_WebGpuLodFarReservePercent = values.lodFarReservePercent;
            r.m_SplatScale = values.splatScale;
            r.m_OpacityScale = values.opacityScale;
            r.SanitizeWebGpuOptions();
            r.InvalidateWebGpuRuntimeState();
        }

        public static string GetSettingsScopeId(GaussianSplatRenderer r)
        {
            return BuildSettingsScope(r).id;
        }

        static bool HasSavedSettings(GaussianSplatRenderer r)
        {
            return r != null && PlayerPrefs.HasKey(Key(r, "MaxVisibleSplats"));
        }

        static string Key(GaussianSplatRenderer r, string name)
        {
            return kPrefsPrefix + BuildSettingsScope(r).key + "." + name;
        }

        static SettingsScope BuildSettingsScope(GaussianSplatRenderer r)
        {
            var scope = new SettingsScope
            {
                id = "renderer:none",
                key = "renderer_none",
                sceneName = string.Empty,
                scenePath = string.Empty,
                packageId = string.Empty,
                bundleFileName = string.Empty,
                assetName = string.Empty,
                rendererPath = string.Empty
            };

            if (r == null)
                return scope;

            Scene scene = r.gameObject.scene;
            scope.sceneName = scene.IsValid() ? scene.name : string.Empty;
            scope.scenePath = scene.IsValid() ? scene.path : string.Empty;
            string sceneId = !string.IsNullOrWhiteSpace(scope.scenePath) ? scope.scenePath : scope.sceneName;
            if (string.IsNullOrWhiteSpace(sceneId))
                sceneId = "unknown_scene";

            scope.rendererPath = GetHierarchyPath(r.transform);
            GaussianSplatAssetBundleLoader loader = FindLoaderForRenderer(r);
            if (loader != null)
            {
                scope.packageId = loader.m_PackageId != null ? loader.m_PackageId.Trim() : string.Empty;
                scope.bundleFileName = loader.m_BundleFileName != null ? loader.m_BundleFileName.Trim() : string.Empty;
            }

            if (r.asset != null)
                scope.assetName = r.asset.name;

            string contentId;
            if (!string.IsNullOrWhiteSpace(scope.packageId))
            {
                contentId = "package:" + scope.packageId;
            }
            else if (!string.IsNullOrWhiteSpace(scope.bundleFileName))
            {
                contentId = "bundle:" + scope.bundleFileName;
            }
            else if (!string.IsNullOrWhiteSpace(scope.assetName))
            {
                contentId = "asset:" + scope.assetName;
            }
            else
            {
                contentId = "renderer:" + scope.rendererPath;
            }

            scope.id = "scene:" + sceneId + "|" + contentId;
            scope.key = SafeKeyPart(sceneId) + "." + SafeKeyPart(contentId);
            return scope;
        }

        static GaussianSplatAssetBundleLoader FindLoaderForRenderer(GaussianSplatRenderer r)
        {
            if (r == null)
                return null;

            var loader = r.GetComponent<GaussianSplatAssetBundleLoader>();
            if (loader != null && (loader.m_Target == null || loader.m_Target == r))
                return loader;

            var loaders = FindObjectsByType<GaussianSplatAssetBundleLoader>();
            foreach (var candidate in loaders)
            {
                if (candidate != null && candidate.m_Target == r)
                    return candidate;
            }
            return null;
        }

        static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        static string SafeKeyPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "empty";

            string trimmed = value.Trim();
            char[] chars = trimmed.ToLowerInvariant().ToCharArray();
            bool changed = false;
            for (int i = 0; i < chars.Length; ++i)
            {
                char c = chars[i];
                bool valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    chars[i] = '_';
                    changed = true;
                }
            }

            string safe = new string(chars).Trim('_');
            if (string.IsNullOrEmpty(safe))
                safe = "value";
            return changed ? safe + "_" + StableHashHex(trimmed) : safe;
        }

        static string StableHashHex(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; ++i)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }
    }
}
