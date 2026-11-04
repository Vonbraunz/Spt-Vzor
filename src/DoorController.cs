using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Vzor
{
    // opens every locked door near the player instead of carrying a keyring around.
    // the key requirement lives in KeyId on the door, so blanking that is what actually
    // drops the lock. the state machine then needs a nudge out of Locked, otherwise you get
    // a door that no longer wants a key but still refuses to move.
    public class DoorController : MonoBehaviour
    {
        private GameWorld _world;
        private Player _player;

        // doors do not move and a map has a few hundred of them, so scan once per raid and
        // keep the list. findobjectsoftype every frame is how you turn a raid into a slideshow.
        private readonly List<Door> _doors = new List<Door>();
        private bool _scanned;

        private void Update()
        {
            try
            {
                if (!TrackWorld()) return;

                if (Input.GetKeyDown(Plugin.DoorUnlockKey.Value)) UnlockNearby();
            }
            catch
            {
                // door mid-transition or player mid-spawn, next press will do
            }
        }

        private bool TrackWorld()
        {
            var world = WorldTracker.Current;

            if (world == null)
            {
                Drop();
                return false;
            }

            if (_world == null || _world.Pointer != world.Pointer)
            {
                Drop();
                _world = world;
            }

            if (_player == null) _player = _world.MainPlayer;

            return _player != null;
        }

        private void Drop()
        {
            _world = null;
            _player = null;
            _doors.Clear();
            _scanned = false;
        }

        private void UnlockNearby()
        {
            if (!_scanned) Scan();

            var radius = Plugin.DoorUnlockRadius.Value;
            var origin = _player.transform.position;

            var touched = 0;
            var opened = 0;
            var sealedCount = 0;

            for (var i = 0; i < _doors.Count; i++)
            {
                var door = _doors[i];
                if (door == null) continue;

                try
                {
                    if (Vector3.Distance(origin, door.transform.position) > radius) continue;

                    // sealed doors are event state, not loot. leave them alone or you break whatever put the plate on
                    if ((door.DoorState & EDoorState.Sealed) != 0)
                    {
                        sealedCount++;
                        continue;
                    }

                    var wantsKey = !string.IsNullOrEmpty(door.KeyId);
                    var locked = (door.DoorState & EDoorState.Locked) != 0;

                    if (!wantsKey && !locked) continue;

                    if (Plugin.DoorUnlockDebug.Value)
                        Plugin.Logger.LogInfo("door " + door.Id + " before key='" + door.KeyId + "' state=" + door.DoorState);

                    door.KeyId = string.Empty;

                    if (locked) door.SetDoorState(EDoorState.Shut, true);

                    // the setter path occasionally leaves the Locked bit set. direct assignment is the
                    // blunt way out and it works, we just do not want to rely on it unless we have to
                    if ((door.DoorState & EDoorState.Locked) != 0)
                    {
                        if (Plugin.DoorUnlockDebug.Value)
                            Plugin.Logger.LogWarning("door " + door.Id + " ignored SetDoorState, forcing open");

                        door.DoorState = EDoorState.Open;
                    }

                    touched++;

                    if (Plugin.DoorUnlockOpen.Value && (door.DoorState & EDoorState.Open) == 0)
                    {
                        door.Interact(EInteractionType.Open);
                        opened++;
                    }

                    if (Plugin.DoorUnlockDebug.Value)
                        Plugin.Logger.LogInfo("door " + door.Id + " after key='" + door.KeyId + "' state=" + door.DoorState);
                }
                catch (System.Exception e)
                {
                    if (Plugin.DoorUnlockDebug.Value)
                        Plugin.Logger.LogWarning("door " + (door != null ? door.Id : "?") + " failed: " + e.Message);
                }
            }

            Plugin.Logger.LogInfo("doors: " + touched + " unlocked, " + opened + " pushed open, " + sealedCount +
                                  " sealed, radius " + radius + "m");
        }

        private void Scan()
        {
            _doors.Clear();

            // keycard doors and sliding doors both inherit from Door, so they come along for free
            var found = UnityEngine.Object.FindObjectsOfType<Door>();
            if (found != null)
                for (var i = 0; i < found.Length; i++)
                    if (found[i] != null) _doors.Add(found[i]);

            _scanned = true;
            Plugin.Logger.LogInfo("doors: tracking " + _doors.Count);
        }
    }
}
