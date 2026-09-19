using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using NdzeVpn.Models;
using NdzeVpn.Services;

// End-to-end checks against the real xray.exe / sing-box.exe. No network VPN server needed:
// the proxy outbound points at a dead address, so "reachable through the tunnel" means the
// request failed and "direct" means it succeeded — which is exactly the split we want to prove.

var failures = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS " : "  FAIL ");
    Console.ResetColor();
    Console.WriteLine(name + (detail is null ? "" : $"  ({detail})"));
    if (!ok) failures++;
}

Console.OutputEncoding = Encoding.UTF8;
AppPaths.EnsureCreated();

// ------------------------------------------------------------------ parser
Console.WriteLine("Share-link parser");

var reality = ShareLinkParser.Parse(
    "vless://0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0@203.0.113.10:443?type=tcp&security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&fp=chrome&sni=www.microsoft.com&sid=6ba85179e30d4fc2&flow=xtls-rprx-vision#%F0%9F%87%A9%F0%9F%87%AA%20Germany%201");
Check("vless reality parsed", reality is { Protocol: ProxyProtocol.Vless, StreamSecurity: "reality", Port: 443, Flow: "xtls-rprx-vision" });
Check("remark decoded with flag", reality?.Remark == "🇩🇪 Germany 1", reality?.Remark);

var ws = ShareLinkParser.Parse("vless://0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0@cdn.example.com:443?type=ws&security=tls&path=%2Fws%3Fed%3D2048&host=cdn.example.com&sni=cdn.example.com#WS");
Check("vless ws path unescaped", ws?.Path == "/ws?ed=2048", ws?.Path);

var grpc = ShareLinkParser.Parse("vless://0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0@g.example.com:443?type=grpc&security=tls&serviceName=grpcsvc&mode=multi#gRPC");
Check("vless grpc serviceName", grpc?.ServiceName == "grpcsvc", grpc?.ServiceName);

var vmessJson = JsonSerializer.Serialize(new { v = "2", ps = "VM", add = "vm.example.com", port = "8443", id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", aid = "0", net = "ws", path = "/v", host = "vm.example.com", tls = "tls" });
var vmess = ShareLinkParser.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(vmessJson)));
Check("vmess base64 json", vmess is { Protocol: ProxyProtocol.Vmess, Port: 8443, Network: "ws", StreamSecurity: "tls" });

var trojan = ShareLinkParser.Parse("trojan://secretpass@tr.example.com:443?sni=tr.example.com#TR");
Check("trojan defaults to tls", trojan is { Protocol: ProxyProtocol.Trojan, StreamSecurity: "tls" });

var ss = ShareLinkParser.Parse("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:pw")) + "@ss.example.com:8388#SS");
Check("shadowsocks SIP002", ss is { Protocol: ProxyProtocol.Shadowsocks, Method: "chacha20-ietf-poly1305", Credential: "pw", Port: 8388 });

var html = "<html><body><p>Your key:</p><code>vless://0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0@h.example.com:443?type=tcp&amp;security=tls#A</code>" +
           "<a href=\"vless://0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0@h.example.com:443?type=tcp&security=tls#A\">copy</a></body></html>";
Check("links scraped from HTML and deduped", ShareLinkParser.ParseMany(html).Count == 1);

var b64sub = Convert.ToBase64String(Encoding.UTF8.GetBytes(reality!.RawUri + "\n" + trojan!.RawUri + "\n"));
var fromB64 = ShareLinkParser.ParseMany(ShareLinkParser.Base64Decode(b64sub));
Check("base64 subscription body", fromB64.Count == 2);

var (code, clean) = CountryFlag.Split("🇳🇱 | Amsterdam");
Check("flag → country code", code == "NL" && clean == "Amsterdam", $"{code} / {clean}");

// ------------------------------------------------------------------ other clients' formats
Console.WriteLine("Formats other clients are served");

