using System;
using EFT;
using UnityEngine;

namespace Vzor
{
    // gameplay toggles that need a write or two per frame, kept away from the ESP code
    public class QolController : MonoBehaviour
    {
        private GameWorld _world;
        private Player _player;
        private float _nextWorldSearch;

        private void Update()
        {
            TrackLocalPlayer();
        }

        // late so our writes land after the game's own update, not before it gets overwritten
        private void LateUpdate()
        {
            if (_player == null) return;

            if (Plugin.QolInfiniteStamina.Value) PinStamina();
            if (Plugin.QolNoRecoil.Value) ApplyWeaponTweaks();
            if (Plugin.QolNoMalfunction.Value) ApplyNoMalfunction();
        }

        private void PinStamina()
        {
            try
            {
                var physical = _player.Physical;
                if (physical == null) return;

                var capacity = physical.StaminaCapacity;

                var stamina = physical.Stamina;
                if (stamina != null && stamina.Current < capacity * 0.99f) stamina.Current = capacity;

                var hands = physical.HandsStamina;
                if (hands != null && hands.Current < capacity * 0.99f) hands.Current = capacity;
            }
            catch
            {
                // player mid-spawn, try again next frame
            }
        }

        private void ApplyWeaponTweaks()
        {
            try
            {
                var pwa = _player.ProceduralWeaponAnimation;
                if (pwa == null) return;

                pwa.CameraSmoothRecoil = 0f;
                pwa.CrankRecoil = false;
                pwa._currentRecoilCameraRotate = Quaternion.identity;
            }
            catch
            {
                // weapon animation not ready
            }
        }

        private void ApplyNoMalfunction()
        {
            try
            {
                var hands = _player.HandsController;
                if (hands == null) return;

                // FirearmController is nested inside Player, which is why a flat type search missed it
                var firearm = hands.TryCast<EFT.Player.FirearmController>();
                if (firearm == null) return;

                var weapon = firearm.Item;
                if (weapon == null) return;

                var template = weapon.Template;
                if (template == null) return;

                var weaponTemplate = template.TryCast<EFT.InventoryLogic.WeaponTemplate>();
                if (weaponTemplate == null) return;

                weaponTemplate.AllowJam = false;
                weaponTemplate.AllowMisfire = false;
                weaponTemplate.AllowSlide = false;
                weaponTemplate.AllowFeed = false;
                weaponTemplate.AllowOverheat = false;
                weaponTemplate.BaseMalfunctionChance = 0f;
                weaponTemplate.DurabilityBurnRatio = 0f;
            }
            catch
            {
                // weapon mid-swap, try again next frame
            }
        }

        private void TrackLocalPlayer()
        {
            var now = Time.unscaledTime;

            if (_world == null && now >= _nextWorldSearch)
            {
                _nextWorldSearch = now + 1f;
                _world = UnityEngine.Object.FindObjectOfType<GameWorld>();
            }

            if (_world != null && _player == null) _player = _world.MainPlayer;

            if (_player == null)
            {
                ClearPointers();
                return;
            }

            try
            {
                var physical = _player.Physical;
                if (physical == null)
                {
                    ClearPointers();
                    return;
                }

                Plugin.LocalStaminaPointer = physical.Stamina != null ? physical.Stamina.Pointer : IntPtr.Zero;
                Plugin.LocalHandsStaminaPointer = physical.HandsStamina != null ? physical.HandsStamina.Pointer : IntPtr.Zero;
                Plugin.LocalOxygenPointer = physical.Oxygen != null ? physical.Oxygen.Pointer : IntPtr.Zero;

                var pwa = _player.ProceduralWeaponAnimation;
                Plugin.LocalPwaPointer = pwa != null ? pwa.Pointer : IntPtr.Zero;

                var firearm = _player.HandsController != null
                    ? _player.HandsController.TryCast<EFT.Player.FirearmController>()
                    : null;

                if (firearm != null)
                {
                    try
                    {
                        Plugin.LocalFirearmPointer = firearm.Pointer;
                        Plugin.AimSettled = firearm.IsAiming && !firearm._inChangeAimingAnimationState;
                    }
                    catch
                    {
                        Plugin.AimSettled = false;
                    }
                }
                else
                {
                    Plugin.AimSettled = false;
                    Plugin.LocalFirearmPointer = IntPtr.Zero;
                }
            }
            catch
            {
                ClearPointers();
            }
        }

        private static void ClearPointers()
        {
            Plugin.LocalStaminaPointer = IntPtr.Zero;
            Plugin.LocalHandsStaminaPointer = IntPtr.Zero;
            Plugin.LocalOxygenPointer = IntPtr.Zero;
            Plugin.LocalPwaPointer = IntPtr.Zero;
        }

            }
}
