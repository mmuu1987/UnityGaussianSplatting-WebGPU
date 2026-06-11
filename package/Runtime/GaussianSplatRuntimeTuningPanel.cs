// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

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
        readonly List<GaussianSplatRenderer> m_Renderers = new();

        public static bool BlocksCameraInput { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreateForDevelopmentBuilds()
        {
            if (!Debug.isDebugBuild && !Application.isEditor && !Application.absoluteURL.Contains("gsPanel=1"))
                return;
            if (FindObjectOfType<GaussianSplatRuntimeTuningPanel>() != null)
                return;

            var go = new GameObject("Gaussian Splat Runtime Tuning Panel");
            DontDestroyOnLoad(go);
            go.AddComponent<GaussianSplatRuntimeTuningPanel>();
        }

        void Awake()
        {
            m_Visible = m_ShowOnStart;
            RefreshRenderers();
            if (m_Target == null && m_Renderers.Count != 0)
                m_Target = m_Renderers[0];
            if (m_LoadSavedOnStart && m_Target != null)
                LoadSettings(m_Target);
        }

        void Update()
        {
            if (Input.GetKeyDown(m_ToggleKey))
                m_Visible = !m_Visible;

            UpdateCursorLock();
        }

        void OnGUI()
        {
            if (!m_Visible)
                return;

            m_WindowRect = GUILayout.Window(GetInstanceID(), m_WindowRect, DrawWindow, "GS WebGPU Tuning");
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
            GUILayout.Label($"F8: hide/show panel. Panel visible = mouse look unlocked.");
            GUILayout.Label("Tip: tune here first, then use Print to copy final values.");

            DrawTargetSelector();

            if (m_Target == null)
            {
                GUILayout.Label("No GaussianSplatRenderer found.");
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }

            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Width(400), GUILayout.Height(610));

            GUILayout.Label($"Target: {m_Target.name}");
            GUILayout.Label($"Drawn Points / Total Points: {m_Target.renderSplatCount:N0} / {m_Target.splatCount:N0}");
            GUILayout.Space(8);

            DrawBool("01 Sort Only When Camera Changes", ref m_Target.m_WebGpuCpuSortOnlyWhenCameraChanges,
                "Only sort when camera moves/rotates enough. Faster.");
            DrawFloat("02 Sort Move Threshold", ref m_Target.m_WebGpuCpuSortPositionThreshold, 0, 0.2f,
                "Bigger = less CPU, slower transparency update. Try 0.01-0.05.");
            DrawFloat("03 Sort Rotate Threshold", ref m_Target.m_WebGpuCpuSortAngleThreshold, 0, 2.0f,
                "Bigger = less CPU. Try 0.25-1.");

            GUILayout.Space(8);
            DrawSortMode();
            DrawInt("04 Depth bucket count", ref m_Target.m_WebGpuCpuSortBucketCount, 256, 16384,
                "More = better sort, more CPU. Try 2048-8192.");
            DrawBool("05 Cache Point Positions", ref m_Target.m_WebGpuCpuSortCachePositions,
                "Faster sort but uses much more memory. Usually OFF for big Web scenes.");

            GUILayout.Space(8);
            DrawBool("06 Chunk Frustum Culling", ref m_Target.m_WebGpuChunkFrustumCulling,
                "Only process chunks visible by camera. Main speed switch.");
            DrawFloat("07 Culling padding", ref m_Target.m_WebGpuChunkCullPadding, 0, 10,
                "Increase if edge splats pop/disappear.");
            DrawInt("08 Visible Points Budget", ref m_Target.m_WebGpuMaxVisibleSplats, 0, 6000000,
                "Per-frame point budget. Bigger = better quality but slower. 0 = unlimited.");

            GUILayout.Space(8);
            DrawBool("09 Distance LOD", ref m_Target.m_WebGpuDistanceLod,
                "When over budget, make mid/far points sparse instead of dropping all.");
            DrawFloat("10 Near Full Quality Distance", ref m_Target.m_WebGpuLodNearDistance, 0, 300,
                "Near range keeps full density. Increase if near/mid has holes.");
            DrawFloat("11 Far LOD Start Distance", ref m_Target.m_WebGpuLodFarDistance, 0, 600,
                "Far LOD starts after this distance. Increase if far is too sparse.");
            DrawInt("12 Mid Sample Step", ref m_Target.m_WebGpuLodMidSampleStep, 1, 8,
                "Mid range: take every Nth point. 1=no sampling, 2=half.");
            DrawInt("13 Far Sample Step", ref m_Target.m_WebGpuLodFarSampleStep, 1, 16,
                "Far range: take every Nth point. Bigger=faster but sparser.");
            DrawInt("14 Max sample step", ref m_Target.m_WebGpuLodMaxSampleStep, 1, 32,
                "When budget is still not enough, farthest chunks can become this sparse.");
            DrawInt("15 Far Reserve Percent", ref m_Target.m_WebGpuLodFarReservePercent, 0, 50,
                "Reserve part of Max Visible for far scenery. Increase if far disappears.");

            GUILayout.Space(8);
            DrawFloat("16 Splat size", ref m_Target.m_SplatScale, 0.5f, 2.0f,
                "Slightly increase to fill small holes. Try 1.05-1.15.");
            DrawFloat("17 Opacity", ref m_Target.m_OpacityScale, 0.1f, 3.0f,
                "Increase if image is too thin. Too high becomes blurry/white.");

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
                SaveSettings(m_Target);
            if (GUILayout.Button("Load"))
                LoadSettings(m_Target);
            if (GUILayout.Button("Print"))
                LogSettings(m_Target);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        void DrawTargetSelector()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Target", GUILayout.Width(48));
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                RefreshRenderers();

            if (m_Renderers.Count <= 1)
            {
                GUILayout.Label(m_Target ? m_Target.name : "None");
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

        void RefreshRenderers()
        {
            m_Renderers.Clear();
            m_Renderers.AddRange(FindObjectsOfType<GaussianSplatRenderer>());
            m_Renderers.RemoveAll(r => r == null);
        }

        static void DrawBool(string label, ref bool value, string help)
        {
            value = GUILayout.Toggle(value, new GUIContent(label, help));
        }

        static void DrawFloat(string label, ref float value, float min, float max, string help)
        {
            GUILayout.Label(new GUIContent($"{label}: {value:0.###}", help));
            value = GUILayout.HorizontalSlider(value, min, max);
        }

        static void DrawInt(string label, ref int value, int min, int max, string help)
        {
            GUILayout.Label(new GUIContent($"{label}: {value:N0}", help));
            value = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
        }

        void DrawSortMode()
        {
            GUILayout.Label("CPU Sort Mode");
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(m_Target.m_WebGpuCpuSortMode == GaussianSplatRenderer.WebGpuCpuSortMode.DepthBuckets, "Fast", GUI.skin.button))
                m_Target.m_WebGpuCpuSortMode = GaussianSplatRenderer.WebGpuCpuSortMode.DepthBuckets;
            if (GUILayout.Toggle(m_Target.m_WebGpuCpuSortMode == GaussianSplatRenderer.WebGpuCpuSortMode.Exact, "Exact Slow", GUI.skin.button))
                m_Target.m_WebGpuCpuSortMode = GaussianSplatRenderer.WebGpuCpuSortMode.Exact;
            GUILayout.EndHorizontal();
        }

        static void SaveSettings(GaussianSplatRenderer r)
        {
            if (r == null) return;
            PlayerPrefs.SetInt(Key("SortOnChange"), r.m_WebGpuCpuSortOnlyWhenCameraChanges ? 1 : 0);
            PlayerPrefs.SetFloat(Key("SortPosThreshold"), r.m_WebGpuCpuSortPositionThreshold);
            PlayerPrefs.SetFloat(Key("SortAngleThreshold"), r.m_WebGpuCpuSortAngleThreshold);
            PlayerPrefs.SetInt(Key("SortMode"), (int)r.m_WebGpuCpuSortMode);
            PlayerPrefs.SetInt(Key("BucketCount"), r.m_WebGpuCpuSortBucketCount);
            PlayerPrefs.SetInt(Key("CachePositions"), r.m_WebGpuCpuSortCachePositions ? 1 : 0);
            PlayerPrefs.SetInt(Key("ChunkCulling"), r.m_WebGpuChunkFrustumCulling ? 1 : 0);
            PlayerPrefs.SetFloat(Key("ChunkPadding"), r.m_WebGpuChunkCullPadding);
            PlayerPrefs.SetInt(Key("MaxVisibleSplats"), r.m_WebGpuMaxVisibleSplats);
            PlayerPrefs.SetInt(Key("DistanceLod"), r.m_WebGpuDistanceLod ? 1 : 0);
            PlayerPrefs.SetFloat(Key("LodNear"), r.m_WebGpuLodNearDistance);
            PlayerPrefs.SetFloat(Key("LodFar"), r.m_WebGpuLodFarDistance);
            PlayerPrefs.SetInt(Key("LodMidStep"), r.m_WebGpuLodMidSampleStep);
            PlayerPrefs.SetInt(Key("LodFarStep"), r.m_WebGpuLodFarSampleStep);
            PlayerPrefs.SetInt(Key("LodMaxStep"), r.m_WebGpuLodMaxSampleStep);
            PlayerPrefs.SetInt(Key("LodFarReserve"), r.m_WebGpuLodFarReservePercent);
            PlayerPrefs.SetFloat(Key("SplatScale"), r.m_SplatScale);
            PlayerPrefs.SetFloat(Key("OpacityScale"), r.m_OpacityScale);
            PlayerPrefs.Save();
            Debug.Log("Gaussian WebGPU runtime tuning settings saved.");
        }

        static void LoadSettings(GaussianSplatRenderer r)
        {
            if (r == null || !PlayerPrefs.HasKey(Key("MaxVisibleSplats"))) return;
            r.m_WebGpuCpuSortOnlyWhenCameraChanges = PlayerPrefs.GetInt(Key("SortOnChange"), r.m_WebGpuCpuSortOnlyWhenCameraChanges ? 1 : 0) != 0;
            r.m_WebGpuCpuSortPositionThreshold = PlayerPrefs.GetFloat(Key("SortPosThreshold"), r.m_WebGpuCpuSortPositionThreshold);
            r.m_WebGpuCpuSortAngleThreshold = PlayerPrefs.GetFloat(Key("SortAngleThreshold"), r.m_WebGpuCpuSortAngleThreshold);
            r.m_WebGpuCpuSortMode = (GaussianSplatRenderer.WebGpuCpuSortMode)PlayerPrefs.GetInt(Key("SortMode"), (int)r.m_WebGpuCpuSortMode);
            r.m_WebGpuCpuSortBucketCount = PlayerPrefs.GetInt(Key("BucketCount"), r.m_WebGpuCpuSortBucketCount);
            r.m_WebGpuCpuSortCachePositions = PlayerPrefs.GetInt(Key("CachePositions"), r.m_WebGpuCpuSortCachePositions ? 1 : 0) != 0;
            r.m_WebGpuChunkFrustumCulling = PlayerPrefs.GetInt(Key("ChunkCulling"), r.m_WebGpuChunkFrustumCulling ? 1 : 0) != 0;
            r.m_WebGpuChunkCullPadding = PlayerPrefs.GetFloat(Key("ChunkPadding"), r.m_WebGpuChunkCullPadding);
            r.m_WebGpuMaxVisibleSplats = PlayerPrefs.GetInt(Key("MaxVisibleSplats"), r.m_WebGpuMaxVisibleSplats);
            r.m_WebGpuDistanceLod = PlayerPrefs.GetInt(Key("DistanceLod"), r.m_WebGpuDistanceLod ? 1 : 0) != 0;
            r.m_WebGpuLodNearDistance = PlayerPrefs.GetFloat(Key("LodNear"), r.m_WebGpuLodNearDistance);
            r.m_WebGpuLodFarDistance = PlayerPrefs.GetFloat(Key("LodFar"), r.m_WebGpuLodFarDistance);
            r.m_WebGpuLodMidSampleStep = PlayerPrefs.GetInt(Key("LodMidStep"), r.m_WebGpuLodMidSampleStep);
            r.m_WebGpuLodFarSampleStep = PlayerPrefs.GetInt(Key("LodFarStep"), r.m_WebGpuLodFarSampleStep);
            r.m_WebGpuLodMaxSampleStep = PlayerPrefs.GetInt(Key("LodMaxStep"), r.m_WebGpuLodMaxSampleStep);
            r.m_WebGpuLodFarReservePercent = PlayerPrefs.GetInt(Key("LodFarReserve"), r.m_WebGpuLodFarReservePercent);
            r.m_SplatScale = PlayerPrefs.GetFloat(Key("SplatScale"), r.m_SplatScale);
            r.m_OpacityScale = PlayerPrefs.GetFloat(Key("OpacityScale"), r.m_OpacityScale);
            Debug.Log("Gaussian WebGPU runtime tuning settings loaded.");
        }

        static void LogSettings(GaussianSplatRenderer r)
        {
            if (r == null) return;
            Debug.Log(
                "Gaussian WebGPU tuning:\n" +
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

        static string Key(string name) => kPrefsPrefix + name;
    }
}
