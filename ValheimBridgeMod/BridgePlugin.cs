using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ValheimBridgeMod
{
    [BepInPlugin("dakota.valheim.StreamBridge", "StreamBridge", "0.3.0")]
    public class BridgePlugin : BaseUnityPlugin
    {
        internal static ManualLogSource LogS;
        private static readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private int _port = 28575;

        // Services
        private SpawnService _spawn;
        private ItemService _items;
        private EffectService _effects;

        void Awake()
        {
            LogS = Logger;
            new Harmony("dakota.valheim.StreamBridge").PatchAll();

            _spawn = new SpawnService(this, Logger);
            _items = new ItemService(Logger);
            _effects = new EffectService(Logger);

            try
            {
                _cts = new CancellationTokenSource();
                StartHttpServer(_port, _cts.Token);
                Logger.LogInfo($"StreamBridge listening on http://127.0.0.1:{_port}/ (try /ping/)");
            }
            catch (Exception e) { Logger.LogError(e); }
        }

        void OnDestroy()
        {
            try { _cts?.Cancel(); _listener?.Close(); } catch { }
        }

        void Update()
        {
            while (_mainThread.TryDequeue(out var act))
            {
                try { act(); } catch (Exception e) { LogS.LogError(e); }
            }
        }

        private void StartHttpServer(int port, CancellationToken token)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/ping/");
            _listener.Prefixes.Add($"http://localhost:{port}/ping/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/spawn/");
            _listener.Prefixes.Add($"http://localhost:{port}/spawn/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/give/");
            _listener.Prefixes.Add($"http://localhost:{port}/give/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/effect/");
            _listener.Prefixes.Add($"http://localhost:{port}/effect/");
            _listener.Start();

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    HttpListenerContext ctx = null;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch when (token.IsCancellationRequested) { break; }
                    catch (Exception e) { LogS.LogError($"GetContext failed: {e}"); continue; }
                    _ = HandleRequest(ctx);
                }
            }, token);
        }

        private static string[] Parts(string absolutePath)
        {
            return absolutePath.Trim().Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                var path = req.Url.AbsolutePath;
                var parts = Parts(path);
                LogS.LogInfo($"HTTP {req.HttpMethod} {path}");

                // Health
                if (parts.Length == 1 && parts[0].Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJson(res, "{\"ok\":true,\"pong\":true}"); return;
                }

                // Commands must be POST (except ping)
                if (req.HttpMethod != "POST")
                {
                    res.StatusCode = 405; await WriteText(res, "method_not_allowed"); return;
                }

                // ---------------- SPAWN ----------------
                if (parts.Length >= 2 && parts[0].Equals("spawn", StringComparison.OrdinalIgnoreCase))
                {
                    var pattern = parts[1].ToLowerInvariant();

                    // /spawn/front/<Prefab>/<stars>/<count?>/<distance?>/<delay?>/<ttl?>/
                    if (pattern == "front")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/front/<Prefab>/<stars>/<count?>/<distance?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 1; float distance = 6f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var d)) distance = Math.Max(0.5f, d);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnFront(prefab, stars, count, distance, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_front\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"distance\":{distance},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/ring/<Prefab>/<stars>/<count?>/<radius?>/<delay?>/<ttl?>/
                    if (pattern == "ring")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/ring/<Prefab>/<stars>/<count?>/<radius?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 6; float radius = 8f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var r)) radius = Math.Max(0.5f, r);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnRing(prefab, stars, count, radius, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_ring\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"radius\":{radius},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/line/<Prefab>/<stars>/<count?>/<spacing?>/<distance?>/<delay?>/<ttl?>/
                    if (pattern == "line")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/line/<Prefab>/<stars>/<count?>/<spacing?>/<distance?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 5; float spacing = 2.5f, distance = 8f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var s)) spacing = Math.Max(0.5f, s);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var d)) distance = Math.Max(0f, d);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnLine(prefab, stars, count, spacing, distance, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_line\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"spacing\":{spacing},\"distance\":{distance},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/arc/<Prefab>/<stars>/<count?>/<radius?>/<spanDeg?>/<delay?>/<ttl?>/
                    if (pattern == "arc")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/arc/<Prefab>/<stars>/<count?>/<radius?>/<spanDeg?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 6; float radius = 8f, spanDeg = 120f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var r)) radius = Math.Max(0.5f, r);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var s)) spanDeg = UnityEngine.Mathf.Clamp(s, 1f, 360f);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnArc(prefab, stars, count, radius, spanDeg, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_arc\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"radius\":{radius},\"span\":{spanDeg},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/wedge/<Prefab>/<stars>/<count?>/<spacing?>/<distance?>/<delay?>/<ttl?>/
                    if (pattern == "wedge")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/wedge/<Prefab>/<stars>/<count?>/<spacing?>/<distance?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 7; float spacing = 2.5f, distance = 6f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var s)) spacing = Math.Max(0.5f, s);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var d)) distance = Math.Max(0f, d);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnWedge(prefab, stars, count, spacing, distance, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_wedge\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"spacing\":{spacing},\"distance\":{distance},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/grid/<Prefab>/<stars>/<rows?>/<cols?>/<spacing?>/<distance?>/<delay?>/<ttl?>/
                    if (pattern == "grid")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/grid/<Prefab>/<stars>/<rows?>/<cols?>/<spacing?>/<distance?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, rows = 3, cols = 3; float spacing = 2.5f, distance = 8f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var rr)) rows = Math.Max(1, rr);
                        if (parts.Length >= 6 && int.TryParse(parts[5], out var cc)) cols = Math.Max(1, cc);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var sp)) spacing = Math.Max(0.5f, sp);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var d)) distance = Math.Max(0f, d);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 10 && float.TryParse(parts[9], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnGrid(prefab, stars, rows, cols, spacing, distance, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_grid\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"rows\":{rows},\"cols\":{cols},\"spacing\":{spacing},\"distance\":{distance},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/scatter/<Prefab>/<stars>/<count?>/<radius?>/<distance?>/<delay?>/<ttl?>/
                    if (pattern == "scatter")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/scatter/<Prefab>/<stars>/<count?>/<radius?>/<distance?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 10; float radius = 10f, distance = 8f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var r)) radius = Math.Max(0.5f, r);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var d)) distance = Math.Max(0f, d);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnScatter(prefab, stars, count, radius, distance, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_scatter\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"radius\":{radius},\"distance\":{distance},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    // /spawn/rain/<Prefab>/<stars>/<count?>/<radius?>/<height?>/<delay?>/<ttl?>/
                    if (pattern == "rain")
                    {
                        if (parts.Length < 4) { res.StatusCode = 400; await WriteText(res, "usage: /spawn/rain/<Prefab>/<stars>/<count?>/<radius?>/<height?>/<delay?>/<ttl?>/"); return; }
                        var prefab = parts[2];
                        int stars = 0, count = 10; float radius = 10f, height = 15f, delay = 0f, ttl = 0f;
                        int.TryParse(parts[3], out stars); stars = SpawnService.ClampStars(stars);
                        if (parts.Length >= 5 && int.TryParse(parts[4], out var c)) count = SpawnService.ClampCount(c);
                        if (parts.Length >= 6 && float.TryParse(parts[5], out var r)) radius = Math.Max(0.5f, r);
                        if (parts.Length >= 7 && float.TryParse(parts[6], out var h)) height = Math.Max(0.5f, h);
                        if (parts.Length >= 8 && float.TryParse(parts[7], out var dl)) delay = SpawnService.ClampDelay(dl);
                        if (parts.Length >= 9 && float.TryParse(parts[8], out var tt)) ttl = SpawnService.ClampTTL(tt);

                        _mainThread.Enqueue(() => _spawn.TrySpawnRain(prefab, stars, count, radius, height, delay, ttl));
                        await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"spawn_rain\",\"prefab\":\"{prefab}\",\"stars\":{stars},\"count\":{count},\"radius\":{radius},\"height\":{height},\"delay\":{delay},\"ttl\":{ttl}}}");
                        return;
                    }

                    res.StatusCode = 404; await WriteText(res, "spawn_pattern_not_found"); return;
                }

                // ---------------- GIVE ----------------
                if (parts.Length >= 2 && parts[0].Equals("give", StringComparison.OrdinalIgnoreCase))
                {
                    var itemPrefabName = parts[1];
                    int amount = 1;
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var a)) amount = Math.Max(1, a);

                    _mainThread.Enqueue(() => _items.TryGiveItemGeneric(itemPrefabName, amount));
                    await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"give_item\",\"item\":\"{itemPrefabName}\",\"amount\":{amount}}}");
                    return;
                }

                // ---------------- EFFECT ----------------
                if (parts.Length >= 2 && parts[0].Equals("effect", StringComparison.OrdinalIgnoreCase))
                {
                    var effectName = parts[1];
                    float dur = 10f;
                    if (parts.Length >= 3 && float.TryParse(parts[2], out var s)) dur = Math.Max(1f, s);

                    _mainThread.Enqueue(() => _effects.TryApplyEffectGeneric(effectName, dur));
                    await WriteJson(res, $"{{\"ok\":true,\"enqueued\":\"effect\",\"name\":\"{effectName}\",\"duration\":{dur}}}");
                    return;
                }

                res.StatusCode = 404; await WriteText(res, "not_found");
            }
            catch (Exception e)
            {
                LogS.LogError($"HandleRequest error: {e}");
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }

        private static async Task WriteJson(HttpListenerResponse res, string json, int statusCode = 200)
        {
            res.StatusCode = statusCode; res.ContentType = "application/json";
            var data = Encoding.UTF8.GetBytes(json);
            await res.OutputStream.WriteAsync(data, 0, data.Length);
            res.Close();
        }

        private static async Task WriteText(HttpListenerResponse res, string text, int statusCode = 200)
        {
            res.StatusCode = statusCode; res.ContentType = "text/plain; charset=utf-8";
            var data = Encoding.UTF8.GetBytes(text);
            await res.OutputStream.WriteAsync(data, 0, data.Length);
            res.Close();
        }
    }
}
