using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace QOL_Realisim_Fixes.Patches
{
    // Missile.Warhead.Detonate() spawns one of six VFX prefabs (airEffect/
    // armorEffect/terrainEffect/waterSurfaceEffect/underwaterEffect/
    // fizzleEffect) via a plain UnityEngine.Object.Instantiate() call --
    // completely separate from both the flightSound-repurposing path
    // (MissileFlightSoundPatch) and the shared ExplosionAudioManager system
    // (ExplosionAudioCockpitFilterPatch). Whatever plays THIS prefab's own
    // detonation sound is entirely prefab-defined: some may carry their own
    // ExplosionAudio component (self-registering with ExplosionAudioManager
    // in its own Start(), already covered), but simpler/smaller ones --
    // root-caused for the smallest IR missile, reported to reuse the same
    // explosion asset as unguided rockets -- instead have a plain
    // AudioSource with Play On Awake. Unity plays that automatically the
    // instant the prefab is instantiated, with no method call at all for
    // Harmony to intercept directly.
    //
    // All six branches call the identical Instantiate(GameObject, Transform)
    // overload, so a transpiler swap here catches whichever one actually
    // fires without needing to duplicate Detonate()'s own branching logic
    // (underwater/terrain/armor/water-surface/air/fizzle) to figure out
    // which prefab was chosen.
    [HarmonyPatch(typeof(Missile.Warhead), "Detonate")]
    internal static class MissileWarheadEffectAudioPatch
    {
        // Object.Instantiate(GameObject, Transform) as it appears in
        // Detonate()'s decompiled source is actually the GENERIC
        // Instantiate<T>(T, Transform) overload constructed with T=GameObject
        // (confirmed by the assignment to a GameObject-typed local with no
        // cast -- the non-generic Object-returning overload would require
        // one). AccessTools.Method with concrete parameter types matches
        // against the method as reflection reports its open generic
        // definition's parameters (the generic parameter T itself, not
        // GameObject), so a naive lookup here would silently fail to match
        // anything and MethodReplacer would quietly leave the method
        // untouched -- found by hand instead: locate the open generic
        // Instantiate<T>(T, Transform) definition (distinguished from the
        // 1-arg and (T, Vector3, Quaternion)/(T, Transform, bool) 3-arg
        // overloads by exact parameter shape) and construct the same closed
        // form the compiler would have emitted.
        private static readonly MethodInfo InstantiateMethod = FindInstantiateGameObjectTransformMethod();
        private static readonly MethodInfo InstantiateReplacement = AccessTools.Method(
            typeof(MissileWarheadEffectAudioPatch), nameof(InstantiateWithAudioTakeover));

        private static MethodInfo FindInstantiateGameObjectTransformMethod()
        {
            foreach (MethodInfo method in typeof(Object).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "Instantiate" || !method.IsGenericMethodDefinition)
                {
                    continue;
                }
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 && parameters[1].ParameterType == typeof(Transform))
                {
                    return method.MakeGenericMethod(typeof(GameObject));
                }
            }
            SoundPropagation.Log.LogWarning(
                "[MissileWarheadAudioDiag] Could not find UnityEngine.Object.Instantiate<T>(T, Transform) via "
                + "reflection -- missile warhead VFX audio takeover will not run.");
            return null;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            if (InstantiateMethod == null)
            {
                return list;
            }

            // MethodReplacer silently leaves everything unchanged if its
            // target MethodInfo doesn't match what's actually in the IL --
            // no exception, no indication anything went wrong. Counted by
            // hand here so a reflection mismatch shows up as a loud warning
            // instead of a quiet no-op that looks identical to "this
            // particular missile has no warhead effects."
            int matches = list.FindAll(instruction =>
                instruction.Calls(InstantiateMethod)).Count;
            if (matches == 0)
            {
                SoundPropagation.Log.LogWarning(
                    "[MissileWarheadAudioDiag] Instantiate<GameObject>(GameObject, Transform) not found in "
                    + "Warhead.Detonate()'s IL -- missile warhead VFX audio takeover did not apply.");
                return list;
            }
            return Transpilers.MethodReplacer(list, InstantiateMethod, InstantiateReplacement);
        }

        private static GameObject InstantiateWithAudioTakeover(GameObject original, Transform parent)
        {
            GameObject instance = Object.Instantiate(original, parent);
            if (instance != null)
            {
                // Checking synchronously right here is too early for a
                // prefab that configures its own audio the same way
                // vanilla's own ExplosionAudio does -- assigning .clip
                // inside ITS OWN Start(), not at construction. Start() for
                // a just-instantiated object runs on the next Update pass,
                // not synchronously inside Instantiate(), so a helper
                // component that waits one frame runs after every other
                // script on this object has had its own Start() called,
                // regardless of Unity's unspecified inter-script Start()
                // ordering within that frame.
                //
                // A stuck-motor-effect report was chased through several
                // rewrites of this method (routing the delay through this
                // mod's own driver, then a plain per-frame queue, then a
                // dedicated coroutine host) on the theory that a component
                // sitting on this same GameObject was interfering with a
                // particle system's own cleanup -- none of those changes
                // actually affected the reported symptom, and the real
                // cause turned out to be a completely different mod adding
                // lights to the same effect. This is back to the original,
                // simplest form.
                instance.AddComponent<DelayedWarheadAudioTakeover>().PrefabName = original.name;
            }
            return instance;
        }

        private class DelayedWarheadAudioTakeover : MonoBehaviour
        {
            public string PrefabName;

            private System.Collections.IEnumerator Start()
            {
                yield return null;
                TakeOverAudio(gameObject, PrefabName);
                Destroy(this);
            }
        }

        private static void TakeOverAudio(GameObject instance, string prefabName)
        {
            // Already self-registers with ExplosionAudioManager via its own
            // Start() -- already covered by ExplosionAudioCockpitFilterPatch,
            // so don't double-handle it here.
            if (instance.GetComponentInChildren<ExplosionAudio>() != null)
            {
                SoundPropagation.Log.LogInfo(
                    $"[MissileWarheadAudioDiag] '{prefabName}' has its own ExplosionAudio component -- "
                    + "deferring to ExplosionAudioManager, not taking over here.");
                return;
            }

            AudioSource[] sources = instance.GetComponentsInChildren<AudioSource>();
            SoundPropagation.Log.LogInfo(
                $"[MissileWarheadAudioDiag] '{prefabName}' spawned, no ExplosionAudio -- found {sources.Length} "
                + $"AudioSource(s) one frame later: {string.Join(", ", System.Array.ConvertAll(sources, s =>
                    $"'{s.gameObject.name}' clip={(s.clip != null ? s.clip.name : "null")} "
                    + $"isPlaying={s.isPlaying} playOnAwake={s.playOnAwake} volume={s.volume:F2}"))}");

            foreach (AudioSource source in sources)
            {
                if (source.clip == null)
                {
                    continue;
                }
                // Same "let the real source briefly start, then take over"
                // pattern used everywhere else in this mod -- Play On Awake
                // (or any other Start()-triggered clip) has already started
                // it by the time this runs, one frame after spawn.
                AudioClip clip = source.clip;
                Vector3 position = source.transform.position;
                source.Stop();
                SoundPropagation.ScheduleDelayedOneShot(source, clip, position);
            }
        }
    }
}
