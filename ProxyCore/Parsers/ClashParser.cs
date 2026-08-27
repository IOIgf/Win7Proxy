using System;
using System.Collections.Generic;
using System.IO;
using ProxyCore.Models;
using YamlDotNet.RepresentationModel;

namespace ProxyCore.Parsers
{
    /// <summary>
    /// Clash / mihomo 订阅解析：YAML 中的 proxies 列表。
    /// 支持 ss / vmess / vless / trojan / socks / http 节点。
    /// </summary>
    public class ClashParser : ISubscriptionParser
    {
        public bool CanParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var t = raw.Trim();
            return (t.Contains("proxies:") || t.StartsWith("proxies:")) &&
                   (t.Contains("proxy-groups:") || t.Contains("rules:") || t.Contains("port:"));
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
                var node = ParseProxy(m);
                if (node != null) result.Add(node);
            }
            return result;
        }

        private static Node ParseProxy(YamlMappingNode m)
        {
            var type = GetStr(m, "type").ToLowerInvariant();
            var n = new Node { Remarks = GetStr(m, "name") };
            n.Address = GetStr(m, "server");
            n.Port = GetInt(m, "port");

            switch (type)
            {
                case "ss":
                case "shadowsocks":
                    n.Type = NodeType.Shadowsocks;
                    n.EncryptMethod = GetStr(m, "cipher");
                    n.Password = GetStr(m, "password");
                    break;
                case "vmess":
                    n.Type = NodeType.Vmess;
                    n.UUID = GetStr(m, "uuid");
                    n.Security = GetStr(m, "cipher"); if (string.IsNullOrEmpty(n.Security)) n.Security = "aes-128-gcm";
                    n.TLS = GetStr(m, "tls") == "true";
                    n.Network = GetStr(m, "network"); if (string.IsNullOrEmpty(n.Network)) n.Network = "tcp";
                    n.SNI = GetStr(m, "servername");
                    ApplyWsOpts(m, n);
                    break;
                case "vless":
                    n.Type = NodeType.Vless;
                    n.UUID = GetStr(m, "uuid");
                    n.TLS = GetStr(m, "tls") == "true";
                    n.Flow = GetStr(m, "flow");
                    n.Network = GetStr(m, "network"); if (string.IsNullOrEmpty(n.Network)) n.Network = "tcp";
                    n.SNI = GetStr(m, "servername");
                    n.Fingerprint = GetStr(m, "client-fingerprint");
                    ApplyWsOpts(m, n);
                    break;
                case "trojan":
                    n.Type = NodeType.Trojan;
                    n.Password = GetStr(m, "password");
                    n.TLS = true;
                    n.SNI = GetStr(m, "sni"); if (string.IsNullOrEmpty(n.SNI)) n.SNI = GetStr(m, "servername");
                    n.Network = GetStr(m, "network"); if (string.IsNullOrEmpty(n.Network)) n.Network = "tcp";
                    ApplyWsOpts(m, n);
                    break;
                case "socks":
                case "socks5":
                    n.Type = NodeType.Socks;
                    n.UUID = GetStr(m, "username");
                    n.Password = GetStr(m, "password");
                    break;
                case "http":
                case "https":
                    n.Type = NodeType.Http;
                    n.UUID = GetStr(m, "username");
                    n.Password = GetStr(m, "password");
                    n.TLS = type == "https";
                    break;
                default:
                    return null;
            }
            return n;
        }

        private static void ApplyWsOpts(YamlMappingNode m, Node n)
        {
            if (!m.Children.TryGetValue(new YamlScalarNode("ws-opts"), out var wsNode)) return;
            if (!(wsNode is YamlMappingNode ws)) return;
            n.Path = GetStr(ws, "path");
            if (ws.Children.TryGetValue(new YamlScalarNode("headers"), out var hNode) && hNode is YamlMappingNode h)
            {
                n.Host = GetStr(h, "Host");
            }
        }

        private static string GetStr(YamlMappingNode m, string key)
        {
            if (m.Children.TryGetValue(new YamlScalarNode(key), out var v))
                return (v as YamlScalarNode)?.Value ?? "";
            return "";
        }

        private static int GetInt(YamlMappingNode m, string key)
        {
            var s = GetStr(m, key);
            int.TryParse(s, out var i);
            return i;
        }
    }
}
