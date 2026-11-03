using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Configuration;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using UnityEngine;

namespace Vzor
{
    // serves the map page and a live state feed over loopback. the game loop builds one json
    // string per tick and the socket thread only ever reads that string, so no game object is
    // ever touched off the unity thread. that is the whole safety rule, do not break it.
    public class WebMapController : MonoBehaviour
    {
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private volatile string _state = "{\"inRaid\":false}";

        private string _root;
        private string _calibrationPath;
        private float _nextPush;
        private float _nextStart;
        private float _nextExfil;
        private float _nextLoot;
        private float _nextTransit;
        private float _nextQuest;
        private string _questJson = "{\"items\":[],\"quests\":[],\"zones\":[],\"loot\":[],\"containers\":[]}";
        private string _questContainerJson = "[]";
        private float _nextQuestContainer;
        private readonly HashSet<string> _wantedQuestItems = new HashSet<string>();
        private string _transitJson = "[]";
        private string _lootJson = "[]";
        private DateTime _lootWantedUntil = DateTime.MinValue;
        private string _exfilJson = "[]";

        // entity key -> where it was last seen alive. when a key stops appearing, that last
        // position becomes an x on the map.
        private GameWorld _trackedWorld;
        private readonly Dictionary<string, Seen> _seen = new Dictionary<string, Seen>();
        private readonly List<Seen> _dead = new List<Seen>();

        private sealed class Seen
        {
            public float X;
            public float Y;
            public float Z;
            public string Role;
            public string Side;
            public bool Alive;
        }

        private void Awake()
        {
            var dir = Path.GetDirectoryName(typeof(WebMapController).Assembly.Location) ?? ".";
            _root = Path.GetFullPath(Path.Combine(dir, "web"));
            _calibrationPath = Path.Combine(_root, "calibration.txt");
        }

        private void Update()
        {
            try
            {
                if (!Plugin.WebMapEnabled.Value)
                {
                    StopServer();
                    return;
                }

                if (!_running)
                {
                    var nowStart = Time.unscaledTime;
                    if (nowStart < _nextStart) return;
                    _nextStart = nowStart + 5f;
                    StartServer();
                    if (!_running) return;
                }

                var now = Time.unscaledTime;
                var world = WorldTracker.Current;

                // a new raid means a new set of bodies. drop everything from the last one.
                if (!ReferenceEquals(world, _trackedWorld))
                {
                    _trackedWorld = world;
                    _seen.Clear();
                    _dead.Clear();
                }

                // exfils move about as often as the weather, so they get their own slower timer
                if (now >= _nextExfil)
                {
                    _nextExfil = now + 2f;
                    _exfilJson = world != null ? BuildExfils(world) : "[]";
                }

                // quest state changes as you loot and hand in, so it gets its own slow-ish tick
                if (now >= _nextQuest)
                {
                    _nextQuest = now + 10f;
                    _questJson = world != null
                        ? BuildQuestData(world)
                        : "{\"items\":[],\"quests\":[],\"zones\":[],\"loot\":[]}";
                }

                // walking container inventories is the expensive part, so it runs less often
                if (now >= _nextQuestContainer)
                {
                    _nextQuestContainer = now + 30f;
                    _questContainerJson = world != null ? BuildQuestContainers(world) : "[]";
                }

                // transits are fixed for the raid, so they get a slow timer and no per-tick cost
                if (now >= _nextTransit)
                {
                    _nextTransit = now + 10f;
                    _transitJson = world != null ? BuildTransits(world) : "[]";
                }

                // loot is expensive to serialize, so only build it while a page is actually
                // asking for it. ten seconds of grace covers a closed and reopened tab.
                if (now >= _nextLoot)
                {
                    _nextLoot = now + 5f;
                    if (world != null && DateTime.UtcNow < _lootWantedUntil) _lootJson = BuildLoot(world);
                }

                if (now < _nextPush) return;
                _nextPush = now + 1f / Mathf.Clamp(Plugin.WebMapRate.Value, 1f, 30f);
                _state = BuildState();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug("web map: " + ex.Message);
            }
        }

        private void StartServer()
        {
            try
            {
                var port = Math.Max(1024, Math.Min(65535, Plugin.WebMapPort.Value));
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _thread = new Thread(Serve)
                {
                    IsBackground = true,
                    Name = "VzorWebMap"
                };
                _thread.Start();
                Plugin.Logger.LogInfo("web map listening on http://127.0.0.1:" + port + "/");
            }
            catch (Exception ex)
            {
                _running = false;
                Plugin.Logger.LogError("web map could not bind port: " + ex.Message);
            }
        }

        private void StopServer()
        {
            if (!_running) return;

            _running = false;

            try { _listener.Stop(); } catch { }
            try { if (_thread != null) _thread.Join(500); } catch { }

            _listener = null;
            _thread = null;
            Plugin.Logger.LogInfo("web map stopped");
        }

        private void OnDestroy()
        {
            StopServer();
        }

        private void Serve()
        {
            while (_running)
            {
                TcpClient client;

                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    if (!_running) return;
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    Handle(client);
                }
                catch
                {
                    // a browser closing a tab mid request is not an emergency
                }
                finally
                {
                    try { client.Close(); } catch { }
                }
            }
        }

        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 2000;
            client.SendTimeout = 2000;