// Xray JSON: what Happ (and this panel type) hands out — an array of complete configs.
var xrayJson = """
[
  {"remarks":"🇫🇮 Helsinki","outbounds":[
    {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"fi.example.com","port":443,
      "users":[{"id":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","flow":"xtls-rprx-vision","encryption":"none"}]}]},
     "streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"www.microsoft.com",
      "publicKey":"Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw","shortId":"6ba85179e30d4fc2","fingerprint":"firefox","spiderX":"/"}}},
    {"tag":"direct","protocol":"freedom"}]},
  {"remarks":"HY2 node","outbounds":[{"tag":"proxy","protocol":"hysteria2","settings":{}}]}
]
""";
var fromXray = ConfigImporter.Parse(xrayJson);
Check("Xray JSON recognised", fromXray.Format == "Xray JSON", fromXray.Format);
Check("Xray JSON node parsed", fromXray.Nodes is [{ Protocol: ProxyProtocol.Vless, Address: "fi.example.com", Port: 443, StreamSecurity: "reality", Flow: "xtls-rprx-vision" }]);
Check("Xray JSON reality fields", fromXray.Nodes[0].PublicKey.Length > 20 && fromXray.Nodes[0].Sni == "www.microsoft.com");
Check("Xray JSON remark kept", fromXray.Nodes[0].Remark == "🇫🇮 Helsinki", fromXray.Nodes[0].Remark);
Check("hysteria2 reported as unsupported", fromXray.Unsupported.Any(u => u.StartsWith("hysteria2")), string.Join(", ", fromXray.Unsupported));

// sing-box JSON: what SFI, Karing and INCY are served.
var singBoxJson = """
{"outbounds":[
  {"type":"selector","tag":"sel","outbounds":["a"]},
  {"type":"vless","tag":"🇳🇱 Amsterdam","server":"nl.example.com","server_port":443,
   "uuid":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","flow":"xtls-rprx-vision",
   "tls":{"enabled":true,"server_name":"www.cloudflare.com","utls":{"enabled":true,"fingerprint":"chrome"},
          "reality":{"enabled":true,"public_key":"Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw","short_id":"ab12"}}},
  {"type":"trojan","tag":"TR","server":"tr.example.com","server_port":8443,"password":"secret",
   "tls":{"enabled":true,"server_name":"tr.example.com"},"transport":{"type":"ws","path":"/ws","headers":{"Host":"tr.example.com"}}},
  {"type":"hysteria2","tag":"HY","server":"hy.example.com","server_port":443,"password":"x"},
  {"type":"direct","tag":"direct"}]}
""";
var fromSingBox = ConfigImporter.Parse(singBoxJson);
Check("sing-box JSON recognised", fromSingBox.Format == "sing-box JSON", fromSingBox.Format);
Check("sing-box vless + reality", fromSingBox.Nodes.Any(n => n is { Protocol: ProxyProtocol.Vless, StreamSecurity: "reality", Sni: "www.cloudflare.com", Fingerprint: "chrome" }));
Check("sing-box trojan over ws", fromSingBox.Nodes.Any(n => n is { Protocol: ProxyProtocol.Trojan, Network: "ws", Path: "/ws", Host: "tr.example.com", Port: 8443 }));
Check("sing-box selector/direct skipped", fromSingBox.Nodes.Count == 2, $"{fromSingBox.Nodes.Count} nodes");

// Clash / Mihomo YAML.
var clashYaml = """
port: 7890
proxies:
  - {name: "🇩🇪 Frankfurt", type: vless, server: de.example.com, port: 443, uuid: 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0, network: ws, tls: true, servername: de.example.com, path: /ws}
  - name: "SS node"
    type: ss
    server: ss.example.com
    port: 8388
    cipher: chacha20-ietf-poly1305
    password: pw
  - {name: "HY", type: hysteria2, server: hy.example.com, port: 443, password: x}
proxy-groups:
  - {name: auto, type: url-test}
""";
var fromClash = ConfigImporter.Parse(clashYaml);
Check("Clash YAML recognised", fromClash.Format == "Clash YAML", fromClash.Format);
Check("Clash vless over ws", fromClash.Nodes.Any(n => n is { Protocol: ProxyProtocol.Vless, Network: "ws", Address: "de.example.com", Path: "/ws" }));
Check("Clash block-style ss", fromClash.Nodes.Any(n => n is { Protocol: ProxyProtocol.Shadowsocks, Address: "ss.example.com", Port: 8388, Method: "chacha20-ietf-poly1305" }));
Check("Clash groups not taken for nodes", fromClash.Nodes.Count == 2, $"{fromClash.Nodes.Count} nodes");

