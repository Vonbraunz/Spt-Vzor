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

            var me = world.MainPlayer;
            var players = world.AllAlivePlayersList;
            var sb = new StringBuilder(4096);

            sb.Append("{\"inRaid\":true");
            sb.Append(",\"map\":\"").Append(JsonEscape(SafeLocationId(world))).Append('"');

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

            sb.Append("]}");
            return sb.ToString();
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
