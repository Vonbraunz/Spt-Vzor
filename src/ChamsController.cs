using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Vzor
{
    // the whole mod is basically this file. swap body skin materials for one of the shaders
    // baked into chams.bundle, remember the originals, put them back when asked.
    public class ChamsController : MonoBehaviour
    {
        private struct Applied
        {
            public Renderer Renderer;
            public Il2CppReferenceArray<Material> Original;
            public int BodyId;
        }

        // renderer instance id -> what we replaced, so we can undo it later
        private readonly Dictionary<int, Applied> _applied = new Dictionary<int, Applied>();

        private AssetBundle _bundle;
        private Shader _shader;
        private Material _material;
        private string _materialSignature;
        private readonly Dictionary<ChamsType, Material> _typeMaterials = new Dictionary<ChamsType, Material>();
        private readonly HashSet<ChamsType> _loggedTypes = new HashSet<ChamsType>();

        private enum ChamsType
        {
            Usec,
            Bear,
            Scav,
            Boss,
            Special
        }

        private GameWorld _world;
        private float _nextTick;
        private bool _active;
        private bool _bundleFailed;
        private bool _shaderLogged;

        // the original mod filters gear by material name. it is fragile and honestly a bit
        // embarrassing, but eft names its skin materials after the gear, so it works. mostly.
        private static readonly string[] GearMarkers =
        {
            "armour", "armor", "helmet", "glasses", "cover", "rig", "belt",
            "wear", "mask", "balaclava", "vest"
        };

        private void Start()
        {
            _active = Plugin.Enabled.Value;

            // deliberately do not touch the bundle here. loading a shader compiled by a
            // different unity patch version is the one genuinely risky thing this mod does,
            // so it waits until you actually ask for chams. until then we poll a key and nothing else.
            if (_active) EnsureReady();
        }

        private void Update()
        {
            Plugin.CheckConfigFile();

            // no hotkeys any more. config is the only switch, and it is watched live.
            _active = Plugin.Enabled.Value;
            if (!_active)
            {
                RestoreAll();
                return;
            }

            if (!EnsureReady()) return;

            var now = Time.unscaledTime;
            if (now < _nextTick) return;
            _nextTick = now + Mathf.Max(0.05f, Plugin.RefreshInterval.Value);

            RebuildMaterial(false);
            Repaint();
        }

        private bool EnsureReady()
        {
            // do nothing at all until a world exists. loading an asset bundle during boot means
            // eft's shader warmup runs afterwards and stomps the shader we are holding, and then
            // the retry path spins forever reloading a bundle that is already mounted. learned that
            // the loud way. chams only make sense in a raid anyway, and the only safe way to know
            // a raid is running is to be handed the world instead of searching for it.
            _world = WorldTracker.Current;
            if (_world == null) return false;

            if (_shader == null)
            {
                // only ever hit the disk once. if the handle is already good, the shader dying is
                // warmup's fault, so re-resolve from the mounted bundle instead of reloading the file.
                if (_bundle == null && !_bundleFailed) LoadBundle();
                if (_bundle != null) ResolveShader();
                if (_shader == null) return false;
            }

            RebuildMaterial(false);
            return _material != null;
        }

        private void OnDestroy()
        {
            RestoreAll();
        }

        private static bool IsPlayerVisible(Player player)
        {
            var camera = ViewCamera.Get();
            if (camera == null) return true;

            var from = camera.transform.position;
            var to = player.transform.position + Vector3.up * 1.3f;
            return Visibility.HasLineOfSight(from, to, player.transform);
        }

        private void LoadBundle()
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var path = Path.Combine(dir, "chams.bundle");

            if (!File.Exists(path))
            {
                _bundleFailed = true;
                Plugin.Logger.LogError("chams.bundle is missing, expected it next to the plugin at " + path);
                return;
            }

            _bundle = AssetBundle.LoadFromFile(path);
            if (_bundle == null)
            {
                _bundleFailed = true;
                Plugin.Logger.LogError("AssetBundle.LoadFromFile returned null for " + path);
                return;
            }

            ResolveShader();
        }

        private void ResolveShader()
        {
            var name = Plugin.Shader.Value;
            var asset = _bundle.LoadAsset(name, Il2CppType.Of<Shader>());
            _shader = asset == null ? null : asset.TryCast<Shader>();

            if (_shader == null)
            {
                _bundleFailed = true;
                Plugin.Logger.LogError("shader '" + name + "' is not in chams.bundle");
                return;
            }

            if (!_shaderLogged)
            {
                _shaderLogged = true;
                Plugin.Logger.LogInfo("loaded shader '" + name + "'");
                DumpShaderProperties();
            }
        }

        // one shot diagnostic. the shader ships compiled, so this is the only way to find out
        // which colour properties it actually has without unpacking the bundle by hand.
        private void DumpShaderProperties()
        {
            try
            {
                var count = _shader.GetPropertyCount();
                var names = new List<string>();

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        names.Add(_shader.GetPropertyName(i) + ":" + _shader.GetPropertyType(i));
                    }
                    catch
                    {
                        names.Add("?");
                    }
                }

                Plugin.Logger.LogInfo("chams shader properties (" + count + "): " + string.Join(", ", names));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("chams: could not read shader properties: " + ex.Message);
            }
        }

        private void RebuildMaterial(bool force)
        {
            if (_shader == null) return;

            var signature = Plugin.Shader.Value + "|" + Plugin.VisibleColor.Value + "|" + Plugin.OccludedColor.Value +
                            "|" + Plugin.ChamsPerType.Value + "|" + Plugin.ChamsUsecColor.Value + "|" +
                            Plugin.ChamsBearColor.Value + "|" + Plugin.ChamsScavColor.Value + "|" +
                            Plugin.ChamsBossColor.Value + "|" + Plugin.ChamsSpecialColor.Value + "|" +
                            Plugin.ChamsOccludedAlpha.Value + "|" + Plugin.ChamsVisibleAlpha.Value;

            if (!force && _material != null && signature == _materialSignature) return;

            // colours or shader changed, so put the game back the way we found it first
            RestoreAll();

            if (_material != null) Destroy(_material);

            foreach (var existing in _typeMaterials.Values)
                if (existing != null)
                    Destroy(existing);

            _typeMaterials.Clear();

            _material = BuildMaterial(ColorUtil.Parse(Plugin.VisibleColor.Value, Color.cyan),
                ColorUtil.Parse(Plugin.OccludedColor.Value, Color.blue));

            _typeMaterials[ChamsType.Usec] = BuildTypeMaterial(Plugin.ChamsUsecColor.Value, new Color(0.2f, 0.4f, 1f));
            _typeMaterials[ChamsType.Bear] = BuildTypeMaterial(Plugin.ChamsBearColor.Value, new Color(0.2f, 0.8f, 0.2f));
            _typeMaterials[ChamsType.Scav] = BuildTypeMaterial(Plugin.ChamsScavColor.Value, new Color(0.9f, 0.9f, 0.9f));
            _typeMaterials[ChamsType.Boss] = BuildTypeMaterial(Plugin.ChamsBossColor.Value, new Color(1f, 0.2f, 0.2f));
            _typeMaterials[ChamsType.Special] = BuildTypeMaterial(Plugin.ChamsSpecialColor.Value, new Color(1f, 0.9f, 0.2f));

            _materialSignature = signature;
        }

        private Material BuildMaterial(Color visible, Color occluded)
        {
            visible.a = Plugin.ChamsVisibleAlpha.Value;

            var material = new Material(_shader);
            material.name = "Vzor";
            material.mainTexture = null;

            SetColor(material, "_Color_Visible", visible);
            SetColor(material, "_Color_Occluded", occluded);

            // the bundle is compiled, not source, so we cannot read its property names. set the
            // single-colour names too, otherwise a shader that only exposes _Color renders white
            // and it looks like the faction colouring is broken when it is the material at fault.
            SetColor(material, "_Color", visible);
            SetColor(material, "_VisibleColor", visible);
            SetColor(material, "_OccludedColor", occluded);

            return material;
        }

        private static void SetColor(Material material, string name, Color color)
        {
            if (material.HasProperty(name)) material.SetColor(name, color);
        }

        private Material BuildTypeMaterial(string hex, Color fallback)
        {
            var visible = ColorUtil.Parse(hex, fallback);
            var occluded = new Color(visible.r, visible.g, visible.b, Plugin.ChamsOccludedAlpha.Value);
            return BuildMaterial(visible, occluded);
        }

        private Material MaterialFor(Player player)
        {
            if (!Plugin.ChamsPerType.Value) return _material;

            var type = GetChamsType(player);
            return _typeMaterials.TryGetValue(type, out var material) ? material : _material;
        }

        // temporary diagnostic. tells us whether a white body is a colour problem or a faction
        // resolution problem, without needing a second raid to find out.
        private void LogTypeOnce(Player player)
        {
            // one line per faction. a cap on total lines just meant twenty scavs and never the
            // boss we were actually trying to diagnose.
            var typeNow = GetChamsType(player);
            if (_loggedTypes.Contains(typeNow)) return;
            _loggedTypes.Add(typeNow);

            try
            {
                var role = "?";
                var side = "?";

                var profile = player.Profile;
                var info = profile != null ? profile.Info : null;
                if (info != null)
                {
                    side = info.Side.ToString();

                    var settings = info.Settings;
                    if (settings != null) role = settings.Role.ToString();
                }

                var colour = "?";
                if (_typeMaterials.TryGetValue(typeNow, out var material) && material != null)
                    colour = Hex(material.GetColor("_Color_Visible"));

                Plugin.Logger.LogInfo("chams: " + typeNow + " side=" + side + " role=" + role +
                    " colour=" + colour + " nick=" + PlayerName(player));
            }
            catch
            {
                // logging must never be the reason a raid dies
            }
        }

        private static string Hex(Color c)
        {
            return "#" + Mathf.RoundToInt(c.r * 255f).ToString("X2") +
                   Mathf.RoundToInt(c.g * 255f).ToString("X2") +
                   Mathf.RoundToInt(c.b * 255f).ToString("X2");
        }

        private static string PlayerName(Player player)
        {
            try
            {
                var profile = player.Profile;
                var info = profile != null ? profile.Info : null;
                if (info == null) return "?";
                var nick = info.Nickname;
                return string.IsNullOrEmpty(nick) ? "?" : nick;
            }
            catch
            {
                return "?";
            }
        }

        private static ChamsType GetChamsType(Player player)
        {
            try
            {
                var profile = player.Profile;
                var info = profile != null ? profile.Info : null;
                if (info == null) return ChamsType.Scav;

                // role first. ai pmcs have side=savage like every other bot and carry their
                // actual faction in the role, which is why pmcs used to come out scav coloured.
                var settings = info.Settings;
                if (settings != null)
                {
                    var byRole = TypeFromRole(settings.Role.ToString());
                    if (byRole != ChamsType.Scav) return byRole;
                }

                var side = info.Side;
                if (side == EPlayerSide.Usec) return ChamsType.Usec;
                if (side == EPlayerSide.Bear) return ChamsType.Bear;
            }
            catch
            {
                // profile not ready
            }

            return ChamsType.Scav;
        }

        private static ChamsType TypeFromRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return ChamsType.Scav;

            var r = role.ToLowerInvariant();

            if (r.Contains("boss")) return ChamsType.Boss;

            // raiders, rogues, cultists and everybody's followers. shoot first, sort it out later.
            if (r.Contains("pmcbot") || r.Contains("exusec") || r.Contains("sectant") ||
                r.Contains("follower") || r.Contains("blackdivision") || r.Contains("raider"))
                return ChamsType.Special;

            if (r.Contains("pmcusec")) return ChamsType.Usec;
            if (r.Contains("pmcbear")) return ChamsType.Bear;

            return ChamsType.Scav;
        }

        private void Repaint()
        {
            // the world is handed to us at raid start, never searched for. see worldtracker.
            _world = WorldTracker.Current;
            if (_world == null) return;

            var players = _world.AllAlivePlayersList;
            if (players == null) return;

            var cam = Camera.main;
            var camPos = cam != null ? cam.transform.position : Vector3.zero;
            var maxDistance = Plugin.MaxDistance.Value;

            for (var i = 0; i < players.Count; i++)
            {
                try
                {
                    var player = players[i];
                    if (player == null || player.IsYourPlayer) continue;

                    if (cam != null && Vector3.Distance(camPos, player.transform.position) > maxDistance) continue;

                    var body = player.PlayerBody;
                    if (body == null) continue;

                    var bodyId = player.GetInstanceID();
                    if (Plugin.ChamsOcclusion.Value && !IsPlayerVisible(player))
                    {
                        RestoreBody(bodyId);
                        continue;
                    }

                    var skins = body.BodySkins;
                    if (skins == null) continue;

                    LogTypeOnce(player);
                    var material = MaterialFor(player);

                    var enumerator = skins.GetEnumerator();
                    while (enumerator.MoveNext()) PaintSkin(enumerator.Current.Value, bodyId, material);
                }
                catch (Exception ex)
                {
                    // a player that despawns mid-loop is normal, not an emergency
                    Plugin.Logger.LogDebug("player skipped: " + ex.Message);
                }
            }
        }

        private void PaintSkin(EFT.Visual.LoddedSkin loddedSkin, int bodyId, Material material)
        {
            if (loddedSkin == null) return;

            var lods = loddedSkin._lods;
            if (lods == null) return;

            for (var i = 0; i < lods.Length; i++)
            {
                var skin = lods[i];
                if (skin == null) continue;

                var renderer = skin.SkinnedMeshRenderer;
                if (renderer == null) continue;

                PaintRenderer(renderer, bodyId, material);
            }
        }

        private void PaintRenderer(Renderer renderer, int bodyId, Material material)
        {
            if (renderer == null) return;

            var id = renderer.GetInstanceID();

            // already ours? cheaper to check than to rebuild the array
            if (_applied.TryGetValue(id, out var existing))
            {
                var current = renderer.sharedMaterial;
                if (current != null && material != null && current.GetInstanceID() == material.GetInstanceID()) return;
                _applied.Remove(id);
            }

            var original = renderer.sharedMaterials;
            if (original == null || original.Length == 0) return;

            if (!Plugin.GearChams.Value && LooksLikeGear(original)) return;

            var replacement = new Il2CppReferenceArray<Material>(original.Length);
            for (var i = 0; i < original.Length; i++) replacement[i] = material;

            _applied[id] = new Applied { Renderer = renderer, Original = original, BodyId = bodyId };
            renderer.sharedMaterials = replacement;
        }

        private static bool LooksLikeGear(Il2CppReferenceArray<Material> materials)
        {
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                var name = material.name;
                if (string.IsNullOrEmpty(name)) continue;

                name = name.ToLowerInvariant();
                for (var k = 0; k < GearMarkers.Length; k++)
                    if (name.Contains(GearMarkers[k])) return true;
            }

            return false;
        }

        // occlusion goes on and off, so a body that just disappeared behind a wall has to be
        // put back exactly as it was. we track which body owns which renderer for that.
        private void RestoreBody(int bodyId)
        {
            List<int> found = null;

            foreach (var entry in _applied)
            {
                if (entry.Value.BodyId != bodyId) continue;
                (found ?? (found = new List<int>())).Add(entry.Key);
            }

            if (found == null) return;

            for (var i = 0; i < found.Count; i++)
            {
                var id = found[i];
                try
                {
                    var applied = _applied[id];
                    if (applied.Renderer != null) applied.Renderer.sharedMaterials = applied.Original;
                }
                catch
                {
                    // renderer went away with the body
                }

                _applied.Remove(id);
            }
        }

        private void RestoreAll()
        {
            foreach (var entry in _applied)
            {
                try
                {
                    if (entry.Value.Renderer != null) entry.Value.Renderer.sharedMaterials = entry.Value.Original;
                }
                catch
                {
                    // renderer already gone with the raid, nothing to restore
                }
            }

            _applied.Clear();
        }
    }
}
