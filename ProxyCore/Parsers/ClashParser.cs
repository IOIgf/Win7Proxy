using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ProxyCore.Models;
using YamlDotNet.RepresentationModel;

namespace ProxyCore.Parsers
{
    /// <summary>
    /// Clash / mihomo 订阅解析：YAML 中的 proxies 列表。
    /// 支持 ss / vmess / vless / trojan / socks / http(s) 节点。
    /// </summary>
    public class ClashParser : ISubscriptionParser
    {
        public bool CanParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var t = raw.Trim();
            // 肉眼先过一遍，明显不是 YAML 的直接排除，省掉一次 YAML 解析
            if (t.StartsWith("{") || t.StartsWith("[")) return false;
            if (t.IndexOf("proxies:", StringComparison.Ordinal) < 0) return false;
            try
            {
                var stream = new YamlStream();
                using (var reader = new StringReader(t))
                    stream.Load(reader);
                if (stream.Documents.Count == 0) return false;
                var root = stream.Documents[0].RootNode as YamlMappingNode;
                if (root == null) return false;
                return root.Children.TryGetValue(new YamlScalarNode("proxies"), out var p) && p is YamlSequenceNode;
            }
            catch
            {
                return false;
            }
        }

        public List<Node> Parse(string raw)
        {
            var result = new List<Node>();
            var stream = new YamlStream();
            using (var reader = new StringReader(raw))
                stream.Load(reader);
            if (stream.Documents.Count == 0) return result;
            var root = stream.Documents[0].RootNode as YamlMappingNode;
            if (root == null) return result;

            if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxiesNode))
                return result;
            if (!(proxiesNode is YamlSequenceNode seq)) return result;

            foreach (var item in seq)
            {
                if (!(item is YamlMappingNode m)) continue;
                try
                {
                    var node = ParseProxy(m);
                    if (node != null) result.Add(node);
                }
                catch
                {
                    // 单个节点解析失败不阻断整体
                }
            }
            return result;
        }

        private static Node ParseProxy(YamlMappingNode m)
        {
            var type = GetStr(m, "type").ToLowerInvariant();
            var n = new Node { Remarks = GetStr(m, "name") };
            n.Address = GetStr(m, "server", "servername-host");
            n.Port = GetInt(m, "port");

            switch (type)
            {
                case "ss":
                case "shadowsocks":
                    n.Type = NodeType.Shadowsocks;
                    n.EncryptMethod = GetStr(m, "cipher", "method");
                    n.Password = GetStr(m, "password");
                    if (m.Children.ContainsKey(new YamlScalarNode("plugin")))
                        n.Extra["plugin"] = GetStr(m, "plugin");
                    break;

                case "vmess":
                    n.Type = NodeType.Vmess;
                    n.UUID = GetStr(m, "uuid", "password");
                    n.Security = GetStr(m, "cipher");
                    if (string.IsNullOrEmpty(n.Security)) n.Security = "auto";
                    n.Network = NetUtil.NormalizeNetwork(GetStr(m, "network"));
                    n.SNI = GetStr(m, "servername", "sni", "server-name");
                    n.Fingerprint = GetStr(m, "client-fingerprint");
                    n.Alpn = GetListStr(m, "alpn");
                    ApplySecurity(m, n, "tls");
                    ApplyTransportOpts(m, n);
                    break;

                case "vless":
                    n.Type = NodeType.Vless;
                    n.UUID = GetStr(m, "uuid", "password");
                    n.Flow = GetStr(m, "flow");
                    n.Network = NetUtil.NormalizeNetwork(GetStr(m, "network"));
                    n.SNI = GetStr(m, "servername", "sni", "server-name");
                    n.Fingerprint = GetStr(m, "client-fingerprint");
                    n.Alpn = GetListStr(m, "alpn");
                    // mihomo 里 REALITY 节点仍然写 tls: true，靠 reality-opts 区分
                    var isReality = ApplyReality(m, n);
                    if (!isReality) ApplySecurity(m, n, "tls");
                    ApplyTransportOpts(m, n);
                    break;

                case "trojan":
                    n.Type = NodeType.Trojan;
                    n.Password = GetStr(m, "password");
                    n.Network = NetUtil.NormalizeNetwork(GetStr(m, "network"));
                    n.SNI = GetStr(m, "sni", "servername", "server-name");
                    n.Fingerprint = GetStr(m, "client-fingerprint");
                    n.Alpn = GetListStr(m, "alpn");
                    n.TLS = true;
                    n.Extra["security"] = "tls";
                    ApplyTransportOpts(m, n);
                    break;

                case "socks":
                case "socks5":
                    n.Type = NodeType.Socks;
                    n.UUID = GetStr(m, "username");
                    n.Password = GetStr(m, "password");
                    break;

                case "hysteria2":
                case "hy2":
                    n.Type = NodeType.Hysteria2;
                    n.Password = GetStr(m, "password", "auth", "auth-str");
                    n.SNI = GetStr(m, "sni", "servername");
                    n.Fingerprint = GetStr(m, "fingerprint", "client-fingerprint");
                    n.Alpn = GetListStr(m, "alpn");
                    n.Obfs = GetStr(m, "obfs");
                    n.ObfsPassword = GetStr(m, "obfs-password", "obfs-pwd");
                    n.PinnedCertSha256 = GetStr(m, "pinSHA256", "pin-sha256");
                    n.UpMbps = ParseMbps(GetStr(m, "up", "up-mbps"));
                    n.DownMbps = ParseMbps(GetStr(m, "down", "down-mbps"));
                    var ports = GetStr(m, "ports", "mport");
                    if (!string.IsNullOrEmpty(ports)) n.Ports = ports;
                    n.TLS = true;
                    n.Extra["security"] = "tls";
                    break;

                case "http":
                case "https":
                    n.Type = NodeType.Http;
                    n.UUID = GetStr(m, "username");
                    n.Password = GetStr(m, "password");
                    n.Path = GetStr(m, "path");
                    n.TLS = type == "https" || GetBool(m, "tls");
                    if (n.TLS) n.Extra["security"] = "tls";
                    break;

                default:
                    return null;
            }

            n.AllowInsecure = GetBool(m, "skip-cert-verify");
            // pinSHA256 任何类型都可能带；以前只在 hysteria2 分支里读，trojan/vless 带了也会被丢掉。
            if (string.IsNullOrEmpty(n.PinnedCertSha256))
                n.PinnedCertSha256 = GetStr(m, "pinSHA256", "pin-sha256", "pinnedPeerCertSha256", "certSha256");
            return n;
        }

        /// <summary>读取 reality-opts（public-key / short-id）；命中返回 true。</summary>
        private static bool ApplyReality(YamlMappingNode m, Node n)
        {
            if (!m.Children.TryGetValue(new YamlScalarNode("reality-opts"), out var rNode)) return false;
            if (!(rNode is YamlMappingNode r)) return false;
            var pub = GetStr(r, "public-key", "publicKey");
            if (string.IsNullOrEmpty(pub)) return false;
            n.PublicKey = pub;
            n.ShortId = GetStr(r, "short-id", "shortId");
            n.TLS = true;
            n.Extra["security"] = "reality";
            if (string.IsNullOrEmpty(n.Fingerprint)) n.Fingerprint = "chrome";
            return true;
        }

        /// <summary>读取 tls 开关（Clash 里是 YAML 布尔）。</summary>
        private static void ApplySecurity(YamlMappingNode m, Node n, string key)
        {
            var tls = GetBool(m, key, "tls");
            n.TLS = tls;
            n.Extra["security"] = tls ? "tls" : "none";
        }

        /// <summary>读取 ws-opts / h2-opts / grpc-opts。</summary>
        private static void ApplyTransportOpts(YamlMappingNode m, Node n)
        {
            // ws
            if (m.Children.TryGetValue(new YamlScalarNode("ws-opts"), out var wsNode) && wsNode is YamlMappingNode ws)
            {
                n.Network = "ws";
                n.Path = GetStr(ws, "path");
                if (ws.Children.TryGetValue(new YamlScalarNode("headers"), out var hNode) && hNode is YamlMappingNode h)
                {
                    n.Host = GetStr(h, "Host", "host");
                    var early = GetStr(ws, "max-early-data", "early-data-header-name");
                    if (!string.IsNullOrEmpty(early)) n.Extra["maxEarlyData"] = early;
                }
            }

            // h2
            if (m.Children.TryGetValue(new YamlScalarNode("h2-opts"), out var h2Node) && h2Node is YamlMappingNode h2)
            {
                n.Network = "h2";
                n.Path = GetStr(h2, "path");
                var hosts = GetListStr(h2, "host");
                if (!string.IsNullOrEmpty(hosts)) n.Host = hosts.Split(',')[0];
            }

            // grpc
            if (m.Children.TryGetValue(new YamlScalarNode("grpc-opts"), out var gNode) && gNode is YamlMappingNode g)
            {
                n.Network = "grpc";
                n.ServiceName = GetStr(g, "grpc-service-name", "serviceName");
            }

            // 顶层也可能直接给 path / serviceName
            if (string.IsNullOrEmpty(n.Path))
            {
                var p = GetStr(m, "path", "ws-path");
                if (!string.IsNullOrEmpty(p)) n.Path = p;
            }
            if (string.IsNullOrEmpty(n.ServiceName))
            {
                var s = GetStr(m, "serviceName", "grpc-service-name");
                if (!string.IsNullOrEmpty(s)) n.ServiceName = s;
            }
            if (string.IsNullOrEmpty(n.Network)) n.Network = "tcp";
        }

        private static string GetStr(YamlMappingNode m, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (m.Children.TryGetValue(new YamlScalarNode(k), out var v))
                {
                    var s = (v as YamlScalarNode)?.Value;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return "";
        }

        /// <summary>alpn 在 YAML 里是列表，取出后拼成逗号分隔字符串。</summary>
        private static string GetListStr(YamlMappingNode m, string key)
        {
            if (!m.Children.TryGetValue(new YamlScalarNode(key), out var v)) return "";
            if (v is YamlSequenceNode seq)
            {
                var parts = new List<string>();
                foreach (var item in seq)
                {
                    var s = (item as YamlScalarNode)?.Value;
                    if (!string.IsNullOrEmpty(s)) parts.Add(s);
                }
                return string.Join(",", parts.ToArray());
            }
            return (v as YamlScalarNode)?.Value ?? "";
        }

        private static bool GetBool(YamlMappingNode m, params string[] keys)
        {
            var s = GetStr(m, keys);
            if (string.IsNullOrEmpty(s)) return false;
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private static int GetInt(YamlMappingNode m, string key)
        {
            var s = GetStr(m, key);
            int i;
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
            return i;
        }

        /// <summary>带宽字段可能是 "100"、"100 Mbps"、"100Mbps"，取前导数字。</summary>
        private static int ParseMbps(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            var v = 0;
            var any = false;
            foreach (var ch in s.Trim())
            {
                if (ch < '0' || ch > '9') break;
                v = v * 10 + (ch - '0');
                any = true;
            }
            return any ? v : 0;
        }
    }
}
