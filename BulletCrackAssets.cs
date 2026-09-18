using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Loads the custom bullet-crack sound (Sounds\bulletcrack.wav, embedded
    // directly into the mod DLL) at runtime via WavUtility -- a single raw
    // clip doesn't need the heavier AssetBundle pipeline LifeboatAssets uses
    // for a whole prefab/model, just a plain PCM parse straight into an
    // AudioClip.
    internal static class BulletCrackAssets
    {
        private const string ResourceName = "QOL_Realisim_Fixes.bulletcrack";

        private static AudioClip _clip;
        private static bool _loadAttempted;

        internal static AudioClip Clip
        {
            get
            {
                if (!_loadAttempted)
                {
                    _loadAttempted = true;
                    _clip = LoadClip();
                }
                return _clip;
            }
        }

        private static AudioClip LoadClip()
        {
            try
            {
                byte[] bytes;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        SoundPropagation.Log.LogWarning($"[BulletCrackDiag] Embedded resource '{ResourceName}' not found.");
                        return null;
                    }
                    bytes = new byte[stream.Length];
                    int totalRead = 0;
                    while (totalRead < bytes.Length)
                    {
                        int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                        if (read <= 0)
                        {
                            break;
                        }
                        totalRead += read;
                    }
                }

                AudioClip clip = WavUtility.ToAudioClip(bytes, "BulletCrack");
                if (clip == null)
                {
                    SoundPropagation.Log.LogWarning("[BulletCrackDiag] Failed to parse bulletcrack.wav -- falling back to no custom crack clip.");
                }
                else
                {
                    SoundPropagation.Log.LogInfo($"[BulletCrackDiag] Loaded custom crack clip, length={clip.length:F3}s.");
                }
                return clip;
            }
            catch (Exception e)
            {
                SoundPropagation.Log.LogWarning($"[BulletCrackDiag] Exception loading bulletcrack.wav: {e}");
                return null;
            }
        }
    }
}
