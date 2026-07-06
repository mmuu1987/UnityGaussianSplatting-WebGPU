// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Lightweight loading overlay for WebGPU Gaussian package loading.
    /// Uses IMGUI to avoid UI package dependencies in Web builds.
    /// </summary>
    [DefaultExecutionOrder(9000)]
    public class GaussianSplatLoadingOverlay : MonoBehaviour
    {
        public static bool s_AutoCreateEnabled = false;

        public bool m_ShowWhenLoadersExist;
        public bool m_AutoHideWhenReady = true;
        [Min(0)] public float m_MinVisibleSeconds = 0.25f;
        public Color m_BackgroundColor = new Color(0.3301887f, 0.3301887f, 0.3301887f, 1f);
        [Range(0.1f, 1.0f)] public float m_BackgroundOpacity = 1f;
        public KeyCode m_ToggleKey = KeyCode.None;

        readonly List<GaussianSplatAssetBundleLoader> m_Loaders = new();
        GUIStyle m_TitleStyle;
        GUIStyle m_StatusStyle;
        GUIStyle m_ErrorStyle;
        Texture2D m_WhiteTexture;
        float m_NextRefreshTime;
        float m_VisibleSince = -1;
        bool m_ForceHidden;
        bool m_Show;
        bool m_HasError;
        string m_Status;
        float m_Progress;

        public static bool BlocksSceneInput { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (!s_AutoCreateEnabled)
                return;

            if (FindObjectOfType<GaussianSplatLoadingOverlay>() != null)
                return;

            var go = new GameObject("Gaussian Splat Loading Overlay");
            DontDestroyOnLoad(go);
            go.AddComponent<GaussianSplatLoadingOverlay>();
        }

        void Update()
        {
            if (m_ToggleKey != KeyCode.None && Input.GetKeyDown(m_ToggleKey))
                m_ForceHidden = !m_ForceHidden;

            RefreshLoaders(false);
            UpdateState();
        }

        void OnDisable()
        {
            BlocksSceneInput = false;
        }

        void OnDestroy()
        {
            BlocksSceneInput = false;
            if (m_WhiteTexture != null)
                Destroy(m_WhiteTexture);
        }

        void OnGUI()
        {
            if (!m_Show || m_ForceHidden)
                return;

            EnsureStyles();

            Rect screen = new Rect(0, 0, Screen.width, Screen.height);
            Color backgroundColor = m_BackgroundColor;
            backgroundColor.a *= m_BackgroundOpacity;
            DrawRect(screen, backgroundColor);

            float panelWidth = Mathf.Min(560, Screen.width - 48);
            float panelHeight = 148;
            Rect panel = new Rect(
                (Screen.width - panelWidth) * 0.5f,
                (Screen.height - panelHeight) * 0.5f,
                panelWidth,
                panelHeight);

            GUILayout.BeginArea(panel);
            GUILayout.Label(m_HasError ? "Point Cloud Load Failed" : "Loading Point Cloud", m_TitleStyle);
            GUILayout.Space(18);

            Rect barBg = GUILayoutUtility.GetRect(panelWidth, 16);
            DrawRect(barBg, new Color(1, 1, 1, 0.16f));
            Rect barFill = new Rect(barBg.x, barBg.y, Mathf.Clamp01(m_Progress) * barBg.width, barBg.height);
            DrawRect(barFill, m_HasError ? new Color(0.95f, 0.25f, 0.18f, 0.95f) : new Color(0.2f, 0.72f, 1.0f, 0.95f));

            GUILayout.Space(10);
            GUILayout.Label($"{Mathf.RoundToInt(Mathf.Clamp01(m_Progress) * 100)}%", m_StatusStyle);
            GUILayout.Label(string.IsNullOrEmpty(m_Status) ? "Preparing..." : m_Status, m_HasError ? m_ErrorStyle : m_StatusStyle);
            GUILayout.EndArea();
        }

        void RefreshLoaders(bool force)
        {
            if (!force && Time.unscaledTime < m_NextRefreshTime && m_Loaders.Count != 0)
                return;

            m_NextRefreshTime = Time.unscaledTime + 0.25f;
            m_Loaders.Clear();
            m_Loaders.AddRange(FindObjectsOfType<GaussianSplatAssetBundleLoader>());
            m_Loaders.RemoveAll(loader => loader == null || !loader.isActiveAndEnabled);
        }

        void UpdateState()
        {
            bool shouldShow = false;
            bool hasError = false;
            float progressSum = 0;
            int progressCount = 0;
            string status = null;

            for (int i = 0; i < m_Loaders.Count; ++i)
            {
                var loader = m_Loaders[i];
                if (loader == null)
                    continue;

                bool loaderExpected = loader.m_LoadOnStart || loader.isLoading || loader.hasError;
                bool loaderReady = loader.isLoaded;
                if (loaderExpected && !loaderReady)
                    shouldShow = true;

                if (loader.hasError)
                {
                    hasError = true;
                    status = loader.errorMessage;
                }
                else if (loader.isLoading || (loaderExpected && !loaderReady))
                {
                    status = loader.status;
                }

                if (loaderExpected)
                {
                    progressSum += Mathf.Clamp01(loader.progress);
                    ++progressCount;
                }
            }

            if (m_ShowWhenLoadersExist && m_Loaders.Count != 0 && progressCount == 0)
            {
                shouldShow = true;
                status = "Preparing loaders...";
            }

            float progress = progressCount != 0 ? progressSum / progressCount : 1;
            if (hasError)
                shouldShow = true;

            if (shouldShow && m_VisibleSince < 0)
                m_VisibleSince = Time.unscaledTime;

            bool minTimeElapsed = m_VisibleSince < 0 || Time.unscaledTime - m_VisibleSince >= m_MinVisibleSeconds;
            if (!shouldShow && (!m_AutoHideWhenReady || !minTimeElapsed))
                shouldShow = m_Show;

            if (!shouldShow)
                m_VisibleSince = -1;

            m_Show = shouldShow;
            m_HasError = hasError;
            m_Status = status;
            m_Progress = hasError ? progress : Mathf.Clamp01(progress);
            BlocksSceneInput = m_Show && !m_ForceHidden && !m_HasError;
        }

        void EnsureStyles()
        {
            if (m_TitleStyle != null)
                return;

            m_TitleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                wordWrap = false
            };
            m_StatusStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 15,
                normal = { textColor = new Color(0.9f, 0.94f, 1.0f, 0.96f) },
                wordWrap = true
            };
            m_ErrorStyle = new GUIStyle(m_StatusStyle)
            {
                normal = { textColor = new Color(1.0f, 0.68f, 0.62f, 1.0f) }
            };
            m_WhiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            m_WhiteTexture.SetPixel(0, 0, Color.white);
            m_WhiteTexture.Apply(false, true);
        }

        void DrawRect(Rect rect, Color color)
        {
            Color oldColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, m_WhiteTexture);
            GUI.color = oldColor;
        }
    }
}
