using System.Collections.Generic;
using EFT;
using EFT.Quests;

namespace Vzor
{
    // template ids the player currently needs. rebuilt on a timer because quests and the
    // wishlist both change mid-raid, and both live on the profile rather than the world.
    internal static class ItemTargets
    {
        public static readonly HashSet<string> Quest = new HashSet<string>();
        public static readonly HashSet<string> Wishlist = new HashSet<string>();
        public static readonly HashSet<string> Zones = new HashSet<string>();

        private static float _nextRefresh;

        public static void Refresh(GameWorld world, float now)
        {
            if (now < _nextRefresh) return;
            _nextRefresh = now + 30f;

            Quest.Clear();
            Wishlist.Clear();
            Zones.Clear();

            try
            {
                var player = world != null ? world.MainPlayer : null;
                var profile = player != null ? player.Profile : null;
                if (profile == null) return;

                BuildWishlist(profile);
                BuildQuests(profile);
            }
            catch
            {
                // profile swapping between raids, try again in 30 seconds
            }
        }

        private static void BuildWishlist(Profile profile)
        {
            try
            {
                var manager = profile.WishlistManager;
                if (manager == null) return;

                var items = manager.UserItems;
                if (items == null) return;

                var enumerator = items.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var id = enumerator.Current.Key.ToString();
                    if (!string.IsNullOrEmpty(id)) Wishlist.Add(id);
                }
            }
            catch
            {
                // wishlist not synced yet
            }
        }

        private static void BuildQuests(Profile profile)
        {
            try
            {
                var quests = profile.QuestsData;
                if (quests == null) return;

                for (var i = 0; i < quests.Count; i++)
                {
                    var data = quests[i];
                    if (data == null) continue;

                    var status = data.Status;
                    if (status != EQuestStatus.Started && status != EQuestStatus.AvailableForFinish) continue;

                    var quest = data.Quest;
                    if (quest == null) continue;

                    var conditions = quest.NecessaryConditions;
                    if (conditions == null) continue;

                    var enumerator = conditions.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var condition = enumerator.Current;
                        if (condition == null) continue;

                        // find/handover conditions all derive from ConditionMultipleTargets
                        var multiple = condition.TryCast<ConditionMultipleTargets>();
                        if (multiple != null)
                        {
                            var targets = multiple.target;
                            if (targets != null)
                            {
                                for (var t = 0; t < targets.Length; t++)
                                {
                                    var id = targets[t];
                                    if (!string.IsNullOrEmpty(id)) Quest.Add(id);
                                }
                            }
                        }

                        // zone conditions, including place-beacon, carry the world trigger ids
                        var zone = condition.TryCast<ConditionZone>();
                        if (zone != null)
                        {
                            var zoneIds = zone.zoneIds;
                            if (zoneIds != null)
                            {
                                for (var z = 0; z < zoneIds.Length; z++)
                                {
                                    var id = zoneIds[z];
                                    if (!string.IsNullOrEmpty(id)) Zones.Add(id);
                                }
                            }
                        }

                        // stay-in-zone conditions do not derive from ConditionZone
                        var inZone = condition.TryCast<ConditionInZone>();
                        if (inZone != null)
                        {
                            var zoneIds = inZone.zoneIds;
                            if (zoneIds != null)
                            {
                                for (var z = 0; z < zoneIds.Length; z++)
                                {
                                    var id = zoneIds[z];
                                    if (!string.IsNullOrEmpty(id)) Zones.Add(id);
                                }
                            }
                        }

                        // visit-a-place conditions carry one target id
                        var visit = condition.TryCast<ConditionVisitPlace>();
                        if (visit != null)
                        {
                            var id = visit.target;
                            if (!string.IsNullOrEmpty(id)) Zones.Add(id);
                        }
                    }
                }
            }
            catch
            {
                // quest data not ready
            }
        }
    }
}
