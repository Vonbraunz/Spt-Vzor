using System;
using HarmonyLib;
using UnityEngine;

namespace Vzor
{
    // magic bullet, exactly as the reference framework does it: hook InitiateShot and move the
    // shot to the stored target, travelling straight down so it lands on the body.
    // the gun never moves; where you are aiming is irrelevant.
    [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitiateShot")]
    internal static class MagicBulletPatch
    {
        private static void Prefix(EFT.Player.FirearmController __instance,
            ref Vector3 shotPosition, ref Vector3 shotDirection, ref Vector3 fireportPosition)
        {
            if (!Plugin.AimMagicBullet.Value) return;
            if (!Plugin.MagicBulletHasTarget) return;

            try
            {
                if (Plugin.LocalFirearmPointer == IntPtr.Zero) return;
                if (__instance.Pointer != Plugin.LocalFirearmPointer) return;

                shotPosition = Plugin.MagicBulletTarget;
                shotDirection = Vector3.down;
                fireportPosition = Plugin.MagicBulletTarget;
            }
            catch
            {
                // instance torn down mid-shot, leave the shot alone
            }
        }
    }
}
