using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace QOL_Realisim_Fixes
{
    // Custom sonic boom clips, replacing vanilla's single shared clip
    // entirely (see SonicBoomManagePatch). Three independently-randomized
    // pools -- a fresh random pick from the relevant pool every time a boom
    // plays, so the same exact sample never repeats twice in a row:
    //  - Near: aircraft booms under FarDistanceThresholdMeters.
    //  - Far: aircraft booms at or beyond that distance -- no missile
    //    equivalent, missile booms only ever happen at close range.
    //  - Missile: always this pool, regardless of distance.
    // All loaded once, embedded directly in the DLL (Sounds\SonicBooms\*.wav)
    // via WavUtility, same pattern as BulletCrackAssets.
    internal static class SonicBoomAssets
    {
        internal const float FarDistanceThresholdMeters = 2500f;

        // A far boom can genuinely be playing from several thousand meters
        // away -- with vanilla's own AudioSource rolloff (minDistance=1000,
        // maxDistance=5000, inherited via CopyAudioSourceSettings), a boom
        // triggered from well past FarDistanceThresholdMeters would already
        // be attenuated down close to nothing before ever reaching the
        // listener. SonicBoomManagePatch overrides the temp source's own
        // rolloff range to these two values specifically for a far boom, so
        // it always plays as loud as if heard from exactly
        // FarDistanceThresholdMeters (never any louder for being triggered
        // closer to that threshold, since Far never plays under it), fading
        // out toward FarMaxAudibleDistanceMeters instead of continuing to
        // use whatever the real (much larger) distance happens to be.
        internal const float FarMaxAudibleDistanceMeters = 5000f;

        private const string ResourcePrefix = "QOL_Realisim_Fixes.SonicBooms.";

        private static AudioClip[] _near;
        private static AudioClip[] _far;
        private static AudioClip[] _missile;
        private static bool _loadAttempted;

        internal static AudioClip GetRandomNear()
        {
            EnsureLoaded();
            return PickRandom(_near);
        }

        internal static AudioClip GetRandomFar()
        {
            EnsureLoaded();
            return PickRandom(_far);
        }

        internal static AudioClip GetRandomMissile()
        {
            EnsureLoaded();
            return PickRandom(_missile);
        }

        private static void EnsureLoaded()
        {
            if (_loadAttempted)
            {
                return;
            }
            _loadAttempted = true;

            _near = LoadSet("SonicBoom", "Near");
            _far = LoadSet("SonicBoomFar", "Far");
            _missile = LoadSet("MissileBoom", "Missile");
        }

        private static AudioClip PickRandom(AudioClip[] clips)
        {
            if (clips == null || clips.Length == 0)
            {
                return null;
            }
            return clips[UnityEngine.Random.Range(0, clips.Length)];
        }

        // Fixed at 3 per pool to match the actual asset set -- not worth a
        // generic "however many files exist" scan for three hardcoded names.
        private static AudioClip[] LoadSet(string fileNamePrefix, string clipNamePrefix)
        {
            AudioClip[] clips = new AudioClip[3];
            for (int i = 0; i < clips.Length; i++)
            {
                int index = i + 1;
                clips[i] = LoadClip(fileNamePrefix + index + ".wav", clipNamePrefix + index);
            }
            return clips;
        }

        private static AudioClip LoadClip(string fileName, string clipName)
        {
            string resourceName = ResourcePrefix + fileName;
            try
            {
                byte[] bytes;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        SoundPropagation.Log.LogWarning($"[BoomDiag] Embedded resource '{resourceName}' not found.");
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

                AudioClip clip = WavUtility.ToAudioClip(bytes, clipName);
                if (clip == null)
                {
                    SoundPropagation.Log.LogWarning($"[BoomDiag] Failed to parse '{resourceName}'.");
                }
                return clip;
            }
            catch (Exception e)
            {
                SoundPropagation.Log.LogWarning($"[BoomDiag] Exception loading '{resourceName}': {e}");
                return null;
            }
        }
    }
}
