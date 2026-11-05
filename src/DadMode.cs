using System;
using EFT;
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

            // the original dad mode mod blocks this instead of the damage, and it is right: a body
            // part being destroyed is what kills you. limbs still black out, head and chest do not.
            var destroy = AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DestroyBodyPart));

            if (destroy != null)
            {
                harmony.Patch(destroy, prefix: new HarmonyMethod(
                    typeof(DadModePatch).GetMethod(nameof(DadModePatch.DestroyPrefix),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
            }

            // no IsAlive patch here on purpose. that setter is declared on the generic base and
            // harmony refuses to patch it through a closed subclass, which throws and takes every
            // later registration down with it. DestroyBodyPart is the hook that matters anyway.
            return true;
        }

        private static IntPtr _watched;

        // logs the damage type when it does happen, so the next death names its own cause
        internal static void Watch(ActiveHealthController controller)
        {
            if (controller == null) return;

            var pointer = controller.Pointer;
            if (pointer == _watched) return;
            _watched = pointer;

            try
            {
                controller.DiedEvent += OnDied;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[dad] could not watch death: " + e.Message);
            }
        }

        private static void OnDied(EDamageType type)
        {
            Plugin.Logger.LogWarning("[dad] died: " + type);
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
            if (!IsLocal(__instance)) return;

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

        private static float _nextDotLog;

        // bots are simulated with the same health controller in offline spt, so every clamp has to
        // prove it is looking at the local player first. without this you also make bots immortal.
        private static bool IsLocal(ActiveHealthController instance)
        {
            if (instance == null) return false;

            try
            {
                // the controller already knows which player it belongs to. comparing pointers to
                // MainPlayer was a guess that bots made expensive.
                var player = instance.Player;
                return player != null && player.IsYourPlayer;
            }
            catch
            {
                return false;
            }
        }

        // skipping the tick entirely is heavy handed, but the alternative is arithmetic on a rate
        // that applies to every body part at once. dad mode is already a cheat.
        // a destroyed head or chest is the normal death. refuse it, and keep the part at 1 hp so the
        // ui does not show a blacked vital either.
        internal static bool DestroyPrefix(ActiveHealthController __instance, EBodyPart bodyPart)
        {
            if (!Plugin.QolDadMode.Value) return true;
            // common covers blast and fall deaths, which do not go through a limb or the chest
            if (bodyPart != EBodyPart.Head && bodyPart != EBodyPart.Chest && bodyPart != EBodyPart.Common) return true;
            if (!IsLocal(__instance)) return true;

            try
            {
                var health = __instance.GetBodyPartHealth(bodyPart, false);
                if (health.Current < 1f) __instance.Heal(bodyPart, 1f - health.Current);
            }
            catch
            {
                // sweep next frame
            }

            return false;
        }

        // only the flip to dead is swallowed, and only for the local player. everything that leads
        // there still happens: hp drops, limbs black, bleeds tick, the screen goes red.
        internal static bool IsAlivePrefix(ActiveHealthController __instance, bool value)
        {
            if (!Plugin.QolDadMode.Value) return true;
            if (value) return true;
            if (!IsLocal(__instance)) return true;

            Plugin.Logger.LogInfo("[dad] blocked death");

            try
            {
                DadMode.Pin(__instance);
            }
            catch
            {
                // sweep next frame
            }

            return false;
        }
    }
}
