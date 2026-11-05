using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Vzor
{
    // place item triggers and quest triggers are scene furniture. they are all there once the map
    // finishes loading, and they do not move. the esp and the web map both needed the same list,
    // and each of them was doing two findobjectsoftype sweeps every ten seconds, which is where
    // the periodic hitch came from on a big map.
    //
    // one shared scan lives here instead. it runs a handful of times at raid start, which is
    // exactly when a hitch is invisible, and then stops for the rest of the raid.
    internal static class SceneTriggers
    {
        internal sealed class Entry
        {
            public string Id;
            public string Description;
            public Vector3 Position;
            public Transform Transform;
        }

        internal static readonly List<Entry> Place = new List<Entry>();
        internal static readonly List<Entry> Visit = new List<Entry>();

        private static System.IntPtr _world;
        private static int _attempts;
        private static float _nextScan;

        // immediate, then two, four, ten and twenty four seconds in. five goes is plenty for a
        // slow load, and if the triggers are still not there by then they are not coming.
        private static readonly float[] Delays = { 2f, 4f, 10f, 24f };

        internal static void Ensure(GameWorld world, float now)
        {
            if (world == null) return;

            if (world.Pointer != _world)
            {
                _world = world.Pointer;
                _attempts = 0;
                _nextScan = 0f;
                Place.Clear();
                Visit.Clear();
            }

            if (_attempts > Delays.Length) return;
            if (now < _nextScan) return;

            _attempts++;
            _nextScan = now + (_attempts <= Delays.Length ? Delays[_attempts - 1] : 30f);

            Fill();
        }

        internal static void Forget()
        {
            _world = System.IntPtr.Zero;
            _attempts = 0;
            _nextScan = 0f;
            Place.Clear();
            Visit.Clear();
        }

        private static void Fill()
        {
            Place.Clear();
            Visit.Clear();

            try
            {
                var placed = UnityEngine.Object.FindObjectsOfType<PlaceItemTrigger>();
                if (placed != null)
                {
                    for (var i = 0; i < placed.Length; i++)
                    {
                        var trigger = placed[i];
                        if (trigger == null) continue;

                        var id = trigger.TriggerId;
                        if (string.IsNullOrEmpty(id)) id = trigger.Id;
                        if (string.IsNullOrEmpty(id)) continue;

                        Place.Add(new Entry
                        {
                            Id = id,
                            Description = trigger.Description,
                            Position = trigger.transform.position,
                            Transform = trigger.transform
                        });
                    }
                }
            }
            catch
            {
                // triggers not spawned yet, the next attempt will get them
            }

            try
            {
                var visits = UnityEngine.Object.FindObjectsOfType<QuestTrigger>();
                if (visits != null)
                {
                    for (var i = 0; i < visits.Length; i++)
                    {
                        var trigger = visits[i];
                        if (trigger == null) continue;

                        var id = trigger.Id;
                        if (string.IsNullOrEmpty(id)) continue;

                        Visit.Add(new Entry
                        {
                            Id = id,
                            Description = trigger.Description,
                            Position = trigger.transform.position,
                            Transform = trigger.transform
                        });
                    }
                }
            }
            catch
            {
                // same
            }
        }
    }
}