            using (var stream = client.GetStream())
            {
                var head = ReadRequest(stream, out var body, out var headerEnd);
                if (head == null)
                {
                    Respond(stream, 400, "text/plain", "bad request");
                    return;
                }

                var firstLine = head.Substring(0, head.IndexOf("\r\n", StringComparison.Ordinal));
                var parts = firstLine.Split(' ');
                if (parts.Length < 2)
                {
                    Respond(stream, 400, "text/plain", "bad request");
                    return;
                }

                var method = parts[0];
                var path = parts[1];
                var q = path.IndexOf('?');
                if (q >= 0) path = path.Substring(0, q);
                path = Uri.UnescapeDataString(path);

                if (path == "/" || path.Length == 0) path = "/index.html";

                if (path == "/state")
                {
                    RespondText(stream, _state, "application/json");
                    return;
                }

                if (path == "/loot")
                {
                    _lootWantedUntil = DateTime.UtcNow.AddSeconds(10);
                    RespondText(stream, _lootJson, "application/json");
                    return;
                }

                if (path == "/version")
                {
                    RespondText(stream, VersionStamp(), "application/json");
                    return;
                }

                if (path == "/calibration")
                {
                    if (method == "POST")
                    {
                        RespondText(stream, SaveCalibration(body), "application/json");
                        return;
                    }

                    RespondText(stream, ReadCalibration(), "application/json");
                    return;
                }

                if (method != "GET")
                {
                    Respond(stream, 405, "text/plain", "method not allowed");
                    return;
                }

                ServeFile(stream, path);
            }
        }

        private static string ReadRequest(NetworkStream stream, out string body, out int headerEnd)
        {
            body = string.Empty;
            headerEnd = -1;

            var buffer = new MemoryStream();
            var chunk = new byte[8192];

            while (buffer.Length < 128 * 1024)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;

                buffer.Write(chunk, 0, read);

                var bytes = buffer.GetBuffer();
                headerEnd = IndexOfHeaderEnd(bytes, (int)buffer.Length);
                if (headerEnd >= 0) break;
            }

            if (headerEnd < 0) return null;

            var all = buffer.ToArray();
            var header = Encoding.ASCII.GetString(all, 0, headerEnd);

            var length = 0;
            var lengthMatch = Regex.Match(header, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            if (lengthMatch.Success) int.TryParse(lengthMatch.Groups[1].Value, out length);

            var bodyStart = headerEnd + 4;
            if (length > 0 && length < 1024 * 1024)
            {
                var payload = new byte[length];
                var have = Math.Max(0, all.Length - bodyStart);
                if (have > 0) Buffer.BlockCopy(all, bodyStart, payload, 0, Math.Min(have, length));

                var offset = Math.Min(have, length);
                while (offset < length)
                {
                    var read = stream.Read(payload, offset, length - offset);
                    if (read <= 0) break;
                    offset += read;
                }

                body = Encoding.UTF8.GetString(payload, 0, length);
            }

            return header;
        }

        private static int IndexOfHeaderEnd(byte[] bytes, int length)
        {
            for (var i = 0; i + 3 < length; i++)
            {
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    return i;
            }

            return -1;
        }

        // used by the page to notice that app.js changed on disk, so edits apply without the
        // user having to remember to press f5
        private string VersionStamp()
        {
            try
            {
                var file = Path.Combine(_root, "app.js");
                return "{\"v\":\"" + File.GetLastWriteTimeUtc(file).Ticks + "\"}";
            }
            catch
            {
                return "{\"v\":\"0\"}";
            }
        }

        private void ServeFile(NetworkStream stream, string path)
        {
            var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_root, relative));

            // refuse anything that escaped the web folder, because of course people try
            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                Respond(stream, 404, "text/plain", "not found");
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(full);
            }
            catch
            {
                Respond(stream, 500, "text/plain", "read failed");
                return;
            }

            RespondBytes(stream, 200, ContentType(full), bytes);
        }

        private static string ContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();

            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private static void RespondText(NetworkStream stream, string text, string contentType)
        {
            RespondBytes(stream, 200, contentType, Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static void Respond(NetworkStream stream, int status, string contentType, string text)
        {
            RespondBytes(stream, status, contentType, Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static void RespondBytes(NetworkStream stream, int status, string contentType, byte[] body)
        {
            var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : status == 405 ? "Method Not Allowed" : "Error";

            var header = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                         "Content-Type: " + contentType + "\r\n" +
                         "Content-Length: " + body.Length + "\r\n" +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        // calibration lives in a plain text file, one map per line:
        // map rotation scale offsetX offsetY flipX flipY
        // readable, diffable and editable by hand, which beats json parsing in c# for this.
        private string ReadCalibration()
        {
            var sb = new StringBuilder("{");
            var first = true;

            try
            {
                if (File.Exists(_calibrationPath))
                {
                    foreach (var line in File.ReadAllLines(_calibrationPath))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 7) continue;

                        if (!first) sb.Append(',');
                        first = false;

                        sb.Append('"').Append(JsonEscape(parts[0])).Append("\":{");
                        sb.Append("\"rotation\":").Append(Num(parts[1]));
                        sb.Append(",\"scale\":").Append(Num(parts[2]));
                        sb.Append(",\"offsetX\":").Append(Num(parts[3]));
                        sb.Append(",\"offsetY\":").Append(Num(parts[4]));
                        sb.Append(",\"flipX\":").Append(Num(parts[5]));
                        sb.Append(",\"flipY\":").Append(Num(parts[6]));
                        sb.Append('}');
                    }
                }
            }
            catch
            {
                // a corrupt calibration file just means defaults
            }

            sb.Append('}');
            return sb.ToString();
        }

        private string SaveCalibration(string body)
        {
            var map = Regex.Match(body ?? string.Empty, "\"map\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
            if (string.IsNullOrEmpty(map)) return "{\"ok\":false}";

            var values = new[]
            {
                Field(body, "rotation", 0f),
                Field(body, "scale", 1f),
                Field(body, "offsetX", 0f),
                Field(body, "offsetY", 0f),
                Field(body, "flipX", 1f),
                Field(body, "flipY", 1f)
            };

            var lines = new List<string>();

            try
            {
                if (File.Exists(_calibrationPath))
                {
                    foreach (var line in File.ReadAllLines(_calibrationPath))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 1) continue;
                        if (parts[0] == map) continue;
                        lines.Add(line);
                    }
                }

                var sb = new StringBuilder();
                sb.Append(map);
                foreach (var v in values) sb.Append(' ').Append(Num(v));

                lines.Add(sb.ToString());
                File.WriteAllLines(_calibrationPath, lines.ToArray());
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}";
            }

            return "{\"ok\":true}";
        }

        private static float Field(string body, string name, float fallback)
        {
            var match = Regex.Match(body ?? string.Empty,
                "\"" + name + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");

            if (!match.Success) return fallback;

            return float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value) ? value : fallback;
        }

        private string BuildState()
        {
            var world = WorldTracker.Current;
            if (world == null) return "{\"inRaid\":false}";

            // the hideout has a game world but it is not a raid. nothing on the map applies there.
            if (string.Equals(SafeLocationId(world), "hideout", StringComparison.OrdinalIgnoreCase))
                return "{\"inRaid\":false}";

            var me = world.MainPlayer;
            var players = world.AllAlivePlayersList;
            var sb = new StringBuilder(4096);
            var now = Time.unscaledTime;
            var aliveNow = new HashSet<string>();

            sb.Append("{\"inRaid\":true");
            sb.Append(",\"map\":\"").Append(JsonEscape(SafeLocationId(world))).Append('"');
            sb.Append(",\"rate\":").Append(Num(Mathf.Clamp(Plugin.WebMapRate.Value, 1f, 30f)));

            if (me != null)
            {
                sb.Append(",\"x\":").Append(Num(me.Position.x));
                sb.Append(",\"y\":").Append(Num(me.Position.y));
                sb.Append(",\"z\":").Append(Num(me.Position.z));
                sb.Append(",\"yaw\":").Append(Num(me.transform.eulerAngles.y));
            }

            sb.Append(",\"players\":[");

            var first = true;
            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    try
                    {
                        var p = players[i];
                        if (p == null) continue;

                        var info = p.Profile != null ? p.Profile.Info : null;
                        var role = "?";
                        var side = "?";

                        if (info != null)
                        {
                            side = info.Side.ToString();

                            var settings = info.Settings;
                            if (settings != null) role = settings.Role.ToString();
                        }

                        // remember every entity we have seen alive, and where
                        var key = EntityKey(p);
                        aliveNow.Add(key);

                        if (_seen.TryGetValue(key, out var tracked))
                        {
                            tracked.X = p.Position.x;
                            tracked.Y = p.Position.y;
                            tracked.Z = p.Position.z;
                            tracked.Role = role;
                            tracked.Side = side;
                            tracked.Alive = true;
                        }
                        else
                        {
                            _seen[key] = new Seen
                            {
                                X = p.Position.x,
                                Y = p.Position.y,
                                Z = p.Position.z,
                                Role = role,
                                Side = side,
                                Alive = true
                            };
                        }

                        if (!first) sb.Append(',');
                        first = false;

                        sb.Append("{\"x\":").Append(Num(p.Position.x));
                        sb.Append(",\"y\":").Append(Num(p.Position.y));
                        sb.Append(",\"z\":").Append(Num(p.Position.z));
                        sb.Append(",\"name\":\"").Append(JsonEscape(info != null ? info.Nickname : "?")).Append('"');
                        sb.Append(",\"role\":\"").Append(JsonEscape(role)).Append('"');
                        sb.Append(",\"side\":\"").Append(JsonEscape(side)).Append('"');
                        sb.Append(",\"ai\":").Append(p.IsAI ? "true" : "false");
                        sb.Append(",\"me\":").Append(p.IsYourPlayer ? "true" : "false");

                        if (me != null)
                            sb.Append(",\"dist\":").Append(Num(Vector3.Distance(me.Position, p.Position)));

                        sb.Append('}');
                    }
                    catch
                    {
                        // a body that despawned mid loop is normal
                    }
                }
            }

            // anything that was alive on the last push and is not now has left the map. keep the
            // last position and let it stand as an x, because a body that despawned is still
            // worth knowing about.
            foreach (var pair in _seen)
            {
                if (pair.Value.Alive && !aliveNow.Contains(pair.Key))
                {
                    pair.Value.Alive = false;
                    _dead.Add(pair.Value);
                }
            }

            while (_dead.Count > 60) _dead.RemoveAt(0);

            sb.Append("],\"exfils\":").Append(_exfilJson);
            sb.Append(",\"transits\":").Append(_transitJson);
            sb.Append(",\"quest\":").Append(_questJson);

            // bodies are entities that were alive on an earlier push and are gone now. the game
            // removes corpses before we can always read them, but it cannot hide the fact that
            // something stopped existing, so we mark the last place we saw it.
            sb.Append(",\"bodies\":[");

            var firstBody = true;
            for (var i = 0; i < _dead.Count; i++)
            {
                var body = _dead[i];

                if (!firstBody) sb.Append(',');
                firstBody = false;

                sb.Append("{\"x\":").Append(Num(body.X));
                sb.Append(",\"z\":").Append(Num(body.Z));
                sb.Append(",\"role\":\"").Append(JsonEscape(body.Role)).Append('"');
                sb.Append(",\"side\":\"").Append(JsonEscape(body.Side)).Append('"');
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        // exit names are locale ids as often as they are prose. "un-sec" and "wood_sniper_exit"
        // are keys, the game knows the human name, so ask it.
        private static string Localize(string name)
        {
            try
            {
                var manager = LocalizationManager.Instance;
                if (manager == null) return name;

                var localized = manager.LocalizedValue(name);
                if (!string.IsNullOrEmpty(localized)) return localized;
            }
            catch
            {
                // localization not loaded yet
            }

            return name;
        }

        // what is inside the containers on this map, but only items an active quest wants.
        // reading a container's inventory is cheap once and expensive across a whole map, hence
        // the thirty second timer and the cap.
        private string BuildQuestContainers(GameWorld world)
        {
            var sb = new StringBuilder("[");
            var first = true;

            if (_wantedQuestItems.Count == 0)
            {
                sb.Append(']');
                return sb.ToString();
            }

            var wanted = new HashSet<string>(_wantedQuestItems);

            try
            {
                var list = world.LootList;
                if (list != null)
                {
                    var found = 0;

                    for (var i = 0; i < list.Count && found < 200; i++)
                    {
                        LootableContainer container = null;

                        try
                        {
                            var entry = list[i];
                            if (entry != null) container = entry.TryCast<LootableContainer>();
                        }
                        catch
                        {
                            continue;
                        }

                        if (container == null) continue;

                        var owner = container.ItemOwner;
                        if (owner == null) continue;

                        var root = owner.RootItem;
                        if (root == null) continue;

                        var names = new List<string>();

                        try
                        {
                            var items = EFT.InventoryLogic.ItemExtensions.GetAllItems(root);
                            if (items == null) continue;

                            var enumerator = items.GetEnumerator();
                            while (enumerator.MoveNext())
                            {
                                var child = enumerator.Current;
                                if (child == null) continue;

                                var id = child.StringTemplateId;
                                if (string.IsNullOrEmpty(id) || !wanted.Contains(id)) continue;

                                var name = LootEspController.ResolveName(id, child, child.Name);
                                if (!string.IsNullOrEmpty(name)) names.Add(name);
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        if (names.Count == 0) continue;

                        var position = container.transform.position;
                        if (!first) sb.Append(',');
                        first = false;
                        found++;

                        sb.Append("{\"x\":").Append(Num(position.x));
                        sb.Append(",\"z\":").Append(Num(position.z));
                        sb.Append(",\"n\":\"").Append(JsonEscape(string.Join(", ", names.ToArray()))).Append('"');
                        sb.Append('}');
                    }
                }
            }
            catch
            {
                // loot list mid update
            }

            sb.Append(']');
            return sb.ToString();
        }

        // quests worth showing: those pinned to this map, plus the "any" ones that actually
        // name a zone on this map. everything else is noise for the raid you are in.
        private string BuildQuestData(GameWorld world)
        {
            var locationId = SafeLocationId(world);
            var profile = world.MainPlayer != null ? world.MainPlayer.Profile : null;
            var candidates = new List<QuestEntry>();

            if (profile != null)
            {
                try
                {
                    var data = profile.QuestsData;
                    if (data != null)
                    {
                        for (var i = 0; i < data.Count; i++)
                        {
                            var entry = data[i];
                            if (entry == null) continue;
                            if (entry.Status != EQuestStatus.Started) continue;

                            var quest = entry.Quest;
                            if (quest == null) continue;

                            var template = quest.Template;
                            var questLocation = template != null ? template.LocationId : null;

                            var candidate = new QuestEntry
                            {
                                Name = QuestDisplayName(quest),
                                Status = entry.Status.ToString(),
                                Location = questLocation,
                                IsAny = string.IsNullOrEmpty(questLocation) ||
                                        questLocation.Equals("any", StringComparison.OrdinalIgnoreCase),
                                Ids = new List<string>(),
                                Zones = new List<string>(),
                                Locations = new List<string>(),
                                Needs = new List<QuestNeed>()
                            };

                            var conditions = quest.NecessaryConditions;
                            if (conditions != null)
                            {
                                var enumerator = conditions.GetEnumerator();
                                while (enumerator.MoveNext())
                                {
                                    CollectCondition(enumerator.Current, candidate);
                                }
                            }

                            candidates.Add(candidate);
                        }
                    }
                }
                catch
                {
                    // quest data mid update
                }
            }

            // one trigger scan, used both to decide relevance and to place zone markers
            var presentZones = new Dictionary<string, Vector3>();

            if (candidates.Count > 0)
            {
                try
                {
                    var place = UnityEngine.Object.FindObjectsOfType<PlaceItemTrigger>();
                    if (place != null)
                    {
                        for (var i = 0; i < place.Length; i++)
                        {
                            var trigger = place[i];
                            if (trigger == null) continue;

                            var id = trigger.TriggerId;
                            if (string.IsNullOrEmpty(id)) id = trigger.Id;
                            if (string.IsNullOrEmpty(id) || presentZones.ContainsKey(id)) continue;

                            presentZones[id] = trigger.transform.position;
                        }
                    }

                    var visits = UnityEngine.Object.FindObjectsOfType<QuestTrigger>();
                    if (visits != null)
                    {
                        for (var i = 0; i < visits.Length; i++)
                        {
                            var trigger = visits[i];
                            if (trigger == null) continue;

                            var id = trigger.Id;
                            if (string.IsNullOrEmpty(id) || presentZones.ContainsKey(id)) continue;

                            presentZones[id] = trigger.transform.position;
                        }
                    }
                }
                catch
                {
                    // triggers not spawned yet
                }
            }

            var included = new List<QuestEntry>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var required = new Dictionary<string, int>();
            var zoneNames = new Dictionary<string, string>();

            foreach (var candidate in candidates)
            {
                bool include;

                include = false;

                // a location condition naming this map is the strongest signal there is
                var here = CurrentLocationIds(locationId);

                foreach (var location in candidate.Locations)
                {
                    if (!here.Contains(location)) continue;
                    include = true;
                    break;
                }

                if (!include && !candidate.IsAny)
                    include = QuestAppliesHere(candidate.Location, locationId);

                if (!include && candidate.IsAny)
                {
                    // last resort for an "any" quest: does it name a zone that exists here
                    foreach (var zone in candidate.Zones)
                    {
                        if (!presentZones.ContainsKey(zone)) continue;
                        include = true;
                        break;
                    }
                }

                if (!include) continue;
                if (!seenNames.Add(candidate.Name)) continue;

                included.Add(candidate);

                foreach (var need in candidate.Needs)
                {
                    if (!required.TryGetValue(need.Id, out var existing) || need.Amount > existing)
                        required[need.Id] = need.Amount;
                }

                foreach (var zone in candidate.Zones)
                {
                    if (presentZones.ContainsKey(zone) && !zoneNames.ContainsKey(zone))
                        zoneNames[zone] = candidate.Name;
                }
            }

            // count what you are actually carrying, and only chase the shortfall
            var carried = CountCarried(profile);

            _wantedQuestItems.Clear();

            foreach (var pair in required)
            {
                carried.TryGetValue(pair.Key, out var have);
                if (have < pair.Value) _wantedQuestItems.Add(pair.Key);
            }

            var quests = new StringBuilder("[");

            for (var i = 0; i < included.Count; i++)
            {
                if (i > 0) quests.Append(',');

                quests.Append("{\"n\":\"").Append(JsonEscape(included[i].Name)).Append('"');
                quests.Append(",\"s\":\"").Append(JsonEscape(included[i].Status)).Append('"');
                quests.Append(",\"story\":").Append(included[i].IsAny ? "true" : "false");
                quests.Append(",\"i\":[");

                var firstItem = true;
                var seen = new HashSet<string>();

                foreach (var id in included[i].Ids)
                {
                    if (!_wantedQuestItems.Contains(id) || !seen.Add(id)) continue;

                    var itemName = LootEspController.ResolveName(id, null, id);
                    if (string.IsNullOrEmpty(itemName)) continue;

                    if (!firstItem) quests.Append(',');
                    firstItem = false;

                    quests.Append('"').Append(JsonEscape(itemName)).Append('"');
                }

                quests.Append("]}");
            }

            quests.Append(']');

            var loot = new StringBuilder("[");
            var firstLoot = true;

            if (_wantedQuestItems.Count > 0)
            {
                try
                {
                    var list = world.LootList;
                    if (list != null)
                    {
                        var found = 0;

                        for (var i = 0; i < list.Count && found < 500; i++)
                        {
                            LootItem item = null;

                            try
                            {
                                var entry = list[i];
                                if (entry != null) item = entry.TryCast<LootItem>();
                            }
                            catch
                            {
                                continue;
                            }

                            if (item == null) continue;
                            if (!_wantedQuestItems.Contains(item.TemplateId)) continue;

                            var position = item.transform.position;
                            if (!firstLoot) loot.Append(',');
                            firstLoot = false;
                            found++;

                            loot.Append("{\"x\":").Append(Num(position.x));
                            loot.Append(",\"z\":").Append(Num(position.z));
                            loot.Append(",\"n\":\"").Append(JsonEscape(LootEspController.ResolveName(
                                item.TemplateId, item.Item, item.Name))).Append('"');
                            loot.Append('}');
                        }
                    }
                }
                catch
                {
                    // loot list not ready
                }
            }

            loot.Append(']');

            var zones = new StringBuilder("[");
            var firstZone = true;

            foreach (var pair in presentZones)
            {
                if (!zoneNames.TryGetValue(pair.Key, out var label)) continue;
                AppendZone(zones, ref firstZone, pair.Value, pair.Key, zoneNames);
            }

            zones.Append(']');

            var sb = new StringBuilder(4096);
            sb.Append("{\"items\":[");

            var firstWanted = true;
            foreach (var id in _wantedQuestItems)
            {
                if (!firstWanted) sb.Append(',');
                firstWanted = false;
                sb.Append('"').Append(JsonEscape(id)).Append('"');
            }

            sb.Append("],\"quests\":").Append(quests);
            sb.Append(",\"zones\":").Append(zones);
            sb.Append(",\"loot\":").Append(loot);
            sb.Append(",\"containers\":").Append(_questContainerJson);
            sb.Append('}');

            return sb.ToString();
        }
        // quest locations are mongoids, not folder names, so "bigmap" has to be translated
        // through the location data before it can be compared.
        private static Dictionary<string, string> _locationIds;
        private static Dictionary<string, string> _locationNames;

        private static string LocationMongoId(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;

            if (_locationIds == null)
            {
                _locationIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var pluginDir = Path.GetDirectoryName(typeof(WebMapController).Assembly.Location);
                    var gameRoot = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", ".."));
                    var locations = Path.Combine(gameRoot, "SPT_Runtime", "SPT_Data", "database", "locations");

                    _locationNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (Directory.Exists(locations))
                    {
                        foreach (var dir in Directory.GetDirectories(locations))
                        {
                            var file = Path.Combine(dir, "base.json");
                            if (!File.Exists(file)) continue;

                            var folderName = Path.GetFileName(dir);
                            var hasId = false;
                            var hasName = false;

                            foreach (var line in File.ReadLines(file))
                            {
                                if (!hasId)
                                {
                                    var id = Regex.Match(line, "\"_Id\"\\s*:\\s*\"([0-9a-fA-F]{24})\"");
                                    if (id.Success)
                                    {
                                        _locationIds[folderName] = id.Groups[1].Value;
                                        hasId = true;
                                    }
                                }

                                if (!hasName)
                                {
                                    // root level only: nested keys are indented further
                                    var name = Regex.Match(line, "^  \"Name\"\\s*:\\s*\"([^\"]+)\"");
                                    if (name.Success)
                                    {
                                        _locationNames[folderName] = name.Groups[1].Value;
                                        hasName = true;
                                    }
                                }

                                if (hasId && hasName) break;
                            }
                        }
                    }
                }
                catch
                {
                    // no location data, fall back to accepting everything
                }
            }

            return _locationIds.TryGetValue(folder, out var found) ? found : null;
        }

        private static string BaseFolder(string folder)
        {
            switch (folder.ToLowerInvariant())
            {
                case "lighthouse2": return "lighthouse";
                case "laboratory_dark": return "laboratory";
                case "terminal_ui": return "terminal";
                case "sandbox_high": return "sandbox";
                case "sandbox_start": return "sandbox";
                default: return folder;
            }
        }

        // every location id that counts as "here": the map itself, its base variant, and the
        // other factory shift.
        private static HashSet<string> CurrentLocationIds(string locationId)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddLocationKeys(locationId, keys);
            AddLocationKeys(BaseFolder(locationId), keys);

            if (locationId.StartsWith("factory4", StringComparison.OrdinalIgnoreCase))
            {
                AddLocationKeys("factory4_day", keys);
                AddLocationKeys("factory4_night", keys);
            }

            return keys;
        }

        // location conditions name a map ("Interchange"), quest templates use a mongoid, and
        // our own ids are folder names. accept all three spellings.
        private static void AddLocationKeys(string folder, HashSet<string> keys)
        {
            if (string.IsNullOrEmpty(folder)) return;

            LocationMongoId(folder);

            keys.Add(folder);

            if (_locationIds != null && _locationIds.TryGetValue(folder, out var id) && !string.IsNullOrEmpty(id))
                keys.Add(id);

            if (_locationNames != null && _locationNames.TryGetValue(folder, out var name) && !string.IsNullOrEmpty(name))
                keys.Add(name);
        }

        private static bool QuestAppliesHere(string questLocation, string locationId)
        {
            if (string.IsNullOrEmpty(questLocation)) return false;
            if (questLocation.Equals("any", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(locationId)) return true;

            if (questLocation.Equals(locationId, StringComparison.OrdinalIgnoreCase)) return true;

            var current = LocationMongoId(locationId);
            if (!string.IsNullOrEmpty(current) && questLocation.Equals(current, StringComparison.OrdinalIgnoreCase))
                return true;

            // variants of the same map share quests with their base folder
            var baseId = LocationMongoId(BaseFolder(locationId));
            if (!string.IsNullOrEmpty(baseId) && questLocation.Equals(baseId, StringComparison.OrdinalIgnoreCase))
                return true;

            // day and night factory are the same building
            if (locationId.StartsWith("factory4", StringComparison.OrdinalIgnoreCase))
            {
                var day = LocationMongoId("factory4_day");
                var night = LocationMongoId("factory4_night");

                if ((!string.IsNullOrEmpty(day) && questLocation.Equals(day, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(night) && questLocation.Equals(night, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            // if we could not read the location data at all, do not hide anything
            return string.IsNullOrEmpty(current);
        }

        // quest objectives nest: a CounterCreator holds the real conditions, which is where the
        // Location condition lives. walk down into it or the map test never sees anything.
        private static void CollectCondition(Condition condition, QuestEntry candidate)
        {
            if (condition == null) return;

            var multiple = condition.TryCast<ConditionMultipleTargets>();
            if (multiple != null && multiple.target != null)
            {
                var need = (int)condition.value;

                for (var t = 0; t < multiple.target.Length; t++)
                {
                    var id = multiple.target[t];
                    if (string.IsNullOrEmpty(id)) continue;

                    candidate.Ids.Add(id);
                    candidate.Needs.Add(new QuestNeed { Id = id, Amount = need });
                }
            }

            var locationCondition = condition.TryCast<ConditionLocation>();
            if (locationCondition != null && locationCondition.target != null)
            {
                for (var t = 0; t < locationCondition.target.Length; t++)
                {
                    var id = locationCondition.target[t];
                    if (!string.IsNullOrEmpty(id) && !candidate.Locations.Contains(id))
                        candidate.Locations.Add(id);
                }
            }

            CollectZoneIds(condition.TryCast<ConditionZone>(), candidate.Zones);
            CollectZoneIds(condition.TryCast<ConditionInZone>(), candidate.Zones);

            var visit = condition.TryCast<ConditionVisitPlace>();
            if (visit != null && !string.IsNullOrEmpty(visit.target) && !candidate.Zones.Contains(visit.target))
                candidate.Zones.Add(visit.target);

            var counter = condition.TryCast<ConditionCounterCreator>();
            if (counter != null)
            {
                try
                {
                    var nested = counter.Conditions;
                    if (nested != null)
                    {
                        var enumerator = nested.GetEnumerator();
                        while (enumerator.MoveNext()) CollectCondition(enumerator.Current, candidate);
                    }
                }
                catch
                {
                    // nested conditions unreadable
                }
            }
        }

        private sealed class QuestEntry
        {
            public string Name;
            public string Status;
            public string Location;
            public bool IsAny;
            public List<string> Ids;
            public List<string> Zones;
            public List<string> Locations;
            public List<QuestNeed> Needs;
        }

        private sealed class QuestNeed
        {
            public string Id;
            public int Amount;
        }

        // what the player is carrying, by template id. equipment covers pockets, rig and
        // backpack; quest items live in their own container and count too.
        private static Dictionary<string, int> CountCarried(Profile profile)
        {
            var counts = new Dictionary<string, int>();

            if (profile == null) return counts;

            try
            {
                var inventory = profile.Inventory;
                if (inventory == null) return counts;

                CountInto(inventory.Equipment, counts);
                CountInto(inventory.QuestRaidItems, counts);
            }
            catch
            {
                // inventory mid change
            }

            return counts;
        }

        private static void CountInto(EFT.InventoryLogic.Item root, Dictionary<string, int> counts)
        {
            if (root == null) return;

            try
            {
                var items = EFT.InventoryLogic.ItemExtensions.GetAllItems(root);
                if (items == null) return;

                var enumerator = items.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var child = enumerator.Current;
                    if (child == null) continue;

                    var id = child.StringTemplateId;
                    if (string.IsNullOrEmpty(id)) continue;

                    counts.TryGetValue(id, out var count);
                    counts[id] = count + 1;
                }
            }
            catch
            {
                // container not readable
            }
        }
        private static void CollectZoneIds(ConditionZone zone, List<string> into)
        {
            if (zone == null || zone.zoneIds == null) return;

            for (var i = 0; i < zone.zoneIds.Length; i++)
            {
                var id = zone.zoneIds[i];
                if (!string.IsNullOrEmpty(id) && !into.Contains(id)) into.Add(id);
            }
        }

        private static void CollectZoneIds(ConditionInZone zone, List<string> into)
        {
            if (zone == null || zone.zoneIds == null) return;

            for (var i = 0; i < zone.zoneIds.Length; i++)
            {
                var id = zone.zoneIds[i];
                if (!string.IsNullOrEmpty(id) && !into.Contains(id)) into.Add(id);
            }
        }

        private static void AppendZone(StringBuilder sb, ref bool first, Vector3 position,
            string id, Dictionary<string, string> zoneNames)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!zoneNames.TryGetValue(id, out var label)) return;

            if (!first) sb.Append(',');
            first = false;

            sb.Append("{\"x\":").Append(Num(position.x));
            sb.Append(",\"z\":").Append(Num(position.z));
            sb.Append(",\"n\":\"").Append(JsonEscape(label)).Append('"');
            sb.Append('}');
        }

        private static string QuestDisplayName(Quest quest)
        {
            try
            {
                var name = quest.Name;
                if (!string.IsNullOrEmpty(name)) return name;

                var template = quest.Template;
                return template != null ? template.Name : "Quest";
            }
            catch
            {
                return "Quest";
            }
        }

        private static bool IsTransitPlaceholder(string value)
        {
            if (string.IsNullOrEmpty(value)) return true;

            var trimmed = value.Trim();
            if (trimmed.Length < 6) return false;

            return trimmed.StartsWith("TRANSIT", StringComparison.OrdinalIgnoreCase) &&
                   char.IsDigit(trimmed[trimmed.Length - 1]);
        }

        private string BuildTransits(GameWorld world)
        {
            var sb = new StringBuilder(1024);
            sb.Append('[');

            var first = true;

            try
            {
                var controller = world.TransitController;
                var points = controller != null ? controller.pointsById : null;

                if (points != null)
                {
                    var enumerator = points.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var point = enumerator.Current.Value;
                        if (point == null) continue;

                        try
                        {
                            var parameters = point.parameters;
                            if (parameters == null) continue;

                            // name is the "TRANSIT01" placeholder, description is "Transit to X".
                            // prefer the description and let the placeholder die quietly.
                            var description = parameters.description;
                            var label = Localize(description);

                            if (string.IsNullOrEmpty(label) || label == description)
                            {
                                var fallback = Localize(parameters.name);
                                label = !string.IsNullOrEmpty(fallback) && !IsTransitPlaceholder(fallback)
                                    ? fallback
                                    : "Transit";
                            }

                            // target is a mongoid for the destination location, not a name
                            var target = string.Empty;

                            var position = point.transform.position;

                            if (!first) sb.Append(',');
                            first = false;

                            sb.Append("{\"x\":").Append(Num(position.x));
                            sb.Append(",\"z\":").Append(Num(position.z));
                            sb.Append(",\"n\":\"").Append(JsonEscape(label)).Append('"');
                            sb.Append(",\"t\":\"").Append(JsonEscape(target ?? string.Empty)).Append('"');
                            sb.Append('}');
                        }
                        catch
                        {
                            // one malformed transit is not a reason to lose the rest
                        }
                    }
                }
            }
            catch
            {
                // no transit controller on this map
            }

            sb.Append(']');
            return sb.ToString();
        }

        // every loose item in the raid, capped so a big map cannot turn this into a megabyte of
        // json. the page filters and sorts; this only reports what exists and what it is worth.
        private string BuildLoot(GameWorld world)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append('[');

            var first = true;
            var count = 0;

            try
            {
                var list = world.LootList;
                if (list != null)
                {
                    for (var i = 0; i < list.Count && count < 2000; i++)
                    {
                        LootItem item = null;

                        try
                        {
                            var entry = list[i];
                            if (entry != null) item = entry.TryCast<LootItem>();
                        }
                        catch
                        {
                            continue;
                        }

                        if (item == null) continue;

                        var position = item.transform.position;
                        var templateId = item.TemplateId;

                        var price = 0;
                        try
                        {
                            if (PriceDatabase.TryGet(templateId, out var marketPrice)) price = marketPrice;
                        }
                        catch
                        {
                            // price table not loaded for this file
                        }

                        var type = "?";
                        try { type = item.GetType().Name; } catch { }

                        // item.Name is the raw template id for loose loot. the esp already knows
                        // how to turn a template id into a locale name, so borrow that.
                        var name = item.Name;
                        try { name = LootEspController.ResolveName(templateId, item.Item, item.Name); } catch { }

                        if (!first) sb.Append(',');
                        first = false;
                        count++;

                        sb.Append("{\"x\":").Append(Num(position.x));
                        sb.Append(",\"y\":").Append(Num(position.y));
                        sb.Append(",\"z\":").Append(Num(position.z));
                        sb.Append(",\"n\":\"").Append(JsonEscape(name)).Append('"');
                        sb.Append(",\"p\":").Append(price);
                        sb.Append(",\"t\":\"").Append(JsonEscape(type)).Append('"');
                        sb.Append('}');
                    }
                }
            }
            catch
            {
                // loot hydra not ready on this map
            }

            sb.Append(']');
            return sb.ToString();
        }

        private string BuildExfils(GameWorld world)
        {
            var sb = new StringBuilder("[");
            var first = true;

            try
            {
                var controller = world.ExfiltrationController;
                var points = controller != null ? controller.ExfiltrationPoints : null;

                if (points != null)
                {
                    for (var i = 0; i < points.Length; i++)
                    {
                        var point = points[i];
                        if (point == null) continue;

                        var settings = point.Settings;
                        if (settings == null) continue;

                        var name = settings.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        name = Localize(name);

                        var status = point.Status;
                        if (status == EExfiltrationStatus.NotPresent || status == EExfiltrationStatus.Hidden)
                            continue;

                        var position = point.transform.position;
                        var open = status == EExfiltrationStatus.RegularMode ||
                                   status == EExfiltrationStatus.Countdown;

                        if (!first) sb.Append(',');
                        first = false;

                        sb.Append("{\"x\":").Append(Num(position.x));
                        sb.Append(",\"z\":").Append(Num(position.z));
                        sb.Append(",\"name\":\"").Append(JsonEscape(name)).Append('"');
                        sb.Append(",\"open\":").Append(open ? "true" : "false").Append('}');
                    }
                }
            }
            catch
            {
                // exfil controller not ready on this map
            }

            sb.Append(']');
            return sb.ToString();
        }

        // profile id is stable for the life of an entity. instance id is the fallback for bots
        // whose profile has not been built yet.
        private static string EntityKey(Player player)
        {
            try
            {
                var profile = player.Profile;
                if (profile != null)
                {
                    var id = profile.Id.ToString();
                    if (!string.IsNullOrEmpty(id) && id != "0") return id;
                }
            }
            catch
            {
                // profile mid construction
            }

            return "iid" + player.GetInstanceID();
        }

        private static string SafeLocationId(GameWorld world)
        {
            try
            {
                return world.LocationId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Num(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Num(string value)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? Num(parsed)
                : "0";
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length + 8);

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
