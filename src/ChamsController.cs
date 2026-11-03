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
            public string Signature;
        }

        // renderer instance id -> what we replaced, so we can undo it later
        private readonly Dictionary<int, Applied> _applied = new Dictionary<int, Applied>();

        private AssetBundle _bundle;
        private Shader _shader;
        private Material _material;
        private string _materialSignature;
        private readonly Dictionary<ChamsType, Material> _typeMaterials = new Dictionary<ChamsType, Material>();
        private readonly Dictionary<string, Material> _tintMaterials = new Dictionary<string, Material>();
        private bool _loggedTintFailure;
        private bool _loggedTintActive;
        private bool _loggedTintShader;
        private bool _loggedOutline;
        private bool _loggedOutlineFailure;

        // outline mode: a second skinned copy of the mesh, carrying the chams colour, inflated a
        // little so it reads as a rim around a body whose own materials we never touched
        private struct Shell
        {
            public GameObject Object;
            public int BodyId;
        }

        private readonly Dictionary<int, Shell> _shells = new Dictionary<int, Shell>();

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
            }
        }

        private void RebuildMaterial(bool force)
        {
            if (_shader == null) return;

            var signature = Plugin.Shader.Value + "|" + Plugin.VisibleColor.Value + "|" + Plugin.OccludedColor.Value +
                            "|" + Plugin.ChamsPerType.Value + "|" + Plugin.ChamsUsecColor.Value + "|" +
                            Plugin.ChamsBearColor.Value + "|" + Plugin.ChamsScavColor.Value + "|" +
                            Plugin.ChamsBossColor.Value + "|" + Plugin.ChamsSpecialColor.Value + "|" +
                            Plugin.ChamsOccludedAlpha.Value + "|" + Plugin.ChamsVisibleAlpha.Value + "|" +
                            Plugin.ChamsKeepTexture.Value;

            if (!force && _material != null && signature == _materialSignature) return;

            // colours or shader changed, so put the game back the way we found it first
            RestoreAll();

            if (_material != null) Destroy(_material);

            foreach (var textured in _tintMaterials.Values)
                if (textured != null)
                    Destroy(textured);

            _tintMaterials.Clear();

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
            try
            {
                material.SetColor(name, color);
            }
            catch
            {
                // material has no such property, which is fine
            }
        }

        private Material BuildTypeMaterial(string hex, Color fallback)
        {
            var visible = ColorUtil.Parse(hex, fallback);
            var occluded = new Color(visible.r, visible.g, visible.b, Plugin.ChamsOccludedAlpha.Value);
            return BuildMaterial(visible, occluded);
        }

        // tint mode: copy the game's own material and tint it, so textures, cutout and blend mode
        // all survive. swapping in the chams shader made bodies with alpha textures go see
        // through, which is exactly what a body should not do.
        private Material TintedClone(Material original, ChamsType type)
        {
            if (original == null) return null;

            var key = type + "|c" + original.GetInstanceID();
            if (_tintMaterials.TryGetValue(key, out var cached) && cached != null) return cached;

            Material clone;
            try
            {
                clone = new Material(original);
            }
            catch
            {
                return null;
            }

            clone.name = "Vzor tint " + type;

            var tint = TintFor(type);
            var applied = 0;
            var names = string.Empty;

            // do not guess property names. walk the shader, tint every colour it exposes, and
            // log what we found once. guessing _Color four ways is how you get a silent no-op.
            try
            {
                var shader = clone.shader;
                var count = shader != null ? shader.GetPropertyCount() : 0;

                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var property = shader.GetPropertyName(i);
                        if (string.IsNullOrEmpty(property)) continue;

                        var lower = property.ToLowerInvariant();
                        if (!lower.Contains("color") && !lower.Contains("tint")) continue;
                        if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;

                        clone.SetColor(property, tint);
                        applied++;

                        if (names.Length < 240) names += property + " ";
                    }
                    catch
                    {
                        // one odd property is not a reason to give up on the material
                    }
                }
            }
            catch
            {
                // shader not readable
            }

            if (!_loggedTintShader)
            {
                _loggedTintShader = true;
                Plugin.Logger.LogInfo("chams: tint swept " + applied + " colour properties: " + names);
            }

            // nothing to tint means this shader would render exactly as it does now, so let the
            // caller fall back to the flat chams material instead of pretending it worked
            if (applied == 0) return null;

            _tintMaterials[key] = clone;
            return clone;
        }

        private Color TintFor(ChamsType type)
        {
            var faction = _typeMaterials.TryGetValue(type, out var material) && material != null
                ? material.GetColor("_Color_Visible")
                : Color.white;

            // full faction colour. half way to white was invisible on dark body shaders, and a
            // tint you cannot see is the same as no tint.
            faction.a = 1f;
            return faction;
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

                    var type = GetChamsType(player);
                    var enumerator = skins.GetEnumerator();
                    while (enumerator.MoveNext()) PaintSkin(enumerator.Current.Value, bodyId, type);
                }
                catch (Exception ex)
                {
                    // a player that despawns mid-loop is normal, not an emergency
                    Plugin.Logger.LogDebug("player skipped: " + ex.Message);
                }
            }
        }

        private void PaintSkin(EFT.Visual.LoddedSkin loddedSkin, int bodyId, ChamsType type)
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

                PaintRenderer(renderer, bodyId, type);
            }
        }

        private void PaintRenderer(Renderer renderer, int bodyId, ChamsType type)
        {
            if (renderer == null) return;

            var id = renderer.GetInstanceID();

            var original = renderer.sharedMaterials;
            if (original == null || original.Length == 0) return;

            if (!Plugin.GearChams.Value && LooksLikeGear(original)) return;

            // outline mode: body materials stay exactly as the game made them, and a second
            // skinned copy carries the colour instead
            if (Plugin.ChamsOutline.Value)
            {
                CreateShell(renderer, bodyId, type);
                return;
            }

            // in tint mode the replacement is a per submesh copy of the game's own material, so
            // the signature has to cover which materials we cloned, not just one shared one
            var signature = type + "|" + Plugin.ChamsKeepTexture.Value;

            if (_applied.TryGetValue(id, out var existing) && existing.Signature == signature)
                return;

            var replacement = new Il2CppReferenceArray<Material>(original.Length);

            if (Plugin.ChamsPerType.Value && Plugin.ChamsKeepTexture.Value)
            {
                if (!_loggedTintActive)
                {
                    _loggedTintActive = true;
                    Plugin.Logger.LogInfo("chams: tint mode active");
                }

                var flat = Plugin.ChamsPerType.Value && _typeMaterials.TryGetValue(type, out var typed)
                    ? typed
                    : _material;

                for (var i = 0; i < original.Length; i++)
                {
                    var clone = TintedClone(original[i], type);
                    replacement[i] = clone != null ? clone : flat;

                    if (clone == null && !_loggedTintFailure)
                    {
                        _loggedTintFailure = true;
                        Plugin.Logger.LogWarning("chams: a body material could not be cloned, falling back to the flat shader");
                    }
                }
            }
            else
            {
                var flat = Plugin.ChamsPerType.Value && _typeMaterials.TryGetValue(type, out var typed)
                    ? typed
                    : _material;

                if (flat == null) return;
                for (var i = 0; i < original.Length; i++) replacement[i] = flat;
            }

            _applied[id] = new Applied
            {
                Renderer = renderer,
                Original = original,
                BodyId = bodyId,
                Signature = signature
            };

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
        private void CreateShell(Renderer renderer, int bodyId, ChamsType type)
        {
            var id = renderer.GetInstanceID();

            if (_shells.TryGetValue(id, out var existing) && existing.Object != null) return;

            var source = renderer.TryCast<SkinnedMeshRenderer>();
            if (source == null || source.sharedMesh == null) return;
            if (!source.enabled) return;

            var material = Plugin.ChamsPerType.Value && _typeMaterials.TryGetValue(type, out var typed)
                ? typed
                : _material;

            if (material == null) return;

            GameObject host = null;

            try
            {
                // clone the game's own renderer object. assembling one by hand loses the exact
                // bone binding, which is how you get a body with no legs and feet in the walls.
                var cloned = UnityEngine.Object.Instantiate(renderer.gameObject, renderer.transform.parent);
                host = cloned != null ? cloned.TryCast<GameObject>() : null;
                if (host == null) return;

                // anything the source carried as children is not ours to duplicate
                if (host.transform.childCount > 0)
                {
                    Destroy(host);
                    return;
                }

                host.name = "Vzor shell";
                host.layer = renderer.gameObject.layer;
                host.transform.localPosition = renderer.transform.localPosition;
                host.transform.localRotation = renderer.transform.localRotation;
                host.transform.localScale = renderer.transform.localScale * Plugin.ChamsOutlineScale.Value;

                var shell = host.GetComponent<SkinnedMeshRenderer>();
                if (shell == null)
                {
                    Destroy(host);
                    return;
                }

                shell.bones = source.bones;
                shell.rootBone = source.rootBone;

                var materials = new Il2CppReferenceArray<Material>(1);
                materials[0] = material;
                shell.sharedMaterials = materials;

                shell.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.updateWhenOffscreen = true;

                _shells[id] = new Shell { Object = host, BodyId = bodyId };

                if (!_loggedOutline)
                {
                    _loggedOutline = true;
                    Plugin.Logger.LogInfo("chams: outline shell created, scale " + Plugin.ChamsOutlineScale.Value);
                }
            }
            catch (Exception ex)
            {
                if (host != null) Destroy(host);

                if (!_loggedOutlineFailure)
                {
                    _loggedOutlineFailure = true;
                    Plugin.Logger.LogWarning("chams: shell failed: " + ex.Message);
                }
            }
        }

        private void RestoreBody(int bodyId)
        {
            // shells first, they are the cheap part and they have to go with the body
            List<int> shells = null;

            foreach (var entry in _shells)
            {
                if (entry.Value.BodyId != bodyId) continue;
                (shells ?? (shells = new List<int>())).Add(entry.Key);
            }

            if (shells != null)
            {
                for (var i = 0; i < shells.Count; i++)
                {
                    var shell = _shells[shells[i]];
                    try
                    {
                        if (shell.Object != null) Destroy(shell.Object);
                    }
                    catch
                    {
                        // already gone with the body
                    }

                    _shells.Remove(shells[i]);
                }
            }

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
            foreach (var shell in _shells.Values)
            {
                try
                {
                    if (shell.Object != null) Destroy(shell.Object);
                }
                catch
                {
                    // already gone with the raid
                }
            }

            _shells.Clear();

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
