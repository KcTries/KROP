using System.IO;
using System.Reflection;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // The lifeboat model ships as an AssetBundle embedded directly in this
    // DLL (Assets\lifeboat, added as an EmbeddedResource in the csproj)
    // rather than as a loose file next to it -- the model isn't expected to
    // change often, so a single self-contained DLL is worth the small size
    // increase and one extra extraction step at startup.
    internal static class LifeboatAssets
    {
        private const string ResourceName = "QOL_Realisim_Fixes.lifeboat";
        private const string PrefabAssetName = "Navy_Liferaft_symmetrictexfix";

        internal static GameObject LifeboatPrefab { get; private set; }

        internal static void Initialize()
        {
            using (Stream resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resourceStream == null)
                {
                    SoundPropagation.Log.LogError($"[LifeboatAssets] Embedded resource '{ResourceName}' not found -- lifeboats will not spawn.");
                    return;
                }

                // Stream.Read isn't guaranteed to fill the buffer in one
                // call -- loop until it does (or hits EOF), same fix
                // already applied to BulletCrackAssets' own embedded-resource
                // load for the identical reason.
                byte[] bundleBytes = new byte[resourceStream.Length];
                int totalRead = 0;
                while (totalRead < bundleBytes.Length)
                {
                    int read = resourceStream.Read(bundleBytes, totalRead, bundleBytes.Length - totalRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    totalRead += read;
                }

                AssetBundle bundle = AssetBundle.LoadFromMemory(bundleBytes);
                if (bundle == null)
                {
                    SoundPropagation.Log.LogError("[LifeboatAssets] Failed to load the lifeboat AssetBundle from memory.");
                    return;
                }

                LifeboatPrefab = bundle.LoadAsset<GameObject>(PrefabAssetName);
                if (LifeboatPrefab == null)
                {
                    SoundPropagation.Log.LogError($"[LifeboatAssets] Asset '{PrefabAssetName}' not found in the lifeboat bundle.");
                }
            }
        }
    }
}
