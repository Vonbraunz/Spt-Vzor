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

            // kill is the last door. some things do not damage you, they just declare you dead,
            // and a trap that does that walks past the clamp, the destroy guard and everything
            // else. refuse the declaration too.
            var kill = AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.Kill));

            if (kill != null)
            {
                harmony.Patch(kill, prefix: new HarmonyMethod(
                    typeof(DadModePatch).GetMethod(nameof(DadModePatch.KillPrefix),
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

                // one line per raid that says whether the game thinks this controller is ours.
                // if this ever prints "not you", the local check below is the reason dad mode
                // looks asleep.
                var who = controller.Player == null
                    ? "null"
                    : (controller.Player.IsYourPlayer ? "you" : "not you");

                Plugin.Logger.LogInfo("[dad] watching controller, player=" + who);
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
            // common is where blast and fall damage lands, and leaving it out is how a claymore
            // still killed you with both vitals sitting at 1.
            if (bodyPart != EBodyPart.Head && bodyPart != EBodyPart.Chest && bodyPart != EBodyPart.Common) return;
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


        // bots are simulated with the same health controller in offline spt, so every clamp has to
        // prove it is looking at the local player first. without this you also make bots immortal.
        private static bool _diagHit;
        private static bool _diagMiss;

        private static bool IsLocal(ActiveHealthController instance)
        {
            if (instance == null) return false;

            try
            {
                // the controller already knows which player it belongs to. comparing pointers to
                // MainPlayer was a guess that bots made expensive.
                var player = instance.Player;

                if (player != null && player.IsYourPlayer)
                {
                    Diag(ref _diagHit, "[dad] local check hit (IsYourPlayer)");
                    return true;
                }

                // offline there is exactly one human in the raid, so a non ai controller is you
                // even when the flag has not been set yet. this is the fallback that keeps dad
                // mode from quietly doing nothing.
                if (player != null && !player.IsAI)
                {
                    Diag(ref _diagHit, "[dad] local check hit (not ai)");
                    return true;
                }

                Diag(ref _diagMiss, "[dad] local check missed: player=" +
                    (player == null ? "null" : (player.IsAI ? "ai" : "human")) +
                    ", we still let the damage through");

                return false;
            }
            catch (Exception e)
            {
                Diag(ref _diagMiss, "[dad] local check threw: " + e.Message);
                return false;
            }
        }

        // one line each way, per session. enough to tell "dad mode is asleep" apart from
        // "dad mode is awake and the damage never came through ApplyDamage".
        private static void Diag(ref bool flag, string line)
        {
            if (flag) return;
            flag = true;
            Plugin.Logger.LogInfo(line);
        }

        // mine blasts and scripted traps often skip the damage pipeline and just call kill.
        private static float _nextKillLog;

        internal static bool KillPrefix(ActiveHealthController __instance, EDamageType damageType)
        {
            if (!Plugin.QolDadMode.Value) return true;
            if (!IsLocal(__instance)) return true;

            var now = UnityEngine.Time.unscaledTime;

            if (now > _nextKillLog)
            {
                _nextKillLog = now + 5f;
                Plugin.Logger.LogInfo("[dad] refused kill (" + damageType + ")");
            }

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

        // a destroyed head or chest is the normal death. refuse it, and keep the part at 1 hp so the
        // ui does not show a blacked vital either.
        internal static bool DestroyPrefix(ActiveHealthController __instance, EBodyPart bodyPart, EDamageType damageType)
        {
            if (!Plugin.QolDadMode.Value) return true;
            // common covers blast and fall deaths, which do not go through a limb or the chest.
            // stomach matches the original mod's critical list.
            if (bodyPart != EBodyPart.Head && bodyPart != EBodyPart.Chest &&
                bodyPart != EBodyPart.Common && bodyPart != EBodyPart.Stomach) return true;
            if (!IsLocal(__instance)) return true;

            try
            {
                var health = __instance.GetBodyPartHealth(bodyPart, false);
                if (health.Current < 1f) __instance.Heal(bodyPart, 1f - health.Current);

                Plugin.Logger.LogInfo("[dad] refused destroy " + bodyPart + " (" + damageType +
                                      ") at " + health.Current.ToString("0.0"));
            }
            catch
            {
                // sweep next frame
            }

            return false;
        }

    }
}
