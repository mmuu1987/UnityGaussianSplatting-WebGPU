# Unity Gaussian Splatting WebGPU Porting Plan

## Goal

Port the runtime viewer path of this Unity Gaussian Splatting package to Unity 6 Web builds running on desktop Chrome with WebGPU enabled.

This plan is intentionally scoped to WebGPU, not WebGL2. WebGL2 should be treated as unsupported for this package's current architecture because the runtime depends on compute shaders, GPU buffers, procedural drawing, and GPU-side sorting.

## Target Platform

- Unity 6.
- Desktop Google Chrome.
- Unity Web build with WebGPU as the primary graphics API.
- WebGL2 fallback should be disabled during the first porting phase so failures are explicit.

## Non-Goals For The First Port

- Mobile browser support.
- Broad browser compatibility.
- WebGL2 renderer parity.
- Editor-time splat editing in Web builds.
- Runtime export/edit operations in Web builds.
- Matching native desktop performance at million-plus splat counts before a working WebGPU baseline exists.

## Current Runtime Rendering Path

The current package uses this frame flow:

1. Upload packed splat data into GPU buffers and textures.
2. Use compute shader kernels to calculate per-splat camera depth.
3. Use GPU radix sort to sort splats by depth.
4. Use compute shader kernels to calculate view-dependent splat data, including projected ellipse axes and color.
5. Draw instanced procedural quads with the sorted order buffer.
6. Composite the Gaussian splat render target into the camera target.

Important files:

- `Runtime/GaussianSplatRenderer.cs`
- `Runtime/GpuSorting.cs`
- `Shaders/SplatUtilities.compute`
- `Shaders/DeviceRadixSort.hlsl`
- `Shaders/SortCommon.hlsl`
- `Shaders/RenderGaussianSplats.shader`
- `Shaders/GaussianSplatting.hlsl`

## Main Compatibility Risks

### WebGL2 Is Not Viable

Unity WebGL2 does not provide the compute-shader path this package requires. The current renderer gates setup on `SystemInfo.supportsComputeShaders`, then uses compute dispatches every frame. A WebGL2 port would require a separate renderer architecture.

### GPU Sorting Must Be Replaced Or Reworked

The current GPU radix sort depends heavily on HLSL wave intrinsics:

- `WavePrefixSum`
- `WaveReadLaneAt`
- `WaveActiveBallot`
- `WaveGetLaneIndex`
- `WaveGetLaneCount`

Unity WebGPU currently does not support wave intrinsics. Therefore, `DeviceRadixSort.hlsl` and `SortCommon.hlsl` should not be expected to compile or run as-is on WebGPU.

### Raw Buffers Need Verification

The shaders and C# code use raw and byte-address buffer patterns:

- `GraphicsBuffer.Target.Raw`
- `ByteAddressBuffer`
- `RWByteAddressBuffer`

These might need to be converted to `StructuredBuffer<uint>` and `RWStructuredBuffer<uint>` for WebGPU stability.

### Texture And Render Target Formats Need Verification

The existing renderer uses formats such as:

- `GraphicsFormat.R16G16B16A16_SFloat` for Gaussian splat render targets.
- `GraphicsFormat.RGBA_BC7_UNorm` as one possible color data format.

For WebGPU, format support should be checked at runtime with `SystemInfo.IsFormatSupported` or equivalent format capability checks. Prefer safer test formats first, such as `Norm8x4` or `Float16x4`, before testing BC7.

### GPU Readback And Editing Are Not First-Pass Features

WebGPU does not support synchronous GPU readback in the same way native desktop APIs do. Any export/edit path that depends on reading buffers or textures back to the CPU should be deferred or rewritten around `AsyncGPUReadback`.

## Recommended Porting Strategy

### Current Phase 1 Baseline Changes

The current codebase has a first WebGPU baseline path:

