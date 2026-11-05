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
        internal static readonly EBodyPart[] VitalParts = { EBodyPart.Head, EBodyPart.Chest };

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

                if (damage > allowed) damage = Math.Max(0f, allowed);
            }
            catch
            {
                // health system not ready yet
            }
        }
    }
}
