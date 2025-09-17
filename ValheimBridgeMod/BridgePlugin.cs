using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimBridgeMod
{
    [BepInPlugin("dakota.valheim.streambridge", "Stream Bridge", "1.0.0")]
    public class BridgePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource LogS;

        // ---------- Main-thread dispatcher ----------
        private static readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        // ---------- HTTP ----------
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        // ---------- Services ----------
        private SpawnService _spawn;
        private ItemService _items;
        private EffectService _effects;

        // ---------- Configs  ----------
        private ConfigFile _cfgFile;
        private ConfigEntry<int> CfgPort;
        private ConfigEntry<int> CfgMaxSpawned;
        private ConfigEntry<float> CfgMaxDelay;
        private ConfigEntry<float> CfgMaxTTL;
        private ConfigEntry<int> CfgMaxStars;
        private ConfigEntry<bool> CfgNotify;

        // Exposed for services / UI
        public static bool NotifyEnabled { get; private set; }

        // Static accessors (avoid reflection from services)
        private static BridgePlugin _self;
        public static int MaxStars => _self?.CfgMaxStars?.Value ?? 2;
        public static float MaxDelay => _self?.CfgMaxDelay?.Value ?? 30f;
        public static float MaxTTL => _self?.CfgMaxTTL?.Value ?? 600f;

        // ---------- Lifecycle ----------
        void Awake()
        {
            _self = this;
            LogS = Logger;
            new Harmony("dakota.valheim.streambridge").PatchAll();

            BindConfigToCustomFile(); // custom path + defaults

            _spawn = new SpawnService(this, Logger);
            _items = new ItemService(Logger);
            _effects = new EffectService(this, Logger);

            NotifyEnabled = CfgNotify.Value;

            try
            {
                _cts = new CancellationTokenSource();
                StartHttpServer(CfgPort.Value, _cts.Token);
                Logger.LogInfo($"StreamBridge ready at http://127.0.0.1:{CfgPort.Value}/command");
            }
            catch (Exception e)
            {
                Logger.LogError($"HTTP server failed to start: {e}");
            }
        }

        void OnDestroy()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Close();
                _cfgFile?.Save();
            }
            catch { }
        }

        // Unity is not thread-safe: background HTTP work must enqueue actions here.
        // Update() drains the queue to ensure all engine calls run on the main thread.
        void Update()
        {
            while (_mainThread.TryDequeue(out var act))
            {
                try { act(); }
                catch (Exception e) { Logger.LogError(e); }
            }
        }

        // ---------- CORS ----------
        // Local tooling convenience: wildcard CORS, POST + OPTIONS only.
        // If you ever expose the port, front it with your own auth / origin filtering.
        private static void SetCors(HttpListenerRequest req, HttpListenerResponse res)
        {
            // For local tooling, wildcard is fine (no credentials used)
            res.Headers["Access-Control-Allow-Origin"] = "*";
            res.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
            // Optional: cache preflight for a while
            res.Headers["Access-Control-Max-Age"] = "86400";
        }

        private static bool HandleCorsPreflight(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (!string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return false;

            SetCors(req, res);
            res.StatusCode = 204;  // No Content
            try { res.Close(); } catch { }
            return true;
        }

        // ---------- Config (custom file under BepInEx\StreamBridge) ----------
        // We keep a dedicated config file (outside the default BepInEx configs)
        // so users can spot and edit StreamBridge settings easily.
        private void BindConfigToCustomFile()
        {
            string root = Paths.BepInExRootPath; // e.g., <Game>\BepInEx
            string dir = Path.Combine(root, "StreamBridge");
            Directory.CreateDirectory(dir);

            string cfgPath = Path.Combine(dir, "StreamBridge.cfg");
            _cfgFile = new ConfigFile(cfgPath, saveOnInit: true);

            // Port=28575
            CfgPort = _cfgFile.Bind("Network", "Port", 28575, "Local HTTP port (loopback only).");

            // MaxSpawned=50
            CfgMaxSpawned = _cfgFile.Bind("Limits", "MaxSpawned", 50, "Maximum spawn count per command.");

            // MaxDelay=30
            CfgMaxDelay = _cfgFile.Bind("Limits", "MaxDelay", 30f, "Maximum stagger delay (seconds) per command.");

            // MaxTTL=600
            CfgMaxTTL = _cfgFile.Bind("Limits", "MaxTTL", 600f, "Maximum time-to-live (seconds) for spawned entities.");

            // MaxStars=2
            CfgMaxStars = _cfgFile.Bind("Limits", "MaxStars", 2, "Maximum star level allowed (0..2).");

            // Notify=TRUE (changed default)
            CfgNotify = _cfgFile.Bind("UI", "Notify", true, "Show in-game HUD toasts.");
        }

        // ---------- HTTP server ----------
        // Bind to loopback only.
        private void StartHttpServer(int port, CancellationToken token)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/command/");
            _listener.Prefixes.Add($"http://localhost:{port}/command/");
            _listener.Start();

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    HttpListenerContext ctx = null;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch when (token.IsCancellationRequested) { break; }
                    catch (Exception e) { Logger.LogError(e); continue; }

                    _ = HandleRequest(ctx);
                }
            }, token);
        }

        // ---------- Request handling ----------
        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            SetCors(req, res);
            if (HandleCorsPreflight(req, res)) return;

            if (req.HttpMethod != "POST")
            {
                await WriteJson(res, new { ok = false, error = "bad_request", message = "POST required." }, 400);
                return;
            }

            if (!req.Url.AbsolutePath.TrimEnd('/').EndsWith("/command", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJson(res, new { ok = false, error = "not_found", message = "Use /command endpoint." }, 404);
                return;
            }

            string body;
            try
            {
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await sr.ReadToEndAsync();
                }
            }
            catch
            {
                await WriteJson(res, new { ok = false, error = "bad_request", message = "Failed to read request body." }, 400);
                return;
            }

            JObject doc;
            try { doc = JObject.Parse(body); }
            catch
            {
                await WriteJson(res, new { ok = false, error = "bad_request", message = "Invalid JSON body." }, 400);
                return;
            }

            var commandsTok = doc["commands"] as JArray;
            if (commandsTok == null || commandsTok.Count == 0)
            {
                await WriteJson(res, new { ok = false, error = "bad_request", message = "Missing or empty 'commands' array." }, 400);
                return;
            }

            // Defense-in-depth: even if someone reaches this endpoint, only the actual host/server
            // instance will execute gameplay actions. Non-hosts receive not_host errors per-command.
            if (!IsHost())
            {
                var resultsNotHost = new List<object>(commandsTok.Count);
                foreach (var c in commandsTok)
                {
                    resultsNotHost.Add(new
                    {
                        id = (c as JObject)?.Value<string>("id"),
                        type = (c as JObject)?.Value<string>("type"),
                        ok = false,
                        error = "not_host",
                        message = "This tool must run on the host/server."
                    });
                }
                await WriteJson(res, new { ok = false, results = resultsNotHost });
                return;
            }

            var results = new List<object>(commandsTok.Count);

            foreach (var ctok in commandsTok)
            {
                var (cid, ctypeStr, payload) = ParseCommand(ctok as JObject);

                if (ctypeStr == null)
                {
                    results.Add(new { id = cid, type = (string)null, ok = false, error = "validation_failed", message = "Command missing 'type'." });
                    continue;
                }

                try
                {
                    var r = DispatchCommand(cid, ctypeStr, payload);
                    results.Add(r);
                }
                catch (Exception e)
                {
                    Logger.LogError($"Dispatch error for '{ctypeStr}': {e}");
                    results.Add(new { id = cid, type = ctypeStr, ok = false, error = "internal", message = "Unhandled exception." });
                }
            }

            await WriteJson(res, new { ok = true, results });
        }

        private (string id, string type, JObject payload) ParseCommand(JObject obj)
        {
            if (obj == null) return (null, null, null);
            string id = obj.Value<string>("id");
            string type = obj.Value<string>("type");
            var payload = obj["payload"] as JObject ?? new JObject();
            return (id, type, payload);
        }

        // ---------- Dispatch (validate + enqueue) ----------
        private object DispatchCommand(string id, string type, JObject p)
        {
            switch (type)
            {
                // ----- SPAWNS -----
                case "spawn.front":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 1), 1, CfgMaxSpawned.Value);
                        float distance = ClampF(GetF(p, "distance", 8f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnFront(prefab, stars, count, distance, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, distance, delay, ttl });
                    }

                case "spawn.ring":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 1), 1, CfgMaxSpawned.Value);
                        float radius = ClampF(GetF(p, "radius", 10f), 0f, 200f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnRing(prefab, stars, count, radius, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, radius, delay, ttl });
                    }

                case "spawn.line":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 1), 1, CfgMaxSpawned.Value);
                        float spacing = ClampF(GetF(p, "spacing", 3f), 0f, 100f);
                        float distance = ClampF(GetF(p, "distance", 15f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnLine(prefab, stars, count, spacing, distance, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, spacing, distance, delay, ttl });
                    }

                case "spawn.arc":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 1), 1, CfgMaxSpawned.Value);
                        float radius = ClampF(GetF(p, "radius", 8f), 0f, 200f);
                        float spanDeg = ClampF(GetF(p, "spanDeg", 120f), 0f, 360f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnArc(prefab, stars, count, radius, spanDeg, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, radius, spanDeg, delay, ttl });
                    }

                case "spawn.wedge":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 1), 1, CfgMaxSpawned.Value);
                        float spacing = ClampF(GetF(p, "spacing", 3f), 0f, 100f);
                        float distance = ClampF(GetF(p, "distance", 10f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnWedge(prefab, stars, count, spacing, distance, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, spacing, distance, delay, ttl });
                    }

                case "spawn.grid":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int rows = ClampInt(GetI(p, "rows", 2), 1, CfgMaxSpawned.Value);
                        int cols = ClampInt(GetI(p, "cols", 3), 1, CfgMaxSpawned.Value);
                        float spacing = ClampF(GetF(p, "spacing", 3f), 0f, 100f);
                        float distance = ClampF(GetF(p, "distance", 12f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnGrid(prefab, stars, rows, cols, spacing, distance, delay, ttl));
                        return Queued(id, type, new { prefab, stars, rows, cols, spacing, distance, delay, ttl });
                    }

                case "spawn.scatter":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 10), 1, CfgMaxSpawned.Value);
                        float radius = ClampF(GetF(p, "radius", 10f), 0f, 200f);
                        float distance = ClampF(GetF(p, "distance", 10f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnScatter(prefab, stars, count, radius, distance, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, radius, distance, delay, ttl });
                    }

                case "spawn.rain":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int stars = ClampInt(GetI(p, "stars", 0), 0, MaxStars);
                        int count = ClampInt(GetI(p, "count", 10), 1, CfgMaxSpawned.Value);
                        float radius = ClampF(GetF(p, "radius", 10f), 0f, 200f);
                        float height = ClampF(GetF(p, "height", 15f), 0f, 500f);
                        float delay = ClampF(GetF(p, "delay", 0f), 0f, MaxDelay);
                        float ttl = ClampF(GetF(p, "ttl", 0f), 0f, MaxTTL);

                        Enqueue(() => _spawn.TrySpawnRain(prefab, stars, count, radius, height, delay, ttl));
                        return Queued(id, type, new { prefab, stars, count, radius, height, delay, ttl });
                    }

                // ----- ITEMS -----
                case "item.give":
                    {
                        string prefab = GetS(p, "prefab");
                        if (string.IsNullOrEmpty(prefab)) return Fail(id, type, "validation_failed", "payload.prefab required");

                        int amount = Math.Max(1, GetI(p, "amount", 1));
                        int quality = Math.Max(1, GetI(p, "quality", 1));
                        int variant = Math.Max(0, GetI(p, "variant", 0));

                        Enqueue(() => _items.TryGiveItemGeneric(prefab, amount, quality, variant));
                        return Queued(id, type, new { prefab, amount, quality, variant });
                    }

                // ----- EFFECTS (built-in only) -----
                case "effect.apply":
                    {
                        string name = GetS(p, "name");
                        if (string.IsNullOrEmpty(name)) return Fail(id, type, "validation_failed", "payload.name required");

                        float duration = ClampF(GetF(p, "duration", 0f), 0f, MaxTTL); // 0 = use effect default
                        Enqueue(() => _effects.TryApplyEffectGeneric(name, duration, NotifyEnabled));
                        return Queued(id, type, new { name, duration });
                    }

                default:
                    return Fail(id, type, "bad_request", $"Unknown type '{type}'.");
            }
        }

        // ---------- Helpers ----------
        private static void Enqueue(Action a) => _mainThread.Enqueue(a);

        private static object Queued(string id, string type, object meta)
            => new { id, type, ok = true, queued = true, meta };

        private static object Fail(string id, string type, string code, string msg)
            => new { id, type, ok = false, error = code, message = msg };

        private static async Task WriteJson(HttpListenerResponse res, object obj, int statusCode = 200)
        {
            try
            {
                var json = JsonConvert.SerializeObject(obj);
                var data = Encoding.UTF8.GetBytes(json);
                res.StatusCode = statusCode;
                res.ContentType = "application/json";
                await res.OutputStream.WriteAsync(data, 0, data.Length);
            }
            catch { }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        private static bool IsHost()
        {
            try { return ZNet.instance != null && ZNet.instance.IsServer(); }
            catch { return false; }
        }

        private static string GetS(JObject o, string k, string def = null)
            => (o?[k]?.Type == JTokenType.String) ? (string)o[k] : def;

        private static int GetI(JObject o, string k, int def = 0)
        {
            var tok = o?[k];
            if (tok == null) return def;
            if (tok.Type == JTokenType.Integer) return (int)tok;
            if (tok.Type == JTokenType.Float) return (int)Math.Floor((float)tok);
            return int.TryParse(tok.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
        }

        private static float GetF(JObject o, string k, float def = 0f)
        {
            var tok = o?[k];
            if (tok == null) return def;
            if (tok.Type == JTokenType.Float || tok.Type == JTokenType.Integer) return tok.Value<float>();
            return float.TryParse(tok.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
        }

        private static int ClampInt(int v, int lo, int hi) => Math.Min(Math.Max(v, lo), hi);
        private static float ClampF(float v, float lo, float hi) => Mathf.Clamp(v, lo, hi);
    }
}
