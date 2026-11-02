using UnityEngine;

namespace Vzor
{
    internal static class Visibility
    {
        // everything except unity's ignore-raycast layer
        private const int Mask = ~(1 << 2);

        // one linecast. the ray starts slightly ahead of the camera so the player's own
        // collider never blocks it, and anything belonging to the target's own hierarchy
        // counts as visible. everything else in the way means it stays hidden.
        public static bool HasLineOfSight(Vector3 from, Vector3 to, Transform source)
        {
            try
            {
                var delta = to - from;
                var distance = delta.magnitude;
                if (distance < 0.75f) return true;

                var start = from + delta / distance * 0.4f;
                var rayLength = distance - 0.4f;

                if (!Physics.Linecast(start, to, out var hit, Mask, QueryTriggerInteraction.Ignore)) return true;
                if (hit.transform == null) return true;
                if (source != null && hit.transform.IsChildOf(source)) return true;

                return hit.distance >= rayLength - 0.35f;
            }
            catch
            {
                // physics unhappy, better to show the marker than to lose it
                return true;
            }
        }
    }
}