// Import links of other clients.
var subUrl = "https://panel.example.com/sub/abc123";
var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(subUrl));
Check("happ://add/<base64> unwrapped", ClientLinks.Unwrap($"happ://add/{b64}") == subUrl);
Check("happ://add/<url> unwrapped", ClientLinks.Unwrap($"happ://add/{subUrl}") == subUrl);
Check("v2raytun://import/<url> unwrapped", ClientLinks.Unwrap($"v2raytun://import/{subUrl}") == subUrl);
Check("hiddify://install-config?url= unwrapped", ClientLinks.Unwrap($"hiddify://install-config?url={Uri.EscapeDataString(subUrl)}") == subUrl);
Check("sing-box://import-remote-profile unwrapped", ClientLinks.Unwrap($"sing-box://import-remote-profile?url={Uri.EscapeDataString(subUrl)}") == subUrl);
Check("plain URL left alone", ClientLinks.Unwrap(subUrl) == subUrl);
Check("encrypted happ link detected", ClientLinks.IsEncryptedHapp("happ://crypt4/AAAA"));
Check("source app named", ClientLinks.SourceApp("incy://add/" + b64) == "INCY");
Check("placeholder node detected", ConfigImporter.IsPlaceholder(
    ShareLinkParser.Parse("vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1?encryption=none&type=tcp#App%20not%20supported")!));
Check("device id is stable", ClientIdentity.DeviceId() == ClientIdentity.DeviceId() && ClientIdentity.DeviceId().StartsWith("ndze-"));

// Live check against a real subscription, when one is provided (never stored in the repo).
if (Environment.GetEnvironmentVariable("NDZEVPN_TEST_SUB") is { Length: > 0 } liveSub)
{
    var live = await new KeyCheckService().CheckAsync(liveSub, new AppSettings());
    Check("live subscription returns nodes", live.Ok && live.Nodes.Count > 0,
        live.Ok ? $"{live.Nodes.Count} nodes, {live.Format}, {live.Traffic}" : live.Error);
    if (live.Ok)
    {
        Console.WriteLine($"         title: {live.Title}");
        Console.WriteLine($"         alive: {live.Nodes.Count(n => n.IsAlive)} / {live.Nodes.Count}");
        if (live.Unsupported.Count > 0) Console.WriteLine($"         skipped: {live.Unsupported.Count} unsupported");
    }
}

// ------------------------------------------------------------------ updates / discord
Console.WriteLine("Updates");
Check("tag v1.2.0 parsed", UpdateService.TryParseVersion("v1.2.0", out var v120) && v120 == new Version(1, 2, 0));
Check("tag 1.10.3-beta parsed", UpdateService.TryParseVersion("1.10.3-beta", out var vb) && vb == new Version(1, 10, 3));
Check("1.10.0 newer than 1.9.9", UpdateService.TryParseVersion("v1.10.0", out var a) && UpdateService.TryParseVersion("v1.9.9", out var b) && a > b);
Check("junk tag rejected", !UpdateService.TryParseVersion("latest", out _));
Check("discord 'logo' resolves to repo image", DiscordSettings.ResolveImage("logo").StartsWith("https://raw.githubusercontent.com/"));
Check("discord custom key kept", DiscordSettings.ResolveImage("my_art") == "my_art");

