using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using ScriptsWindows.Core;

namespace ScriptsWindows.Services;

public sealed record DnsAdapter(int Index, string Name, string Description, int Medium, bool Hardware, string Ipv4, string Gateway,
                                string[] Dns4, string[] Dns6, bool Static4, bool Static6, bool HasIpv6)
{
    public string Kind => Medium switch { 9 => "Wi-Fi", 14 => "Ethernet", _ => Hardware ? "Carte réseau" : "Connexion virtuelle (VPN, machine virtuelle…)" };
    public bool IsStatic => Static4 || Static6;
}

public sealed record DnsPreset(string Id, string Name, string Description, string[] V4, string[] V6);

/// <summary>
/// DNS : lecture par carte (index d'interface, jamais le nom inséré dans une commande), mode manuel / automatique lu
/// dans le registre (NameServer de Tcpip / Tcpip6, indépendant de la langue), application IPv4 + IPv6 (l'ancien code ne
/// changeait que l'IPv4 : les DNS IPv6 de la box restaient utilisés), serveur testé AVANT l'application, résolution
/// vérifiée après, retour à la configuration précédente si Internet ne répond plus.
/// </summary>
public static class DnsService
{
    public static readonly DnsPreset[] Presets =
    {
        new("cloudflare", "Cloudflare", "Le plus rapide en général, ne garde pas d'historique exploitable.", new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }),
        new("cloudflare_family", "Cloudflare Familles", "Bloque les sites malveillants et pour adultes (PC d'enfants, poste partagé).", new[] { "1.1.1.3", "1.0.0.3" }, new[] { "2606:4700:4700::1113", "2606:4700:4700::1003" }),
        new("quad9", "Quad9", "Bloque les sites malveillants connus (hameçonnage, virus), organisme européen.", new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }),
        new("google", "Google", "Très fiable partout dans le monde.", new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }),
    };

    private static readonly Dictionary<string, string> KnownServers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["208.67.222.222"] = "OpenDNS", ["208.67.220.220"] = "OpenDNS", ["94.140.14.14"] = "AdGuard", ["94.140.15.15"] = "AdGuard",
        ["1.1.1.2"] = "Cloudflare (anti-malware)", ["1.0.0.2"] = "Cloudflare (anti-malware)", ["9.9.9.11"] = "Quad9", ["149.112.112.11"] = "Quad9",
    };

    public static DnsPreset? FindPreset(string ip) => Presets.FirstOrDefault(p => p.V4.Contains(ip) || p.V6.Contains(ip, StringComparer.OrdinalIgnoreCase));

    /// <summary>Nom lisible d'un serveur DNS : fournisseur public connu, box / routeur (adresse locale) ou fournisseur d'accès.</summary>
    public static string ServerName(string ip, string gateway = "")
    {
        if (FindPreset(ip) is { } p) return p.Name;
        if (KnownServers.TryGetValue(ip, out var n)) return n;
        if (!IPAddress.TryParse(ip, out var a)) return "?";
        if (ip == gateway) return "box / routeur";
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127;
            return priv ? "box / routeur" : "fournisseur d'accès";
        }
        return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal ? "box / routeur" : "fournisseur d'accès";
    }

    /// <summary>Valide une adresse saisie (IPv4 ou IPv6) ; renvoie la forme canonique ou null.</summary>
    public static string? ParseServer(string s)
    {
        s = (s ?? "").Trim();
        return s.Length > 0 && IPAddress.TryParse(s, out var a) && (a.AddressFamily == AddressFamily.InterNetworkV6 || s.Count(c => c == '.') == 3)
               && !IPAddress.IsLoopback(a) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.Broadcast) ? a.ToString() : null;
    }

    /// <summary>Connexions actives, lues en .NET (les modules réseau de PowerShell coûtent ~1 s chacun à froid).</summary>
    public static Task<List<DnsAdapter>> GetAdaptersAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var physical = PhysicalAdapterGuids();
        var list = new List<DnsAdapter>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var p = n.GetIPProperties();
            int index;
            try { index = p.GetIPv4Properties()?.Index ?? p.GetIPv6Properties()?.Index ?? -1; } catch { index = -1; }
            if (index < 0) continue;
            var dns = p.DnsAddresses.Where(a => !(a.AddressFamily == AddressFamily.InterNetworkV6 && a.ToString().StartsWith("fec0:0:0:ffff::", StringComparison.OrdinalIgnoreCase))).ToList();
            string Clean(IPAddress a) => a.AddressFamily == AddressFamily.InterNetworkV6 ? new IPAddress(a.GetAddressBytes()).ToString() : a.ToString();   // sans %zone
            int medium = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 9 : n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet ? 14 : 0;
            list.Add(new DnsAdapter(index, n.Name, n.Description, medium, physical.Contains(n.Id),
                p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "",
                p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "",
                dns.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(Clean).ToArray(),
                dns.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(Clean).ToArray(),
                HasStaticServers(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + n.Id),
                HasStaticServers(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" + n.Id),
                n.Supports(NetworkInterfaceComponent.IPv6)));
        }
        return list.OrderByDescending(a => a.Hardware).ThenBy(a => a.Name).ToList();
    }, ct);

    /// <summary>DNS manuels = valeur NameServer non vide (les DNS reçus de la box sont dans DhcpNameServer).</summary>
    private static bool HasStaticServers(string key)
    {
        try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key); return ((k?.GetValue("NameServer") as string) ?? "").Trim().Length > 0; }
        catch { return false; }
    }

    /// <summary>GUID des cartes physiques : drapeau NCF_PHYSICAL (0x4) de la classe Réseau (source de HardwareInterface).</summary>
    private static HashSet<string> PhysicalAdapterGuids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("NetCfgInstanceId") is string id && k.GetValue("Characteristics") is int c && (c & 0x4) != 0) set.Add(id);
                }
                catch { }
            }
        }
        catch { }
        return set;
    }

    private static string PsList(IEnumerable<string> ips) => "@(" + string.Join(",", ips.Select(i => $"'{ParseServer(i) ?? throw new ArgumentException($"Adresse DNS invalide : {i}")}'")) + ")";

    /// <summary>Temps de réponse (ms) d'un serveur DNS, ou -1 s'il ne répond pas (réseau qui bloque les DNS externes, serveur hors service).</summary>
    public static async Task<Dictionary<string, int>> TestServersAsync(IEnumerable<string> servers, CancellationToken ct = default)
    {
        var ips = servers.Select(s => ParseServer(s)).Where(s => s != null).Cast<string>().Distinct().ToList();
        var r = new Dictionary<string, int>();
        if (ips.Count == 0) return r;
        string json = await PowerShellRunner.RunAsync($@"
$r = @{{}}
foreach ($s in {PsList(ips)}) {{
    $best = -1
    foreach ($n in 1..2) {{
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {{ Resolve-DnsName -Name 'www.msftconnecttest.com' -Server $s -DnsOnly -QuickTimeout -NoHostsFile -ErrorAction Stop | Out-Null; $ms = [int]$sw.ElapsedMilliseconds; if ($best -lt 0 -or $ms -lt $best) {{ $best = $ms }} }} catch {{ }}
    }}
    $r[$s] = $best
}}
ConvertTo-Json -Compress -InputObject $r", ct);
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('{')) return r;
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject()) r[p.Name] = p.Value.GetInt32();
        return r;
    }

    /// <summary>
    /// Applique les serveurs (IPv4 + IPv6 si la carte a l'IPv6) sur les cartes données. Le 1er serveur est testé avant ;
    /// après application, la résolution par Windows est vérifiée et la configuration précédente est rétablie en cas d'échec.
    /// v4 vide = retour en automatique (DNS de la box).
    /// </summary>
    public static async Task<(bool Ok, string Message)> ApplyAsync(IReadOnlyList<DnsAdapter> adapters, string[] v4, string[] v6, CancellationToken ct = default)
    {
        if (adapters.Count == 0) return (false, "aucune connexion à modifier");
        bool reset = v4.Length == 0;
        if (!reset)
        {
            var t = await TestServersAsync(v4.Take(1), ct);
            if (t.GetValueOrDefault(ParseServer(v4[0]) ?? "", -1) < 0)
                return (false, $"le serveur {v4[0]} ne répond pas depuis ce réseau (réseau d'entreprise, d'hôtel ou pare-feu qui impose ses DNS ?) : rien n'a été changé");
        }
        string res = (await PowerShellRunner.RunAsync(BuildApplyScript(adapters, v4, v6), ct)).Trim();
        string last = res.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (last.StartsWith("OK")) return (true, last.Length > 3 ? "appliqué (" + last[3..] + ")" : "appliqué et vérifié");
        if (last == "ROLLBACK") return (false, "Internet ne répondait plus avec ces DNS : configuration précédente rétablie");
        return (false, last.StartsWith("ERR:") ? "Windows a refusé : " + last[4..] + " (configuration précédente rétablie)" : "réponse inattendue : " + res);
    }

    internal static string BuildApplyScript(IReadOnlyList<DnsAdapter> adapters, string[] v4, string[] v6)
    {
        bool reset = v4.Length == 0;
        string apply = string.Join("\n", adapters.Select(a =>
        {
            string prev4 = a.Static4 ? PsList(a.Dns4.Where(s => ParseServer(s) != null)) : "$null";
            string prev6 = a.Static6 ? PsList(a.Dns6.Where(s => ParseServer(s) != null)) : "$null";
            string set = reset ? $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ResetServerAddresses -ErrorAction Stop"
                : a.HasIpv6 && v6.Length > 0
                    ? $"try {{ Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses ({PsList(v4)} + {PsList(v6)}) -ErrorAction Stop }} catch {{ Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses {PsList(v4)} -ErrorAction Stop; $warn += 'IPv6 non modifié sur ' + {a.Index} }}"
                    : $"Set-DnsClientServerAddress -InterfaceIndex {a.Index} -ServerAddresses {PsList(v4)} -ErrorAction Stop";
            return $"$prev += ,@({a.Index}, {prev4}, {prev6}); {set}";
        }));
        string script = $@"
$prev = @(); $warn = @(); $err = ''
try {{
{apply}
}} catch {{ $err = $_.Exception.Message }}
Clear-DnsClientCache -ErrorAction SilentlyContinue
$ok = $false
if (-not $err) {{
    foreach ($n in 1..3) {{
        try {{ Resolve-DnsName -Name 'www.msftconnecttest.com' -DnsOnly -QuickTimeout -ErrorAction Stop | Out-Null; $ok = $true; break }} catch {{ Start-Sleep -Milliseconds 700 }}
    }}
}}
if ($err -or -not $ok) {{
    foreach ($p in $prev) {{
        try {{
            # automatique d'abord (les 2 familles), puis les serveurs manuels d'avant : une famille qui était automatique le redevient
            Set-DnsClientServerAddress -InterfaceIndex $p[0] -ResetServerAddresses -ErrorAction Stop
            $all = @(@($p[1]) + @($p[2]) | Where-Object {{ $_ }})
            if ($all.Count -gt 0) {{ Set-DnsClientServerAddress -InterfaceIndex $p[0] -ServerAddresses $all -ErrorAction Stop }}
        }} catch {{ }}
    }}
    Clear-DnsClientCache -ErrorAction SilentlyContinue
    if ($err) {{ 'ERR:' + $err }} else {{ 'ROLLBACK' }}
}} else {{ 'OK' + ($(if ($warn.Count) {{ ':' + ($warn -join ', ') }} else {{ '' }})) }}";
        return script;
    }

    public static Task FlushAsync(CancellationToken ct = default) => PowerShellRunner.RunAsync("Clear-DnsClientCache -ErrorAction SilentlyContinue", ct);
}
