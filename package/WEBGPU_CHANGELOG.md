# WebGPU 改动记录

## 2026-07-06

本次提交整理了最近几轮 WebGPU 发布、资源包缓存、加载界面和运行时调参相关改动。

### 资源包构建与缓存

- `.bundle` 默认构建到 `Assets/bundleAssets/{PackageId}`，不再长期放在 `StreamingAssets`。
- `bundleAssets` 作为持久缓存目录使用，一键构建不会清空整个目录。
- 重新构建同名资源包时，只清理对应的 `bundleAssets/{PackageId}` 子目录，避免旧 bundle 或 manifest 残留。
- 一键构建选中 Renderer 时，只配置 `GaussianSplatAssetBundleLoader`，保留编辑器里的 `GaussianSplatRenderer.Asset` 引用，方便预览和调试。

### WebGPU 发布流程

- 发布 WebGPU 版本前，会从 `Assets/bundleAssets` 找出当前构建场景需要的资源包。
- 构建时临时复制需要的资源包到 `Assets/StreamingAssets/GaussianSplatPackages`。
- 构建场景副本中才会清空 Renderer 的直接 Asset 引用，原始编辑场景不受影响。
- 构建结束后会清理临时放入 `StreamingAssets` 的资源包，避免下次 Build Settings 切换场景时误带无关 bundle。

### 加载进度界面

- `GaussianSplatLoadingOverlay` 默认不再自动创建。
- 项目自己的加载界面负责显示场景加载和点云 bundle 加载进度，避免 Web 端出现两个进度条。
- 如果确实需要插件自带加载层，可以在场景里手动添加组件，或显式开启自动创建开关。

### 运行时调参与导入导出

- 正式发布版本可以通过 URL 参数 `?gsPanel=1` 打开 WebGPU 调参面板。
- 调参面板的保存键值按“场景路径 + packageId”分组，同一浏览器打开不同场景时不会互相覆盖配置。
- 调参面板新增“导出”按钮，可以复制当前参数 JSON。
- 编辑器新增菜单：`Tools > Gaussian WebGPU > 调试 > 从剪贴板导入运行时调参JSON`。
- 导入时会优先匹配当前打开场景中的 Renderer；如果当前选中了 Renderer，也可以确认后强制应用到选中对象。
- 新增菜单、弹窗和 Web 面板提示已改为中文优先。

### 验证

- 已执行 `dotnet build ..\GaoSiPoJian.sln --no-restore`。
- 编译结果：0 个错误；剩余警告为 Unity 旧 API 或既有未使用字段警告。