// ------------------------------------------------------------------ geo
Console.WriteLine("Rule data");
Check("geo assets seeded into writable data dir", File.Exists(AppPaths.GeoSiteDat) && AppPaths.GeoSiteDat.StartsWith(AppPaths.DataDir), AppPaths.GeoSiteDat);
var tags = GeoAssetService.ReadGeoSiteTags(AppPaths.GeoSiteDat);
Check("geosite.dat enumerated", tags is { Count: > 50 }, $"{tags?.Count} tags");
foreach (var t in RoutingPresets.OptionalBlockedGeoSites.Concat(RoutingPresets.OptionalRussianGeoSites).Concat(RoutingPresets.OptionalAdGeoSites))
{
    var name = t["geosite:".Length..];
    Console.WriteLine($"         {(tags?.Contains(name) == true ? "has " : "miss")} {t}");
}
Check("geosite has 'private'", tags?.Contains("private") == true);

// ------------------------------------------------------------------ xray configs
Console.WriteLine("Xray config (xray run -test)");

var settings = new AppSettings { SocksPort = 21808, HttpPort = 21809, ApiPort = 21085, XrayLogLevel = "warning" };
settings.CustomRules.Add(new RoutingRule { Kind = RuleKind.Domain, Action = RuleAction.Direct, Value = "example.org, example.net" });
settings.CustomRules.Add(new RoutingRule { Kind = RuleKind.GeoSite, Action = RuleAction.Proxy, Value = "definitely-not-a-real-tag" });
settings.CustomRules.Add(new RoutingRule { Kind = RuleKind.Port, Action = RuleAction.Block, Value = "6881-6889" });
settings.CustomRules.Add(new RoutingRule { Kind = RuleKind.Process, Action = RuleAction.Direct, Value = "cs2.exe" });

foreach (var node in new[] { reality, ws!, grpc!, vmess!, trojan, ss! })
{
    foreach (var mode in Enum.GetValues<RoutingMode>())
    {
        settings.RoutingMode = mode;
        var builder = new XrayConfigBuilder { KnownGeoSites = tags };
        var path = Path.Combine(AppPaths.RuntimeDir, "selftest.json");
        await File.WriteAllTextAsync(path, builder.Build(node, settings));
        var (ok, output) = await XrayProcess.TestConfigAsync(path);
        Check($"{node.Remark,-12} {node.Network,-5} {mode,-7}", ok, ok ? null : output.Split('\n').LastOrDefault(l => l.Trim().Length > 0));
    }
}

// ------------------------------------------------------------------ sing-box
Console.WriteLine("TUN config (sing-box check)");
var tun = new TunService();
var sbPath = Path.Combine(AppPaths.RuntimeDir, "selftest-sb.json");
await File.WriteAllTextAsync(sbPath, tun.BuildConfig(settings));
var sb = await RunAsync(AppPaths.SingBoxExe, $"check -c \"{sbPath}\"");
Check("sing-box accepts config", sb.Code == 0, sb.Code == 0 ? null : sb.Output.Trim());

// ------------------------------------------------------------------ live split routing
Console.WriteLine("Live split routing (dead proxy node: direct must work, tunnel must not)");

settings.RoutingMode = RoutingMode.Smart;
settings.CustomRules.Clear();
var dead = reality.Clone();
dead.Address = "127.0.0.1";
dead.Port = 9; // discard port: nothing listens, so any proxied request fails fast

var liveBuilder = new XrayConfigBuilder { KnownGeoSites = tags };
var accessLog = Path.Combine(AppPaths.RuntimeDir, "selftest-access.log");
if (File.Exists(accessLog)) File.Delete(accessLog);
// Access log records the outbound Xray picked for each connection — the routing decision itself,
// independent of whether a given site likes .NET's TLS fingerprint.
var liveJson = liveBuilder.Build(dead, settings).Replace("\"access\": \"none\"", "\"access\": " + JsonSerializer.Serialize(accessLog));
await File.WriteAllTextAsync(AppPaths.XrayConfigFile, liveJson);

using var xray = new XrayProcess();
Check("xray started", xray.Start(AppPaths.XrayConfigFile));
await Task.Delay(1500);

