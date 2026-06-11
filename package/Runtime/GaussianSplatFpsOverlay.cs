// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [DefaultExecutionOrder(10001)]
    public class GaussianSplatFpsOverlay : MonoBehaviour
    {
        public bool m_Show = true;
        public KeyCode m_ToggleKey = KeyCode.F9;
        public int m_FontSize = 18;
        public float m_UpdateInterval = 0.25f;
        public float m_Width = 110;
        public float m_Height = 34;

        float m_TimeLeft;
        int m_FrameCount;
        float m_AccumulatedTime;
        float m_Fps;
        GUIStyle m_LabelStyle;
        Texture2D m_BackgroundTexture;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreateForDevelopmentBuilds()
        {
            if (!Debug.isDebugBuild && !Application.isEditor && !Application.absoluteURL.Contains("gsFps=1"))
                return;
            if (FindObjectOfType<GaussianSplatFpsOverlay>() != null)
                return;

            var go = new GameObject("Gaussian Splat FPS Overlay");
            DontDestroyOnLoad(go);
            go.AddComponent<GaussianSplatFpsOverlay>();
        }

        void Awake()
        {
            m_TimeLeft = Mathf.Max(0.05f, m_UpdateInterval);
        }

        void Update()
        {
            if (Input.GetKeyDown(m_ToggleKey))
                m_Show = !m_Show;

            m_TimeLeft -= Time.unscaledDeltaTime;
            m_AccumulatedTime += Time.unscaledDeltaTime;
            ++m_FrameCount;

            if (m_TimeLeft > 0)
                return;

            m_Fps = m_FrameCount / Mathf.Max(0.0001f, m_AccumulatedTime);
            m_TimeLeft = Mathf.Max(0.05f, m_UpdateInterval);
            m_AccumulatedTime = 0;
            m_FrameCount = 0;
        }

        void OnGUI()
        {
            if (!m_Show)
                return;

            EnsureStyle();

            string text = $"FPS {m_Fps:0.0}";
            const float padding = 8;
            float width = Mathf.Max(80, m_Width);
            float height = Mathf.Max(24, m_Height);
            Rect rect = new Rect(
                Screen.width - width - padding,
                padding,
                width,
                height);

            GUI.DrawTexture(rect, m_BackgroundTexture, ScaleMode.StretchToFill);
            GUI.Label(rect, text, m_LabelStyle);
        }

        void EnsureStyle()
        {
            if (m_LabelStyle != null && m_LabelStyle.fontSize == m_FontSize)
                return;

            m_LabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(8, m_FontSize),
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                clipping = TextClipping.Overflow
            };

            if (m_BackgroundTexture == null)
            {
                m_BackgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                m_BackgroundTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.55f));
                m_BackgroundTexture.Apply(false, true);
            }
        }

        void OnDestroy()
        {
            if (m_BackgroundTexture != null)
                Destroy(m_BackgroundTexture);
        }
    }
}
