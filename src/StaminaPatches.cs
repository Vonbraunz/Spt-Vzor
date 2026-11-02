using System;
using HarmonyLib;

namespace Vzor
{
    // infinite stamina without killing endurance xp. the game spends stamina normally, the
    // caller awards the skill progress, then we put the value back before the next frame.
    // scoped to the local player's three pools by pointer, so bots still get tired.
    [HarmonyPatch(typeof(Stamina), "Consume")]
    internal static class StaminaConsumePatch
    {
        private static void Prefix(Stamina __instance, out float __state)
        {
            __state = -1f;

            if (!Plugin.QolInfiniteStamina.Value) return;

            try
            {
                if (IsLocalPlayerStamina(__instance)) __state = __instance.Current;
            }
            catch
            {
                // leave it disabled for this call
            }
        }

        private static void Postfix(Stamina __instance, float __state)
        {
            if (__state < 0f) return;

            try
            {
                __instance.Current = __state;
            }
            catch
            {
                // instance died mid-call, nothing to restore
            }
        }

        private static bool IsLocalPlayerStamina(Stamina stamina)
        {
            var pointer = stamina.Pointer;
            if (pointer == IntPtr.Zero) return false;

            if (pointer == Plugin.LocalStaminaPointer) return true;
            if (pointer == Plugin.LocalHandsStaminaPointer) return true;
            if (pointer == Plugin.LocalOxygenPointer) return true;

            return false;
        }
    }
}
