# Unity Gaussian Splatting WebGPU

这是一个基于 [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) 的 Unity 6 WebGPU 适配分支，目标是在桌面版 Chrome 的 WebGPU 环境中运行 Gaussian Splatting 点云/高斯泼溅查看器。

原项目主要面向 D3D12、Vulkan、Metal 等原生图形 API。这个分支的重点不是重写完整插件，而是保留原有 Unity Gaussian Splatting 工作流，并为 WebGPU 做一条可用的运行时渲染路径。

![Screenshot](/docs/Images/shotOverview.jpg?raw=true "Screenshot")

## 当前目标平台

- Unity 6。
- 桌面版 Google Chrome。
- Unity Web 平台，使用 WebGPU 图形 API。
- 推荐先关闭 WebGL2 fallback，让 WebGPU 问题直接暴露。

WebGL2 不是当前目标。这个插件依赖 compute shader、GPU buffer、procedural draw 和排序流程，用 WebGL2 做同等效果需要另一套渲染架构。

## WebGPU 适配内容

这个分支已经加入一套 WebGPU baseline viewer，主要修改点如下：

- 避开原项目依赖 wave intrinsics 的 GPU radix sort。
- 在 WebGPU 下使用 CPU fallback 排序。
- 支持 `Depth Buckets` 粗深度桶排序，适合大场景实时浏览。
- 保留 `Exact` 精确 CPU 排序，用于质量对比或小数据量测试。
- 支持仅在摄像机明显移动/旋转后重新排序，减少每帧 CPU 压力。
- WebGPU 渲染路径跳过 `CSCalcViewData`，改为在渲染 shader 的 vertex 阶段直接计算 view data。
- WebGPU 下禁用或空实现编辑、导出、GPU radix sort 等不适合首版 Web 运行时的 compute kernel。
- 支持分块视锥裁剪，只处理当前摄像机可能看到的 splat。
- 支持可见 splat 数量预算，避免大场景在浏览器端一次绘制过多数据。
- 支持距离 LOD 采样，近处保持密度，中远处按步长降采样。
- 支持远处预算保留比例，避免预算被近处数据吃完后远景完全消失。
- 提供运行时调参面板，方便 Web build 中直接调参数，不需要反复打包。
- 提供右上角 FPS 显示组件，方便观察浏览器端性能。

更详细的迁移记录见：

- [`package/WEBGPU_PORTING_PLAN.md`](package/WEBGPU_PORTING_PLAN.md)

## 主要文件

WebGPU 相关核心改动集中在：

- `package/Runtime/GaussianSplatRenderer.cs`
- `package/Runtime/GpuSorting.cs`
- `package/Runtime/GaussianSplatRuntimeTuningPanel.cs`
- `package/Runtime/GaussianSplatFpsOverlay.cs`
- `package/Editor/GaussianSplatRendererEditor.cs`
- `package/Shaders/SplatUtilities.compute`
- `package/Shaders/GaussianSplatting.hlsl`
- `package/Shaders/RenderGaussianSplats.shader`

## 运行时调参

Development Build 或 URL 中包含 `gsPanel=1` 时，会自动创建 WebGPU 调参面板。

- `F8`：显示/隐藏 WebGPU runtime tuning panel。
- `F9`：显示/隐藏 FPS overlay。
- `Save`：把当前参数保存到 `PlayerPrefs`。
- `Load`：从 `PlayerPrefs` 恢复上次保存的参数。
- `Print`：把当前参数打印到浏览器控制台，方便回填 Inspector。

面板使用英文显示，主要是为了避免 WebGL/WebGPU build 中 IMGUI 中文字体缺失导致文字不显示。

常用参数含义：

- `Max Visible Splats`：当前最多绘制多少个 splat。数值越大画面越完整，但越容易卡。
- `Distance LOD`：开启后，中远距离会降采样，减少绘制压力。
- `LOD Near Distance`：这个距离以内尽量保持原始密度。
- `LOD Far Distance`：超过这个距离后按远距离采样规则处理。
- `Mid Sample Step`：中距离每隔多少个 splat 取一个。
- `Far Sample Step`：远距离每隔多少个 splat 取一个。
- `Max Sample Step`：预算不够时允许使用的最大降采样步长。
- `Far Reserve Percent`：给远处保留的预算比例，避免远景完全消失。
- `Chunk Frustum Culling`：开启分块视锥裁剪，摄像机看不到的块不参与排序和绘制。
- `Chunk Cull Padding`：裁剪边界扩张量，调大可以减少边缘突然消失。
- `Sort Mode`：`Depth Buckets` 更快，`Exact` 更准但更慢。
- `Bucket Count`：深度桶数量，越大排序越细，但 CPU 成本也会增加。

