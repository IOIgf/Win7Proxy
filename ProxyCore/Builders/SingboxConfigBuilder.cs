using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// sing-box 配置生成。与 v2ray 系配置格式差别很大：出站没有 protocol/settings 之分，
    /// 而是扁平的 type + 具体字段；传输层是独立的 transport 对象；路由规则改用 rule_set。
    /// 支持 h2（transport.type = "http"）与 REALITY，不支持 XTLS flow。
    /// </summary>
    public static class SingboxConfigBuilder
    {
        /// <param name="geoDbAvailable">core 目录下是否已放置规则模式需要的三个 .srs 文件。</param>
        public static string BuildJson(Node node, ProxyMode mode, bool geoDbAvailable, InboundOptions inbound)
        {
            if (node == null) throw new ProxyCoreException("节点为空，无法生成配置。");
            inbound = inbound ?? InboundOptions.Default;

            var root = new JObject
            {
                ["log"] = new JObject {
                    // 不写 output 文件：日志要能进程序的日志框（捕获 stdout/stderr），
                    // 否则内核报错只落在 sing-box.log 里，界面上看不到。
                    ["level"] = "warn",
                    ["timestamp"] = true
                },
                ["inbounds"] = new JArray { MixedInbound(inbound) },
                ["outbounds"] = new JArray {
                    BuildProxyOutbound(node),
                    new JObject { ["type"] = "direct", ["tag"] = "direct" },
                    new JObject { ["type"] = "block", ["tag"] = "block" }
                }
            };

            root["route"] = BuildRoute(mode, geoDbAvailable);
            root["dns"] = BuildDns(mode, geoDbAvailable);
            return root.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        // ---------- 入站 ----------

        // sing-box 1.13.0 起，inbound 上的 sniff / sniff_override_destination 字段被彻底移除，
        // 嗅探改为在 route.rules 里用 { "action": "sniff" } 触发（默认不改写目标地址，仅用于路由决策，
        // 等价于 v2ray 的 routeOnly）。见 BuildRoute。
        // sing-box 有原生 mixed 入站：一个端口同时接受 SOCKS 与 HTTP。
        private static JObject MixedInbound(InboundOptions inbound)
        {
            return new JObject {
                ["type"] = "mixed",
                ["tag"] = "mixed",
                ["listen"] = inbound.ListenAddress,
                ["listen_port"] = inbound.MixedPort
            };
        }

        // ---------- 出站 ----------

        private static JObject BuildProxyOutbound(Node n)
        {
            var ob = new JObject { ["tag"] = "proxy" };

            switch (n.Type)
            {
                case NodeType.Vmess:
                    ob["type"] = "vmess";
                    ob["uuid"] = n.UUID;
                    ob["security"] = string.IsNullOrEmpty(n.Security) ? "auto" : n.Security;
                    ob["alter_id"] = 0;
                    break;

                case NodeType.Vless:
                    ob["type"] = "vless";
                    ob["uuid"] = n.UUID;
                    // sing-box 主线构建不含 XTLS，写了反而会失败，因此不输出 flow
                    break;

                case NodeType.Trojan:
                    ob["type"] = "trojan";
                    ob["password"] = n.Password;
                    break;

                case NodeType.Shadowsocks:
                    ob["type"] = "shadowsocks";
                    ob["method"] = n.EncryptMethod;
                    ob["password"] = n.Password;
                    // sing-box 出站原生支持 SIP003 插件（仅 obfs-local 与 v2ray-plugin），
                    // 直接透传即可；能力检查在 CoreSpec.ReasonUnsupported 里做。
                    var plugin = SsPluginParser.Parse(n.Extra);
                    if (plugin != null)
                    {
                        ob["plugin"] = plugin.IsObfs ? "obfs-local" : plugin.Name;
                        if (!string.IsNullOrEmpty(plugin.Options)) ob["plugin_opts"] = plugin.Options;
                    }
                    break;

                case NodeType.Socks:
                    ob["type"] = "socks";
                    ob["version"] = "5";
                    if (!string.IsNullOrEmpty(n.UUID)) ob["username"] = n.UUID;
                    if (!string.IsNullOrEmpty(n.Password)) ob["password"] = n.Password;
                    break;

                case NodeType.Http:
                    ob["type"] = "http";
                    if (!string.IsNullOrEmpty(n.UUID)) ob["username"] = n.UUID;
                    if (!string.IsNullOrEmpty(n.Password)) ob["password"] = n.Password;
                    break;

                case NodeType.Hysteria2:
                    ob["type"] = "hysteria2";
                    ob["password"] = n.Password;
                    if (!string.IsNullOrEmpty(n.Obfs) && !string.IsNullOrEmpty(n.ObfsPassword))
                        ob["obfs"] = new JObject { ["type"] = n.Obfs, ["password"] = n.ObfsPassword };
                    if (n.UpMbps > 0) ob["up_mbps"] = n.UpMbps;
                    if (n.DownMbps > 0) ob["down_mbps"] = n.DownMbps;
                    break;

                default:
                    throw new ProxyCoreException("不支持的节点类型: " + n.Type);
            }

            ob["server"] = n.Address;
            ob["server_port"] = n.Port;

            if (n.Type == NodeType.Hysteria2)
            {
                var ports = NetUtil.SplitPorts(n.Ports);
                if (ports.Count > 0) ob["server_ports"] = new JArray(ports.ToArray());
            }

            var transport = BuildTransport(n);
            if (transport != null) ob["transport"] = transport;

            var tls = n.Type == NodeType.Hysteria2 ? BuildHysteria2Tls(n) : BuildTls(n);
            if (tls != null) ob["tls"] = tls;

            return ob;
        }

        private static JObject BuildTransport(Node n)
        {
            var network = NetUtil.NormalizeNetwork(n.Network);
            var path = string.IsNullOrEmpty(n.Path) ? "/" : n.Path;
            var host = string.IsNullOrEmpty(n.Host) ? FirstNonEmpty(n.SNI, n.Address) : n.Host;

            switch (network)
            {
                case "ws":
                    return new JObject {
                        ["type"] = "ws",
                        ["path"] = path,
                        ["headers"] = new JObject { ["Host"] = host }
                    };
                case "grpc":
                    return new JObject {
                        ["type"] = "grpc",
                        ["service_name"] = n.ServiceName ?? ""
                    };
                case "h2":
                    // sing-box 里 HTTP/2 的 transport 就叫 http
                    return new JObject {
                        ["type"] = "http",
                        ["host"] = new JArray { host },
                        ["path"] = path
                    };
                case "httpupgrade":
                    return new JObject {
                        ["type"] = "httpupgrade",
                        ["host"] = host,
                        ["path"] = path
                    };
                case "quic":
                    return new JObject { ["type"] = "quic" };
                default:
                    return null; // tcp 不需要 transport
            }
        }

        private static JObject BuildTls(Node n)
        {
            string raw = "";
            if (n.Extra != null && n.Extra.TryGetValue("security", out var s)) raw = s;
            var mode = NetUtil.NormalizeSecurity(raw);
            if (mode == "" || mode == "none") mode = n.TLS ? "tls" : "none";

            if (mode == "none") return null;

            var serverName = string.IsNullOrEmpty(n.SNI) ? FirstNonEmpty(n.Host, n.Address) : n.SNI;
            var tls = new JObject {
                ["enabled"] = true,
                ["server_name"] = serverName,
                ["insecure"] = n.AllowInsecure
            };

            var alpn = NetUtil.SplitAlpn(n.Alpn);
            if (alpn.Count == 0) alpn.AddRange(new[] { "h2", "http/1.1" });
            tls["alpn"] = new JArray(alpn.ToArray());

            if (mode == "reality")
            {
                tls["reality"] = new JObject {
                    ["enabled"] = true,
                    ["public_key"] = n.PublicKey,
                    ["short_id"] = n.ShortId ?? ""
                };
            }

            if (!string.IsNullOrEmpty(n.Fingerprint))
                tls["utls"] = new JObject { ["enabled"] = true, ["fingerprint"] = n.Fingerprint };

            return tls;
        }

        /// <summary>
        /// Hysteria2 基于 QUIC，TLS 恒开启且 ALPN 必须是 h3；不能套用 v2ray 系的 h2 默认值。
        /// </summary>
        private static JObject BuildHysteria2Tls(Node n)
        {
            var serverName = string.IsNullOrEmpty(n.SNI) ? FirstNonEmpty(n.Host, n.Address) : n.SNI;
            var tls = new JObject
            {
                ["enabled"] = true,
                ["server_name"] = serverName,
                ["insecure"] = n.AllowInsecure
            };

            var alpn = NetUtil.SplitAlpn(n.Alpn);
            if (alpn.Count == 0) alpn.Add("h3");
            tls["alpn"] = new JArray(alpn.ToArray());

            return tls;
        }

        // ---------- 路由 / DNS ----------

        private static JObject BuildRoute(ProxyMode mode, bool geoDb)
        {
            var rules = new JArray();
            var sets = new JArray();

            // sing-box 1.13+：嗅探由路由规则 action 触发，且必须放在其他规则之前，
            // 这样后续基于域名/协议的规则才能用到嗅探结果。默认不改写目标地址（routeOnly）。
            rules.Add(new JObject { ["action"] = "sniff" });

            if (mode == ProxyMode.Rule && geoDb)
            {
                rules.Add(new JObject { ["outbound"] = "block", ["rule_set"] = new JArray { "geosite-category-ads-all" } });
                rules.Add(new JObject { ["outbound"] = "direct", ["rule_set"] = new JArray { "geosite-cn" } });
                rules.Add(new JObject { ["outbound"] = "direct", ["rule_set"] = new JArray { "geoip-cn" } });

                sets.Add(LocalSet("geosite-category-ads-all", CoreConstants.GeoSiteAdsSrs));
                sets.Add(LocalSet("geosite-cn", CoreConstants.GeoSiteCnSrs));
                sets.Add(LocalSet("geoip-cn", CoreConstants.GeoIpCnSrs));
            }

            // 私有网段与本机始终直连（不依赖 geo 数据）
            rules.Add(new JObject { ["outbound"] = "direct", ["ip_is_private"] = true });

            var route = new JObject {
                ["rules"] = rules,
                ["final"] = "proxy",
                // sing-box 1.12+ 要求显式声明默认域名解析器（1.14 起缺失直接报错），
                // 指向远程 DNS（走代理），出站拨号时用它解析域名。
                ["default_domain_resolver"] = "dns-remote"
            };
            if (sets.Count > 0) route["rule_set"] = sets;
            return route;
        }

        private static JObject LocalSet(string tag, string file)
        {
            return new JObject {
                ["type"] = "local",
                ["tag"] = tag,
                ["format"] = "binary",
                ["path"] = file
            };
        }

        private static JObject BuildDns(ProxyMode mode, bool geoDb)
        {
            var servers = new JArray();
            var rules = new JArray();

            if (mode == ProxyMode.Rule)
            {
                // sing-box 1.12+ 的 DNS server 新格式：type + server（旧 address 写法在 1.14.0 移除）
                servers.Add(new JObject { ["type"] = "udp", ["tag"] = "dns-direct", ["server"] = "223.5.5.5", ["detour"] = "direct" });
                if (geoDb)
                {
                    rules.Add(new JObject {
                        ["rule_set"] = new JArray { "geosite-cn" },
                        ["server"] = "dns-direct"
                    });
                }
            }

            servers.Add(new JObject { ["type"] = "udp", ["tag"] = "dns-remote", ["server"] = "8.8.8.8", ["detour"] = "proxy" });

            var dns = new JObject {
                ["servers"] = servers,
                ["final"] = "dns-remote",
                ["strategy"] = "ipv4_only"
            };
            if (rules.Count > 0) dns["rules"] = rules;
            return dns;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }
    }
}
