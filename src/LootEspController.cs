using System;
using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Vzor
{
    // screen-space markers for loot. deliberately not chams: material swapping every loose
    // round on streets is how you turn a 90 fps raid into a slideshow. dots and labels cost
    // nothing and work through walls, which is the entire point.
    public class LootEspController : MonoBehaviour
    {
        private enum Kind
        {
            Item,
            Container,
            Corpse,
            Exfil,
            Place,
            Grenade
        }

        // order is the draw order. higher draws later, so quest items and keys sit on top
        // of the pile instead of underneath forty bolts.
        private enum Category
        {
            Other,
            Mods,
            Gear,
            Food,
            Ammo,
            Barter,
            Medical,
            Weapon,
            Key,
            Quest,
            Wishlist
        }

        private struct ItemInfo
        {
            public string Label;
            public int Value;
            public Category Category;
        }

        private struct Marker
        {
            public Vector3 World;
            public string Label;
            public Kind Kind;
            public float Distance;
            public int Value;
            public Transform Source;
            public Category Category;
            public float DotSize;
        }

        private struct ScreenMarker
        {
            public float X;
            public float Y;
            public string Label;
            public Color Color;
            public float Distance;
            public int Priority;
            public bool AlwaysLabel;
            public float DotSize;
        }

        private readonly List<Marker> _markers = new List<Marker>();
        private readonly List<Marker> _grenadeMarkers = new List<Marker>();
        private readonly List<ScreenMarker> _screen = new List<ScreenMarker>();
        private readonly Dictionary<string, float> _labelWidths = new Dictionary<string, float>();
        private readonly List<Rect> _placedLabels = new List<Rect>();
        private readonly List<LootableContainer> _containers = new List<LootableContainer>();
        private readonly List<Vector3> _containerCenters = new List<Vector3>();
        private readonly List<string> _containerNames = new List<string>();
        private readonly List<int> _containerValues = new List<int>();
        private readonly List<int> _containerCounts = new List<int>();
        private readonly List<float> _containerContentsAt = new List<float>();
        private readonly HashSet<int> _seen = new HashSet<int>();

        private GameWorld _world;
        private Camera _camera;
        private Texture2D _dot;
        private GUIStyle _labelStyle;
        private int _labelStyleSize = -1;
        private bool _asciiOnly;

        private float _nextScan;
        private float _nextContainerScan;
        private int _rebuildCursor = -1;
        private int _rebuildPass;

        private System.IntPtr _containerWorld;
        private int _contentsBudget;
        private bool _active;
        private bool _loggedCamera;
        private bool _loggedScan;
        private bool _loggedContainers;

        private float _nextCameraPick;
        private int _scannedLoot;
        private int _valueFiltered;

        private void Start()
        {
            _active = Plugin.EspEnabled.Value;
        }

        private void Update()
        {
            // config is the only switch now. no hotkeys, no input polling.
            _active = Plugin.EspEnabled.Value;
            if (!_active) return;

            var now = Time.unscaledTime;

            // the container list fills a few hundred entries a frame instead of one long walk.
            // it used to be the single biggest stall in the mod on a loot heavy map.
            PumpContainerRebuild(now);

            if (now >= _nextScan)
            {
                _nextScan = now + 0.25f;

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                Scan(now);
                LogTiming("esp scan (" + _markers.Count + " markers, " + _containers.Count + " containers)",
                          started);
            }

            UpdateGrenades();
            Project();
        }

        private static void LogTiming(string label, long started)
        {
            var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                     System.Diagnostics.Stopwatch.Frequency;

            if (ms >= 10.0)
                Plugin.Logger.LogInfo("[timing] " + label + " " + ms.ToString("0.0") + " ms");
        }

        private void OnGUI()
        {
            if (!_active || _screen.Count == 0) return;

            EnsureGui();

            // eft leaves a scaled gui matrix active during ongui. without this, every label
            // gets squashed into a horizontal smear of dashes. found that out the hard way.
            var restoreMatrix = GUI.matrix;
            var restoreColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;

            var labels = Plugin.EspLabels.Value;
            var labelDistance = Plugin.EspLabelDistance.Value;
            var stack = Plugin.EspStackLabels.Value;
            var fontSize = (float)_labelStyle.fontSize;

            _placedLabels.Clear();

            // place in reverse priority order, so keys, quest items and containers get the
            // slot closest to the dot and the junk stacks above them instead of the other way round.
            for (var i = _screen.Count - 1; i >= 0; i--)
            {
                var m = _screen[i];

                var size = m.DotSize > 0f ? m.DotSize : 4f;
                GUI.color = m.Color;
                GUI.DrawTexture(new Rect(m.X - size * 0.5f, m.Y - size * 0.5f, size, size), _dot);

                if (!labels || string.IsNullOrEmpty(m.Label)) continue;
                if (!m.AlwaysLabel && m.Distance > labelDistance) continue;

                // measure once per unique label and cache it. CalcSize allocates a GUIContent,
                // and doing that sixty times a frame is how you get frame time spikes.
                if (!_labelWidths.TryGetValue(m.Label, out var width))
                {
                    width = _labelStyle.CalcSize(new GUIContent(m.Label)).x;
                    _labelWidths[m.Label] = width;
                }

                // pad the measured width. CalcSize rounds, GUI.Label clips, and the last
                // closing bracket pays for it. four pixels of insurance.
                width += fontSize * 0.6f;

                var rect = new Rect(m.X - width * 0.5f, m.Y - fontSize - 4f, width, fontSize + 6f);
                if (stack) rect = Deoverlap(rect, fontSize);
                _placedLabels.Add(rect);

                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), m.Label, _labelStyle);

                GUI.color = m.Color;
                GUI.Label(rect, m.Label, _labelStyle);
            }

            GUI.color = restoreColor;
            GUI.matrix = restoreMatrix;
        }

        
        
        
        
        // push a label upward until it stops colliding with one already placed. capped so a
        // pathological pile of loot can't walk a label off the top of the screen.
        private Rect Deoverlap(Rect rect, float fontSize)
        {
            var step = fontSize + 2f;

            for (var guard = 0; guard < 16; guard++)
            {
                var collision = false;
                for (var i = 0; i < _placedLabels.Count; i++)
                {
                    if (!rect.Overlaps(_placedLabels[i])) continue;
                    collision = true;
                    break;
                }

                if (!collision) return rect;
                rect.y -= step;
            }

            return rect;
        }

        private void EnsureGui()
        {
            if (_dot == null)
            {
                _dot = new Texture2D(1, 1);
                _dot.SetPixel(0, 0, Color.white);
                _dot.Apply();
            }

            var size = Plugin.EspFontSize.Value;
            if (_labelStyle != null && _labelStyleSize == size) return;

            _labelWidths.Clear();

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = size;
            _labelStyle.alignment = TextAnchor.MiddleCenter;
            _labelStyle.normal.textColor = Color.white;

            // the real cause of "labels are cut off horizontally". unity clips label text to
            // the rect by default, and any measurement error eats the last glyph. let it overflow.
            _labelStyle.wordWrap = false;
            _labelStyle.clipping = TextClipping.Overflow;

            // the stock unity font has no cyrillic or cjk glyphs, which turns russian bot
            // nicknames into a row of dashes. borrow a font from windows that does.
            try
            {
                var font = Font.CreateDynamicFontFromOSFont("Segoe UI", size);
                if (font != null)
                {
                    _labelStyle.font = font;
                    _asciiOnly = false;
                }
                else
                {
                    _asciiOnly = true;
                }
            }
            catch
            {
                _asciiOnly = true;
            }

            _labelStyleSize = size;
        }

        
        private void Scan(float now)
        {
            // world comes from the tracker, see worldtracker for why we stopped searching
            _world = WorldTracker.Current;
            if (_world == null)
            {
                _markers.Clear();
                return;
            }

            if (_camera == null || now >= _nextCameraPick)
            {
                _nextCameraPick = now + 5f;
                _camera = ViewCamera.Get();
            }

            if (_camera == null)
            {
                _markers.Clear();
                if (!_loggedCamera)
                {
                    _loggedCamera = true;
                    Plugin.Logger.LogWarning("loot esp: Camera.main is null, nothing will draw until it exists");
                }
                return;
            }

            var camPos = _camera.transform.position;
            var viewOnly = Plugin.EspViewOnly.Value;
            var planes = viewOnly ? GeometryUtility.CalculateFrustumPlanes(_camera) : null;
            var lootDistance = Plugin.EspLootDistance.Value;

            _markers.Clear();
            _seen.Clear();
            _scannedLoot = 0;
            _valueFiltered = 0;
            _contentsBudget = 4;

            ItemTargets.Refresh(_world, now);

            if (Plugin.EspItems.Value || Plugin.EspCorpses.Value)
                ScanLoot(camPos, planes, viewOnly, lootDistance);
            if (Plugin.EspContainers.Value)
                ScanContainers(camPos, planes, viewOnly, lootDistance, now);
            if (Plugin.EspCorpses.Value)
                ScanObservedCorpses(camPos, planes, viewOnly, lootDistance);
            if (Plugin.EspShowExfils.Value)
                ScanExfils(camPos);
            if (Plugin.EspShowZones.Value)
                ScanQuestZones(camPos, now);

            if (!_loggedScan)
            {
                _loggedScan = true;

                var items = 0;
                var containers = 0;
                var corpses = 0;
                for (var i = 0; i < _markers.Count; i++)
                {
                    if (_markers[i].Kind == Kind.Item) items++;
                    else if (_markers[i].Kind == Kind.Container) containers++;
                    else corpses++;
                }

                Plugin.Logger.LogInfo("loot esp first scan: " + _markers.Count + " markers (" + items + " items, " +
                                      containers + " containers, " + corpses + " corpses), " +
                                      (viewOnly ? "in-view, normal loot within " + lootDistance + "m" : "sphere mode") +
                                      ", loot entries seen " + _scannedLoot + ", filtered by value " + _valueFiltered +
                                      ", min value " + Plugin.EspMinValue.Value);

                // sample a few labels so we can see what the localiser actually returns
                var samples = 0;
                for (var i = 0; i < _markers.Count && samples < 10; i++)
                {
                    var marker = _markers[i];
                    samples++;
                    Plugin.Logger.LogInfo("  sample " + marker.Kind + ": '" + marker.Label + "' value=" + marker.Value +
                                          " dist=" + Mathf.Round(marker.Distance) + "m");
                }
            }
        }

        private void ScanLoot(Vector3 camPos, Il2CppStructArray<Plane> planes, bool viewOnly, float lootDistance)
        {
            var loot = _world.LootItems;
            if (loot == null) return;

            var list = loot._iteration;
            if (list == null) return;

            var occlusion = Plugin.EspOcclusion.Value;

            for (var i = 0; i < list.Count; i++)
            {
                try
                {
                    var entry = list[i];
                    if (entry == null) continue;

                    // distance check on the pivot first, it is far cheaper than reading bounds
                    var pivot = entry.transform.position;
                    var pivotDistance = Vector3.Distance(camPos, pivot);
                    if (!_seen.Add(entry.GetInstanceID())) continue;

                    var corpse = entry.TryCast<Corpse>();
                    if (corpse != null)
                    {
                        if (!Plugin.EspCorpses.Value) continue;
                        if (!IsCandidate(pivot, pivotDistance, lootDistance, false, planes, viewOnly)) continue;

                        var center = VisualCenter(entry);
                        if (!IsSane(center)) continue;
                        if (occlusion && !Visibility.HasLineOfSight(camPos, center, entry.transform)) continue;

                        _markers.Add(new Marker
                        {
                            World = center,
                            Label = CorpseLabel(corpse),
                            Kind = Kind.Corpse,
                            Distance = pivotDistance,
                            Source = entry.transform
                        });
                        continue;
                    }

                    if (!Plugin.EspItems.Value) continue;

                    var label = ItemLabel(entry, out var value, out var category);
                    _scannedLoot++;

                    if (!IsCategoryEnabled(category)) continue;

                    if (Plugin.EspQuestOnly.Value && category != Category.Quest && category != Category.Wishlist)
                        continue;

                    if (Plugin.EspMinValue.Value > 0 && value < Plugin.EspMinValue.Value)
                    {
                        _valueFiltered++;
                        continue;
                    }

                    if (Plugin.EspHighValueOnly.Value && value < Plugin.EspHighValueThreshold.Value)
                    {
                        _valueFiltered++;
                        continue;
                    }

                    // quest items get their own range instead of ignoring the limit. everything
                    // else keeps the old behaviour: high value and wishlist are unlimited.
                    var limit = lootDistance;
                    var unlimited = false;

                    if (category == Category.Quest)
                    {
                        limit = Plugin.EspQuestDistance.Value;
                    }
                    else if (value > 0 && value >= Plugin.EspHighValueThreshold.Value)
                    {
                        unlimited = true;
                    }
                    else if (category == Category.Wishlist)
                    {
                        unlimited = true;
                    }

                    if (!IsCandidate(pivot, pivotDistance, limit, unlimited, planes, viewOnly)) continue;

                    var itemCenter = VisualCenter(entry);
                    if (!IsSane(itemCenter)) continue;
                    if (occlusion && !Visibility.HasLineOfSight(camPos, itemCenter, entry.transform)) continue;

                    if (Plugin.EspShowQuestProgress.Value &&
                        ItemTargets.Progress.TryGetValue(entry.TemplateId, out var progress) &&
                        progress.Required > 0)
                    {
                        label += " [" + progress.Current + "/" + progress.Required + "]";
                    }

                    if (Plugin.EspShowValue.Value && value > 0) label += " [" + value / 1000 + "K]";

                    label = Shorten(label, Plugin.EspLabelLength.Value);

                    _markers.Add(new Marker
                    {
                        World = itemCenter,
                        Label = label,
                        Kind = Kind.Item,
                        Distance = pivotDistance,
                        Value = value,
                        Source = entry.transform,
                        Category = category
                    });
                }
                catch
                {
                    // loot despawned mid-scan, normal
                }
            }
        }

        private void ScanContainers(Vector3 camPos, Il2CppStructArray<Plane> planes, bool viewOnly,
            float lootDistance, float now)
        {
            for (var i = 0; i < _containers.Count && i < _containerCenters.Count; i++)
            {
                try
                {
                    if (_containers[i] == null) continue;
                    var position = _containerCenters[i];
                    var distance = Vector3.Distance(camPos, position);

                    // the interaction state is where the search timer lives. zero it while the
                    // container is close, because opening one rebuilds the duration.
                    if (Plugin.QolInstantSearch.Value && distance <= 15f)
                    {
                        try
                        {
                            var interaction = _containers[i]._interaction;
                            if (interaction != null)
                            {
                                interaction.Duration = 0.01f;
                                interaction.EndTime = 0f;
                            }
                        }
                        catch
                        {
                            // interaction not ready
                        }
                    }

                    if (!IsCandidate(position, distance, lootDistance, false, planes, viewOnly)) continue;
                    if (Plugin.EspOcclusion.Value && !Visibility.HasLineOfSight(camPos, position, _containers[i].transform)) continue;

                    // contents are read lazily, only when the container is close, and refreshed
                    // every 10 seconds so it tracks what you have already taken out of it. the
                    // budget keeps a rebuild from reading every nearby container in one frame.
                    if (Plugin.EspScanContents.Value && _contentsBudget > 0 && now >= _containerContentsAt[i] &&
                        distance <= Plugin.EspContentsDistance.Value)
                    {
                        _contentsBudget--;
                        if (Plugin.EspUseLivePrices.Value) PriceDatabase.EnsureLoaded(Plugin.EspPriceFile.Value);

                        ComputeContents(_containers[i], out var count, out var value);
                        _containerValues[i] = value;
                        _containerCounts[i] = count;
                        _containerContentsAt[i] = now + 10f;
                        _containerNames[i] = BuildContainerName(_containers[i], count, value);
                    }

                    if (Plugin.EspHighValueOnly.Value && Plugin.EspScanContents.Value &&
                        _containerValues[i] >= 0 && _containerValues[i] < Plugin.EspHighValueThreshold.Value) continue;

                    _markers.Add(new Marker
                    {
                        World = position,
                        Label = _containerNames[i],
                        Kind = Kind.Container,
                        Distance = distance
                    });
                }
                catch
                {
                    // container gone, normal
                }
            }
        }

        // the container list used to be rebuilt in one go every ten seconds, on the main thread,
        // walking every interactable on the map. on interchange that was half a second. now the
        // walk is a few hundred entries per frame: the list fills in a second or two, the cost per
        // frame is under a millisecond, and nobody sees it.
        private void PumpContainerRebuild(float now)
        {
            if (!Plugin.EspContainers.Value) return;

            if (_world != null && _world.Pointer != _containerWorld)
            {
                _containerWorld = _world.Pointer;
                _rebuildPass = 0;
                _nextContainerScan = 0f;
                _rebuildCursor = -1;
                _loggedContainers = false;
            }

            if (now >= _nextContainerScan)
            {
                // the first few passes catch containers that spawn late, then they are all here
                // and a rebuild every ten seconds is just a hitch with a rhythm.
                _rebuildPass++;
                _nextContainerScan = now + (_rebuildPass <= 3 ? 10f : 60f);
                _rebuildCursor = 0;

                _containers.Clear();
                _containerCenters.Clear();
                _containerNames.Clear();
                _containerValues.Clear();
                _containerCounts.Clear();
                _containerContentsAt.Clear();
            }

            if (_rebuildCursor < 0) return;

            var source = _world != null && _world.World != null
                ? _world.World._interactableObjectsForNetSync
                : null;

            // some scenes expose no registry at all. that is exactly what the scene scan fallback
            // is for, and it has to run when the walk has nothing to walk, not only when the walk
            // finishes empty. the hideout is one of those scenes.
            if (source == null || source.Length == 0)
            {
                _rebuildCursor = -1;
                FinishContainerPass(true);
                return;
            }

            var budget = 200;

            while (budget-- > 0 && _rebuildCursor < source.Length)
            {
                try
                {
                    var entry = source[_rebuildCursor];
                    if (entry != null)
                    {
                        var container = entry.TryCast<LootableContainer>();
                        if (container != null) AddContainer(container);
                    }
                }
                catch
                {
                    // interactive object vanished
                }

                _rebuildCursor++;
            }

            if (_rebuildCursor < source.Length) return;

            _rebuildCursor = -1;
            FinishContainerPass(false);
        }

        private void FinishContainerPass(bool sceneScan)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();

            // the registry came back empty, so fall back to the scene. it costs a sweep, but this
            // happens at most once a minute and only on maps that have no registry.
            if (_containers.Count == 0)
            {
                var found = UnityEngine.Object.FindObjectsOfType<LootableContainer>();
                if (found != null)
                    for (var i = 0; i < found.Length; i++) AddContainer(found[i]);
            }

            LogTiming("container scene scan", started);

            if (!_loggedContainers)
            {
                _loggedContainers = true;
                Plugin.Logger.LogInfo("loot esp: tracking " + _containers.Count + " containers" +
                                      (sceneScan ? " (no registry, scene scan)" : string.Empty));
            }
        }

        private void AddContainer(LootableContainer container)
        {
            if (container == null) return;

            var center = ContainerCenter(container);
            if (!IsSane(center)) return;

            _containers.Add(container);
            _containerCenters.Add(center);
            _containerNames.Add(Shorten(ContainerLabel(container), Plugin.EspLabelLength.Value));
            _containerValues.Add(-1);
            _containerCounts.Add(-1);
            _containerContentsAt.Add(0f);
        }

        private string BuildContainerName(LootableContainer container, int count, int value)
        {
            var name = ContainerLabel(container);
            if (Plugin.EspScanContents.Value && count > 0) name += " [" + count + " items, " + value / 1000 + "K]";
            return Shorten(name, Plugin.EspLabelLength.Value);
        }

        // walk the container's inventory without opening it. the client already has the
        // contents, which is why this works at all.
        private static void ComputeContents(LootableContainer container, out int count, out int value)
        {
            count = 0;
            value = 0;

            try
            {
                var owner = container.ItemOwner;
                if (owner == null) return;

                var root = owner.RootItem;
                if (root == null) return;

                var items = EFT.InventoryLogic.ItemExtensions.GetAllItems(root);
                if (items == null) return;

                var enumerator = items.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var child = enumerator.Current;
                    if (child == null) continue;

                    count++;

                    if (PriceDatabase.TryGet(child.StringTemplateId, out var price))
                    {
                        value += price;
                        continue;
                    }

                    try
                    {
                        var template = child.Template;
                        if (template != null) value += template.CreditsPrice;
                    }
                    catch
                    {
                        // template not ready
                    }
                }
            }
            catch
            {
                // inventory not readable yet
            }
        }

        private void ScanExfils(Vector3 camPos)
        {
            try
            {
                var controller = _world.ExfiltrationController;
                if (controller == null) return;

                var points = controller.ExfiltrationPoints;
                if (points == null) return;

                var maxDistance = Plugin.EspExfilDistance.Value;

                for (var i = 0; i < points.Length; i++)
                {
                    var point = points[i];
                    if (point == null) continue;

                    var settings = point.Settings;
                    if (settings == null) continue;

                    var name = settings.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    var status = point.Status;
                    if (status == EExfiltrationStatus.NotPresent || status == EExfiltrationStatus.Hidden) continue;

                    var position = point.transform.position + Vector3.up * 1.2f;
                    var distance = Vector3.Distance(camPos, position);
                    if (distance > maxDistance) continue;

                    var open = status == EExfiltrationStatus.RegularMode || status == EExfiltrationStatus.Countdown;

                    _markers.Add(new Marker
                    {
                        World = position,
                        Label = Shorten(name, Plugin.EspLabelLength.Value),
                        Kind = Kind.Exfil,
                        Distance = distance,
                        Value = open ? 1 : 0,
                        Source = point.transform
                    });
                }
            }
            catch
            {
                // exfil controller not ready on this map
            }
        }

        private void ScanQuestZones(Vector3 camPos, float now)
        {
            // only bother if an active quest actually names a zone on this map
            if (ItemTargets.Zones.Count == 0) return;

            // one shared scan for the whole plugin. it runs a handful of times at raid start and
            // then stops, instead of two findobjectsoftype sweeps every ten seconds per feature.
            SceneTriggers.Ensure(_world, now);

            var maxDistance = Plugin.EspZoneDistance.Value;

            AddZoneMarkers(SceneTriggers.Place, camPos, maxDistance, "Quest zone");
            AddZoneMarkers(SceneTriggers.Visit, camPos, maxDistance, "Visit zone");
        }

        private void AddZoneMarkers(List<SceneTriggers.Entry> triggers, Vector3 camPos, float maxDistance,
            string fallback)
        {
            for (var i = 0; i < triggers.Count; i++)
            {
                try
                {
                    var entry = triggers[i];
                    if (entry == null || !ItemTargets.Zones.Contains(entry.Id)) continue;

                    var position = entry.Position + Vector3.up * 1.2f;
                    var distance = Vector3.Distance(camPos, position);
                    if (distance > maxDistance) continue;

                    _markers.Add(new Marker
                    {
                        World = position,
                        Label = Shorten(ZoneLabel(entry.Id, entry.Description, fallback),
                                        Plugin.EspLabelLength.Value),
                        Kind = Kind.Place,
                        Distance = distance,
                        Source = entry.Transform
                    });
                }
                catch
                {
                    // trigger vanished
                }
            }
        }

        // grenades move fast and the loot scan is 4hz, which looked like a slideshow.
        // they get their own list, refreshed every frame, and drawn bigger.
        private void UpdateGrenades()
        {
            _grenadeMarkers.Clear();

            if (!Plugin.EspShowGrenades.Value || _world == null) return;

            var camera = _camera != null ? _camera : ViewCamera.Get();
            if (camera == null) return;

            ScanGrenades(camera.transform.position);
        }

        // bsg leaks raw locale keys through trigger descriptions, which is where the
        // "TriggerWithID" tags came from. the quest name is a better label anyway, so prefer
        // it and only fall back to the trigger text when it looks like prose rather than a key.
        private static string ZoneLabel(string id, string description, string fallback)
        {
            if (ItemTargets.ZoneNames.TryGetValue(id, out var questName) && !string.IsNullOrEmpty(questName))
                return questName;

            if (!string.IsNullOrEmpty(description) &&
                description.IndexOf("TriggerWith", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return description;
            }

            return fallback;
        }

        private void ScanGrenades(Vector3 camPos)
        {
            try
            {
                var grenades = _world.Grenades;
                if (grenades == null) return;

                var list = grenades._iteration;
                if (list == null) return;

                var maxDistance = Plugin.EspGrenadeDistance.Value;

                for (var i = 0; i < list.Count; i++)
                {
                    var grenade = list[i];
                    if (grenade == null) continue;
                    if (grenade._isDestroyed) continue;

                    var position = grenade.transform.position + Vector3.up * 0.15f;
                    var distance = Vector3.Distance(camPos, position);
                    if (distance > maxDistance) continue;

                    _grenadeMarkers.Add(new Marker
                    {
                        World = position,
                        Label = string.Empty,
                        Kind = Kind.Grenade,
                        Distance = distance,
                        Source = grenade.transform,
                        DotSize = 16f
                    });
                }
            }
            catch
            {
                // grenade list not ready
            }
        }

        private void ScanObservedCorpses(Vector3 camPos, Il2CppStructArray<Plane> planes, bool viewOnly,
            float lootDistance)
        {
            var corpses = _world.ObservedPlayersCorpses;
            if (corpses == null) return;

            var enumerator = corpses.GetEnumerator();
            while (enumerator.MoveNext())
            {
                try
                {
                    var corpse = enumerator.Current.Value;
                    if (corpse == null) continue;
                    if (!_seen.Add(corpse.GetInstanceID())) continue;

                    var position = VisualCenter(corpse);
                    var distance = Vector3.Distance(camPos, position);
                    if (!IsCandidate(position, distance, lootDistance, false, planes, viewOnly)) continue;
                    if (!IsSane(position)) continue;
                    if (Plugin.EspOcclusion.Value && !Visibility.HasLineOfSight(camPos, position, corpse.transform)) continue;

                    _markers.Add(new Marker
                    {
                        World = position,
                        Label = CorpseLabel(corpse),
                        Kind = Kind.Corpse,
                        Distance = distance
                    });
                }
                catch
                {
                    // corpse despawned mid-scan
                }
            }
        }

        // in-view mode: frustum test on the point, plus an optional hard ceiling.
        // sphere mode: the old behaviour, distance only.
        // unlimited means high value, quest or wishlist: distance is ignored, the frustum still applies.
        private static bool IsCandidate(Vector3 point, float distance, float limit, bool unlimited,
            Il2CppStructArray<Plane> planes, bool viewOnly)
        {
            if (!unlimited && distance > limit) return false;
            if (!viewOnly || planes == null) return true;

            for (var i = 0; i < planes.Length; i++)
            {
                var plane = planes[i];
                if (Vector3.Dot(plane.normal, point) + plane.distance < 0f) return false;
            }

            return true;
        }

        // transforms occasionally hand back garbage or sit at world origin before the object
        // is initialised. drawing a marker at 0,0,0 is how you get a dot in the floor.


        private static bool IsSane(Vector3 position)
        {
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z)) return false;
            if (float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z)) return false;
            return position.sqrMagnitude > 0.01f;
        }

        private static Vector3 VisualCenter(LootItem item)
        {
            // the _renderers list is not always populated for static ground loot. when it is
            // empty, walk the children instead of trusting the pivot, which sits on the floor.
            var center = UnionBounds(item._renderers);
            if (center.HasValue) return center.Value;

            try
            {
                var children = item.GetComponentsInChildren<Renderer>(true);
                if (children != null)
                {
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    var found = false;

                    for (var i = 0; i < children.Length; i++)
                    {
                        var renderer = children[i];
                        if (renderer == null) continue;

                        var bounds = renderer.bounds;
                        if (bounds.size.sqrMagnitude <= 0f) continue;

                        min = Vector3.Min(min, bounds.min);
                        max = Vector3.Max(max, bounds.max);
                        found = true;
                    }

                    if (found) return (min + max) * 0.5f;
                }
            }
            catch
            {
                // no children to measure
            }

            // last resort before the floor: the bound collider, which the game uses for pickup
            try
            {
                var box = item._boundCollider;
                if (box != null)
                {
                    var bounds = box.bounds;
                    if (bounds.size.sqrMagnitude > 0f) return bounds.center;
                }
            }
            catch
            {
                // collider not ready
            }

            return item.transform.position + Vector3.up * 0.15f;
        }

        private static Vector3? UnionBounds(Il2CppSystem.Collections.Generic.List<Renderer> renderers)
        {
            if (renderers == null || renderers.Count == 0) return null;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var found = false;
            var limit = Math.Min(renderers.Count, 8);

            for (var i = 0; i < limit; i++)
            {
                Renderer renderer = null;
                try
                {
                    renderer = renderers[i];
                }
                catch
                {
                    continue;
                }

                if (renderer == null) continue;

                var bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude <= 0f) continue;

                min = Vector3.Min(min, bounds.min);
                max = Vector3.Max(max, bounds.max);
                found = true;
            }

            return found ? (min + max) * 0.5f : (Vector3?)null;
        }

        private static Vector3 ContainerCenter(LootableContainer container)
        {
            // the visual mesh is often not parented to the container, so child renderers and
            // the pivot both lie. the interaction collider is the thing you actually aim at.
            try
            {
                var collider = container.Collider;
                if (collider != null)
                {
                    var bounds = collider.bounds;
                    if (bounds.size.sqrMagnitude > 0f) return bounds.center;
                }
            }
            catch
            {
                // collider not ready
            }

            // then every renderer under it, inactive included
            try
            {
                var renderers = container.GetComponentsInChildren<Renderer>(true);
                if (renderers != null && renderers.Length > 0)
                {
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    var found = false;

                    for (var i = 0; i < renderers.Length; i++)
                    {
                        var renderer = renderers[i];
                        if (renderer == null) continue;

                        var bounds = renderer.bounds;
                        if (bounds.size.sqrMagnitude <= 0f) continue;

                        min = Vector3.Min(min, bounds.min);
                        max = Vector3.Max(max, bounds.max);
                        found = true;
                    }

                    if (found) return (min + max) * 0.5f;
                }
            }
            catch
            {
                // fall through
            }

            return container.transform.position + Vector3.up * 0.4f;
        }

        private static string ItemLabel(LootItem item, out int value, out Category category)
        {
            var info = DescribeItem(item);
            value = info.Value;
            category = info.Category;
            return info.Label;
        }

        private static ItemInfo DescribeItem(LootItem item)
        {
            var info = new ItemInfo { Category = Category.Other };
            var templateId = item.TemplateId;
            EFT.InventoryLogic.Item root = null;

            try
            {
                var owner = item.ItemOwner;
                if (owner != null) root = owner.RootItem;
            }
            catch
            {
                // owner not ready
            }

            var templatePrice = 0;

            if (root != null)
            {
                try
                {
                    var template = root.Template;
                    if (template != null)
                    {
                        templatePrice = template.CreditsPrice;

                        // template.questitem is bsg's "this item belongs to some quest" flag,
                        // not "belongs to a quest you are on". it was painting half the map as
                        // quest loot for quests nobody had accepted, so the category is decided
                        // further down from the active quest conditions instead.
                    }
                }
                catch
                {
                    // template not loaded yet
                }

                try
                {
                    info.Category = ToCategory(ItemCategories.Of(root));
                }
                catch
                {
                    // template chain not ready, leave as other
                }
            }

            // flea db first, template base price as the fallback
            if (Plugin.EspUseLivePrices.Value)
            {
                PriceDatabase.EnsureLoaded(Plugin.EspPriceFile.Value);
                if (PriceDatabase.TryGet(templateId, out var marketPrice)) info.Value = marketPrice;
                else info.Value = templatePrice;
            }
            else
            {
                info.Value = templatePrice;
            }

            info.Label = ResolveName(templateId, root, item.Name);

            // wishlist wins over quest, since you put it there on purpose
            if (!string.IsNullOrEmpty(templateId))
            {
                if (Plugin.EspShowWishlist.Value && ItemTargets.Wishlist.Contains(templateId))
                    info.Category = Category.Wishlist;
                else if (ItemTargets.Quest.Contains(templateId))
                    info.Category = Category.Quest;
            }

            return info;
        }

        // the categories come from the template tree now, this is just the enum mapping
        private static Category ToCategory(string category)
        {
            switch (category)
            {
                case "weapon": return Category.Weapon;
                case "ammo": return Category.Ammo;
                case "medical": return Category.Medical;
                case "food": return Category.Food;
                case "key": return Category.Key;
                case "gear": return Category.Gear;
                case "mod": return Category.Mods;
                case "barter": return Category.Barter;
                default: return Category.Other;
            }
        }

        private static bool Has(string text, string needle)
        {
            return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // the locale lookup returns the key itself on a miss, which is how "<templateId> Name"
        // ends up on screen looking like a cheat from 2009. compare against the key, then fall
        // back through the item object, then the raw name.
        internal static string ResolveName(string templateId, EFT.InventoryLogic.Item root, string rawName)
        {
            if (!string.IsNullOrEmpty(templateId))
            {
                var nameKey = templateId + " Name";
                var localized = EFT.LocalizationExtensions.Localized(nameKey, string.Empty);
                if (!string.IsNullOrEmpty(localized) && localized != nameKey && IsUsable(localized, templateId))
                    return localized;

                var shortKey = templateId + " ShortName";
                localized = EFT.LocalizationExtensions.Localized(shortKey, string.Empty);
                if (!string.IsNullOrEmpty(localized) && localized != shortKey && IsUsable(localized, templateId))
                    return localized;
            }

            if (root != null)
            {
                try
                {
                    var localized = EFT.LocalizationExtensions.LocalizedShortName(root);
                    if (!string.IsNullOrEmpty(localized) && IsUsable(localized, templateId)) return localized;
                }
                catch
                {
                    // localiser unhappy, keep going
                }
            }

            var cleaned = CleanName(rawName);
            if (IsUsable(cleaned, templateId)) return cleaned;

            if (!string.IsNullOrEmpty(templateId))
                return templateId.Length > 8 ? templateId.Substring(0, 8) : templateId;

            return "?";
        }

        private static string ContainerLabel(LootableContainer container)
        {
            // static containers carry the template id directly, and the locale key is
            // "<templateId> Name". this is what the framework does and it works.
            try
            {
                var template = container.Template;
                if (!string.IsNullOrEmpty(template))
                {
                    var key = template + " Name";
                    var localized = EFT.LocalizationExtensions.Localized(key, string.Empty);
                    if (!string.IsNullOrEmpty(localized) && localized != key && IsUsable(localized, template))
                        return localized;
                }
            }
            catch
            {
                // localiser not ready
            }

            try
            {
                var owner = container.ItemOwner;
                if (owner != null)
                {
                    var root = owner.RootItem;
                    if (root != null)
                    {
                        var localized = EFT.LocalizationExtensions.LocalizedShortName(root);
                        if (!string.IsNullOrEmpty(localized) && IsUsable(localized, null)) return localized;
                    }
                }
            }
            catch
            {
                // fall through
            }

            var cleaned = CleanName(container.name);
            return IsUsable(cleaned, null) ? cleaned : "Container";
        }

        private static bool IsUsable(string text, string templateId)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var trimmed = text.Trim();
            if (trimmed == "ShortName" || trimmed == "Name") return false;
            if (!string.IsNullOrEmpty(templateId) && trimmed == templateId) return false;
            if (trimmed.Length >= 24 && LooksLikeHexId(trimmed)) return false;

            return true;
        }

        private static bool LooksLikeHexId(string text)
        {
            if (text.Length < 24) return false;

            for (var i = 0; i < 24; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }

            return true;
        }

        // eft occasionally hands back "5fa9a8918b8774e7b3d8b1ed Shrimp". nobody asked for that.
        private static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var text = name.Trim();
            if (text.Length > 25 && text[24] == ' ' && LooksLikeHexId(text))
                text = text.Substring(25).Trim();

            // localisation keys end in " ShortName" or " Name". strip the suffix so a failed
            // lookup reads as something instead of the literal word.
            if (text.EndsWith(" ShortName", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 10).Trim();
            else if (text.EndsWith(" Name", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 5).Trim();

            return text;
        }

        private string CorpseLabel(Corpse corpse)
        {
            var profileId = corpse.PlayerProfileID;
            if (!string.IsNullOrEmpty(profileId))
            {
                try
                {
                    var players = _world._allPlayersEverExistedByProfileId;
                    if (players != null && players.ContainsKey(profileId))
                    {
                        var player = players[profileId];
                        if (player != null && player.Profile != null)
                        {
                            var nickname = player.Profile.Nickname;
                            if (!string.IsNullOrEmpty(nickname))
                            {
                                var safe = AsciiSafe(nickname);
                                if (!string.IsNullOrEmpty(safe)) return safe;
                            }
                        }
                    }
                }
                catch
                {
                    // profile cache miss, fall through to the side label
                }
            }

            return "Corpse (" + corpse.Side + ")";
        }

        private void Project()
        {
            _screen.Clear();

            if (_camera == null) return;

            ProjectList(_markers);
            ProjectList(_grenadeMarkers);

            // draw low priority first so keys and quest items end up on top of the pile
            _screen.Sort(ScreenOrder);
        }

        private void ProjectList(List<Marker> markers)
        {
            var height = Screen.height;
            var width = Screen.width;

            // if the camera renders into a sub-rect or a scaled buffer, world-to-screen
            // coordinates need remapping onto the gui surface. usually it is identity.
            var rect = _camera.pixelRect;
            var scaleX = width / Mathf.Max(1f, rect.width);
            var scaleY = height / Mathf.Max(1f, rect.height);

            for (var i = 0; i < markers.Count; i++)
            {
                var marker = markers[i];
                var screen = _camera.WorldToScreenPoint(marker.World);
                if (screen.z <= 0f) continue;

                var x = (screen.x - rect.x) * scaleX;
                var y = height - (screen.y - rect.y) * scaleY;
                if (x < -40f || x > width + 40f || y < -40f || y > height + 40f) continue;

                _screen.Add(new ScreenMarker
                {
                    X = x,
                    Y = y,
                    Label = marker.Label,
                    Color = ColorFor(marker.Kind, marker.Category, marker.Value),
                    Distance = marker.Distance,
                    DotSize = marker.DotSize,
                    Priority = marker.Kind == Kind.Container ? 100 :
                        marker.Kind == Kind.Corpse ? 101 :
                        marker.Kind == Kind.Exfil ? 90 :
                        marker.Kind == Kind.Place ? 95 :
                        marker.Kind == Kind.Grenade ? 97 : (int)marker.Category,
                    // exfils, grenades and anything quest related never lose their text
                    AlwaysLabel = marker.Kind == Kind.Exfil ||
                                  marker.Kind == Kind.Place ||
                                  marker.Kind == Kind.Grenade ||
                                  marker.Category == Category.Quest ||
                                  marker.Category == Category.Wishlist
                });
            }
        }

        private static readonly Comparison<ScreenMarker> ScreenOrder =
            (a, b) => a.Priority.CompareTo(b.Priority);

        private static Color ColorFor(Kind kind, Category category, int value)
        {
            switch (kind)
            {
                case Kind.Exfil:
                    return value > 0
                        ? ColorUtil.Parse(Plugin.EspExfilColor.Value, new Color(0f, 0.9f, 1f))
                        : ColorUtil.Parse(Plugin.EspExfilClosedColor.Value, new Color(1f, 0.33f, 0.33f));
                case Kind.Container:
                    return ColorUtil.Parse(Plugin.EspContainerColor.Value, new Color(1f, 0.65f, 0f));
                case Kind.Corpse:
                    return ColorUtil.Parse(Plugin.EspCorpseColor.Value, new Color(1f, 0f, 1f));
                case Kind.Place:
                    return ColorUtil.Parse(Plugin.EspZoneColor.Value, new Color(0.7f, 0.53f, 1f));
                case Kind.Grenade:
                    return ColorUtil.Parse(Plugin.EspGrenadeColor.Value, new Color(1f, 0.4f, 0f));
            }

            // a rare override, off by default now that categories carry the colour meaning
            if (Plugin.EspRareValue.Value > 0 && value >= Plugin.EspRareValue.Value)
                return ColorUtil.Parse(Plugin.EspRareColor.Value, new Color(1f, 0.84f, 0f));

            switch (category)
            {
                case Category.Weapon:
                    return ColorUtil.Parse(Plugin.EspWeaponColor.Value, new Color(1f, 0.35f, 0.35f));
                case Category.Ammo:
                    return ColorUtil.Parse(Plugin.EspAmmoColor.Value, new Color(0.9f, 0.9f, 0.5f));
                case Category.Medical:
                    return ColorUtil.Parse(Plugin.EspMedicalColor.Value, new Color(1f, 0.4f, 0.8f));
                case Category.Food:
                    return ColorUtil.Parse(Plugin.EspFoodColor.Value, new Color(0.6f, 0.9f, 0.4f));
                case Category.Key:
                    return ColorUtil.Parse(Plugin.EspKeyColor.Value, new Color(0.2f, 0.85f, 1f));
                case Category.Gear:
                    return ColorUtil.Parse(Plugin.EspGearColor.Value, new Color(0.75f, 0.75f, 0.75f));
                case Category.Barter:
                    return ColorUtil.Parse(Plugin.EspBarterColor.Value, new Color(1f, 0.75f, 0.2f));
                case Category.Mods:
                    return ColorUtil.Parse(Plugin.EspModsColor.Value, new Color(0.55f, 0.55f, 0.9f));
                case Category.Quest:
                    return ColorUtil.Parse(Plugin.EspQuestColor.Value, new Color(1f, 0.2f, 0.2f));
                case Category.Wishlist:
                    return ColorUtil.Parse(Plugin.EspWishlistColor.Value, new Color(0f, 1f, 0.66f));
                default:
                    return ColorUtil.Parse(Plugin.EspItemColor.Value, new Color(0f, 1f, 0.5f));
            }
        }

        private static bool IsCategoryEnabled(Category category)
        {
            switch (category)
            {
                case Category.Weapon: return Plugin.EspShowWeapons.Value;
                case Category.Ammo: return Plugin.EspShowAmmo.Value;
                case Category.Medical: return Plugin.EspShowMedical.Value;
                case Category.Food: return Plugin.EspShowFood.Value;
                case Category.Key: return Plugin.EspShowKeys.Value;
                case Category.Gear: return Plugin.EspShowGear.Value;
                case Category.Barter: return Plugin.EspShowBarter.Value;
                case Category.Mods: return Plugin.EspShowMods.Value;
                case Category.Quest: return Plugin.EspShowQuest.Value;
                default: return Plugin.EspShowOther.Value;
            }
        }

        // the default unity font has no cyrillic or cjk glyphs. if we managed to borrow a
        // windows font, only control characters get stripped. otherwise, ascii only.
        private string AsciiSafe(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var clean = true;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x20 || (_asciiOnly && c > 0x7E))
                {
                    clean = false;
                    break;
                }
            }

            if (clean) return text;

            var buffer = new char[text.Length];
            var length = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x20) continue;
                if (_asciiOnly && c > 0x7E) continue;
                buffer[length++] = c;
            }

            return length == 0 ? string.Empty : new string(buffer, 0, length).Trim();
        }

        private string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var safe = AsciiSafe(text);
            if (string.IsNullOrEmpty(safe)) return string.Empty;

            return safe.Length <= max ? safe : safe.Substring(0, max - 1) + "~";
        }
    }
}
