using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace Vzor
{
    // entry point. this only declares config and hands a component to unity.
    // the real work lives in ChamsController, because il2cpp games get very rude
    // if plugin code touches game objects before a scene exists.
    [BepInPlugin(Guid, "Vzor", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "local.vzor";
        public const string Version = "0.5.0";

        internal static ManualLogSource Logger;
        internal static ConfigFile File;

        internal static bool AimSettled;
        internal static Vector3 MagicBulletTarget;
        internal static bool MagicBulletHasTarget;
        internal static IntPtr LocalFirearmPointer;

        internal static IntPtr LocalStaminaPointer;
        internal static IntPtr LocalHandsStaminaPointer;
        internal static IntPtr LocalOxygenPointer;
        internal static IntPtr LocalPwaPointer;

        private static DateTime _configStamp;
        private static float _nextConfigCheck;
        private static float _suppressConfigWatch;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Shader;
        internal static ConfigEntry<string> VisibleColor;
        internal static ConfigEntry<string> OccludedColor;
        internal static ConfigEntry<bool> GearChams;
        internal static ConfigEntry<float> MaxDistance;
        internal static ConfigEntry<float> RefreshInterval;
        internal static ConfigEntry<bool> ChamsOcclusion;
        internal static ConfigEntry<bool> ChamsPerType;
        internal static ConfigEntry<string> ChamsUsecColor;
        internal static ConfigEntry<string> ChamsBearColor;
        internal static ConfigEntry<string> ChamsScavColor;
        internal static ConfigEntry<string> ChamsBossColor;
        internal static ConfigEntry<string> ChamsSpecialColor;
        internal static ConfigEntry<float> ChamsOccludedAlpha;
        internal static ConfigEntry<float> ChamsVisibleAlpha;

        internal static ConfigEntry<bool> EspEnabled;
        internal static ConfigEntry<float> EspLabelDistance;
        internal static ConfigEntry<float> EspLootDistance;
        internal static ConfigEntry<bool> EspViewOnly;
        internal static ConfigEntry<bool> EspOcclusion;
        internal static ConfigEntry<bool> EspHighValueOnly;
        internal static ConfigEntry<int> EspHighValueThreshold;
        internal static ConfigEntry<bool> EspScanContents;
        internal static ConfigEntry<float> EspContentsDistance;
        internal static ConfigEntry<int> EspMinValue;
        internal static ConfigEntry<int> EspRareValue;
        internal static ConfigEntry<bool> EspUseLivePrices;
        internal static ConfigEntry<string> EspPriceFile;
        internal static ConfigEntry<bool> EspShowValue;
        internal static ConfigEntry<string> EspRareColor;
        internal static ConfigEntry<bool> EspItems;
        internal static ConfigEntry<bool> EspContainers;
        internal static ConfigEntry<bool> EspCorpses;
        internal static ConfigEntry<bool> EspLabels;
        internal static ConfigEntry<bool> EspStackLabels;
        internal static ConfigEntry<bool> EspShowWeapons;
        internal static ConfigEntry<bool> EspShowAmmo;
        internal static ConfigEntry<bool> EspShowMedical;
        internal static ConfigEntry<bool> EspShowFood;
        internal static ConfigEntry<bool> EspShowKeys;
        internal static ConfigEntry<bool> EspShowGear;
        internal static ConfigEntry<bool> EspShowBarter;
        internal static ConfigEntry<bool> EspShowMods;
        internal static ConfigEntry<bool> EspShowQuest;
        internal static ConfigEntry<bool> EspShowQuestProgress;
        internal static ConfigEntry<bool> EspShowOther;
        internal static ConfigEntry<bool> EspShowExfils;
        internal static ConfigEntry<string> EspExfilColor;
        internal static ConfigEntry<string> EspExfilClosedColor;
        internal static ConfigEntry<float> EspExfilDistance;
        internal static ConfigEntry<bool> EspShowWishlist;
        internal static ConfigEntry<string> EspWishlistColor;
        internal static ConfigEntry<bool> EspQuestOnly;
        internal static ConfigEntry<bool> EspShowZones;
        internal static ConfigEntry<float> EspZoneDistance;
        internal static ConfigEntry<string> EspZoneColor;
        internal static ConfigEntry<bool> EspShowGrenades;
        internal static ConfigEntry<float> EspGrenadeDistance;
        internal static ConfigEntry<string> EspGrenadeColor;
        internal static ConfigEntry<bool> QolInfiniteStamina;
        internal static ConfigEntry<bool> QolNoSway;
        internal static ConfigEntry<bool> QolNoRecoil;
        internal static ConfigEntry<bool> QolNoMalfunction;
        internal static ConfigEntry<bool> QolInstantSearch;
        internal static ConfigEntry<bool> AimEnabled;
        internal static ConfigEntry<KeyCode> AimKey;
        internal static ConfigEntry<KeyCode> MenuKey;
        internal static ConfigEntry<float> AimFov;
        internal static ConfigEntry<float> AimDistance;
        internal static ConfigEntry<float> AimSmooth;
        internal static ConfigEntry<bool> AimHead;
        internal static ConfigEntry<bool> AimVisibleOnly;
        internal static ConfigEntry<bool> AimMagicBullet;
        internal static ConfigEntry<int> EspFontSize;
        internal static ConfigEntry<int> EspLabelLength;
        internal static ConfigEntry<string> EspItemColor;
        internal static ConfigEntry<string> EspWeaponColor;
        internal static ConfigEntry<string> EspAmmoColor;
        internal static ConfigEntry<string> EspMedicalColor;
        internal static ConfigEntry<string> EspFoodColor;
        internal static ConfigEntry<string> EspKeyColor;
        internal static ConfigEntry<string> EspGearColor;
        internal static ConfigEntry<string> EspBarterColor;
        internal static ConfigEntry<string> EspModsColor;
        internal static ConfigEntry<string> EspQuestColor;
        internal static ConfigEntry<string> EspContainerColor;
        internal static ConfigEntry<string> EspCorpseColor;

        // bepinex reads the file once. a timestamp compare is cheaper than making you press a key,
        // so poll it, wait for the write to settle, then reload. our own saves bump the stamp too,
        // which is why StampConfig exists to swallow the self-inflicted ones.
        internal static void CheckConfigFile()
        {
            var now = Time.unscaledTime;
            if (now < _nextConfigCheck) return;
            _nextConfigCheck = now + 0.75f;

            // our own save just touched the file. don't reload it back over the in-memory value.
            if (now < _suppressConfigWatch) return;

            try
            {
                var info = new FileInfo(File.ConfigFilePath);
                if (!info.Exists) return;

                var stamp = info.LastWriteTimeUtc;
                if (stamp == _configStamp) return;

                // editors and the game can touch the file twice in a row, let it settle
                if ((DateTime.UtcNow - stamp).TotalMilliseconds < 200) return;

                _configStamp = stamp;
                File.Reload();
                Logger.LogInfo("config reloaded after file change");
            }
            catch (Exception ex)
            {
                Logger.LogWarning("config watch failed: " + ex.Message);
            }
        }

        internal static void StampConfig()
        {
            _configStamp = CurrentStamp();

            // our own save touches the file too. ignore the watcher for a moment so it can't
            // reload a half-written file and flip the toggle back off by itself.
            _suppressConfigWatch = Time.unscaledTime + 2f;
        }

        private static DateTime CurrentStamp()
        {
            try
            {
                var info = new FileInfo(File.ConfigFilePath);
                return info.Exists ? info.LastWriteTimeUtc : default;
            }
            catch
            {
                return default;
            }
        }

        public override void Load()
        {
            Logger = base.Log;
            File = Config;
            _configStamp = CurrentStamp();


            Enabled = Config.Bind("Chams", "Enabled", false,
                "start with chams already on.");

            Shader = Config.Bind("Chams", "Shader", "chams.shader",
                new ConfigDescription("which shader from chams.bundle to use.",
                    new AcceptableValueList<string>(
                        "chams.shader", "wireframe.shader", "forcefield.shader",
                        "glass.shader", "lava.shader", "outline.shader")));
            VisibleColor = Config.Bind("Chams", "Visible Color", "#00FFFFFF",
                "rgba hex, the colour when the body is in line of sight.");
            OccludedColor = Config.Bind("Chams", "Occluded Color", "#0000FFBF",
                "rgba hex, the colour when the body is behind geometry.");
            GearChams = Config.Bind("Chams", "Gear Chams", false,
                "also paint helmets, rigs, masks and armour. off by default because it turns bodies into soup.");
            MaxDistance = Config.Bind("Chams", "Max Distance", 250f,
                new ConfigDescription("don't bother painting bodies past this range, in metres.",
                    new AcceptableValueRange<float>(10f, 1000f)));
            RefreshInterval = Config.Bind("Chams", "Refresh Interval", 0.25f,
                new ConfigDescription("how often to rescan players and reapply. lower is smoother, costs more cpu.",
                    new AcceptableValueRange<float>(0.05f, 2f)));
            ChamsOcclusion = Config.Bind("Chams", "Occlusion Check", false,
                "only paint players you can actually see. off keeps the classic through-wall chams.");

            ChamsPerType = Config.Bind("Chams", "Per-Type Colors", true,
                "colour chams by faction: usec, bear, scav and boss each get their own colour.");
            ChamsUsecColor = Config.Bind("Chams", "Usec Color", "#3366FF", "usec visible colour.");
            ChamsBearColor = Config.Bind("Chams", "Bear Color", "#33CC33", "bear visible colour.");
            ChamsScavColor = Config.Bind("Chams", "Scav Color", "#E6E6E6", "scav visible colour.");
            ChamsBossColor = Config.Bind("Chams", "Boss Color", "#FF3333", "boss visible colour.");
            ChamsSpecialColor = Config.Bind("Chams", "Cultist Raider Color", "#FFE033",
                "cultists and raiders visible colour.");
            ChamsOccludedAlpha = Config.Bind("Chams", "Type Occluded Alpha", 0.45f,
                new ConfigDescription("alpha used for the through-wall pass of the per-type colours.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ChamsVisibleAlpha = Config.Bind("Chams", "Visible Alpha", 1f,
                new ConfigDescription("opacity of the in-sight chams pass for every colour. lower means you can see the gear underneath.",
                    new AcceptableValueRange<float>(0f, 1f)));

            EspEnabled = Config.Bind("Loot ESP", "Enabled", true,
                "draw dots and labels on loot.");


            EspLabelDistance = Config.Bind("Loot ESP", "Label Distance", 18f,
                new ConfigDescription("dots draw at the distances above, but text only appears inside this range. keeps the screen readable.",
                    new AcceptableValueRange<float>(0f, 300f)));
            EspLootDistance = Config.Bind("Loot ESP", "Loot Distance", 10f,
                new ConfigDescription("range for normal loot. high value items, quest items and wishlist items ignore this and show at any distance.",
                    new AcceptableValueRange<float>(1f, 500f)));
            EspViewOnly = Config.Bind("Loot ESP", "In View Only", true,
                "show only what is inside the camera frustum, instead of a sphere around you.");

            EspOcclusion = Config.Bind("Loot ESP", "Occlusion Check", true,
                "only show markers with a clear line of sight from the camera. off means you see loot through every wall again.");

            EspHighValueOnly = Config.Bind("Loot ESP", "High Value Only", false,
                "hide everything below the high value threshold. a quick way to stop looking at bolts.");
            EspHighValueThreshold = Config.Bind("Loot ESP", "High Value Threshold", 150000,
                new ConfigDescription("roubles. items and container contents below this are hidden while High Value Only is on.",
                    new AcceptableValueRange<int>(0, 2000000)));

            EspScanContents = Config.Bind("Loot ESP", "Scan Container Contents", false,
                "walk each container's inventory and show item count plus total value. costs a little cpu every 10 seconds.");
            EspContentsDistance = Config.Bind("Loot ESP", "Contents Distance", 10f,
                new ConfigDescription("only containers this close get their contents read, in metres. keeps streets from melting.",
                    new AcceptableValueRange<float>(1f, 100f)));
            EspItems = Config.Bind("Loot ESP", "Show Items", true,
                "loose loot on the ground.");
            EspContainers = Config.Bind("Loot ESP", "Show Containers", true,
                "crates, cases, safes and other lootable containers. rescanned every 10 seconds.");
            EspCorpses = Config.Bind("Loot ESP", "Show Corpses", true,
                "bodies. labelled with the nickname when the profile cache knows it, otherwise the side.");
            EspLabels = Config.Bind("Loot ESP", "Show Labels", true,
                "text next to each dot. off means dots only.");
            EspStackLabels = Config.Bind("Loot ESP", "Stack Labels", true,
                "push overlapping labels upward so they don't sit on top of each other. dots stay on the item.");
            EspShowWeapons = Config.Bind("Loot ESP", "Show Weapons", true, "guns and knives.");
            EspShowAmmo = Config.Bind("Loot ESP", "Show Ammo", true, "ammunition and grenades.");
            EspShowMedical = Config.Bind("Loot ESP", "Show Medical", true, "meds, medkits, stims.");
            EspShowFood = Config.Bind("Loot ESP", "Show Food", true, "food and drink.");
            EspShowKeys = Config.Bind("Loot ESP", "Show Keys", true, "keys and keycards.");
            EspShowGear = Config.Bind("Loot ESP", "Show Gear", true, "armour, rigs, helmets, backpacks.");
            EspShowBarter = Config.Bind("Loot ESP", "Show Barter", true, "barter and valuables.");
            EspShowMods = Config.Bind("Loot ESP", "Show Mods", true, "weapon mods.");
            EspShowQuest = Config.Bind("Loot ESP", "Show Quest", true, "quest items.");
            EspShowQuestProgress = Config.Bind("Loot ESP", "Show Quest Progress", true,
                "append progress to quest item labels, e.g. [3/5], from your active quest counters.");
            EspShowOther = Config.Bind("Loot ESP", "Show Other", true, "anything that fits no category.");
            EspShowExfils = Config.Bind("Loot ESP", "Show Exfils", true, "extraction points. blue when open, red when not.");

            EspExfilColor = Config.Bind("Loot ESP", "Exfil Color", "#00E5FF", "rgba hex for open extracts.");
            EspExfilClosedColor = Config.Bind("Loot ESP", "Exfil Closed Color", "#FF5555", "rgba hex for extracts you can't use yet.");
            EspExfilDistance = Config.Bind("Loot ESP", "Exfil Distance", 400f,
                new ConfigDescription("exfils are shown regardless of the view cap, up to this range.",
                    new AcceptableValueRange<float>(20f, 2000f)));
            EspShowWishlist = Config.Bind("Loot ESP", "Show Wishlist", true,
                "highlight items on your in-game wishlist in their own colour.");
            EspWishlistColor = Config.Bind("Loot ESP", "Wishlist Color", "#00FFAA", "rgba hex for wishlist items.");
            EspQuestOnly = Config.Bind("Loot ESP", "Quest Items Only", false,
                "hide everything that isn't required by an active quest or on your wishlist.");
            EspShowZones = Config.Bind("Loot ESP", "Show Quest Zones", true,
                "mark place-item and plant-beacon zones the game knows about for your active quests.");
            EspZoneDistance = Config.Bind("Loot ESP", "Zone Distance", 300f,
                new ConfigDescription("quest zones are shown up to this range, independent of the view cap.",
                    new AcceptableValueRange<float>(10f, 2000f)));
            EspZoneColor = Config.Bind("Loot ESP", "Zone Color", "#B388FF", "rgba hex for quest zones.");
            EspShowGrenades = Config.Bind("Loot ESP", "Show Grenades", true, "mark live thrown grenades. yes, including the ones landing at your feet.");

            EspGrenadeDistance = Config.Bind("Loot ESP", "Grenade Distance", 100f,
                new ConfigDescription("grenades are shown up to this range, independent of the view cap.",
                    new AcceptableValueRange<float>(5f, 500f)));
            EspGrenadeColor = Config.Bind("Loot ESP", "Grenade Color", "#FF2222", "rgba hex for live grenades.");

            QolInfiniteStamina = Config.Bind("QoL", "Infinite Stamina", false,
                "keep your stamina pinned at capacity. the game still sees the consumption, so endurance keeps levelling.");

            QolNoSway = Config.Bind("QoL", "No Sway", false,
                "skip the weapon effector pass, which kills sway, breathing wobble and weapon bob. the gun will feel rigid in your hands, which is the point.");

            QolNoRecoil = Config.Bind("QoL", "No Recoil", false,
                "skip the camera recoil kick and zero the smooth recoil value.");

            QolNoMalfunction = Config.Bind("QoL", "No Malfunction", false,
                "disable jams, misfires, slide and feed failures, and durability burn on the weapon you are holding. writes the weapon template, so it sticks per weapon type.");
            QolInstantSearch = Config.Bind("QoL", "Instant Search", false,
                "mark container contents as already known and searched, so they are revealed on open. ported from asler's SPT-LootInstantSearch.");

            AimEnabled = Config.Bind("Aimbot", "Enabled", false,
                "aim at the closest enemy inside the FOV while the aim key is held. aim only, no triggerbot.");
            AimKey = Config.Bind("Aimbot", "Aim Key", KeyCode.LeftControl,
                "hold this to aim. left control by default.");

            MenuKey = Config.Bind("Menu", "Toggle Key", KeyCode.Insert,
                "opens the in-game config menu. insert by default, since nothing else wants it.");            AimFov = Config.Bind("Aimbot", "FOV", 20f,
                new ConfigDescription("total field of view cone, in degrees. half this is the actual snap radius.",
                    new AcceptableValueRange<float>(1f, 180f)));
            AimDistance = Config.Bind("Aimbot", "Max Distance", 200f,
                new ConfigDescription("ignore targets further than this, in metres.",
                    new AcceptableValueRange<float>(1f, 800f)));
            AimSmooth = Config.Bind("Aimbot", "Smooth", 0f,
                new ConfigDescription("degrees per second. 0 snaps instantly, higher values glide. anything above 720 is basically snap.",
                    new AcceptableValueRange<float>(0f, 5000f)));
            AimHead = Config.Bind("Aimbot", "Target Head", true,
                "aim at head height. off means chest, which is faster to acquire and less obvious.");
            AimVisibleOnly = Config.Bind("Aimbot", "Visible Only", false,
                "require line of sight. off means it will try to aim through walls, which is a good way to shoot a wall.");
            AimMagicBullet = Config.Bind("Aimbot", "Magic Bullet", false,
                "redirect every shot to the closest on-screen head, regardless of where the gun is pointed. the gun never moves and walls are not consulted.");
            EspFontSize = Config.Bind("Loot ESP", "Font Size", 10,
                new ConfigDescription("label text size in pixels.",
                    new AcceptableValueRange<int>(8, 24)));
            EspLabelLength = Config.Bind("Loot ESP", "Label Length", 40,
                new ConfigDescription("hard cap on label characters, so a 60 character rifle name doesn't eat the screen.",
                    new AcceptableValueRange<int>(8, 80)));
            EspItemColor = Config.Bind("Loot ESP", "Item Color", "#00FF7F",
                "rgba hex for loose loot that fits no other category.");
            EspWeaponColor = Config.Bind("Loot ESP", "Weapon Color", "#FF5959", "guns and knives.");
            EspAmmoColor = Config.Bind("Loot ESP", "Ammo Color", "#E6E680", "ammunition and grenades.");
            EspMedicalColor = Config.Bind("Loot ESP", "Medical Color", "#FF66CC", "meds, medkits, stims.");
            EspFoodColor = Config.Bind("Loot ESP", "Food Color", "#99E666", "food and drink.");
            EspKeyColor = Config.Bind("Loot ESP", "Key Color", "#33D9FF", "keys and keycards.");
            EspGearColor = Config.Bind("Loot ESP", "Gear Color", "#BFBFBF", "armour, rigs, helmets, backpacks.");
            EspBarterColor = Config.Bind("Loot ESP", "Barter Color", "#FFBF33", "barter and valuables.");
            EspModsColor = Config.Bind("Loot ESP", "Mods Color", "#8C8CE6", "weapon mods.");
            EspQuestColor = Config.Bind("Loot ESP", "Quest Color", "#FF3333",
                "quest items. uses the template's QuestItem flag, not guesswork.");
            EspContainerColor = Config.Bind("Loot ESP", "Container Color", "#FFA500",
                "rgba hex for containers.");
            EspCorpseColor = Config.Bind("Loot ESP", "Corpse Color", "#FF00FF",
                "rgba hex for corpses.");
            EspMinValue = Config.Bind("Loot ESP", "Min Value", 8000,
                new ConfigDescription("hide loose loot worth less than this, in roubles. 0 shows everything, which is a lot of bolts.",
                    new AcceptableValueRange<int>(0, 500000)));
            EspRareValue = Config.Bind("Loot ESP", "Rare Value", 80000,
                new ConfigDescription("items at or above this value get the rare colour.",
                    new AcceptableValueRange<int>(1000, 2000000)));
            EspRareColor = Config.Bind("Loot ESP", "Rare Color", "#FFD700",
                "rgba hex for valuable loot.");
            EspUseLivePrices = Config.Bind("Loot ESP", "Use Flea Prices", true,
                "use the flea price database next to the plugin instead of the template's base price.");
            EspPriceFile = Config.Bind("Loot ESP", "Price File", "prices-regular.json",
                "which price file to read. prices-regular.json or prices-pve.json.");
            EspShowValue = Config.Bind("Loot ESP", "Show Value", false,
                "append the handbook price to item labels, e.g. [85K].");

            if (EspUseLivePrices.Value) PriceDatabase.EnsureLoaded(EspPriceFile.Value);

            AddComponent<ChamsController>();
            AddComponent<LootEspController>();
            AddComponent<QolController>();
            AddComponent<AimbotController>();
            AddComponent<MenuController>();

            try
            {
                var harmony = new HarmonyLib.Harmony(Guid);
                var method = HarmonyLib.AccessTools.Method(typeof(Stamina), "Consume");
                if (method != null)
                {
                    harmony.Patch(method,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(StaminaConsumePatch).GetMethod("Prefix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("stamina consume patch applied");
                }
                else
                {
                    Logger.LogWarning("stamina consume patch skipped, method not found");
                }

                var recoilMethod = HarmonyLib.AccessTools.Method(
                    typeof(EFT.Animations.ProceduralWeaponAnimation), "AddHandRecoilRotateToCamera");
                if (recoilMethod != null)
                {
                    harmony.Patch(recoilMethod,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(RecoilRotatePatch).GetMethod("Prefix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("recoil rotate patch applied");
                }
                else
                {
                    Logger.LogWarning("recoil rotate patch skipped, method not found");
                }

                var breathMethod = HarmonyLib.AccessTools.Method(
                    typeof(EFT.Animations.BreathEffector), "Process");
                if (breathMethod != null)
                {
                    var prefix = new HarmonyLib.HarmonyMethod(
                        typeof(BreathEffectorPatch).GetMethod("Prefix",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                    var postfix = new HarmonyLib.HarmonyMethod(
                        typeof(BreathEffectorPatch).GetMethod("Postfix",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));

                    harmony.Patch(breathMethod, prefix: prefix, postfix: postfix);
                    Logger.LogInfo("breath effector patch applied");
                }
                else
                {
                    Logger.LogWarning("breath effector patch skipped, method not found");
                }

                var swayMethod = HarmonyLib.AccessTools.Method(
                    typeof(EFT.Animations.ProceduralWeaponAnimation), "UpdateSwaySettings");
                if (swayMethod != null)
                {
                    harmony.Patch(swayMethod,
                        postfix: new HarmonyLib.HarmonyMethod(
                            typeof(SwaySettingsPatch).GetMethod("Postfix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("sway settings postfix applied");
                }
                else
                {
                    Logger.LogWarning("sway settings patch skipped, method not found");
                }

                var magicMethod = HarmonyLib.AccessTools.Method(
                    typeof(EFT.Player.FirearmController), "InitiateShot");
                if (magicMethod != null)
                {
                    harmony.Patch(magicMethod,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(MagicBulletPatch).GetMethod("Prefix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("magic bullet patch applied");
                }
                else
                {
                    Logger.LogWarning("magic bullet patch skipped, method not found");
                }

                // PlayerSearchController does not override IsItemKnown, so patch the concrete
                // controllers instead. each patch is individually guarded: a single Harmony
                // failure used to abort the whole block and silently disable every later feature.
                var searchControllers = new[]
                {
                    typeof(EFT.ActiveSearchController),
                    typeof(EFT.NetworkSearchController)
                };

                foreach (var controllerType in searchControllers)
                {
                    try
                    {
                        var knownMethod = HarmonyLib.AccessTools.Method(controllerType, "IsItemKnown");
                        if (knownMethod == null)
                        {
                            Logger.LogWarning("instant search: IsItemKnown missing on " + controllerType.Name);
                            continue;
                        }

                        harmony.Patch(knownMethod,
                            prefix: new HarmonyLib.HarmonyMethod(
                                typeof(InstantSearchKnownPatch).GetMethod("Prefix",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                        Logger.LogInfo("instant search known-items patch applied: " + controllerType.Name);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("instant search known-items patch failed on " + controllerType.Name + ": " + ex.Message);
                    }
                }

                try
                {
                    var showMethod = HarmonyLib.AccessTools.Method(
                        typeof(EFT.UI.DragAndDrop.SearchableView), "Show");
                    if (showMethod != null)
                    {
                        harmony.Patch(showMethod,
                            postfix: new HarmonyLib.HarmonyMethod(
                                typeof(InstantSearchShowPatch).GetMethod("Postfix",
                                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                        Logger.LogInfo("instant search view patch applied");
                    }
                    else
                    {
                        Logger.LogWarning("instant search view patch skipped, method not found");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError("instant search view patch failed: " + ex.Message);
                }

                var curveMethod = HarmonyLib.AccessTools.Method(typeof(WeaponRecoilProcessBase), "CalculateRecoil");
                if (curveMethod != null)
                {
                    harmony.Patch(curveMethod,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(RecoilCurvePatch).GetMethod("Prefix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("recoil curve patch applied");
                }
                else
                {
                    Logger.LogWarning("recoil curve patch skipped, method not found");
                }

                var recoilEffectorMethod = HarmonyLib.AccessTools.Method(typeof(ShotEffector), "Process");                if (recoilEffectorMethod != null)
                {
                    harmony.Patch(recoilEffectorMethod,
                        prefix: new HarmonyLib.HarmonyMethod(
                            typeof(RecoilEffectorPatch).GetMethod("Prefix",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
                    Logger.LogInfo("recoil effector patch applied");
                }
                else
                {
                    Logger.LogWarning("recoil effector patch skipped, method not found");
                }

                // capture the raid world when the game makes one, instead of every controller
                // searching the scene for it. that search is what broke character creation.
                // each hook is patched on its own: one bad hook used to take out the other two,
                // and then every feature silently stopped working in raid.
                var capture = new HarmonyLib.HarmonyMethod(
                    typeof(WorldTracker).GetMethod("Capture",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                var forget = new HarmonyLib.HarmonyMethod(
                    typeof(WorldTracker).GetMethod("Forget",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));

                var worldHooks = new[]
                {
                    new { Name = "OnGameStarted", Postfix = capture },
                    new { Name = "Awake", Postfix = capture },
                    new { Name = "Dispose", Postfix = forget }
                };

                foreach (var hook in worldHooks)
                {
                    try
                    {
                        var hookMethod = HarmonyLib.AccessTools.Method(typeof(EFT.GameWorld), hook.Name);
                        if (hookMethod == null)
                        {
                            Logger.LogWarning("world tracker: " + hook.Name + " not found");
                            continue;
                        }

                        harmony.Patch(hookMethod, postfix: hook.Postfix);
                        Logger.LogInfo("world tracker hook applied: " + hook.Name);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("world tracker hook failed on " + hook.Name + ": " + ex.Message);
                    }
                }

            }
            catch (Exception ex)
            {
                Logger.LogError("stamina consume patch failed: " + ex.Message);
            }
            Logger.LogInfo("Vzor " + Version + " loaded. config only, no hotkeys.");
        }
    }
}
