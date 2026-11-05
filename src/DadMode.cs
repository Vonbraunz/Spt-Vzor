using System;
using EFT.HealthSystem;
using HarmonyLib;

namespace Vzor
{
    // dad gamer mode: the two parts whose blackout kills you stop at 1 hp instead of reaching
    // zero. the prefix clamps damage before it lands, the sweep below catches anything that got
    // in by another route (bleeds, distributed damage, a wound you spawned with).
    internal static class DadMode
    {
        // common is the pool explosions, falls and scripted damage use. leaving it out is how a
        // grenade still kills you with both vitals at 1 hp.
        internal static readonly EBodyPart[] VitalParts = { EBodyPart.Head, EBodyPart.Chest, EBodyPart.Common };

        internal static bool Clamp(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage));
            if (method == null) return false;

            harmony.Patch(method, prefix: new HarmonyMethod(
                typeof(DadModePatch).GetMethod(nameof(DadModePatch.Prefix),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));

            return true;
        }

        internal static void Pin(ActiveHealthController controller)
        {
            if (controller == null) return;

            for (var i = 0; i < VitalParts.Length; i++)
            {
                var part = VitalParts[i];

                try
                {
                    var health = controller.GetBodyPartHealth(part, false);
                    if (health.Current < 1f) controller.Heal(part, 1f - health.Current);
                }
                catch
                {
                    // controller mid rebuild, next frame will do
                }
            }
        }
    }

    internal static class DadModePatch
    {
        // ref damage so the clamp really lands. anything that would take a vital part under 1 hp
        // is trimmed to exactly 1 instead of being blocked outright, so the fight still hurts.
        internal static void Prefix(ActiveHealthController __instance, EBodyPart bodyPart, ref float damage)
        {
            if (!Plugin.QolDadMode.Value) return;
            if (damage <= 0f) return;
            if (bodyPart != EBodyPart.Head && bodyPart != EBodyPart.Chest) return;

            try
            {
                var health = __instance.GetBodyPartHealth(bodyPart, false);
                var allowed = health.Current - 1f;

                if (damage > allowed)
                {
                    // log every clamp. it is rare, and it answers "what killed me" the next time
                    // something gets through anyway.
                    Plugin.Logger.LogInfo("[dad] clamped " + bodyPart + " from " + damage.ToString("0.0") +
                                          " to " + Math.Max(0f, allowed).ToString("0.0") +
                                          " (current " + health.Current.ToString("0.0") + ")");
                    damage = Math.Max(0f, allowed);
                }
            }
            catch
            {
                // health system not ready yet
            }
        }
    }
}
