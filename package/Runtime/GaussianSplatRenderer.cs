// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    public readonly struct GaussianSplatLoadReport
    {
        public GaussianSplatLoadReport(GaussianSplatRenderer renderer, string stage, float normalized, long loadedBytes, long totalBytes)
        {
            Renderer = renderer;
            Stage = stage;
            Normalized = normalized;
            LoadedBytes = loadedBytes;
            TotalBytes = totalBytes;
        }

        public GaussianSplatRenderer Renderer { get; }
        public string Stage { get; }
        public float Normalized { get; }
        public long LoadedBytes { get; }
        public long TotalBytes { get; }
    }

    class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker s_ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker s_ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => ms_Instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem ms_Instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> m_Splats = new();
        readonly HashSet<Camera> m_CameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> m_ActiveSplats = new();

        CommandBuffer m_CommandBuffer;

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (!r || m_Splats.ContainsKey(r))
                return;

            if (m_Splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            m_Splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!m_Splats.ContainsKey(r))
                return;
            m_Splats.Remove(r);
            if (m_Splats.Count == 0)
            {
                if (m_CameraCommandBuffersDone != null)
                {
                    if (m_CommandBuffer != null)
                    {
                        foreach (var cam in m_CameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                        }
                    }
                    m_CameraCommandBuffersDone.Clear();
                }

                m_ActiveSplats.Clear();
                m_CommandBuffer?.Dispose();
                m_CommandBuffer = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
            // gather all active & valid splat objects
            m_ActiveSplats.Clear();
            foreach (var kvp in m_Splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                m_ActiveSplats.Add((kvp.Key, kvp.Value));
            }
            if (m_ActiveSplats.Count == 0)
                return false;

            // sort them by order and depth from camera
            var camTr = cam.transform;
            m_ActiveSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.m_RenderOrder;
                var orderB = b.Item1.m_RenderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = null;
            foreach (var kvp in m_ActiveSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs.m_MatComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                bool forceSort = gs.PrepareRenderForCamera(cam, matrix);
                int sortNthFrame = Mathf.Max(1, gs.m_SortNthFrame);
                if (forceSort || gs.m_FrameCounter % sortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.m_FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.m_RenderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs.m_MatDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs.m_MatDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs.m_MatDebugBoxes,
                    _ => gs.m_MatSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.m_GpuChunks);

                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.m_GpuView);

                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.m_GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.m_SplatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.m_OpacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.m_PointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.m_SHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.m_SHOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(s_ProfCalcView);
                gs.CalcViewData(cmb, cam);
                cmb.EndSample(s_ProfCalcView);

                // draw
                int indexCount = 6;
                int instanceCount = gs.renderSplatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.m_RenderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.m_RenderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs.m_GpuChunksValid ? gs.m_GpuChunks.count : 0;

                cmb.BeginSample(s_ProfDraw);
                cmb.DrawProcedural(gs.m_GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(s_ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            m_CommandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !m_CameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, m_CommandBuffer);
                m_CameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            m_CommandBuffer.Clear();
            return m_CommandBuffer;
        }

        void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            m_CommandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            m_CommandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            m_CommandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            m_CommandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, m_CommandBuffer);

            // compose
            m_CommandBuffer.BeginSample(s_ProfCompose);
            m_CommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            m_CommandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
            m_CommandBuffer.EndSample(s_ProfCompose);
            m_CommandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }
        public enum WebGpuCpuSortMode
        {
            DepthBuckets,
            Exact
        }
        public GaussianSplatAsset m_Asset;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        public int m_RenderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        public float m_SplatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        public float m_OpacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        public int m_SHOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        public int m_SortNthFrame = 1;
        [Tooltip("WebGPU CPU sort: only re-sort when camera or object movement exceeds the thresholds below.")]
        public bool m_WebGpuCpuSortOnlyWhenCameraChanges = true;
        [Min(0)] [Tooltip("WebGPU CPU sort position threshold, in world units.")]
        public float m_WebGpuCpuSortPositionThreshold = 0.01f;
        [Min(0)] [Tooltip("WebGPU CPU sort rotation threshold, in degrees.")]
        public float m_WebGpuCpuSortAngleThreshold = 0.25f;
        [Tooltip("WebGPU CPU sort mode. Depth buckets are much faster for large scenes; exact sort is higher quality but slower.")]
        public WebGpuCpuSortMode m_WebGpuCpuSortMode = WebGpuCpuSortMode.DepthBuckets;
        [Min(16)] [Tooltip("WebGPU depth bucket count. More buckets improve ordering quality but add a little CPU work.")]
        public int m_WebGpuCpuSortBucketCount = 4096;
        [Tooltip("WebGPU CPU sort: cache decoded positions for faster sorting. Disable for large scenes to reduce memory use.")]
        public bool m_WebGpuCpuSortCachePositions;
        [Tooltip("WebGPU: cull fixed-size splat chunks against the camera frustum before CPU sorting and drawing.")]
        public bool m_WebGpuChunkFrustumCulling = true;
        [Min(0)] [Tooltip("Extra local-space padding added to chunk bounds before frustum culling. Increase if splats pop near screen edges.")]
        public float m_WebGpuChunkCullPadding = 1.0f;
        [Min(0)] [Tooltip("WebGPU: maximum visible splats after frustum culling. If exceeded, nearest chunks are kept first. Set to 0 for unlimited.")]
        public int m_WebGpuMaxVisibleSplats = 2_000_000;
        [Tooltip("WebGPU: when visible splats exceed the budget, keep distant chunks visible by sampling fewer splats from them instead of dropping them immediately.")]
        public bool m_WebGpuDistanceLod = true;
        [Min(0)] [Tooltip("WebGPU distance LOD: chunks nearer than this world-space distance are kept at full density when possible.")]
        public float m_WebGpuLodNearDistance = 25.0f;
        [Min(0)] [Tooltip("WebGPU distance LOD: chunks farther than this world-space distance use the far sample step.")]
        public float m_WebGpuLodFarDistance = 80.0f;
        [Min(1)] [Tooltip("WebGPU distance LOD: sample every Nth splat for middle-distance chunks.")]
        public int m_WebGpuLodMidSampleStep = 2;
        [Min(1)] [Tooltip("WebGPU distance LOD: sample every Nth splat for far chunks.")]
        public int m_WebGpuLodFarSampleStep = 4;
        [Min(1)] [Tooltip("WebGPU distance LOD: maximum sample step used when the visible splat budget is still exceeded.")]
        public int m_WebGpuLodMaxSampleStep = 16;
        [Range(0, 50)] [Tooltip("WebGPU distance LOD: percentage of the visible splat budget reserved for far chunks so distant scenery does not disappear completely.")]
        public int m_WebGpuLodFarReservePercent = 20;

        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;

        public GaussianCutout[] m_Cutouts;

        public Shader m_ShaderSplats;
        public Shader m_ShaderComposite;
        public Shader m_ShaderDebugPoints;
        public Shader m_ShaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        public ComputeShader m_CSSplatUtilities;

        int m_SplatCount; // initially same as asset splat count, but editing can change this
        int m_RenderSplatCount;
        GraphicsBuffer m_GpuSortDistances;
        internal GraphicsBuffer m_GpuSortKeys;
        GraphicsBuffer m_GpuPosData;
        GraphicsBuffer m_GpuOtherData;
        GraphicsBuffer m_GpuSHData;
        Texture m_GpuColorData;
        internal GraphicsBuffer m_GpuChunks;
        internal bool m_GpuChunksValid;
        internal GraphicsBuffer m_GpuView;
        internal GraphicsBuffer m_GpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer m_GpuEditCutouts;
        GraphicsBuffer m_GpuEditCountsBounds;
        GraphicsBuffer m_GpuEditSelected;
        GraphicsBuffer m_GpuEditDeleted;
        GraphicsBuffer m_GpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer m_GpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer m_GpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting m_Sorter;
        GpuSorting.Args m_SorterArgs;

        internal Material m_MatSplats;
        internal Material m_MatComposite;
        internal Material m_MatDebugPoints;
        internal Material m_MatDebugBoxes;

        internal int m_FrameCounter;
        GaussianSplatAsset m_PrevAsset;
        Hash128 m_PrevHash;
        bool m_Registered;
        bool m_LoadingResources;
        bool m_SortFallbackLogged;
        bool m_CpuSortFallbackLogged;
        bool m_WebGpuEditUnsupportedLogged;
        int[] m_KernelHandles;
        Vector3[] m_CpuSortPositions;
        CpuSortItem[] m_CpuSortItems;
        uint[] m_CpuSortKeys;
        int[] m_CpuSortBucketCounts;
        int[] m_CpuSortBucketOffsets;
        int[] m_CpuSortBucketWrite;
        Hash128 m_CpuSortDataHash;
        WebGpuCpuSortMode m_CpuSortModeCached;
        int m_CpuSortBucketCountCached;
        bool m_CpuSortHasLastState;
        Vector3 m_CpuSortLastCameraPosition;
        Quaternion m_CpuSortLastCameraRotation;
        Matrix4x4 m_CpuSortLastObjectMatrix;
        CpuChunk[] m_CpuChunks;
        int[] m_CpuVisibleChunks;
        int[] m_CpuVisibleChunkSampleSteps;
        CpuVisibleChunk[] m_CpuVisibleChunkCandidates;
        bool[] m_CpuVisibleChunkSelected;
        int m_CpuVisibleChunkCount;
        Hash128 m_CpuChunkDataHash;
        float m_CpuChunkCullPaddingCached = float.NaN;
        bool m_CpuChunkVisibilityValid;

        static readonly ProfilerMarker s_ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);
        public const int kDefaultMaxUploadBytesPerFrame = 64 * 1024 * 1024;
        const int kMinWebGpuCpuSortBucketCount = 16;
        const int kMaxWebGpuCpuSortBucketCount = 65536;
        const int kMaxWebGpuLodSampleStep = 1024;
        const uint kSortPartitionSize = 3840;
        const uint kSortRadix = 256;
        const uint kSortPasses = 4;
        struct CpuSortItem
        {
            public float depth;
            public uint index;
        }
        struct CpuChunk
        {
            public int start;
            public int count;
            public Bounds bounds;
        }
        struct CpuVisibleChunk
        {
            public int chunkIndex;
            public float distanceSq;
        }
        sealed class CpuVisibleChunkComparer : IComparer<CpuVisibleChunk>
        {
            public int Compare(CpuVisibleChunk a, CpuVisibleChunk b)
            {
                int distanceCompare = a.distanceSq.CompareTo(b.distanceSq);
                return distanceCompare != 0 ? distanceCompare : a.chunkIndex.CompareTo(b.chunkIndex);
            }
        }
        sealed class CpuSortItemComparer : IComparer<CpuSortItem>
        {
            public int Compare(CpuSortItem a, CpuSortItem b)
            {
                int depthCompare = a.depth.CompareTo(b.depth);
                return depthCompare != 0 ? depthCompare : a.index.CompareTo(b.index);
            }
        }
        static readonly CpuSortItemComparer s_CpuSortItemComparer = new CpuSortItemComparer();
        static readonly CpuVisibleChunkComparer s_CpuVisibleChunkComparer = new CpuVisibleChunkComparer();
        static readonly ushort[] s_CubeIndices =
        {
            0, 1, 2, 1, 3, 2,
            4, 6, 5, 5, 6, 7,
            0, 2, 4, 4, 2, 6,
            1, 5, 3, 5, 7, 3,
            0, 4, 1, 4, 5, 1,
            2, 3, 6, 3, 7, 6
        };

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
        }

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => m_Asset;
        public int splatCount => m_SplatCount;
        internal int renderSplatCount => m_RenderSplatCount;

        enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
        }
        static readonly string[] s_KernelNames =
        {
            "CSSetIndices",
            "CSCalcDistances",
            "CSCalcViewData",
            "CSUpdateEditData",
            "CSInitEditData",
            "CSClearBuffer",
            "CSInvertSelection",
            "CSSelectAll",
            "CSOrBuffers",
            "CSSelectionUpdate",
            "CSTranslateSelection",
            "CSRotateSelection",
            "CSScaleSelection",
            "CSExportData",
            "CSCopySplats",
        };

        public bool HasValidAsset =>
            m_Asset != null &&
            m_Asset.splatCount > 0 &&
            m_Asset.formatVersion == GaussianSplatAsset.kCurrentVersion &&
            m_Asset.posData != null &&
            m_Asset.otherData != null &&
            m_Asset.shData != null &&
            m_Asset.colorData != null;
        public bool HasValidRenderSetup =>
            m_GpuPosData != null &&
            m_GpuOtherData != null &&
            m_GpuSHData != null &&
            m_GpuColorData != null &&
            m_GpuChunks != null &&
            m_GpuView != null &&
            m_GpuIndexBuffer != null &&
            m_GpuSortDistances != null &&
            m_GpuSortKeys != null;

        const int kGpuViewDataSize = 40;

        /// <summary>
        /// Assigns externally-created GPU resources to this renderer.
        /// The renderer takes ownership of the provided buffers and texture, and will dispose them on disable or replacement.
        /// This is intended for builds that keep Gaussian data outside Unity serialized assets.
        /// </summary>
        public void SetExternalGpuResources(
            GaussianSplatAsset externalAsset,
            int externalSplatCount,
            GraphicsBuffer posData,
            GraphicsBuffer otherData,
            GraphicsBuffer shData,
            Texture colorData,
            GraphicsBuffer chunkData,
            bool chunkDataValid)
        {
            if (externalAsset == null)
                throw new ArgumentNullException(nameof(externalAsset));
            if (externalSplatCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(externalSplatCount), externalSplatCount, "External splat count must be greater than zero.");
            if (externalAsset.formatVersion != GaussianSplatAsset.kCurrentVersion)
                throw new ArgumentException("External Gaussian splat asset format version does not match this renderer package.", nameof(externalAsset));
            if (posData == null)
                throw new ArgumentNullException(nameof(posData));
            if (otherData == null)
                throw new ArgumentNullException(nameof(otherData));
            if (shData == null)
                throw new ArgumentNullException(nameof(shData));
            if (colorData == null)
                throw new ArgumentNullException(nameof(colorData));
            if (!resourcesAreSetUp)
                throw new InvalidOperationException($"{nameof(GaussianSplatRenderer)} component is not set up correctly, or platform does not support compute shaders.");

            if (m_LoadingResources)
                throw new InvalidOperationException("Gaussian splat resources are already loading.");

            DisposeResourcesForAsset();
            EnsureMaterials();
            EnsureSorterAndRegister();

            m_Asset = externalAsset;
            m_SplatCount = externalSplatCount;
            m_RenderSplatCount = externalSplatCount;
            m_GpuPosData = posData;
            m_GpuOtherData = otherData;
            m_GpuSHData = shData;
            m_GpuColorData = colorData;

            if (chunkData != null)
            {
                m_GpuChunks = chunkData;
                m_GpuChunksValid = chunkDataValid;
            }
            else
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, externalSplatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(s_CubeIndices);

            InitSortBuffers(externalSplatCount);
            UpdateLoadedAssetTracking();
        }

        void CreateResourcesForAsset()
        {
            if (!CanCreateResources())
                return;

            m_SplatCount = asset.splatCount;
            m_RenderSplatCount = m_SplatCount;
            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            m_GpuPosData.SetData(asset.posData.GetData<uint>());
            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            m_GpuOtherData.SetData(asset.otherData.GetData<uint>());
            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            m_GpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            m_GpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_GpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                m_GpuChunksValid = false;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(s_CubeIndices);

            InitSortBuffers(splatCount);
            UpdateLoadedAssetTracking();
        }

        public IEnumerator LoadResourcesAsync(Action<GaussianSplatLoadReport> onProgress, int maxUploadBytesPerFrame = kDefaultMaxUploadBytesPerFrame)
        {
            while (m_LoadingResources)
                yield return null;

            if (HasValidRenderSetup)
            {
                EnsureMaterials();
                EnsureSorterAndRegister();
                UpdateLoadedAssetTracking();
                ReportLoadProgress(onProgress, "Gaussian splat already loaded", 1, 1);
                yield break;
            }

            if (!CanCreateResources())
            {
                ReportLoadProgress(onProgress, "Gaussian splat skipped", 1, 1);
                yield break;
            }

            DisposeResourcesForAsset();
            EnsureMaterials();

            m_LoadingResources = true;
            maxUploadBytesPerFrame = Mathf.Max(4, maxUploadBytesPerFrame);

            var progress = new LoadProgress(CalculateTotalLoadBytes(asset));
            ReportLoadProgress(onProgress, "Preparing Gaussian splat", progress.LoadedBytes, progress.TotalBytes);
            yield return null;

            m_SplatCount = asset.splatCount;
            m_RenderSplatCount = m_SplatCount;

            m_GpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            yield return UploadBufferDataAsync(m_GpuPosData, asset.posData.GetData<uint>(), "Uploading positions", UnsafeUtility.SizeOf<uint>(), progress, onProgress, maxUploadBytesPerFrame);

            m_GpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            yield return UploadBufferDataAsync(m_GpuOtherData, asset.otherData.GetData<uint>(), "Uploading transforms", UnsafeUtility.SizeOf<uint>(), progress, onProgress, maxUploadBytesPerFrame);

            m_GpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            yield return UploadBufferDataAsync(m_GpuSHData, asset.shData.GetData<uint>(), "Uploading spherical harmonics", UnsafeUtility.SizeOf<uint>(), progress, onProgress, maxUploadBytesPerFrame);

            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            ReportLoadProgress(onProgress, "Preparing color texture", progress.LoadedBytes, progress.TotalBytes);
            yield return null;
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            progress.Add(asset.colorData.dataSize);
            ReportLoadProgress(onProgress, "Uploading color texture", progress.LoadedBytes, progress.TotalBytes);
            yield return null;
            tex.Apply(false, true);
            m_GpuColorData = tex;
            ReportLoadProgress(onProgress, "Color texture uploaded", progress.LoadedBytes, progress.TotalBytes);
            yield return null;

            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                int chunkStride = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)(asset.chunkData.dataSize / chunkStride), chunkStride) { name = "GaussianChunkData" };
                yield return UploadBufferDataAsync(m_GpuChunks, asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>(), "Uploading chunks", chunkStride, progress, onProgress, maxUploadBytesPerFrame);
                m_GpuChunksValid = true;
            }
            else
            {
                m_GpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) { name = "GaussianChunkData" };
                m_GpuChunksValid = false;
                ReportLoadProgress(onProgress, "No chunk data", progress.LoadedBytes, progress.TotalBytes);
                yield return null;
            }

            m_GpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_Asset.splatCount, kGpuViewDataSize);
            m_GpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            m_GpuIndexBuffer.SetData(s_CubeIndices);
            progress.Add(EstimateViewAndIndexBytes(asset.splatCount));
            ReportLoadProgress(onProgress, "Preparing view buffers", progress.LoadedBytes, progress.TotalBytes);
            yield return null;

            ReportLoadProgress(onProgress, "Preparing sort buffers", progress.LoadedBytes, progress.TotalBytes);
            yield return null;
            InitSortBuffers(splatCount);
            progress.Add(EstimateSortBufferBytes(asset.splatCount));

            UpdateLoadedAssetTracking();
            m_LoadingResources = false;
            ReportLoadProgress(onProgress, "Gaussian splat ready", progress.TotalBytes, progress.TotalBytes);
            yield return null;
        }

        bool CanCreateResources()
        {
            if (!HasValidAsset)
                return false;

            if (resourcesAreSetUp)
                return true;

            Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders", this);
            return false;
        }

        IEnumerator UploadBufferDataAsync<T>(
            GraphicsBuffer buffer,
            NativeArray<T> data,
            string stage,
            int elementSize,
            LoadProgress progress,
            Action<GaussianSplatLoadReport> onProgress,
            int maxUploadBytesPerFrame) where T : struct
        {
            int elementsPerFrame = Mathf.Max(1, maxUploadBytesPerFrame / Mathf.Max(1, elementSize));
            for (int offset = 0; offset < data.Length; offset += elementsPerFrame)
            {
                int count = Mathf.Min(elementsPerFrame, data.Length - offset);
                buffer.SetData(data, offset, offset, count);
                progress.Add((long)count * elementSize);
                ReportLoadProgress(onProgress, stage, progress.LoadedBytes, progress.TotalBytes);
                yield return null;
            }
        }

        static long CalculateTotalLoadBytes(GaussianSplatAsset targetAsset)
        {
            if (targetAsset == null)
                return 1;

            long total = 0;
            total += targetAsset.posData != null ? targetAsset.posData.dataSize : 0;
            total += targetAsset.otherData != null ? targetAsset.otherData.dataSize : 0;
            total += targetAsset.shData != null ? targetAsset.shData.dataSize : 0;
            total += targetAsset.colorData != null ? targetAsset.colorData.dataSize : 0;
            total += targetAsset.chunkData != null ? targetAsset.chunkData.dataSize : 0;
            total += EstimateViewAndIndexBytes(targetAsset.splatCount);
            total += EstimateSortBufferBytes(targetAsset.splatCount);
            return Math.Max(1, total);
        }

        static long EstimateViewAndIndexBytes(int count)
        {
            return (long)count * kGpuViewDataSize + s_CubeIndices.Length * sizeof(ushort);
        }

        static long EstimateSortBufferBytes(int count)
        {
            if (count <= 0)
                return 0;

            long threadBlocks = ((long)count + kSortPartitionSize - 1) / kSortPartitionSize;
            long scratchBufferSize = threadBlocks * kSortRadix;
            long reducedScratchBufferSize = kSortRadix * kSortPasses;
            return (long)count * 4 * 4 + (scratchBufferSize + reducedScratchBufferSize) * 4;
        }

        void ReportLoadProgress(Action<GaussianSplatLoadReport> onProgress, string stage, long loadedBytes, long totalBytes)
        {
            totalBytes = Math.Max(1, totalBytes);
            loadedBytes = Math.Min(totalBytes, Math.Max(0, loadedBytes));
            float normalized = Mathf.Clamp01((float)((double)loadedBytes / totalBytes));
            onProgress?.Invoke(new GaussianSplatLoadReport(this, stage, normalized, loadedBytes, totalBytes));
        }

        void UpdateLoadedAssetTracking()
        {
            m_PrevAsset = m_Asset;
            m_PrevHash = m_Asset ? m_Asset.dataHash : new Hash128();
        }

        sealed class LoadProgress
        {
            public LoadProgress(long totalBytes)
            {
                TotalBytes = Math.Max(1, totalBytes);
            }

            public long LoadedBytes { get; private set; }
            public long TotalBytes { get; }

            public void Add(long bytes)
            {
                LoadedBytes = Math.Min(TotalBytes, LoadedBytes + Math.Max(0, bytes));
            }
        }

        int GetKernelIndex(KernelIndices kernel)
        {
            if (m_KernelHandles == null)
                m_KernelHandles = CreateKernelCache();

            int idx = (int)kernel;
            if (idx < 0 || idx >= s_KernelNames.Length || m_CSSplatUtilities == null)
                return -1;

            int cached = m_KernelHandles[idx];
            if (cached != int.MinValue)
                return cached;

            string kernelName = s_KernelNames[idx];
            cached = m_CSSplatUtilities.HasKernel(kernelName) ? m_CSSplatUtilities.FindKernel(kernelName) : -1;
            if (cached >= 0 && !m_CSSplatUtilities.IsSupported(cached))
                cached = -1;
            m_KernelHandles[idx] = cached;
            return cached;
        }

        static int[] CreateKernelCache()
        {
            var cache = new int[s_KernelNames.Length];
            for (int i = 0; i < cache.Length; ++i)
                cache[i] = int.MinValue;
            return cache;
        }

        uint GetKernelThreadGroupSizeX(int kernelIndex)
        {
            m_CSSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            return Math.Max(1u, gsX);
        }

        static int DivRoundUp(int count, uint groupSize)
        {
            int gs = Math.Max(1, (int)groupSize);
            return (count + gs - 1) / gs;
        }

        static bool IsWebGpuGraphicsDevice()
        {
            return SystemInfo.graphicsDeviceType.ToString() == "WebGPU";
        }

        bool IsWebGpuRuntimeEditingUnsupported()
        {
            if (!IsWebGpuGraphicsDevice())
                return false;

            if (!m_WebGpuEditUnsupportedLogged)
            {
                Debug.LogWarning("Gaussian splat editing/export is disabled on the WebGPU runtime baseline path.", this);
                m_WebGpuEditUnsupportedLogged = true;
            }
            return true;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static bool ClampField(ref int value, int min, int max)
        {
            int clamped = Mathf.Clamp(value, min, max);
            if (clamped == value)
                return false;
            value = clamped;
            return true;
        }

        static bool ClampField(ref float value, float min, float max, float fallback)
        {
            float clamped = IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback;
            if (Mathf.Approximately(clamped, value))
                return false;
            value = clamped;
            return true;
        }

        public void SanitizeWebGpuOptions()
        {
            if (m_WebGpuCpuSortMode != WebGpuCpuSortMode.DepthBuckets &&
                m_WebGpuCpuSortMode != WebGpuCpuSortMode.Exact)
            {
                m_WebGpuCpuSortMode = WebGpuCpuSortMode.DepthBuckets;
            }

            ClampField(ref m_WebGpuCpuSortPositionThreshold, 0, 1000, 0.01f);
            ClampField(ref m_WebGpuCpuSortAngleThreshold, 0, 180, 0.25f);
            ClampField(ref m_WebGpuCpuSortBucketCount, kMinWebGpuCpuSortBucketCount, kMaxWebGpuCpuSortBucketCount);
            ClampField(ref m_WebGpuChunkCullPadding, 0, 1000, 1.0f);
            ClampField(ref m_WebGpuMaxVisibleSplats, 0, int.MaxValue);
            ClampField(ref m_WebGpuLodNearDistance, 0, 100000, 25.0f);
            ClampField(ref m_WebGpuLodFarDistance, 0, 100000, 80.0f);
            if (m_WebGpuLodFarDistance < m_WebGpuLodNearDistance)
                m_WebGpuLodFarDistance = m_WebGpuLodNearDistance;

            ClampField(ref m_WebGpuLodMidSampleStep, 1, kMaxWebGpuLodSampleStep);
            ClampField(ref m_WebGpuLodFarSampleStep, 1, kMaxWebGpuLodSampleStep);
            ClampField(ref m_WebGpuLodMaxSampleStep, 1, kMaxWebGpuLodSampleStep);
            m_WebGpuLodMaxSampleStep = Mathf.Max(m_WebGpuLodMaxSampleStep, Mathf.Max(m_WebGpuLodMidSampleStep, m_WebGpuLodFarSampleStep));
            ClampField(ref m_WebGpuLodFarReservePercent, 0, 50);

            ClampField(ref m_SplatScale, 0.1f, 2.0f, 1.0f);
            ClampField(ref m_OpacityScale, 0.05f, 20.0f, 1.0f);
        }

        public void InvalidateWebGpuRuntimeState()
        {
            m_CpuSortHasLastState = false;
            m_CpuChunkVisibilityValid = false;
        }

        void OnValidate()
        {
            SanitizeWebGpuOptions();
            InvalidateWebGpuRuntimeState();
        }

        void DispatchUtils(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            int kernelIndex = GetKernelIndex(kernel);
            if (kernelIndex < 0)
                return;

            uint gsX = GetKernelThreadGroupSizeX(kernelIndex);
            cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, DivRoundUp(count, gsX), 1, 1);
        }

        void InitSortKeysOnCpu(int count)
        {
            if (count <= 0)
                return;

            const int kChunkSize = 64 * 1024;
            uint[] indices = new uint[Math.Min(count, kChunkSize)];
            for (int offset = 0; offset < count; offset += indices.Length)
            {
                int chunkCount = Math.Min(indices.Length, count - offset);
                for (int i = 0; i < chunkCount; ++i)
                    indices[i] = (uint)(offset + i);
                m_GpuSortKeys.SetData(indices, 0, offset, chunkCount);
            }
        }

        bool InitVisibleSortKeysOnCpu()
        {
            if (!m_CpuChunkVisibilityValid)
                return false;
            if (m_RenderSplatCount <= 0)
                return true;

            try
            {
                const int kChunkSize = 64 * 1024;
                uint[] indices = new uint[Math.Min(m_RenderSplatCount, kChunkSize)];
                int dstOffset = 0;
                int tempCount = 0;

                for (int visibleChunkIndex = 0; visibleChunkIndex < m_CpuVisibleChunkCount; ++visibleChunkIndex)
                {
                    CpuChunk chunk = m_CpuChunks[m_CpuVisibleChunks[visibleChunkIndex]];
                    int end = chunk.start + chunk.count;
                    int sampleStep = GetVisibleChunkSampleStep(visibleChunkIndex);
                    for (int i = chunk.start; i < end; i += sampleStep)
                    {
                        indices[tempCount++] = (uint)i;
                        if (tempCount == indices.Length)
                        {
                            m_GpuSortKeys.SetData(indices, 0, dstOffset, tempCount);
                            dstOffset += tempCount;
                            tempCount = 0;
                        }
                    }
                }

                if (tempCount > 0)
                    m_GpuSortKeys.SetData(indices, 0, dstOffset, tempCount);
            }
            catch (OutOfMemoryException)
            {
                return false;
            }

            return true;
        }

        static ushort ReadUInt16(NativeArray<byte> data, int offset)
        {
            return (ushort)(data[offset] | (data[offset + 1] << 8));
        }

        static uint ReadUInt32(NativeArray<byte> data, int offset)
        {
            return (uint)(data[offset] |
                          (data[offset + 1] << 8) |
                          (data[offset + 2] << 16) |
                          (data[offset + 3] << 24));
        }

        static Vector3 DecodeVector(NativeArray<byte> data, int offset, GaussianSplatAsset.VectorFormat format)
        {
            switch (format)
            {
                case GaussianSplatAsset.VectorFormat.Float32:
                    return new Vector3(
                        math.asfloat(ReadUInt32(data, offset)),
                        math.asfloat(ReadUInt32(data, offset + 4)),
                        math.asfloat(ReadUInt32(data, offset + 8)));
                case GaussianSplatAsset.VectorFormat.Norm16:
                    return new Vector3(
                        ReadUInt16(data, offset) / 65535.0f,
                        ReadUInt16(data, offset + 2) / 65535.0f,
                        ReadUInt16(data, offset + 4) / 65535.0f);
                case GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint enc = ReadUInt32(data, offset);
                    return new Vector3(
                        (enc & 2047) / 2047.0f,
                        ((enc >> 11) & 1023) / 1023.0f,
                        ((enc >> 21) & 2047) / 2047.0f);
                }
                case GaussianSplatAsset.VectorFormat.Norm6:
                {
                    ushort enc = ReadUInt16(data, offset);
                    return new Vector3(
                        (enc & 63) / 63.0f,
                        ((enc >> 6) & 31) / 31.0f,
                        ((enc >> 11) & 31) / 31.0f);
                }
                default:
                    return Vector3.zero;
            }
        }

        bool TryGetCpuSortSourceData(
            out NativeArray<byte> posBytes,
            out int posStride,
            out NativeArray<GaussianSplatAsset.ChunkInfo> chunks,
            out bool hasChunks)
        {
            posBytes = default;
            posStride = 0;
            chunks = default;
            hasChunks = false;

            if (!HasValidAsset || m_Asset.posData == null)
                return false;

            posBytes = m_Asset.posData.GetData<byte>();
            posStride = GaussianSplatAsset.GetVectorSize(m_Asset.posFormat);
            if (posBytes.Length < m_SplatCount * posStride)
                return false;

            hasChunks = m_Asset.chunkData != null && m_Asset.chunkData.dataSize != 0;
            if (hasChunks)
                chunks = m_Asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>();

            return true;
        }

        static Vector3 DecodeCpuSortPosition(
            int index,
            NativeArray<byte> posBytes,
            int posStride,
            GaussianSplatAsset.VectorFormat posFormat,
            NativeArray<GaussianSplatAsset.ChunkInfo> chunks,
            bool hasChunks)
        {
            Vector3 pos = DecodeVector(posBytes, index * posStride, posFormat);
            if (hasChunks)
            {
                int chunkIndex = index / GaussianSplatAsset.kChunkSize;
                if (chunkIndex < chunks.Length)
                {
                    var chunk = chunks[chunkIndex];
                    pos = new Vector3(
                        Mathf.Lerp(chunk.posX.x, chunk.posX.y, pos.x),
                        Mathf.Lerp(chunk.posY.x, chunk.posY.y, pos.y),
                        Mathf.Lerp(chunk.posZ.x, chunk.posZ.y, pos.z));
                }
            }
            return pos;
        }

        static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            Vector3 center = matrix.MultiplyPoint3x4(bounds.center);
            Vector3 extents = bounds.extents;
            Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0, 0));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0, extents.y, 0));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0, 0, extents.z));
            extents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, extents * 2);
        }

        bool ShouldUseWebGpuChunkCulling()
        {
            return IsWebGpuGraphicsDevice() && m_WebGpuChunkFrustumCulling;
        }

        bool EnsureCpuChunks()
        {
            SanitizeWebGpuOptions();
            if (!TryGetCpuSortSourceData(out var posBytes, out int posStride, out var chunks, out bool hasChunks))
                return false;

            float chunkCullPadding = Mathf.Max(0, m_WebGpuChunkCullPadding);
            if (m_CpuChunks != null &&
                m_CpuVisibleChunks != null &&
                m_CpuVisibleChunkSampleSteps != null &&
                m_CpuVisibleChunkCandidates != null &&
                m_CpuVisibleChunkSelected != null &&
                m_CpuChunkDataHash == m_Asset.dataHash &&
                Mathf.Approximately(m_CpuChunkCullPaddingCached, chunkCullPadding))
            {
                return true;
            }

            try
            {
                int chunkCount = hasChunks
                    ? chunks.Length
                    : Mathf.Max(1, (m_SplatCount + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize);
                m_CpuChunks = new CpuChunk[chunkCount];
                m_CpuVisibleChunks = new int[chunkCount];
                m_CpuVisibleChunkSampleSteps = new int[chunkCount];
                m_CpuVisibleChunkCandidates = new CpuVisibleChunk[chunkCount];
                m_CpuVisibleChunkSelected = new bool[chunkCount];
                m_CpuVisibleChunkCount = 0;
                m_CpuChunkDataHash = m_Asset.dataHash;
                m_CpuChunkVisibilityValid = false;

                for (int chunkIndex = 0; chunkIndex < chunkCount; ++chunkIndex)
                {
                    int start = chunkIndex * GaussianSplatAsset.kChunkSize;
                    int count = Mathf.Min(GaussianSplatAsset.kChunkSize, m_SplatCount - start);
                    if (count <= 0)
                    {
                        m_CpuChunks[chunkIndex] = new CpuChunk { start = start, count = 0, bounds = new Bounds() };
                        continue;
                    }

                    Bounds bounds;
                    if (hasChunks && chunkIndex < chunks.Length)
                    {
                        var chunk = chunks[chunkIndex];
                        Vector3 min = new Vector3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
                        Vector3 max = new Vector3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
                        bounds = new Bounds();
                        bounds.SetMinMax(min, max);
                    }
                    else
                    {
                        Vector3 first = DecodeCpuSortPosition(start, posBytes, posStride, m_Asset.posFormat, chunks, false);
                        bounds = new Bounds(first, Vector3.zero);
                        for (int i = 1; i < count; ++i)
                        {
                            Vector3 pos = DecodeCpuSortPosition(start + i, posBytes, posStride, m_Asset.posFormat, chunks, false);
                            bounds.Encapsulate(pos);
                        }
                    }

                    if (chunkCullPadding > 0)
                        bounds.Expand(chunkCullPadding * 2.0f);

                    m_CpuChunks[chunkIndex] = new CpuChunk
                    {
                        start = start,
                        count = count,
                        bounds = bounds
                    };
                }
                m_CpuChunkCullPaddingCached = chunkCullPadding;
            }
            catch (OutOfMemoryException)
            {
                Debug.LogWarning("Not enough memory to allocate WebGPU chunk culling data. Rendering the full splat asset.", this);
                m_CpuChunks = null;
                m_CpuVisibleChunks = null;
                m_CpuVisibleChunkSampleSteps = null;
                m_CpuVisibleChunkCandidates = null;
                m_CpuVisibleChunkSelected = null;
                m_CpuVisibleChunkCount = 0;
                m_CpuChunkDataHash = default;
                m_CpuChunkCullPaddingCached = float.NaN;
                m_CpuChunkVisibilityValid = false;
                return false;
            }

            return true;
        }

        int GetDistanceLodSampleStep(float distanceSq)
        {
            if (!m_WebGpuDistanceLod)
                return 1;

            float nearDistance = Mathf.Max(0, m_WebGpuLodNearDistance);
            float farDistance = Mathf.Max(nearDistance, m_WebGpuLodFarDistance);
            if (distanceSq <= nearDistance * nearDistance)
                return 1;
            if (distanceSq <= farDistance * farDistance)
                return Mathf.Max(1, m_WebGpuLodMidSampleStep);
            return Mathf.Max(1, m_WebGpuLodFarSampleStep);
        }

        int ClampLodSampleStep(int step)
        {
            return Mathf.Clamp(step, 1, Mathf.Max(1, m_WebGpuLodMaxSampleStep));
        }

        static int CountSampledSplats(int count, int sampleStep)
        {
            sampleStep = Mathf.Max(1, sampleStep);
            return count <= 0 ? 0 : (count + sampleStep - 1) / sampleStep;
        }

        int GetVisibleChunkSampleStep(int visibleChunkIndex)
        {
            if (m_CpuVisibleChunkSampleSteps == null ||
                visibleChunkIndex < 0 ||
                visibleChunkIndex >= m_CpuVisibleChunkSampleSteps.Length)
            {
                return 1;
            }
            return Mathf.Max(1, m_CpuVisibleChunkSampleSteps[visibleChunkIndex]);
        }

        bool TryAppendVisibleCandidate(
            int candidateIndex,
            int oldVisibleChunkCount,
            int budgetLimit,
            bool useBudget,
            bool allowIncreaseSampleStep,
            ref bool changedVisibility,
            ref int write,
            ref int visibleSplatCount)
        {
            int chunkIndex = m_CpuVisibleChunkCandidates[candidateIndex].chunkIndex;
            float distanceSq = m_CpuVisibleChunkCandidates[candidateIndex].distanceSq;
            CpuChunk chunk = m_CpuChunks[chunkIndex];
            int sampleStep = useBudget && m_WebGpuDistanceLod
                ? ClampLodSampleStep(GetDistanceLodSampleStep(distanceSq))
                : 1;
            int sampledCount = CountSampledSplats(chunk.count, sampleStep);

            if (useBudget && visibleSplatCount + sampledCount > budgetLimit)
            {
                while (allowIncreaseSampleStep &&
                       m_WebGpuDistanceLod &&
                       sampleStep < Mathf.Max(1, m_WebGpuLodMaxSampleStep) &&
                       visibleSplatCount + sampledCount > budgetLimit)
                {
                    sampleStep = ClampLodSampleStep(sampleStep * 2);
                    sampledCount = CountSampledSplats(chunk.count, sampleStep);
                }
            }

            if (useBudget && visibleSplatCount + sampledCount > budgetLimit)
                return false;

            if (write >= oldVisibleChunkCount ||
                m_CpuVisibleChunks[write] != chunkIndex ||
                m_CpuVisibleChunkSampleSteps[write] != sampleStep)
            {
                changedVisibility = true;
            }

            m_CpuVisibleChunks[write] = chunkIndex;
            m_CpuVisibleChunkSampleSteps[write] = sampleStep;
            ++write;
            visibleSplatCount += sampledCount;
            return true;
        }

        internal bool PrepareRenderForCamera(Camera cam, Matrix4x4 matrix)
        {
            SanitizeWebGpuOptions();
            if (!ShouldUseWebGpuChunkCulling())
            {
                bool changed = m_CpuChunkVisibilityValid || m_RenderSplatCount != m_SplatCount;
                m_CpuChunkVisibilityValid = false;
                m_CpuVisibleChunkCount = 0;
                m_RenderSplatCount = m_SplatCount;
                if (changed)
                    m_CpuSortHasLastState = false;
                return changed;
            }

            if (!EnsureCpuChunks())
            {
                bool changed = m_CpuChunkVisibilityValid || m_RenderSplatCount != m_SplatCount;
                m_CpuChunkVisibilityValid = false;
                m_CpuVisibleChunkCount = 0;
                m_RenderSplatCount = m_SplatCount;
                if (changed)
                    m_CpuSortHasLastState = false;
                return changed;
            }

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            int oldVisibleChunkCount = m_CpuVisibleChunkCount;
            int oldVisibleSplatCount = m_RenderSplatCount;
            bool changedVisibility = !m_CpuChunkVisibilityValid;
            int candidateCount = 0;
            int candidateSplatCount = 0;
            Vector3 cameraPosition = cam.transform.position;

            for (int i = 0; i < m_CpuChunks.Length; ++i)
            {
                CpuChunk chunk = m_CpuChunks[i];
                if (chunk.count <= 0)
                    continue;

                Bounds worldBounds = TransformBounds(chunk.bounds, matrix);
                if (!GeometryUtility.TestPlanesAABB(planes, worldBounds))
                    continue;

                m_CpuVisibleChunkCandidates[candidateCount++] = new CpuVisibleChunk
                {
                    chunkIndex = i,
                    distanceSq = (worldBounds.center - cameraPosition).sqrMagnitude
                };
                candidateSplatCount += chunk.count;
            }

            int visibleSplatBudget = Mathf.Max(0, m_WebGpuMaxVisibleSplats);
            bool useBudget = visibleSplatBudget > 0 && candidateSplatCount > visibleSplatBudget;
            if (useBudget)
                Array.Sort(m_CpuVisibleChunkCandidates, 0, candidateCount, s_CpuVisibleChunkComparer);

            int write = 0;
            int visibleSplatCount = 0;
            if (useBudget && m_WebGpuDistanceLod)
            {
                Array.Clear(m_CpuVisibleChunkSelected, 0, candidateCount);
                int farReserveBudget = visibleSplatBudget * Mathf.Clamp(m_WebGpuLodFarReservePercent, 0, 50) / 100;
                int nearBudget = Mathf.Max(0, visibleSplatBudget - farReserveBudget);

                for (int candidateIndex = 0; candidateIndex < candidateCount; ++candidateIndex)
                {
                    if (TryAppendVisibleCandidate(candidateIndex, oldVisibleChunkCount, nearBudget, true, false,
                            ref changedVisibility, ref write, ref visibleSplatCount))
                        m_CpuVisibleChunkSelected[candidateIndex] = true;
                }

                for (int candidateIndex = candidateCount - 1; candidateIndex >= 0; --candidateIndex)
                {
                    if (m_CpuVisibleChunkSelected[candidateIndex])
                        continue;

                    if (TryAppendVisibleCandidate(candidateIndex, oldVisibleChunkCount, visibleSplatBudget, true, true,
                            ref changedVisibility, ref write, ref visibleSplatCount))
                        m_CpuVisibleChunkSelected[candidateIndex] = true;
                }
            }
            else
            {
                for (int candidateIndex = 0; candidateIndex < candidateCount; ++candidateIndex)
                {
                    TryAppendVisibleCandidate(candidateIndex, oldVisibleChunkCount, visibleSplatBudget, useBudget, true,
                        ref changedVisibility, ref write, ref visibleSplatCount);
                }
            }

            if (oldVisibleChunkCount != write || oldVisibleSplatCount != visibleSplatCount)
                changedVisibility = true;

            m_CpuVisibleChunkCount = write;
            m_RenderSplatCount = visibleSplatCount;
            m_CpuChunkVisibilityValid = true;
            if (changedVisibility)
                m_CpuSortHasLastState = false;
            return changedVisibility;
        }

        bool EnsureCpuSortCache()
        {
            SanitizeWebGpuOptions();
            if (!TryGetCpuSortSourceData(out var posBytes, out int posStride, out var chunks, out bool hasChunks))
                return false;

            if (!m_WebGpuCpuSortCachePositions && m_CpuSortPositions != null)
                m_CpuSortPositions = null;

            int bucketCount = Mathf.Clamp(m_WebGpuCpuSortBucketCount, kMinWebGpuCpuSortBucketCount, kMaxWebGpuCpuSortBucketCount);
            bool usesBuckets = m_WebGpuCpuSortMode == WebGpuCpuSortMode.DepthBuckets;
            bool canReuseSortArrays = m_CpuSortKeys != null &&
                                      m_CpuSortKeys.Length == m_SplatCount &&
                                      m_CpuSortDataHash == m_Asset.dataHash &&
                                      m_CpuSortModeCached == m_WebGpuCpuSortMode &&
                                      (!usesBuckets || m_CpuSortBucketCountCached == bucketCount) &&
                                      (usesBuckets
                                          ? m_CpuSortBucketCounts != null && m_CpuSortBucketCounts.Length == bucketCount
                                          : m_CpuSortItems != null && m_CpuSortItems.Length == m_SplatCount);
            bool canReusePositions = !m_WebGpuCpuSortCachePositions ||
                                     (m_CpuSortPositions != null &&
                                      m_CpuSortPositions.Length == m_SplatCount &&
                                      m_CpuSortDataHash == m_Asset.dataHash);
            if (canReuseSortArrays && canReusePositions)
                return true;

            try
            {
                m_CpuSortKeys = new uint[m_SplatCount];
                m_CpuSortDataHash = m_Asset.dataHash;
                m_CpuSortModeCached = m_WebGpuCpuSortMode;
                m_CpuSortBucketCountCached = bucketCount;
                m_CpuSortHasLastState = false;

                if (usesBuckets)
                {
                    m_CpuSortItems = null;
                    m_CpuSortBucketCounts = new int[bucketCount];
                    m_CpuSortBucketOffsets = new int[bucketCount];
                    m_CpuSortBucketWrite = new int[bucketCount];
                }
                else
                {
                    m_CpuSortItems = new CpuSortItem[m_SplatCount];
                    m_CpuSortBucketCounts = null;
                    m_CpuSortBucketOffsets = null;
                    m_CpuSortBucketWrite = null;
                }

                if (m_WebGpuCpuSortCachePositions)
                {
                    m_CpuSortPositions = new Vector3[m_SplatCount];
                    for (int i = 0; i < m_SplatCount; ++i)
                        m_CpuSortPositions[i] = DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                }
                else
                {
                    m_CpuSortPositions = null;
                }
            }
            catch (OutOfMemoryException)
            {
                Debug.LogWarning("Not enough memory to allocate WebGPU CPU sort buffers. Falling back to asset order for this splat asset.", this);
                m_CpuSortPositions = null;
                m_CpuSortItems = null;
                m_CpuSortKeys = null;
                m_CpuSortBucketCounts = null;
                m_CpuSortBucketOffsets = null;
                m_CpuSortBucketWrite = null;
                m_CpuSortDataHash = default;
                m_CpuSortHasLastState = false;
                return false;
            }
            return true;
        }

        static bool MatrixChanged(Matrix4x4 a, Matrix4x4 b, float epsilon)
        {
            for (int i = 0; i < 16; ++i)
            {
                if (Mathf.Abs(a[i] - b[i]) > epsilon)
                    return true;
            }
            return false;
        }

        bool ShouldRunCpuSort(Camera cam, Matrix4x4 matrix)
        {
            if (!m_WebGpuCpuSortOnlyWhenCameraChanges || !m_CpuSortHasLastState)
                return true;

            float posThreshold = Mathf.Max(0, m_WebGpuCpuSortPositionThreshold);
            if ((cam.transform.position - m_CpuSortLastCameraPosition).sqrMagnitude > posThreshold * posThreshold)
                return true;

            float angleThreshold = Mathf.Max(0, m_WebGpuCpuSortAngleThreshold);
            if (Quaternion.Angle(cam.transform.rotation, m_CpuSortLastCameraRotation) > angleThreshold)
                return true;

            return MatrixChanged(matrix, m_CpuSortLastObjectMatrix, 0.00001f);
        }

        void StoreCpuSortState(Camera cam, Matrix4x4 matrix)
        {
            m_CpuSortHasLastState = true;
            m_CpuSortLastCameraPosition = cam.transform.position;
            m_CpuSortLastCameraRotation = cam.transform.rotation;
            m_CpuSortLastObjectMatrix = matrix;
        }

        bool SortPointsExactlyOnCpu(
            Matrix4x4 matrixMV,
            NativeArray<byte> posBytes,
            int posStride,
            NativeArray<GaussianSplatAsset.ChunkInfo> chunks,
            bool hasChunks)
        {
            if (m_CpuSortItems == null || m_CpuSortKeys == null)
                return false;

            int itemCount = 0;
            if (m_CpuChunkVisibilityValid)
            {
                for (int visibleChunkIndex = 0; visibleChunkIndex < m_CpuVisibleChunkCount; ++visibleChunkIndex)
                {
                    CpuChunk chunk = m_CpuChunks[m_CpuVisibleChunks[visibleChunkIndex]];
                    int end = chunk.start + chunk.count;
                    int sampleStep = GetVisibleChunkSampleStep(visibleChunkIndex);
                    for (int i = chunk.start; i < end; i += sampleStep)
                    {
                        Vector3 pos = m_CpuSortPositions != null
                            ? m_CpuSortPositions[i]
                            : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                        m_CpuSortItems[itemCount++] = new CpuSortItem
                        {
                            depth = matrixMV.MultiplyPoint3x4(pos).z,
                            index = (uint)i
                        };
                    }
                }
            }
            else
            {
                for (int i = 0; i < m_SplatCount; ++i)
                {
                    Vector3 pos = m_CpuSortPositions != null
                        ? m_CpuSortPositions[i]
                        : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                    m_CpuSortItems[itemCount++] = new CpuSortItem
                    {
                        depth = matrixMV.MultiplyPoint3x4(pos).z,
                        index = (uint)i
                    };
                }
            }

            if (itemCount <= 0)
                return true;

            Array.Sort(m_CpuSortItems, 0, itemCount, s_CpuSortItemComparer);
            for (int i = 0; i < itemCount; ++i)
                m_CpuSortKeys[i] = m_CpuSortItems[i].index;

            m_GpuSortKeys.SetData(m_CpuSortKeys, 0, 0, itemCount);
            return true;
        }

        bool SortPointsByDepthBucketsOnCpu(
            Matrix4x4 matrixMV,
            NativeArray<byte> posBytes,
            int posStride,
            NativeArray<GaussianSplatAsset.ChunkInfo> chunks,
            bool hasChunks)
        {
            if (m_CpuSortKeys == null ||
                m_CpuSortBucketCounts == null ||
                m_CpuSortBucketOffsets == null ||
                m_CpuSortBucketWrite == null)
            {
                return false;
            }

            int bucketCount = m_CpuSortBucketCounts.Length;
            if (bucketCount <= 1)
                return false;

            int activeSplatCount = m_CpuChunkVisibilityValid ? m_RenderSplatCount : m_SplatCount;
            if (activeSplatCount <= 0)
                return true;

            float minDepth = float.PositiveInfinity;
            float maxDepth = float.NegativeInfinity;
            if (m_CpuChunkVisibilityValid)
            {
                for (int visibleChunkIndex = 0; visibleChunkIndex < m_CpuVisibleChunkCount; ++visibleChunkIndex)
                {
                    CpuChunk chunk = m_CpuChunks[m_CpuVisibleChunks[visibleChunkIndex]];
                    int end = chunk.start + chunk.count;
                    int sampleStep = GetVisibleChunkSampleStep(visibleChunkIndex);
                    for (int i = chunk.start; i < end; i += sampleStep)
                    {
                        Vector3 pos = m_CpuSortPositions != null
                            ? m_CpuSortPositions[i]
                            : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                        float depth = matrixMV.MultiplyPoint3x4(pos).z;
                        if (depth < minDepth) minDepth = depth;
                        if (depth > maxDepth) maxDepth = depth;
                    }
                }
            }
            else
            {
                for (int i = 0; i < m_SplatCount; ++i)
                {
                    Vector3 pos = m_CpuSortPositions != null
                        ? m_CpuSortPositions[i]
                        : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                    float depth = matrixMV.MultiplyPoint3x4(pos).z;
                    if (depth < minDepth) minDepth = depth;
                    if (depth > maxDepth) maxDepth = depth;
                }
            }

            if (float.IsNaN(minDepth) || float.IsInfinity(minDepth) ||
                float.IsNaN(maxDepth) || float.IsInfinity(maxDepth))
            {
                return false;
            }

            Array.Clear(m_CpuSortBucketCounts, 0, bucketCount);
            float depthRange = maxDepth - minDepth;
            float bucketScale = depthRange > 1.0e-6f ? (bucketCount - 1) / depthRange : 0;

            if (m_CpuChunkVisibilityValid)
            {
                for (int visibleChunkIndex = 0; visibleChunkIndex < m_CpuVisibleChunkCount; ++visibleChunkIndex)
                {
                    CpuChunk chunk = m_CpuChunks[m_CpuVisibleChunks[visibleChunkIndex]];
                    int end = chunk.start + chunk.count;
                    int sampleStep = GetVisibleChunkSampleStep(visibleChunkIndex);
                    for (int i = chunk.start; i < end; i += sampleStep)
                    {
                        Vector3 pos = m_CpuSortPositions != null
                            ? m_CpuSortPositions[i]
                            : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                        float depth = matrixMV.MultiplyPoint3x4(pos).z;
                        int bucket = Mathf.Clamp((int)((depth - minDepth) * bucketScale), 0, bucketCount - 1);
                        ++m_CpuSortBucketCounts[bucket];
                    }
                }
            }
            else
            {
                for (int i = 0; i < m_SplatCount; ++i)
                {
                    Vector3 pos = m_CpuSortPositions != null
                        ? m_CpuSortPositions[i]
                        : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                    float depth = matrixMV.MultiplyPoint3x4(pos).z;
                    int bucket = Mathf.Clamp((int)((depth - minDepth) * bucketScale), 0, bucketCount - 1);
                    ++m_CpuSortBucketCounts[bucket];
                }
            }

            int offset = 0;
            for (int bucket = 0; bucket < bucketCount; ++bucket)
            {
                m_CpuSortBucketOffsets[bucket] = offset;
                m_CpuSortBucketWrite[bucket] = offset;
                offset += m_CpuSortBucketCounts[bucket];
            }

            if (m_CpuChunkVisibilityValid)
            {
                for (int visibleChunkIndex = 0; visibleChunkIndex < m_CpuVisibleChunkCount; ++visibleChunkIndex)
                {
                    CpuChunk chunk = m_CpuChunks[m_CpuVisibleChunks[visibleChunkIndex]];
                    int end = chunk.start + chunk.count;
                    int sampleStep = GetVisibleChunkSampleStep(visibleChunkIndex);
                    for (int i = chunk.start; i < end; i += sampleStep)
                    {
                        Vector3 pos = m_CpuSortPositions != null
                            ? m_CpuSortPositions[i]
                            : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                        float depth = matrixMV.MultiplyPoint3x4(pos).z;
                        int bucket = Mathf.Clamp((int)((depth - minDepth) * bucketScale), 0, bucketCount - 1);
                        m_CpuSortKeys[m_CpuSortBucketWrite[bucket]++] = (uint)i;
                    }
                }
            }
            else
            {
                for (int i = 0; i < m_SplatCount; ++i)
                {
                    Vector3 pos = m_CpuSortPositions != null
                        ? m_CpuSortPositions[i]
                        : DecodeCpuSortPosition(i, posBytes, posStride, m_Asset.posFormat, chunks, hasChunks);
                    float depth = matrixMV.MultiplyPoint3x4(pos).z;
                    int bucket = Mathf.Clamp((int)((depth - minDepth) * bucketScale), 0, bucketCount - 1);
                    m_CpuSortKeys[m_CpuSortBucketWrite[bucket]++] = (uint)i;
                }
            }

            m_GpuSortKeys.SetData(m_CpuSortKeys, 0, 0, activeSplatCount);
            return true;
        }

        bool SortPointsOnCpu(Camera cam, Matrix4x4 matrix)
        {
            if (!EnsureCpuSortCache())
                return InitVisibleSortKeysOnCpu();
            if (!ShouldRunCpuSort(cam, matrix))
                return true;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;
            Matrix4x4 matrixMV = worldToCamMatrix * matrix;
            if (!TryGetCpuSortSourceData(out var posBytes, out int posStride, out var chunks, out bool hasChunks))
                return false;

            bool sorted = m_WebGpuCpuSortMode == WebGpuCpuSortMode.DepthBuckets
                ? SortPointsByDepthBucketsOnCpu(matrixMV, posBytes, posStride, chunks, hasChunks)
                : SortPointsExactlyOnCpu(matrixMV, posBytes, posStride, chunks, hasChunks);
            if (!sorted)
                return InitVisibleSortKeysOnCpu();

            StoreCpuSortState(cam, matrix);
            return true;
        }

        void InitSortBuffers(int count)
        {
            m_GpuSortDistances?.Dispose();
            m_GpuSortKeys?.Dispose();
            m_SorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

            // init keys buffer to splat indices
            int setIndicesKernel = GetKernelIndex(KernelIndices.SetIndices);
            if (setIndicesKernel >= 0)
            {
                m_CSSplatUtilities.SetBuffer(setIndicesKernel, Props.SplatSortKeys, m_GpuSortKeys);
                m_CSSplatUtilities.SetInt(Props.SplatCount, m_GpuSortDistances.count);
                uint gsX = GetKernelThreadGroupSizeX(setIndicesKernel);
                m_CSSplatUtilities.Dispatch(setIndicesKernel, DivRoundUp(m_GpuSortDistances.count, gsX), 1, 1);
            }
            else
            {
                InitSortKeysOnCpu(count);
            }

            m_SorterArgs.inputKeys = m_GpuSortDistances;
            m_SorterArgs.inputValues = m_GpuSortKeys;
            m_SorterArgs.count = (uint)count;
            if (m_Sorter != null && m_Sorter.Valid)
                m_SorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);

            m_SortFallbackLogged = false;
            m_CpuSortHasLastState = false;
            m_RenderSplatCount = count;
            m_CpuChunkVisibilityValid = false;
        }

        bool resourcesAreSetUp => m_ShaderSplats != null && m_ShaderComposite != null && m_ShaderDebugPoints != null &&
                                  m_ShaderDebugBoxes != null && m_CSSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public bool CanUseExternalGpuResources => resourcesAreSetUp;

        public void UnloadSplatResources(bool clearAssetReference = false)
        {
            DisposeResourcesForAsset();
            if (clearAssetReference)
                m_Asset = null;
            UpdateLoadedAssetTracking();
        }

        public void EnsureMaterials()
        {
            if (m_MatSplats == null && resourcesAreSetUp)
            {
                m_MatSplats = new Material(m_ShaderSplats) {name = "GaussianSplats"};
                m_MatComposite = new Material(m_ShaderComposite) {name = "GaussianClearDstAlpha"};
                m_MatDebugPoints = new Material(m_ShaderDebugPoints) {name = "GaussianDebugPoints"};
                m_MatDebugBoxes = new Material(m_ShaderDebugBoxes) {name = "GaussianDebugBoxes"};
            }
        }

        public void EnsureSorterAndRegister()
        {
            if (m_Sorter == null && resourcesAreSetUp)
            {
                m_Sorter = new GpuSorting(m_CSSplatUtilities);
            }

            if (!m_Registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                m_Registered = true;
            }
        }

        public void OnEnable()
        {
            m_FrameCounter = 0;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();
            EnsureSorterAndRegister();

            if (!HasValidRenderSetup)
                CreateResourcesForAsset();
        }

        int SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel)
        {
            ComputeShader cs = m_CSSplatUtilities;
            int kernelIndex = GetKernelIndex(kernel);
            if (kernelIndex < 0)
                return -1;

            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, m_GpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, m_GpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, m_GpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, m_GpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, m_GpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, m_GpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, m_SplatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, m_Cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, m_GpuEditCutouts);
            return kernelIndex;
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, m_GpuPosData);
            mat.SetBuffer(Props.SplatOther, m_GpuOtherData);
            mat.SetBuffer(Props.SplatSH, m_GpuSHData);
            mat.SetTexture(Props.SplatColor, m_GpuColorData);
            mat.SetBuffer(Props.SplatSelectedBits, m_GpuEditSelected ?? m_GpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, m_GpuEditDeleted ?? m_GpuPosData);
            mat.SetInt(Props.SplatBitsValid, m_GpuEditSelected != null && m_GpuEditDeleted != null ? 1 : 0);
            uint format = (uint)m_Asset.posFormat | ((uint)m_Asset.scaleFormat << 8) | ((uint)m_Asset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, m_SplatCount);
            mat.SetInteger(Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
        }

        static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        void DisposeResourcesForAsset()
        {
            DestroyImmediate(m_GpuColorData);
            m_GpuColorData = null;

            DisposeBuffer(ref m_GpuPosData);
            DisposeBuffer(ref m_GpuOtherData);
            DisposeBuffer(ref m_GpuSHData);
            DisposeBuffer(ref m_GpuChunks);

            DisposeBuffer(ref m_GpuView);
            DisposeBuffer(ref m_GpuIndexBuffer);
            DisposeBuffer(ref m_GpuSortDistances);
            DisposeBuffer(ref m_GpuSortKeys);

            DisposeBuffer(ref m_GpuEditSelectedMouseDown);
            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);
            DisposeBuffer(ref m_GpuEditSelected);
            DisposeBuffer(ref m_GpuEditDeleted);
            DisposeBuffer(ref m_GpuEditCountsBounds);
            DisposeBuffer(ref m_GpuEditCutouts);

            m_SorterArgs.resources.Dispose();
            m_SortFallbackLogged = false;
            m_CpuSortFallbackLogged = false;
            m_CpuSortPositions = null;
            m_CpuSortItems = null;
            m_CpuSortKeys = null;
            m_CpuSortBucketCounts = null;
            m_CpuSortBucketOffsets = null;
            m_CpuSortBucketWrite = null;
            m_CpuSortDataHash = default;
            m_CpuSortHasLastState = false;
            m_CpuChunks = null;
            m_CpuVisibleChunks = null;
            m_CpuVisibleChunkSampleSteps = null;
            m_CpuVisibleChunkCandidates = null;
            m_CpuVisibleChunkSelected = null;
            m_CpuVisibleChunkCount = 0;
            m_CpuChunkDataHash = default;
            m_CpuChunkCullPaddingCached = float.NaN;
            m_CpuChunkVisibilityValid = false;

            m_SplatCount = 0;
            m_RenderSplatCount = 0;
            m_GpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        public void OnDisable()
        {
            DisposeResourcesForAsset();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            m_Registered = false;
            m_LoadingResources = false;

            DestroyImmediate(m_MatSplats);
            DestroyImmediate(m_MatComposite);
            DestroyImmediate(m_MatDebugPoints);
            DestroyImmediate(m_MatDebugBoxes);
        }

        internal void CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (IsWebGpuGraphicsDevice())
                return;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            // calculate view dependent data for each splat
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.CalcViewData);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatScale, m_SplatScale);
            cmb.SetComputeFloatParam(m_CSSplatUtilities, Props.SplatOpacityScale, m_OpacityScale);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOrder, m_SHOrder);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SHOnly, m_SHOnly ? 1 : 0);

            uint gsX = GetKernelThreadGroupSizeX(kernelIndex);
            cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, DivRoundUp(m_GpuView.count, gsX), 1, 1);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;

            EnsureSorterAndRegister();
            if (m_Sorter == null || !m_Sorter.Valid)
            {
                if (IsWebGpuGraphicsDevice() && SortPointsOnCpu(cam, matrix))
                {
                    if (!m_CpuSortFallbackLogged)
                    {
                        Debug.LogWarning("Gaussian splat GPU radix sort is not available on this graphics API. Using CPU depth sort for the WebGPU baseline path.", this);
                        m_CpuSortFallbackLogged = true;
                    }
                    return;
                }

                if (!m_SortFallbackLogged)
                {
                    Debug.LogWarning("Gaussian splat GPU radix sort is not available on this graphics API. Rendering in asset order for the WebGPU baseline path.", this);
                    m_SortFallbackLogged = true;
                }
                return;
            }

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            // calculate distance to the camera for each splat
            int calcDistancesKernel = GetKernelIndex(KernelIndices.CalcDistances);
            if (calcDistancesKernel < 0)
                return;

            cmd.BeginSample(s_ProfSort);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatSortDistances, m_GpuSortDistances);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatSortKeys, m_GpuSortKeys);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatChunks, m_GpuChunks);
            cmd.SetComputeBufferParam(m_CSSplatUtilities, calcDistancesKernel, Props.SplatPos, m_GpuPosData);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatFormat, (int)m_Asset.posFormat);
            cmd.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatCount, m_SplatCount);
            cmd.SetComputeIntParam(m_CSSplatUtilities, Props.SplatChunkCount, m_GpuChunksValid ? m_GpuChunks.count : 0);
            uint gsX = GetKernelThreadGroupSizeX(calcDistancesKernel);
            cmd.DispatchCompute(m_CSSplatUtilities, calcDistancesKernel, DivRoundUp(m_GpuSortDistances.count, gsX), 1, 1);

            // sort the splats
            m_Sorter.Dispatch(cmd, m_SorterArgs);
            cmd.EndSample(s_ProfSort);
        }

        public void Update()
        {
            if (m_LoadingResources)
                return;

            var curHash = m_Asset ? m_Asset.dataHash : new Hash128();
            if (m_PrevAsset != m_Asset || m_PrevHash != curHash)
            {
                m_PrevAsset = m_Asset;
                m_PrevHash = curHash;
                if (resourcesAreSetUp)
                {
                    DisposeResourcesForAsset();
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!m_Asset || m_Asset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = m_Asset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            int kernelIndex = GetKernelIndex(KernelIndices.ClearBuffer);
            if (kernelIndex < 0)
                return;

            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, buf);
            m_CSSplatUtilities.SetInt(Props.BufferSize, buf.count);
            uint gsX = GetKernelThreadGroupSizeX(kernelIndex);
            m_CSSplatUtilities.Dispatch(kernelIndex, DivRoundUp(buf.count, gsX), 1, 1);
        }

        void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            int kernelIndex = GetKernelIndex(KernelIndices.OrBuffers);
            if (kernelIndex < 0)
                return;

            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.SrcBuffer, src);
            m_CSSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, dst);
            m_CSSplatUtilities.SetInt(Props.BufferSize, dst.count);
            uint gsX = GetKernelThreadGroupSizeX(kernelIndex);
            m_CSSplatUtilities.Dispatch(kernelIndex, DivRoundUp(dst.count, gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (m_GpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            int initKernelIndex = GetKernelIndex(KernelIndices.InitEditData);
            if (initKernelIndex < 0)
                return;

            m_CSSplatUtilities.SetBuffer(initKernelIndex, Props.DstBuffer, m_GpuEditCountsBounds);
            m_CSSplatUtilities.Dispatch(initKernelIndex, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            int updateKernelIndex = SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData);
            if (updateKernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, updateKernelIndex, Props.DstBuffer, m_GpuEditCountsBounds);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            uint gsX = GetKernelThreadGroupSizeX(updateKernelIndex);
            cmb.DispatchCompute(m_CSSplatUtilities, updateKernelIndex, DivRoundUp(m_GpuEditSelected.count, gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[m_GpuEditCountsBounds.count];
            m_GpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        void UpdateCutoutsBuffer()
        {
            int bufferSize = m_Cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (m_GpuEditCutouts == null || m_GpuEditCutouts.count != bufferSize)
            {
                m_GpuEditCutouts?.Dispose();
                m_GpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (m_Cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < m_Cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(m_Cutouts[i], matrix);
                }
            }

            m_GpuEditCutouts.SetData(data);
            data.Dispose();
        }

        bool EnsureEditingBuffers()
        {
            if (IsWebGpuRuntimeEditingUnsupported())
                return false;
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (m_GpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (m_SplatCount + 31) / 32;
                m_GpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                m_GpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                m_GpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                m_GpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(m_GpuEditSelected);
                ClearGraphicsBuffer(m_GpuEditSelectedMouseDown);
                ClearGraphicsBuffer(m_GpuEditDeleted);
            }
            return m_GpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(m_GpuEditSelected, m_GpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (IsWebGpuRuntimeEditingUnsupported())
                return;
            if (m_GpuEditPosMouseDown == null)
            {
                m_GpuEditPosMouseDown = new GraphicsBuffer(m_GpuPosData.target | GraphicsBuffer.Target.CopyDestination, m_GpuPosData.count, m_GpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuPosData, m_GpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (IsWebGpuRuntimeEditingUnsupported())
                return;
            if (m_GpuEditOtherMouseDown == null)
            {
                m_GpuEditOtherMouseDown = new GraphicsBuffer(m_GpuOtherData.target | GraphicsBuffer.Target.CopyDestination, m_GpuOtherData.count, m_GpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(m_GpuOtherData, m_GpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(m_GpuEditSelectedMouseDown, m_GpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate);

            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_SelectionRect", new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, m_SplatCount);
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null || m_GpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.RotateSelection);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatOtherMouseDown, m_GpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (m_GpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.SplatPosMouseDown, m_GpuEditPosMouseDown);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, Props.SelectionDelta, scale);

            DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, m_SplatCount);
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(m_GpuEditDeleted, m_GpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.SelectAll);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(m_GpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.InvertSelection);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, Props.DstBuffer, m_GpuEditSelected);
            cmb.SetComputeIntParam(m_CSSplatUtilities, Props.BufferSize, m_GpuEditSelected.count);
            DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, m_GpuEditSelected.count);
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.ExportData);
            if (kernelIndex < 0)
                return false;

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_ExportBuffer", dstData);

            DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, m_SplatCount);
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (IsWebGpuRuntimeEditingUnsupported())
                return;
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, kGpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, m_SplatCount);

            // use the new buffers and the new splat count
            m_GpuPosData.Dispose();
            m_GpuOtherData.Dispose();
            m_GpuSHData.Dispose();
            DestroyImmediate(m_GpuColorData);
            m_GpuView.Dispose();

            m_GpuEditSelected?.Dispose();
            m_GpuEditSelectedMouseDown?.Dispose();
            m_GpuEditDeleted?.Dispose();

            m_GpuPosData = newPosData;
            m_GpuOtherData = newOtherData;
            m_GpuSHData = newSHData;
            m_GpuColorData = newColorData;
            m_GpuView = newGpuView;
            m_GpuEditSelected = newEditSelected;
            m_GpuEditSelectedMouseDown = newEditSelectedMouseDown;
            m_GpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref m_GpuEditPosMouseDown);
            DisposeBuffer(ref m_GpuEditOtherMouseDown);

            m_SplatCount = newSplatCount;
            m_RenderSplatCount = newSplatCount;
            m_CpuChunks = null;
            m_CpuVisibleChunks = null;
            m_CpuVisibleChunkSampleSteps = null;
            m_CpuVisibleChunkCandidates = null;
            m_CpuVisibleChunkSelected = null;
            m_CpuVisibleChunkCount = 0;
            m_CpuChunkDataHash = default;
            m_CpuChunkCullPaddingCached = float.NaN;
            m_CpuChunkVisibilityValid = false;
            m_CpuSortHasLastState = false;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (dst == null || IsWebGpuRuntimeEditingUnsupported() || dst.IsWebGpuRuntimeEditingUnsupported())
                return;

            EditCopySplats(
                dst.transform,
                dst.m_GpuPosData, dst.m_GpuOtherData, dst.m_GpuSHData, dst.m_GpuColorData, dst.m_GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (IsWebGpuRuntimeEditingUnsupported())
                return;
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            int kernelIndex = SetAssetDataOnCS(cmb, KernelIndices.CopySplats);
            if (kernelIndex < 0)
                return;

            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(m_CSSplatUtilities, kernelIndex, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(m_CSSplatUtilities, kernelIndex, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(m_CSSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(m_CSSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(m_CSSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        void DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            DispatchUtils(cmb, kernel, count);
            Graphics.ExecuteCommandBuffer(cmb);
        }

        public GraphicsBuffer GpuEditDeleted => m_GpuEditDeleted;
    }
}
