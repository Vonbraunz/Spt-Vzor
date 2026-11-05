using System;
using EFT.InventoryLogic;

namespace Vzor
{
    // the client stopped giving items useful runtime classes: nearly everything is a plain Item
    // now, so anything that classified by GetType().Name put half the map in the wrong bucket.
    // walk the template tree instead. the category nodes still carry their names, and the chain
    // is short enough to walk every time.
    internal static class ItemCategories
    {
        internal static string Of(Item item)
        {
            if (item == null) return "other";

            try
            {
                var template = item.Template;

                for (var depth = 0; template != null && depth < 16; depth++)
                {
                    var category = FromNode(template._name);
                    if (category != null) return category;

                    template = template.Parent;
                }
            }
            catch
            {
                // template chain mid load
            }

            return "other";
        }

        private static string FromNode(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            switch (name)
            {
                case "Weapon":
                case "Knife":
                case "ThrowWeap":
                    return "weapon";

                case "Ammo":
                case "AmmoBox":
                    return "ammo";

                case "Meds":
                    return "medical";

                case "FoodDrink":
                    return "food";

                case "Key":
                    return "key";

                case "Mod":
                    return "mod";

                case "BarterItem":
                case "SpecItem":
                case "Money":
                case "BattlePassDocument":
                    return "barter";

                case "Equipment":
                case "ArmoredEquipment":
                case "Headphones":
                case "ArmBand":
                case "Armor":
                case "Backpack":
                case "TacticalVest":
                case "Helmet":
                case "Headwear":
                case "FaceCover":
                case "Eyewear":
                    return "gear";

                default:
                    return null;
            }
        }
    }
}