async Task<(bool Ok, string Detail)> ViaProxy(string url, int seconds = 20)
{
    try
    {
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{settings.HttpPort}"), UseProxy = true, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/124.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        return (true, ((int)resp.StatusCode).ToString());
    }
    catch (Exception ex) { return (false, ex.GetBaseException().Message); }
}

var ru = await ViaProxy("https://ya.ru/");
Check("ya.ru really loads through Xray (direct)", ru.Ok, ru.Detail);

var probes = new (string Host, string Expected)[]
{
    ("www.gosuslugi.ru", "direct"), ("online.sberbank.ru", "direct"), ("vk.com", "direct"),
    ("habr.com", "direct"), ("ozon.ru", "direct"),
    ("www.youtube.com", "proxy"), ("discord.com", "proxy"), ("www.instagram.com", "proxy"),
    ("chatgpt.com", "proxy"), ("x.com", "proxy"), ("github.com", "proxy")
};
await Task.WhenAll(probes.Select(p => ViaProxy($"https://{p.Host}/", 4)));
await Task.Delay(800);

// Health check: the node here is deliberately dead while the machine is online, so the monitor must
// say "tunnel down" and not "no internet" — that distinction is what stops pointless reconnects.
settings.LatencyTimeoutMs = 4000;
var health = await LatencyService.CheckHealthAsync(settings);
Check("dead node reported as TunnelDown, not NoInternet", health == TunnelHealth.TunnelDown, health.ToString());
Check("real delay through a dead node is -1", await LatencyService.RealDelayAsync(settings) == -1);

xray.Stop();

var logLines = File.Exists(accessLog) ? File.ReadAllLines(accessLog) : [];
foreach (var (host, expected) in probes)
{
    var line = logLines.FirstOrDefault(l => l.Contains($"//{host}:443"));
    var actual = line is null ? "not logged" : line[(line.LastIndexOf("-> ") + 3)..].TrimEnd(']');
    Check($"{host,-20} -> {expected}", actual == expected, actual);
}

Check("xray restarted for stats", xray.Start(AppPaths.XrayConfigFile));
await Task.Delay(1500);
await ViaProxy("https://ya.ru/");
var stats = new StatsService(settings.ApiPort);
TrafficSnapshot? snap = null;
stats.Updated += (_, s) => snap = s;
stats.Start(TimeSpan.FromMilliseconds(500));
await Task.Delay(1800);
stats.Stop();
Check("stats API answers", snap is not null, snap is null ? "no sample" : $"up {snap.Value.UplinkTotal} B");
Check("stats count the direct outbound too", snap?.DownlinkTotal > 0, $"down {snap?.DownlinkTotal} B");

xray.Stop();

// ------------------------------------------------------------------ measurements (no VPN)
Console.WriteLine("Measurements against the real internet");

Check("machine is online", await LatencyService.IsInternetUpAsync(settings));

var tcp = await LatencyService.TcpPingAsync("ya.ru", 443, 4000);
Check("TCP ping to a live host is a sane number", tcp is > 0 and < 2000, $"{tcp} ms");
Check("TCP ping to a closed port fails fast", await LatencyService.TcpPingAsync("127.0.0.1", 9, 1200) == -1);

settings.SpeedTestSeconds = 4;
settings.SpeedTestStreams = 4;
settings.SpeedTestUpload = true;
var speed = await new SpeedTestService().RunAsync(settings, throughProxy: false, null);
Check("speed test measures a real download", speed.DownMbps > 1, SpeedTestService.FormatMbps(speed.DownMbps));
// Upload is informational: the public endpoints rate-limit repeated runs from one address, and a
// throttled speed-test server is not a bug in this app.
Console.WriteLine($"         upload {SpeedTestService.FormatMbps(speed.UpMbps)}");

Console.WriteLine();
Console.ForegroundColor = failures == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
Console.ResetColor();
return failures == 0 ? 0 : 1;

static async Task<(int Code, string Output)> RunAsync(string exe, string args)
{
    var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    using var p = Process.Start(psi)!;
    var o = await p.StandardOutput.ReadToEndAsync();
    var e = await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    return (p.ExitCode, o + e);
}
