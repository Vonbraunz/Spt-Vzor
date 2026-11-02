using EFT;

namespace Vzor
{
    // the game hands us the raid world instead of us hunting for it. asking unity for a
    // gameworld every frame is what killed the character creation screen: the search itself
    // drags il2cpp into spinning up the gameworld type while eft is halfway through building
    // the side picker, and the create flow never recovers. so we wait to be given one and drop
    // it when the raid is disposed. if this reference is null, we are in a menu and do nothing.
    internal static class WorldTracker
    {
        public static GameWorld Current;

        // parameter name matters. harmony binds postfix arguments by name, and a name it does
        // not recognise is treated as a missing method parameter. __instance is the magic one.
        internal static void Capture(GameWorld __instance)
        {
            if (__instance == null) return;

            // a destroyed object compares equal to null through unity's overload, so this
            // short circuits before we go poking at a dead pointer
            var changed = Current == null || Current.Pointer != __instance.Pointer;

            Current = __instance;

            if (changed) Plugin.Logger.LogInfo("raid world captured");
        }

        internal static void Forget(GameWorld __instance)
        {
            if (Current == null) return;

            if (__instance == null || Current.Pointer == __instance.Pointer)
            {
                Current = null;
                Plugin.Logger.LogInfo("raid world dropped");
            }
        }
    }
}
