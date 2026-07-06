// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GaussianSplatting.Editor
{
    static class GaussianSplatRuntimeTuningImporter
    {
        const string kImportMenu = "Tools/Gaussian WebGPU/调试/从剪贴板导入运行时调参JSON";

        [MenuItem(kImportMenu)]
        static void ImportFromClipboard()
        {
            string json = GUIUtility.systemCopyBuffer;
            if (!GaussianSplatRuntimeTuningPanel.TryParseExportJson(json, out var data, out string error))
            {
                EditorUtility.DisplayDialog(
                    "导入 Gaussian WebGPU 调参",
                    "剪贴板里没有有效的 Gaussian WebGPU 调参 JSON。\n\n" + error,
                    "确定");
                return;
            }

            List<GaussianSplatRenderer> targets = GetSelectedRenderers();
            bool usingSelection = targets.Count != 0;
            if (!usingSelection)
                targets = FindRenderersByScope(data.scopeId);

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "导入 Gaussian WebGPU 调参",
                    "当前打开的场景里没有找到匹配的 GaussianSplatRenderer。\n\n" +
                    "你可以在 Hierarchy 里选中目标 Renderer，然后重新执行导入。\n\n" +
                    "导出作用域：\n" + data.scopeId,
                    "确定");
                return;
            }

            if (usingSelection && HasScopeMismatch(targets, data.scopeId))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "导入 Gaussian WebGPU 调参",
                    "导出的作用域和当前选中的 Renderer 不完全匹配。\n\n" +
                    "导出作用域：\n" + data.scopeId + "\n\n" +
                    "仍然把这些参数应用到当前选中的 Renderer 吗？",
                    "仍然应用",
                    "取消");
                if (!applyAnyway)
                    return;
            }

            Undo.RecordObjects(targets.ToArray(), "导入 Gaussian WebGPU 运行时调参");
            foreach (var renderer in targets)
            {
                GaussianSplatRuntimeTuningPanel.ApplyValues(renderer, data.values);
                EditorUtility.SetDirty(renderer);
                Scene scene = renderer.gameObject.scene;
                if (scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(scene);
            }

            Debug.Log(
                $"已导入 Gaussian WebGPU 运行时调参到 {targets.Count} 个 Renderer。\n" +
                $"导出作用域：{data.scopeId}\n" +
                $"场景：{data.scenePath}\n" +
                $"资源包：{data.packageId}");
        }

        static List<GaussianSplatRenderer> GetSelectedRenderers()
        {
            var result = new List<GaussianSplatRenderer>();
            var seen = new HashSet<GaussianSplatRenderer>();
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null)
                    continue;

                var renderers = go.GetComponentsInChildren<GaussianSplatRenderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer != null && seen.Add(renderer))
                        result.Add(renderer);
                }
            }
            return result;
        }

        static List<GaussianSplatRenderer> FindRenderersByScope(string scopeId)
        {
            var result = new List<GaussianSplatRenderer>();
            if (string.IsNullOrWhiteSpace(scopeId))
                return result;

            foreach (var renderer in Resources.FindObjectsOfTypeAll<GaussianSplatRenderer>())
            {
                if (renderer == null || EditorUtility.IsPersistent(renderer))
                    continue;
                if (!renderer.gameObject.scene.IsValid())
                    continue;
                if (renderer != null && GaussianSplatRuntimeTuningPanel.GetSettingsScopeId(renderer) == scopeId)
                    result.Add(renderer);
            }
            return result;
        }

        static bool HasScopeMismatch(List<GaussianSplatRenderer> renderers, string scopeId)
        {
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;
                if (GaussianSplatRuntimeTuningPanel.GetSettingsScopeId(renderer) != scopeId)
                    return true;
            }
            return false;
        }
    }
}