- `Shaders/SplatUtilities.compute` keeps the viewer kernels but replaces the wave-based radix sort kernels with empty WebGPU implementations.
- `Shaders/SplatUtilities.compute` uses empty WebGPU implementations for edit/export/copy kernels.
- `Shaders/GaussianSplatting.hlsl` treats packed splat buffers as read-only `ByteAddressBuffer` on WebGPU.
- `Runtime/GpuSorting.cs` treats WebGPU as unsupported for the current radix sorter.
- `Runtime/GaussianSplatRenderer.cs` uses a WebGPU CPU sorting fallback when source asset data is available. It defaults to depth bucket sorting for large scenes, with exact sorting retained as a quality/debug option.
- WebGPU CPU sorting reuses the previous order buffer while camera/object movement stays below configurable thresholds.
- WebGPU CPU sorting defaults to a low-memory path that decodes positions on demand instead of permanently caching a `Vector3[]` for every splat. Position caching can be enabled for smaller scenes when sort speed matters more than memory.
- WebGPU can now perform CPU-side chunk frustum culling before sorting and drawing. The first implementation uses the asset's existing 256-splat chunk bounds when available, builds fallback runtime chunks when not, and draws only visible splats through a compacted order buffer. If the visible splat count exceeds the configured WebGPU budget, the renderer applies a distance-based sampling LOD so distant chunks remain visible at lower density before any chunks are skipped.
- `Runtime/GaussianSplatRuntimeTuningPanel.cs` provides a development-build runtime tuning panel for WebGPU parameters. It appears automatically in development builds, can be toggled with F8, and can also be opened in release builds with `?gsPanel=1`. Save/load values are scoped by scene plus bundle package so different scenes do not overwrite each other in the same browser. The Export button copies JSON that can be imported in the editor through `Tools -> Gaussian WebGPU -> 调试 -> 从剪贴板导入运行时调参JSON`.
- `Runtime/GaussianSplatRenderer.cs` skips `CSCalcViewData` on WebGPU because `RenderGaussianSplats.shader` calculates splat view data directly in the vertex shader for the baseline path.
- `Shaders/RenderGaussianSplats.shader` has a WebGPU baseline path that reads packed splat data directly instead of relying on `_SplatViewData`.
- The WebGPU runtime path now sanitizes runtime tuning values before culling/sorting, clamps invalid `PlayerPrefs` or Inspector values, rebuilds chunk culling data when cull padding changes, releases cached CPU positions when position caching is disabled, and explicitly disables edit/export operations on WebGPU builds.
- The runtime tuning panel throttles scene-wide renderer discovery while visible, clamps slider output, validates loaded settings, and limits window dragging to the title area to reduce accidental camera/control conflicts.
- `Runtime/GaussianSplatAssetBundleLoader.cs` and `Editor/GaussianSplatWebGpuPackageBuilder.cs` provide a minimal AssetBundle loading loop for Web builds. Scenes can remove the direct `GaussianSplatRenderer.Asset` reference, keep only a package id on the loader, download/load a bundle at runtime, assign the loaded `GaussianSplatAsset`, and reuse `GaussianSplatRenderer.LoadResourcesAsync` for throttled GPU upload.

This is still only a bring-up path. Depth buckets are expected to be much faster than exact CPU sorting for large assets, but may have some ordering artifacts. If needed, Phase 2 can replace it with a WebGPU-compatible GPU sort.

### Minimal WebGPU AssetBundle Loading Loop

Objective: keep large Gaussian splat assets out of the Unity Web player data file and load them on demand.

Current implementation:

- `GaussianSplatAssetBundleLoader` can be added next to a `GaussianSplatRenderer`.
- The loader resolves a bundle URL from either:
  - `Bundle Url`, if explicitly set;
  - or `Base Url` + `Bundle File Name`;
  - or `StreamingAssets/GaussianSplatPackages/{PackageId}.bundle` when both are empty.
- The loader downloads the bundle with `UnityWebRequestAssetBundle`, loads a `GaussianSplatAsset`, assigns it to the target renderer, then calls `LoadResourcesAsync`.
- The renderer exposes `UnloadSplatResources(clearAssetReference)` so scene controllers can release the previous splat before switching packages.
- WebGPU tools are placed under the top-level menu `Tools -> Gaussian WebGPU`, separated from the original/PC-oriented `Tools -> Gaussian Splats` menu.
- The editor menu `Tools -> Gaussian WebGPU -> 资源包 -> 构建选中的GaussianSplatAsset到bundleAssets` builds the selected `GaussianSplatAsset` directly into `Assets/bundleAssets/{PackageId}/{PackageId}.bundle`.
- The editor menu `Tools -> Gaussian WebGPU -> 资源包 -> 构建选中的GaussianSplatAsset到自定义目录` keeps the manual output-folder workflow for testing or CDN staging.
- The editor menu `Tools -> Gaussian WebGPU -> 资源包 -> 一键构建选中Renderer资源包并配置` is the closest one-click workflow: select scene renderers, build their assigned assets into `Assets/bundleAssets`, and configure loaders while keeping the editor-time direct renderer asset references for preview/editing.
- The editor menu `Tools -> Gaussian WebGPU -> 资源包 -> 仅配置选中Renderer从资源包加载` adds/configures the loader on selected renderers without clearing the direct `GaussianSplatRenderer.Asset` reference.
- After a bundle build, the editor logs a size report with the bundle file size, raw Gaussian data size, compression ratio, splat count, build target, and per-section sizes for position, transform, color, SH, and chunk data. Use this report to decide whether the bottleneck is splat count, color data, or SH data.
- `Editor/GaussianSplatWebGpuPlayerBuilder.cs` adds `Tools -> Gaussian WebGPU -> 发布 -> 构建WebGPU版本`, `构建并运行WebGPU版本`, and `运行上一次WebGPU构建`. These menus switch to WebGL if needed, try to set the graphics API to WebGPU only, run package preflight checks, temporarily copy only the loader-referenced packages from `Assets/bundleAssets` into `Assets/StreamingAssets/GaussianSplatPackages`, clear direct renderer asset references only in the build scene copy, build to `Builds/WebGPU`, then remove the staged StreamingAssets copy.
- `Runtime/GaussianSplatLoadingOverlay.cs` can display a full-screen loading overlay while `GaussianSplatAssetBundleLoader` is downloading/loading/uploading Gaussian data, but auto-creation is disabled by default so project-owned loading UI can be the single visible progress surface. If a scene explicitly adds this component or sets `s_AutoCreateEnabled`, it reads loader status/progress, reports load errors, hides when all loaders are ready, and exposes `GaussianSplatLoadingOverlay.BlocksSceneInput` so scene/player controllers can pause movement during the point-cloud loading window.

