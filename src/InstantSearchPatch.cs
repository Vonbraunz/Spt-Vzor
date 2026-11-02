using System;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace Vzor
{
    // ported from asler's SPT-LootInstantSearch, adapted to 1.2 type names.
    // the trick is not a timer at all: mark every item you look at as already known and
    // already searched, then stop the view asking whether anything is unknown.
    [HarmonyPatch(typeof(PlayerSearchController), "IsItemKnown")]
    internal static class InstantSearchKnownPatch
    {
        private static bool _busy;

        // SetItemAsKnown logs "Item is already found" when it is already known, and this
        // prefix is called constantly for the same items. one attempt per item, then leave it.
        private static readonly System.Collections.Generic.HashSet<IntPtr> Attempted =
            new System.Collections.Generic.HashSet<IntPtr>();

        private static void Prefix(object __instance, Item item)
        {
            if (!Plugin.QolInstantSearch.Value) return;
            if (_busy) return;

            try
            {
                var controller = __instance as PlayerSearchController;
                if (controller == null) return;

                var id = item.Pointer;
                if (!Attempted.Add(id)) return;

                if (Attempted.Count > 20000) Attempted.Clear();

                _busy = true;
                controller.SetItemAsKnown(item, true);
            }
            catch
            {
                // item not ready
            }
            finally
            {
                _busy = false;
            }
        }
    }

    [HarmonyPatch(typeof(SearchableView), "Show")]
    internal static class InstantSearchShowPatch
    {
        private static void Postfix(SearchableView __instance)
        {
            if (!Plugin.QolInstantSearch.Value) return;

            try
            {
                __instance.UpdateSearchState();
            }
            catch
            {
                // view torn down
            }
        }
    }
}
