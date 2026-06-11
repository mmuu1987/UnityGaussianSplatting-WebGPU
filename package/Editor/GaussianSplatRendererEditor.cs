// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;
using GaussianSplatRenderer = GaussianSplatting.Runtime.GaussianSplatRenderer;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderer))]
    [CanEditMultipleObjects]
    public class GaussianSplatRendererEditor : UnityEditor.Editor
    {
        const string kPrefExportBake = "nesnausk.GaussianSplatting.ExportBakeTransform";

        SerializedProperty m_PropAsset;
        SerializedProperty m_PropRenderOrder;
        SerializedProperty m_PropSplatScale;
        SerializedProperty m_PropOpacityScale;
        SerializedProperty m_PropSHOrder;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_PropSortNthFrame;
        SerializedProperty m_PropWebGpuCpuSortOnlyWhenCameraChanges;
        SerializedProperty m_PropWebGpuCpuSortPositionThreshold;
        SerializedProperty m_PropWebGpuCpuSortAngleThreshold;
        SerializedProperty m_PropWebGpuCpuSortMode;
        SerializedProperty m_PropWebGpuCpuSortBucketCount;
        SerializedProperty m_PropWebGpuCpuSortCachePositions;
        SerializedProperty m_PropWebGpuChunkFrustumCulling;
        SerializedProperty m_PropWebGpuChunkCullPadding;
        SerializedProperty m_PropWebGpuMaxVisibleSplats;
        SerializedProperty m_PropWebGpuDistanceLod;
        SerializedProperty m_PropWebGpuLodNearDistance;
        SerializedProperty m_PropWebGpuLodFarDistance;
        SerializedProperty m_PropWebGpuLodMidSampleStep;
        SerializedProperty m_PropWebGpuLodFarSampleStep;
        SerializedProperty m_PropWebGpuLodMaxSampleStep;
        SerializedProperty m_PropWebGpuLodFarReservePercent;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropCutouts;
        SerializedProperty m_PropShaderSplats;
        SerializedProperty m_PropShaderComposite;
        SerializedProperty m_PropShaderDebugPoints;
        SerializedProperty m_PropShaderDebugBoxes;
        SerializedProperty m_PropCSSplatUtilities;

        bool m_ResourcesExpanded = false;
        int m_CameraIndex = 0;

        bool m_ExportBakeTransform;

        static int s_EditStatsUpdateCounter = 0;

        static HashSet<GaussianSplatRendererEditor> s_AllEditors = new();

        public static void BumpGUICounter()
        {
            ++s_EditStatsUpdateCounter;
        }

        public static void RepaintAll()
        {
            foreach (var e in s_AllEditors)
                e.Repaint();
        }

        public void OnEnable()
        {
            m_ExportBakeTransform = EditorPrefs.GetBool(kPrefExportBake, false);

            m_PropAsset = serializedObject.FindProperty("m_Asset");
            m_PropRenderOrder = serializedObject.FindProperty("m_RenderOrder");
            m_PropSplatScale = serializedObject.FindProperty("m_SplatScale");
            m_PropOpacityScale = serializedObject.FindProperty("m_OpacityScale");
            m_PropSHOrder = serializedObject.FindProperty("m_SHOrder");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropSortNthFrame = serializedObject.FindProperty("m_SortNthFrame");
            m_PropWebGpuCpuSortOnlyWhenCameraChanges = serializedObject.FindProperty("m_WebGpuCpuSortOnlyWhenCameraChanges");
            m_PropWebGpuCpuSortPositionThreshold = serializedObject.FindProperty("m_WebGpuCpuSortPositionThreshold");
            m_PropWebGpuCpuSortAngleThreshold = serializedObject.FindProperty("m_WebGpuCpuSortAngleThreshold");
            m_PropWebGpuCpuSortMode = serializedObject.FindProperty("m_WebGpuCpuSortMode");
            m_PropWebGpuCpuSortBucketCount = serializedObject.FindProperty("m_WebGpuCpuSortBucketCount");
            m_PropWebGpuCpuSortCachePositions = serializedObject.FindProperty("m_WebGpuCpuSortCachePositions");
            m_PropWebGpuChunkFrustumCulling = serializedObject.FindProperty("m_WebGpuChunkFrustumCulling");
            m_PropWebGpuChunkCullPadding = serializedObject.FindProperty("m_WebGpuChunkCullPadding");
            m_PropWebGpuMaxVisibleSplats = serializedObject.FindProperty("m_WebGpuMaxVisibleSplats");
            m_PropWebGpuDistanceLod = serializedObject.FindProperty("m_WebGpuDistanceLod");
            m_PropWebGpuLodNearDistance = serializedObject.FindProperty("m_WebGpuLodNearDistance");
            m_PropWebGpuLodFarDistance = serializedObject.FindProperty("m_WebGpuLodFarDistance");
            m_PropWebGpuLodMidSampleStep = serializedObject.FindProperty("m_WebGpuLodMidSampleStep");
            m_PropWebGpuLodFarSampleStep = serializedObject.FindProperty("m_WebGpuLodFarSampleStep");
            m_PropWebGpuLodMaxSampleStep = serializedObject.FindProperty("m_WebGpuLodMaxSampleStep");
            m_PropWebGpuLodFarReservePercent = serializedObject.FindProperty("m_WebGpuLodFarReservePercent");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropCutouts = serializedObject.FindProperty("m_Cutouts");
            m_PropShaderSplats = serializedObject.FindProperty("m_ShaderSplats");
            m_PropShaderComposite = serializedObject.FindProperty("m_ShaderComposite");
            m_PropShaderDebugPoints = serializedObject.FindProperty("m_ShaderDebugPoints");
            m_PropShaderDebugBoxes = serializedObject.FindProperty("m_ShaderDebugBoxes");
            m_PropCSSplatUtilities = serializedObject.FindProperty("m_CSSplatUtilities");

            s_AllEditors.Add(this);
        }

        public void OnDisable()
        {
            s_AllEditors.Remove(this);
        }

        public override void OnInspectorGUI()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs)
                return;

            serializedObject.Update();

            GUILayout.Label("Data Asset", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropAsset);

            if (!gs.HasValidAsset)
            {
                var msg = gs.asset != null && gs.asset.formatVersion != GaussianSplatAsset.kCurrentVersion
                    ? "Gaussian Splat asset version is not compatible, please recreate the asset"
                    : "Gaussian Splat asset is not assigned or is empty";
                EditorGUILayout.HelpBox(msg, MessageType.Error);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Render Options", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderOrder);
            EditorGUILayout.PropertyField(m_PropSplatScale);
            EditorGUILayout.PropertyField(m_PropOpacityScale);
            EditorGUILayout.PropertyField(m_PropSHOrder);
            EditorGUILayout.PropertyField(m_PropSHOnly);
            EditorGUILayout.PropertyField(m_PropSortNthFrame);
            EditorGUILayout.Space();
            GUILayout.Label("WebGPU 选项", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortOnlyWhenCameraChanges,
                new GUIContent("相机变化时才重新排序", "开启后，相机移动或旋转超过下面的阈值才重新排序。更流畅，但移动很轻微时排序不会每帧更新。"));
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortPositionThreshold,
                new GUIContent("重新排序的位移阈值", "相机移动超过这个距离才重新排序。值越大越省 CPU，但移动时透明层次更新会更慢。"));
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortAngleThreshold,
                new GUIContent("重新排序的旋转阈值", "相机旋转超过这个角度才重新排序。值越大越省 CPU，但转头时透明层次更新会更慢。"));
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortMode,
                new GUIContent("CPU 排序模式", "Depth Buckets 是快速近似排序，适合 WebGPU 大场景；Exact 更准但明显更慢。"));
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortBucketCount,
                new GUIContent("深度桶数量", "Depth Buckets 模式用多少个深度分层。值越大排序越细，画质略好，但 CPU 开销也会增加。"));
            EditorGUILayout.PropertyField(m_PropWebGpuCpuSortCachePositions,
                new GUIContent("缓存点位置", "开启后排序更快，但会额外占很多内存。大场景或浏览器内存紧张时建议关闭。"));
            EditorGUILayout.PropertyField(m_PropWebGpuChunkFrustumCulling,
                new GUIContent("开启分块视锥裁剪", "只处理相机能看到的分块。看不到的点云不排序、不绘制，是 WebGPU 大场景提速的基础开关。"));
            EditorGUILayout.PropertyField(m_PropWebGpuChunkCullPadding,
                new GUIContent("分块裁剪边缘余量", "给分块包围盒额外放大一点，避免画面边缘的点云突然消失。值越大越保守，性能收益会稍微降低。"));
            EditorGUILayout.PropertyField(m_PropWebGpuMaxVisibleSplats,
                new GUIContent("最大可见点数量", "每帧最多参与排序和绘制的点数量。值越大画质越好但越卡；值越小越流畅但容易丢远景或出空洞。0 表示不限制。"));
            EditorGUILayout.PropertyField(m_PropWebGpuDistanceLod,
                new GUIContent("开启距离 LOD 降采样", "超过最大可见点数量时，不再直接丢掉远处，而是让中远距离点云变稀，尽量保住远景轮廓。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodNearDistance,
                new GUIContent("近距离范围", "这个距离以内尽量保持满密度。近处如果有空洞，就把这个值调大。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodFarDistance,
                new GUIContent("远距离起点", "超过这个距离开始按远距离规则降采样。远景消失或太稀，就把这个值调大，或降低远距离采样步长。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodMidSampleStep,
                new GUIContent("中距离采样步长", "中距离每隔几个点取一个。1 表示不降采样；2 表示取一半。中距离有空洞时保持 1。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodFarSampleStep,
                new GUIContent("远距离采样步长", "远距离每隔几个点取一个。值越大远景越省性能但越稀；远景太空就调小。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodMaxSampleStep,
                new GUIContent("最大采样步长", "预算还是不够时，远处最多能被降采样到多稀。值太大远景会碎；值太小可能又卡。"));
            EditorGUILayout.PropertyField(m_PropWebGpuLodFarReservePercent,
                new GUIContent("远景保留预算比例", "从最大可见点数量里预留多少百分比给远景。值越大远处越不容易消失，但近中距离预算会变少。"));

            EditorGUILayout.Space();
            GUILayout.Label("Debugging Tweaks", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderer.RenderMode.DebugPoints or (int)GaussianSplatRenderer.RenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PropPointDisplaySize);

            EditorGUILayout.Space();
            m_ResourcesExpanded = EditorGUILayout.Foldout(m_ResourcesExpanded, "Resources", true, EditorStyles.foldoutHeader);
            if (m_ResourcesExpanded)
            {
                EditorGUILayout.PropertyField(m_PropShaderSplats);
                EditorGUILayout.PropertyField(m_PropShaderComposite);
                EditorGUILayout.PropertyField(m_PropShaderDebugPoints);
                EditorGUILayout.PropertyField(m_PropShaderDebugBoxes);
                EditorGUILayout.PropertyField(m_PropCSSplatUtilities);
            }
            bool validAndEnabled = gs && gs.enabled && gs.gameObject.activeInHierarchy && gs.HasValidAsset;
            if (validAndEnabled && !gs.HasValidRenderSetup)
            {
                EditorGUILayout.HelpBox("Shader resources are not set up", MessageType.Error);
                validAndEnabled = false;
            }

            if (validAndEnabled && targets.Length == 1)
            {
                EditCameras(gs);
                EditGUI(gs);
            }
            if (validAndEnabled && targets.Length > 1)
            {
                MultiEditGUI();
            }

            serializedObject.ApplyModifiedProperties();
        }

        void EditCameras(GaussianSplatRenderer gs)
        {
            var asset = gs.asset;
            var cameras = asset.cameras;
            if (cameras != null && cameras.Length != 0)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Cameras", EditorStyles.boldLabel);
                var camIndex = EditorGUILayout.IntSlider("Camera", m_CameraIndex, 0, cameras.Length - 1);
                camIndex = math.clamp(camIndex, 0, cameras.Length - 1);
                if (camIndex != m_CameraIndex)
                {
                    m_CameraIndex = camIndex;
                    gs.ActivateCamera(camIndex);
                }
            }
        }

        void MultiEditGUI()
        {
            DrawSeparator();
            CountTargetSplats(out var totalSplats, out var totalObjects);
            EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
            EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
            {
                EditorGUILayout.HelpBox($"Can't merge, too many splats (max. supported {GaussianSplatAsset.kMaxSplats:N0})", MessageType.Warning);
                return;
            }

            var targetGs = (GaussianSplatRenderer) target;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (no asset or disable)", MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox($"Can't merge into {target.name} (needs to use Very High quality preset)", MessageType.Warning);
                return;
            }
            if (GUILayout.Button($"Merge into {target.name}"))
            {
                MergeSplatObjects();
            }
        }

        void CountTargetSplats(out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        void MergeSplatObjects()
        {
            CountTargetSplats(out var totalSplats, out _);
            if (totalSplats > GaussianSplatAsset.kMaxSplats)
                return;
            var targetGs = (GaussianSplatRenderer) target;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }
            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        void EditGUI(GaussianSplatRenderer gs)
        {
            ++s_EditStatsUpdateCounter;

            DrawSeparator();
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }

            GUILayout.EndHorizontal();
            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Cutout"))
            {
                GaussianCutout cutout = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout)).GetComponent<GaussianCutout>();
                Transform cutoutTr = cutout.transform;
                cutoutTr.SetParent(gs.transform, false);
                cutoutTr.localScale = (gs.asset.boundsMax - gs.asset.boundsMin) * 0.25f;
                gs.m_Cutouts ??= Array.Empty<GaussianCutout>();
                ArrayUtility.Add(ref gs.m_Cutouts, cutout);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
                Selection.activeGameObject = cutout.gameObject;
            }
            if (GUILayout.Button("Use All Cutouts"))
            {
                gs.m_Cutouts = FindObjectsByType<GaussianCutout>(FindObjectsSortMode.InstanceID);
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }

            if (GUILayout.Button("No Cutouts"))
            {
                gs.m_Cutouts = Array.Empty<GaussianCutout>();
                gs.UpdateEditCountsAndBounds();
                EditorUtility.SetDirty(gs);
            }
            GUILayout.EndHorizontal();
            EditorGUILayout.PropertyField(m_PropCutouts);

            bool hasCutouts = gs.m_Cutouts != null && gs.m_Cutouts.Length != 0;
            bool modifiedOrHasCutouts = gs.editModified || hasCutouts;

            var asset = gs.asset;
            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            m_ExportBakeTransform = EditorGUILayout.Toggle("Export in world space", m_ExportBakeTransform);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetBool(kPrefExportBake, m_ExportBakeTransform);
            }

            if (GUILayout.Button("Export PLY"))
                ExportPlyFile(gs, m_ExportBakeTransform);
            if (asset.posFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.scaleFormat > GaussianSplatAsset.VectorFormat.Norm16 ||
                asset.colorFormat > GaussianSplatAsset.ColorFormat.Float16x4 ||
                asset.shFormat > GaussianSplatAsset.SHFormat.Float16)
            {
                EditorGUILayout.HelpBox(
                    "It is recommended to use High or VeryHigh quality preset for editing splats, lower levels are lossy",
                    MessageType.Warning);
            }

            bool displayEditStats = isToolActive || modifiedOrHasCutouts;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");
            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (hasCutouts)
                {
                    if (s_EditStatsUpdateCounter > 10)
                    {
                        gs.UpdateEditCountsAndBounds();
                        s_EditStatsUpdateCounter = 0;
                    }
                }
            }
        }

        static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }

        bool HasFrameBounds()
        {
            return true;
        }

        Bounds OnGetFrameBounds()
        {
            var gs = target as GaussianSplatRenderer;
            if (!gs || !gs.HasValidRenderSetup)
                return new Bounds(Vector3.zero, Vector3.one);
            Bounds bounds = default;
            bounds.SetMinMax(gs.asset.boundsMin, gs.asset.boundsMax);
            if (gs.editSelectedSplats > 0)
            {
                bounds = gs.editSelectedBounds;
            }
            bounds.extents *= 0.7f;
            return TransformBounds(gs.transform, bounds);
        }

        public static Bounds TransformBounds(Transform tr, Bounds bounds )
        {
            var center = tr.TransformPoint(bounds.center);

            var ext = bounds.extents;
            var axisX = tr.TransformVector(ext.x, 0, 0);
            var axisY = tr.TransformVector(0, ext.y, 0);
            var axisZ = tr.TransformVector(0, 0, ext.z);

            // sum their absolute value to get the world extents
            ext.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            ext.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            ext.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);

            return new Bounds { center = center, extents = ext };
        }

        static unsafe void ExportPlyFile(GaussianSplatRenderer gs, bool bakeTransform)
        {
            var path = EditorUtility.SaveFilePanel(
                "Export Gaussian Splat PLY file", "", $"{gs.asset.name}-edit.ply", "ply");
            if (string.IsNullOrWhiteSpace(path))
                return;

            int kSplatSize = UnsafeUtility.SizeOf<Utils.InputSplatData>();
            using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, gs.splatCount, kSplatSize);

            if (!gs.EditExportData(gpuData, bakeTransform))
                return;

            Utils.InputSplatData[] data = new Utils.InputSplatData[gpuData.count];
            gpuData.GetData(data);

            var gpuDeleted = gs.GpuEditDeleted;
            uint[] deleted = new uint[gpuDeleted.count];
            gpuDeleted.GetData(deleted);

            // count non-deleted splats
            int aliveCount = 0;
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                    ++aliveCount;
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            // note: this is a long string! but we don't use multiline literal because we want guaranteed LF line ending
            var header = $"ply\nformat binary_little_endian 1.0\nelement vertex {aliveCount}\nproperty float x\nproperty float y\nproperty float z\nproperty float nx\nproperty float ny\nproperty float nz\nproperty float f_dc_0\nproperty float f_dc_1\nproperty float f_dc_2\nproperty float f_rest_0\nproperty float f_rest_1\nproperty float f_rest_2\nproperty float f_rest_3\nproperty float f_rest_4\nproperty float f_rest_5\nproperty float f_rest_6\nproperty float f_rest_7\nproperty float f_rest_8\nproperty float f_rest_9\nproperty float f_rest_10\nproperty float f_rest_11\nproperty float f_rest_12\nproperty float f_rest_13\nproperty float f_rest_14\nproperty float f_rest_15\nproperty float f_rest_16\nproperty float f_rest_17\nproperty float f_rest_18\nproperty float f_rest_19\nproperty float f_rest_20\nproperty float f_rest_21\nproperty float f_rest_22\nproperty float f_rest_23\nproperty float f_rest_24\nproperty float f_rest_25\nproperty float f_rest_26\nproperty float f_rest_27\nproperty float f_rest_28\nproperty float f_rest_29\nproperty float f_rest_30\nproperty float f_rest_31\nproperty float f_rest_32\nproperty float f_rest_33\nproperty float f_rest_34\nproperty float f_rest_35\nproperty float f_rest_36\nproperty float f_rest_37\nproperty float f_rest_38\nproperty float f_rest_39\nproperty float f_rest_40\nproperty float f_rest_41\nproperty float f_rest_42\nproperty float f_rest_43\nproperty float f_rest_44\nproperty float opacity\nproperty float scale_0\nproperty float scale_1\nproperty float scale_2\nproperty float rot_0\nproperty float rot_1\nproperty float rot_2\nproperty float rot_3\nend_header\n";
            fs.Write(Encoding.UTF8.GetBytes(header));
            for (int i = 0; i < data.Length; ++i)
            {
                int wordIdx = i >> 5;
                int bitIdx = i & 31;
                bool isDeleted = (deleted[wordIdx] & (1u << bitIdx)) != 0;
                bool isCutout = data[i].nor.sqrMagnitude > 0;
                if (!isDeleted && !isCutout)
                {
                    var splat = data[i];
                    byte* ptr = (byte*)&splat;
                    fs.Write(new ReadOnlySpan<byte>(ptr, kSplatSize));
                }
            }

            Debug.Log($"Exported PLY {path} with {aliveCount:N0} splats");
        }
    }
}
