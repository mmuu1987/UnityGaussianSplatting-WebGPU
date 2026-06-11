// SPDX-License-Identifier: MIT

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Minimal runtime loader for keeping GaussianSplatAsset references out of Web builds.
    /// Put the asset and its TextAsset data files into one AssetBundle, then load it by URL/package id.
    /// </summary>
    public class GaussianSplatAssetBundleLoader : MonoBehaviour
    {
        [Tooltip("Renderer that receives the loaded GaussianSplatAsset. If empty, this component searches on the same GameObject.")]
        public GaussianSplatRenderer m_Target;
        [Tooltip("Logical package id. Used as the bundle file name when Bundle Url and Bundle File Name are empty.")]
        public string m_PackageId;
        [Tooltip("Full bundle URL. If set, Base Url and Package Id path composition are ignored.")]
        public string m_BundleUrl;
        [Tooltip("Base URL/folder that contains bundles. Empty means StreamingAssets/GaussianSplatPackages.")]
        public string m_BaseUrl;
        [Tooltip("Bundle file name. Empty means PackageId + .bundle.")]
        public string m_BundleFileName;
        [Tooltip("GaussianSplatAsset name inside the bundle. Empty loads the first GaussianSplatAsset found.")]
        public string m_AssetName;
        [Tooltip("Start loading automatically when this component starts.")]
        public bool m_LoadOnStart = true;
        [Tooltip("Clear an accidental direct Renderer Asset reference before loading the package.")]
        public bool m_ClearRendererAssetBeforeLoad = true;
        [Tooltip("Unload the compressed AssetBundle container after the GaussianSplatAsset is loaded. The loaded asset stays alive.")]
        public bool m_UnloadBundleAfterAssetLoad = true;
        [Min(4)]
        [Tooltip("Maximum Gaussian data bytes uploaded to GPU per frame.")]
        public int m_MaxUploadBytesPerFrame = GaussianSplatRenderer.kDefaultMaxUploadBytesPerFrame;

        public bool isLoading { get; private set; }
        public bool isLoaded { get; private set; }
        public bool hasError { get; private set; }
        public string errorMessage { get; private set; }
        public string status { get; private set; }
        public float progress { get; private set; }

        AssetBundle m_LoadedBundle;
        Coroutine m_LoadCoroutine;

        void Start()
        {
            if (m_LoadOnStart)
                Load();
        }

        void OnDisable()
        {
            if (m_LoadCoroutine != null)
            {
                StopCoroutine(m_LoadCoroutine);
                m_LoadCoroutine = null;
            }
            isLoading = false;
        }

        void OnDestroy()
        {
            UnloadBundle(false);
        }

        public void Load()
        {
            if (m_LoadCoroutine != null)
                StopCoroutine(m_LoadCoroutine);
            m_LoadCoroutine = StartCoroutine(LoadRoutine());
        }

        public void UnloadRendererResources(bool clearRendererAssetReference = true)
        {
            ResolveTarget();
            if (m_Target != null)
                m_Target.UnloadSplatResources(clearRendererAssetReference);
            isLoaded = false;
            hasError = false;
            errorMessage = null;
            progress = 0;
            status = "Unloaded";
        }

        IEnumerator LoadRoutine()
        {
            isLoading = true;
            isLoaded = false;
            hasError = false;
            errorMessage = null;
            progress = 0;
            status = "Resolving renderer";

            ResolveTarget();
            if (m_Target == null)
            {
                Fail("No GaussianSplatRenderer target found.");
                yield break;
            }

            if (m_ClearRendererAssetBeforeLoad)
                m_Target.UnloadSplatResources(true);

            string url = ResolveBundleUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                Fail("No Gaussian splat bundle URL or package id configured.");
                yield break;
            }

            status = "Downloading Gaussian splat bundle";
            using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(url))
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    progress = Mathf.Clamp01(operation.progress * 0.35f);
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Fail($"Failed to download Gaussian splat bundle: {request.error} ({url})");
                    yield break;
                }

                UnloadBundle(false);
                m_LoadedBundle = DownloadHandlerAssetBundle.GetContent(request);
            }

            if (m_LoadedBundle == null)
            {
                Fail($"Failed to open Gaussian splat bundle: {url}");
                yield break;
            }

            status = "Loading Gaussian splat asset";
            AssetBundleRequest assetLoad = string.IsNullOrWhiteSpace(m_AssetName)
                ? m_LoadedBundle.LoadAllAssetsAsync<GaussianSplatAsset>()
                : m_LoadedBundle.LoadAssetAsync<GaussianSplatAsset>(m_AssetName);
            yield return assetLoad;

            var splatAsset = assetLoad.asset as GaussianSplatAsset;
            if (splatAsset == null && assetLoad.allAssets != null && assetLoad.allAssets.Length != 0)
                splatAsset = assetLoad.allAssets[0] as GaussianSplatAsset;
            if (splatAsset == null)
            {
                Fail($"No GaussianSplatAsset found in bundle: {url}");
                yield break;
            }

            m_Target.m_Asset = splatAsset;

            if (m_UnloadBundleAfterAssetLoad)
                UnloadBundle(false);

            status = "Uploading Gaussian splat data";
            yield return m_Target.LoadResourcesAsync(OnRendererLoadProgress, m_MaxUploadBytesPerFrame);

            isLoading = false;
            isLoaded = m_Target.HasValidRenderSetup;
            hasError = false;
            errorMessage = null;
            progress = isLoaded ? 1 : progress;
            status = isLoaded ? "Gaussian splat ready" : "Gaussian splat load skipped";
            m_LoadCoroutine = null;
        }

        void OnRendererLoadProgress(GaussianSplatLoadReport report)
        {
            progress = Mathf.Lerp(0.35f, 1.0f, report.Normalized);
            status = report.Stage;
        }

        void ResolveTarget()
        {
            if (m_Target == null)
                m_Target = GetComponent<GaussianSplatRenderer>();
        }

        string ResolveBundleUrl()
        {
            if (!string.IsNullOrWhiteSpace(m_BundleUrl))
                return m_BundleUrl.Trim();

            string fileName = !string.IsNullOrWhiteSpace(m_BundleFileName)
                ? m_BundleFileName.Trim()
                : !string.IsNullOrWhiteSpace(m_PackageId)
                    ? m_PackageId.Trim() + ".bundle"
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            string baseUrl = !string.IsNullOrWhiteSpace(m_BaseUrl)
                ? m_BaseUrl.Trim()
                : CombineUrl(Application.streamingAssetsPath, "GaussianSplatPackages");
            return CombineUrl(baseUrl, fileName);
        }

        static string CombineUrl(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
                return right ?? string.Empty;
            if (string.IsNullOrEmpty(right))
                return left;
            return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\');
        }

        void UnloadBundle(bool unloadLoadedObjects)
        {
            if (m_LoadedBundle == null)
                return;
            m_LoadedBundle.Unload(unloadLoadedObjects);
            m_LoadedBundle = null;
        }

        void Fail(string message)
        {
            UnloadBundle(false);
            Debug.LogError(message, this);
            hasError = true;
            errorMessage = message;
            status = message;
            progress = 0;
            isLoading = false;
            isLoaded = false;
            m_LoadCoroutine = null;
        }
    }
}