Recommended workflow:

1. Select the scene object that has `GaussianSplatRenderer`.
2. Run `Tools -> Gaussian WebGPU -> 资源包 -> 一键构建选中Renderer资源包并配置`.
3. The generated `.bundle` is written directly under `Assets/bundleAssets/{PackageId}`.
4. Confirm that `GaussianSplatRenderer.Asset` is still available for editor preview and `GaussianSplatAssetBundleLoader.Bundle File Name` is `{PackageId}/{PackageId}.bundle`.
5. Run `Tools -> Gaussian WebGPU -> 发布 -> 构建并运行WebGPU版本` for a local smoke test, or `构建WebGPU版本` for a normal build.
6. The player build is written to `Builds/WebGPU`. The Unity player data should no longer include that direct Gaussian asset reference from the scene.

Preflight checks before player build:

- Warn when a scene `GaussianSplatRenderer.Asset` still has a direct asset reference.
- Warn when a renderer has no `GaussianSplatAssetBundleLoader`.
- Warn when `Load On Start` is disabled.
- Warn when a default StreamingAssets bundle path is missing.

Recommended scene controller integration:

```csharp
if (GaussianSplatting.Runtime.GaussianSplatLoadingOverlay.BlocksSceneInput)
    return;
```

Place this near the top of camera/player input code, or inside `GaussianSceneController` before allowing player control. This keeps the sequence clean:

1. User enters the scene.
2. Loading UI appears.
3. Static scene objects render normally.
4. Gaussian bundle downloads and uploads to GPU.
5. Loading UI hides when the splat renderer is ready.
6. Player input is allowed.

Acceptance criteria:

- A scene can start with an empty `GaussianSplatRenderer.Asset` reference.
- The loader can fetch and load a Gaussian splat bundle at runtime.
- GPU upload still uses the existing per-frame throttled upload path.
- Switching/unloading can release renderer GPU resources before loading the next package.
- The first implementation does not need Addressables, versioned manifests, persistent browser cache management, or multi-package dependency resolution.

### Phase 1: WebGPU Baseline Viewer

Objective: display a small splat asset in desktop Chrome with Unity 6 WebGPU.

Tasks:

1. Create a WebGPU-specific runtime path guarded by platform/API checks.
2. Disable WebGL2 fallback during testing.
3. Disable runtime editing/export/selection features in Web builds.
4. Keep the existing asset upload path if buffer creation works.
5. Skip `CSCalcViewData` on WebGPU and calculate view data in the render shader until a stable compute path is needed.
6. Temporarily bypass the current wave-based GPU radix sort.
7. Render with one of these temporary ordering strategies:
   - no sorting, only to validate draw and shader compatibility;
   - CPU sorting for small test assets;
   - low-frequency CPU sorting;
   - coarse depth bucket sorting.
8. Test with 50k, 100k, and 300k splat assets.

Acceptance criteria:

- Web build launches in desktop Chrome using WebGPU.
- A small Gaussian splat asset renders without shader compile failure.
- Camera movement works.
- Memory usage is stable after loading.
- Failures clearly report the unsupported feature instead of silently falling back to WebGL2.

### Phase 2: WebGPU-Compatible Sorting

Objective: replace the current wave-based radix sort with a WebGPU-compatible ordering path.

Candidate approaches:

- CPU depth sort for small and medium assets.
- CPU coarse sort plus GPU local sort.
- Depth bucket sort.
- Bitonic sort without wave intrinsics.
- Workgroup-local sort and merge without wave intrinsics.
- Precomputed multi-view sorting for constrained camera paths.

Recommended first implementation:

