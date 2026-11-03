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

        // zone trigger id -> name of the quest that owns it. the trigger's own description is
        // frequently a raw locale key, which is how "TriggerWithID" ended up on screen.
        public static readonly Dictionary<string, string> ZoneNames = new Dictionary<string, string>();

        public struct QuestProgress
        {
            public int Current;
            public int Required;
        }

        // template id -> how many you have / how many the quest wants
        public static readonly Dictionary<string, QuestProgress> Progress = new Dictionary<string, QuestProgress>();

        private static readonly Dictionary<string, int> Counters = new Dictionary<string, int>();

        private static float _nextRefresh;

        public static void Refresh(GameWorld world, float now)
        {
            if (now < _nextRefresh) return;

            // this used to be 30 seconds. quest counters move the instant you pick something
            // up, so the labels spent half a minute lying about what you still needed.
            _nextRefresh = now + 1f;

            Quest.Clear();
            Wishlist.Clear();
            Zones.Clear();
            ZoneNames.Clear();
            Progress.Clear();
            Counters.Clear();

            try
            {
                var player = world != null ? world.MainPlayer : null;
                var profile = player != null ? player.Profile : null;
                if (profile == null) return;

                BuildWishlist(profile);
                BuildCounters(profile);
                BuildQuests(profile);
            }
            catch
            {
                // profile swapping between raids, try again next second
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

        private static void BuildCounters(Profile profile)
        {
            try
            {
                var counters = profile.TaskConditionCounters;
                if (counters == null) return;

                var enumerator = counters.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var id = enumerator.Current.Key.ToString();
                    if (string.IsNullOrEmpty(id)) continue;

                    Counters[id] = enumerator.Current.Value.Value;
                }
            }
            catch
            {
                // counters not synced yet
            }
        }

        // true when this condition still wants something from you. no counter yet means it has
        // not been credited, so the marker stays. a required value of zero or less is not a
        // progress condition at all, so treat it as still relevant rather than hiding it.
        private static bool NeedMore(Condition condition)
        {
            try
            {
                var required = (int)condition.value;
                if (required <= 0) return true;

                var id = condition.id.ToString();
                if (!Counters.TryGetValue(id, out var current)) return true;

                return current < required;
            }
            catch
            {
                return true;
            }
        }

        // zone ids come from three different condition types, so keep the bookkeeping in one
        // place, and remember which quest asked for the zone while we are here
        private static void AddZone(string id, string questName)
        {
            if (string.IsNullOrEmpty(id)) return;

            Zones.Add(id);

            if (string.IsNullOrEmpty(questName)) return;
            if (!ZoneNames.ContainsKey(id)) ZoneNames[id] = questName;
        }

        // quest.name is the already resolved display name. template.name is the fallback for
        // the odd quest that has not resolved yet.
        private static string QuestName(Quest quest)
        {
            try
            {
                var name = quest.Name;
                if (!string.IsNullOrEmpty(name)) return name;

                var template = quest.Template;
                return template != null ? template.Name : null;
            }
            catch
            {
                return null;
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

                    var questName = QuestName(quest);

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
                            var conditionId = condition.id.ToString();
                            Counters.TryGetValue(conditionId, out var current);
                            var required = (int)condition.value;

                            // once you have enough, stop pointing at more of them. the config
                            // switch is there for the people who like the full shopping list.
                            var needed = current < required;

                            if (targets != null)
                            {
                                for (var t = 0; t < targets.Length; t++)
                                {
                                    var id = targets[t];
                                    if (string.IsNullOrEmpty(id)) continue;

                                    if (!Plugin.EspQuestNeededOnly.Value || needed) Quest.Add(id);

                                    // keep the larger requirement when several quests want the same item
                                    if (!Progress.TryGetValue(id, out var existing) || required > existing.Required)
                                    {
                                        Progress[id] = new QuestProgress { Current = current, Required = required };
                                    }
                                }
                            }
                        }

                        // zone conditions, including place-beacon, carry the world trigger ids
                        var zone = condition.TryCast<ConditionZone>();
                        if (zone != null && NeedMore(condition))
                        {
                            var zoneIds = zone.zoneIds;
                            if (zoneIds != null)
                            {
                                for (var z = 0; z < zoneIds.Length; z++) AddZone(zoneIds[z], questName);
                            }
                        }

                        // stay-in-zone conditions do not derive from ConditionZone
                        var inZone = condition.TryCast<ConditionInZone>();
                        if (inZone != null && NeedMore(condition))
                        {
                            var zoneIds = inZone.zoneIds;
                            if (zoneIds != null)
                            {
                                for (var z = 0; z < zoneIds.Length; z++) AddZone(zoneIds[z], questName);
                            }
                        }

                        // visit-a-place conditions carry one target id
                        var visit = condition.TryCast<ConditionVisitPlace>();
                        if (visit != null && NeedMore(condition))
                        {
                            AddZone(visit.target, questName);
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