## 推荐调试流程

1. 先用小场景确认 WebGPU build 能正常显示。
2. 开启 `Depth Buckets` 排序。
3. 开启 `Chunk Frustum Culling`。
4. 设置一个浏览器能承受的 `Max Visible Splats`。
5. 如果远处消失，提高 `Far Reserve Percent` 或降低远处采样强度。
6. 如果近处有空洞，提高预算或降低近/中距离采样步长。
7. 如果转向密集区域卡顿，降低预算、提高远处采样步长，或者增大摄像机排序阈值。
8. 调到满意后，用 `Print` 输出参数，再回填到 Inspector。

## 使用方式

下载或 clone 这个仓库后，可以打开示例工程：

- `projects/GaussianExample`
- `projects/GaussianExample-URP`
- `projects/GaussianExample-HDRP`

创建 Gaussian Splat asset 的方式与原项目一致：

1. 在 Unity 中打开 `Tools -> Gaussian Splats -> Create GaussianSplatAsset`。
2. 选择输入的 PLY 或 SPZ 文件。
3. 选择压缩质量和输出目录。
4. 点击 `Create Asset`。
5. 在带有 `GaussianSplatRenderer` 的 GameObject 上，把 `Asset` 字段指向生成的 GaussianSplat asset。

当前支持的输入格式：

- PLY，来自原始 3D Gaussian Splatting 论文工程的点云文件。
- [Scaniverse SPZ](https://scaniverse.com/spz)。

Gaussian Splat 模型通常很大，本仓库不包含模型数据。原论文项目提供过示例模型下载，可参考：

- [graphdeco-inria/gaussian-splatting](https://github.com/graphdeco-inria/gaussian-splatting)

## WebGPU 注意事项

- 大场景在浏览器端很容易遇到内存和帧率瓶颈，需要使用预算、分块裁剪和距离 LOD。
- WebGPU baseline 目前更偏向“能稳定浏览”，不是完全等价于 PC 原生渲染路径。
- `Depth Buckets` 可能出现少量排序误差，但比精确 CPU 排序更适合浏览器端大场景。
- 真正的多层离线 LOD 暂未实现。当前使用的是运行时距离采样 LOD，不会增加资源文件体积。
- 运行时编辑、导出、选择等能力不是 WebGPU 首版目标。
- 原生 PC 路径尽量保持原项目行为，不应因为 WebGPU fallback 影响 D3D12/Vulkan/Metal 的常规渲染。

## 原项目说明

原项目是 Aras Pranckevicius 对 SIGGRAPH 2023 论文 [3D Gaussian Splatting for Real-Time Radiance Field Rendering](https://repo-sam.inria.fr/fungraph/3d-gaussian-splatting/) 的 Unity 实时查看器实现。

原项目文档：

- [Render Pipeline Integration](/docs/render-pipeline-integration.md)
- [Editing Splats](/docs/splat-editing.md)

原作者相关文章：

- [Gaussian Splatting is pretty cool!](https://aras-p.info/blog/2023/09/05/Gaussian-Splatting-is-pretty-cool/)
- [Making Gaussian Splats smaller](https://aras-p.info/blog/2023/09/13/Making-Gaussian-Splats-smaller/)
- [Making Gaussian Splats more smaller](https://aras-p.info/blog/2023/09/27/Making-Gaussian-Splats-more-smaller/)
- [Gaussian Explosion](https://aras-p.info/blog/2023/12/08/Gaussian-explosion/)

## License and External Code Used

The code from the original Unity viewer is under the MIT license. This repository keeps the original license terms.

The project also uses several third-party libraries:

- [zanders3/json](https://github.com/zanders3/json), MIT license, copyright 2018 Alex Parker.
- `DeviceRadixSort` GPU sorting code contributed by Thomas Smith.
- Virtual Reality fixes contributed by [@ninjamode](https://github.com/ninjamode), based on [Unity-VR-Gaussian-Splatting](https://github.com/ninjamode/Unity-VR-Gaussian-Splatting).

请注意，原始论文训练工程的 license 与这个 Unity viewer 的 MIT license 不是一回事。即使 viewer 本身是 MIT，也仍然需要确认你的 Gaussian Splat PLY/SPZ 数据来源是否允许商业使用。
