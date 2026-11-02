using System;
using HarmonyLib;
using UnityEngine;

namespace Vzor
{
    // no recoil: skip the shot effector's process pass. this is where recoil is applied to
    // the camera, and doing it here avoids skipping PWA.Shoot, which left the shot pipeline
    // half-initialised and threw NullReferenceExceptions on weapon swaps.
    [HarmonyPatch(typeof(ShotEffector), "Process")]
    internal static class RecoilEffectorPatch
    {
        private static bool Prefix() => !Plugin.QolNoRecoil.Value;
    }

    // after a reload the game can flip recoil pipelines, and skipping Shoot to cover that
    // left the shot state uninitialised and threw NREs. zeroing the curve multipliers on the
    // shared base class instead: the recoil maths still runs, it just multiplies by nothing.
    [HarmonyPatch(typeof(WeaponRecoilProcessBase), "CalculateRecoil")]
    internal static class RecoilCurvePatch
    {
        private static void Prefix(WeaponRecoilProcessBase __instance)
        {
            if (!Plugin.QolNoRecoil.Value) return;

            try
            {
                __instance.CurveValueMultiply = 0f;
                __instance.CurveAimingValueMultiply = 0f;
            }
            catch
            {
                // process mid-teardown
            }
        }
    }

    [HarmonyPatch(typeof(EFT.Animations.ProceduralWeaponAnimation), "AddHandRecoilRotateToCamera")]
    internal static class RecoilRotatePatch
    {
        private static bool Prefix() => !Plugin.QolNoRecoil.Value;
    }

    // no sway: the aiming wobble is the breath effector, not the PWA sway fields.
    // zero its inputs before it runs and its state after, so whichever order it reads them in,
    // nothing is applied. it plays no part in the ADS transition, so ADS is unaffected.
    [HarmonyPatch(typeof(EFT.Animations.BreathEffector), "Process")]
    internal static class BreathEffectorPatch
    {
        private static void Prefix(EFT.Animations.BreathEffector __instance)
        {
            if (!Plugin.QolNoSway.Value) return;
            Zero(__instance);
        }

        private static void Postfix(EFT.Animations.BreathEffector __instance)
        {
            if (!Plugin.QolNoSway.Value) return;
            Zero(__instance);
        }

        private static void Zero(EFT.Animations.BreathEffector instance)
        {
            try
            {
                instance.Intensity = 0f;
                instance._breathIntensity = 0f;
                instance._shakeIntensity = 0f;
                instance.TremorOn = false;
            }
            catch
            {
                // instance mid-teardown
            }
        }
    }

    // no sway: zero the sway state right after the game computes it. ProcessEffectors only
    // consumes these values, which is why postfixing that did nothing.
    [HarmonyPatch(typeof(EFT.Animations.ProceduralWeaponAnimation), "UpdateSwaySettings")]
    internal static class SwaySettingsPatch
    {
        private static void Postfix(EFT.Animations.ProceduralWeaponAnimation __instance)
        {
            if (!Plugin.QolNoSway.Value) return;

            try
            {
                __instance.AimSwayMax = Vector3.zero;
                __instance.AimSwayMin = Vector3.zero;
                __instance._aimSwayStrength = 0f;
                __instance._swayStrength = 0f;
                __instance._swayFactor = 0f;
                __instance.SwayFalloff = 0f;
                __instance.HandShakeEffectorEnabled = false;
            }
            catch
            {
                // instance mid-teardown
            }
        }
    }
}