Use depth bucket sorting or CPU coarse sorting first. These are more likely to produce a usable viewer quickly than a full WebGPU radix sort rewrite.

Acceptance criteria:

- Visual blending is acceptable during camera movement.
- Sorting cost is measurable and bounded.
- The implementation avoids HLSL wave intrinsics.
- The renderer can handle at least the project's first target asset size on desktop Chrome.

### Phase 2.5: Chunk Culling Before LOD

Objective: reduce the amount of work WebGPU has to sort and draw before introducing full LOD or streaming.

Current implementation:

- `Runtime/GaussianSplatRenderer.cs` exposes `m_WebGpuChunkFrustumCulling` and `m_WebGpuChunkCullPadding`.
- On WebGPU, each frame builds a visible chunk list from camera frustum tests.
- If `m_WebGpuMaxVisibleSplats` is greater than zero and the visible set exceeds that budget, visible chunks are sorted by distance to the camera. Near chunks are kept at higher density, while middle and far chunks use configurable sample steps. A configurable far-budget reserve keeps distant scenery from disappearing completely when near chunks consume most of the budget.
- CPU exact sort and depth bucket sort only process splats inside visible chunks.
- The procedural draw instance count uses the visible splat count instead of the full asset splat count.
- If chunk data is missing, the renderer builds runtime fallback chunks by scanning decoded positions once.

Acceptance criteria:

- Large scenes render with fewer splats when most of the asset is outside the camera frustum.
- Dense viewing directions stay bounded by the configured visible splat budget.
- Distant scenery remains visible through the far-budget reserve, though at lower density when needed.
- Camera movement does not leave stale hidden/visible chunks in the order buffer.
- Edge popping can be controlled by increasing chunk cull padding.
- Far-detail loss is reduced by distance sampling; remaining far-detail popping can be reduced later with real offline LOD.
- Native desktop rendering remains unchanged.

Next steps after this phase:

- Add per-frame visible splat count/profiling logs or UI.
- Add offline or runtime LOD layers once chunk culling behavior is stable.

### Phase 3: Buffer And Shader Cleanup

Objective: make the renderer robust against WebGPU shader and buffer restrictions.

Tasks:

1. Replace `ByteAddressBuffer` and `RWByteAddressBuffer` where necessary.
2. Prefer structured `uint` buffers for packed data if raw buffers cause WebGPU issues.
3. Add format capability checks for render targets and color data.
4. Add WebGPU-specific shader variants only where needed.
5. Strip unsupported shader variants from Web builds.
6. Keep native desktop rendering behavior unchanged.

Acceptance criteria:

- WebGPU shader compile path is clean.
- Native desktop path still works.
- Unsupported formats fail with clear messages or use known fallbacks.

### Phase 4: Performance Pass

Objective: tune the WebGPU viewer after correctness is proven.

Measurements:

- asset download size;
- Unity heap usage;
- GPU buffer memory;
- load time;
- sort time;
- view-data compute time;
- draw time;
- composite time;
- frame time during camera movement.

Likely optimizations:

- reduce SH order for Web builds;
- prefer compressed or quantized data formats that WebGPU handles reliably;
- reduce splat count through offline LOD;
- sort less frequently during slow camera movement;
- use depth buckets instead of full sorting for large assets;
- split large scenes into chunks and cull aggressively.

## Implementation Principles

- Keep native PC behavior intact.
- Add WebGPU-specific paths behind clear platform or graphics API checks.
- Do not attempt to make WebGL2 work by small patches to the current renderer.
- Do not start with editor/export features.
- Make failures explicit and actionable.
- Test with small data first, then scale.
- Prefer a working viewer over perfect sorting in the first milestone.

## Key Open Questions

- What is the first real target asset size?
- Is approximate transparency ordering acceptable?
- Is camera movement free-form, guided, or mostly fixed?
- Can Web builds use reduced SH order or simplified color?
- Is Unity 6 WebGPU-only acceptable for release, or is WebGL2 fallback required later?

## Reference Notes

Unity documentation relevant to this plan:

- WebGL2 is Unity Web's default graphics API, but it lacks modern GPU features needed by this renderer.
- Unity WebGPU supports compute shaders and indirect rendering, but WebGPU support is experimental.
- Unity WebGPU does not support HLSL wave intrinsics.
- Unity WebGPU has texture format and GPU readback restrictions.
- Unity Web builds have browser memory constraints, so asset size and heap usage must be measured early.

Useful docs:

- https://docs.unity3d.com/Manual/WebGL2.html
- https://docs.unity3d.com/Manual/WebGPU-features.html
- https://docs.unity3d.com/Manual/WebGPU-limitations.html
- https://docs.unity3d.com/Manual/class-ComputeShader-introduction.html
- https://docs.unity3d.com/Manual/webgl-memory.html
