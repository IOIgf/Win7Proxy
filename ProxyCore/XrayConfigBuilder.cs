using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 把统一 Node 模型 + 分流模式转换为 xray 配置(JSON)。
    /// 仅生成入站(socks/http) + 出站(proxy/direct/block) + 路由规则 + DNS。
    /// </summary>
    public static class XrayConfigBuilder
    {
        public static XrayConfig Build(Node node, ProxyMode mode)
        {
            if (node == null) throw new ProxyCoreException("节点为空，无法生成配置。");

            var sniffing = new JObject {
                ["enabled"] = true,
                ["destOverride"] = new JArray { "http", "tls", "quic" },
                // routeOnly=true：嗅探结果只用于路由决策，不改变实际连接目标。
                // 这样既能让 geosite 规则命中真实域名，又不会改写出问题。
                ["routeOnly"] = true
            };

            var cfg = new XrayConfig
            {
                Log = new JObject { ["loglevel"] = "warning",
                                    ["access"] = "access.log",
                                    ["error"] = "error.log" },
                Inbounds = new JArray
                {
                    new JObject {
                        ["tag"] = "socks",
                        ["port"] = CoreConstants.SocksPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "socks",
                        ["settings"] = new JObject { ["udp"] = true, ["auth"] = "noauth" },
                        ["sniffing"] = sniffing
                    },
                    new JObject {
                        ["tag"] = "http",
                        ["port"] = CoreConstants.HttpPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "http",
                        ["sniffing"] = sniffing
                    }
                },
                Outbounds = new JArray
                {
                    BuildProxyOutbound(node),
                    new JObject { ["tag"] = "direct", ["protocol"] = "freedom",
                                  ["settings"] = new JObject { ["domainStrategy"] = "UseIPv4" } },
                    new JObject { ["tag"] = "block", ["protocol"] = "blackhole",
                                  ["settings"] = new JObject { ["response"] = new JObject { ["type"] = "http" } } }
                }
            };

            cfg.Routing = BuildRouting(mode);
            cfg.Dns = BuildDns(mode);
            return cfg;
        }

        private static JObject BuildProxyOutbound(Node n)
        {
            var ob = new JObject { ["tag"] = "proxy" };
            switch (n.Type)
            {
                case NodeType.Vmess:
                    ob["protocol"] = "vmess";
                    ob["settings"] = new JObject {
                        ["vnext"] = new JArray {
                            new JObject {
                                ["address"] = n.Address,
                                ["port"] = n.Port,
                                ["users"] = new JArray {
                                    new JObject {
                                        ["id"] = n.UUID,
                                        ["alterId"] = 0,
                                        ["security"] = string.IsNullOrEmpty(n.Security) ? "auto" : n.Security
                                    }
                                }
                            }
                        }
                    };
                    break;
                case NodeType.Vless:
                    ob["protocol"] = "vless";
                    var vlessUser = new JObject {
                        ["id"] = n.UUID,
                        ["encryption"] = "none"
                    };
                    if (!string.IsNullOrEmpty(n.Flow)) vlessUser["flow"] = n.Flow;
                    ob["settings"] = new JObject {
                        ["vnext"] = new JArray {
                            new JObject {
                                ["address"] = n.Address,
                                ["port"] = n.Port,
                                ["users"] = new JArray { vlessUser }
                            }
                        }
                    };
                    break;
                case NodeType.Trojan:
                    ob["protocol"] = "trojan";
                    ob["settings"] = new JObject {
                        ["servers"] = new JArray {
                            new JObject { ["address"] = n.Address, ["port"] = n.Port, ["password"] = n.Password }
                        }
                    };
                    break;
                case NodeType.Shadowsocks:
                    ob["protocol"] = "shadowsocks";
                    ob["settings"] = new JObject {
                        ["servers"] = new JArray {
                            new JObject { ["address"] = n.Address, ["port"] = n.Port,
                                          ["method"] = n.EncryptMethod, ["password"] = n.Password }
                        }
                    };
                    break;
                case NodeType.Socks:
                    ob["protocol"] = "socks";
                    ob["settings"] = BuildAuthServers(n);
                    break;
                case NodeType.Http:
                    ob["protocol"] = "http";
                    ob["settings"] = BuildAuthServers(n);
                    break;
                default:
                    throw new ProxyCoreException("不支持的节点类型: " + n.Type);
            }

            ob["streamSettings"] = BuildStreamSettings(n);
            return ob;
        }

        private static JObject BuildAuthServers(Node n)
        {
            var server = new JObject { ["address"] = n.Address, ["port"] = n.Port };
            if (!string.IsNullOrEmpty(n.UUID) || !string.IsNullOrEmpty(n.Password))
            {
                server["users"] = new JArray {
                    new JObject { ["user"] = n.UUID, ["pass"] = n.Password }
                };
            }
            return new JObject { ["servers"] = new JArray { server } };
        }

        /// <summary>
        /// 决定 TLS 模式：优先看解析器写进 Extra["security"] 的归一化结果，
        /// 这样 vmess / vless / trojan 走同一套判断，不再只认 vless。
        /// </summary>
        private static string TlsMode(Node n)
        {
            if (n.Extra != null && n.Extra.TryGetValue("security", out var s) && !string.IsNullOrEmpty(s))
                return NetUtil.NormalizeSecurity(s);
            return n.TLS ? "tls" : "none";
        }

        private static JObject BuildStreamSettings(Node n)
        {
            var ss = new JObject();
            var network = NetUtil.NormalizeNetwork(n.Network);
            ss["network"] = network;

            switch (network)
            {
                case "ws":
                    ss["wsSettings"] = new JObject {
                        ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                        ["headers"] = new JObject { ["Host"] = string.IsNullOrEmpty(n.Host) ? FirstNonEmpty(n.SNI, n.Address) : n.Host }
                    };
                    break;
                case "grpc":
                    ss["grpcSettings"] = new JObject {
                        ["serviceName"] = n.ServiceName ?? "",
                        ["multiMode"] = false
                    };
                    break;
                case "h2":
                    ss["httpSettings"] = new JObject {
                        ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                        ["host"] = new JArray { string.IsNullOrEmpty(n.Host) ? FirstNonEmpty(n.SNI, n.Address) : n.Host }
                    };
                    break;
                case "httpupgrade":
                    ss["httpupgradeSettings"] = new JObject {
                        ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                        ["host"] = string.IsNullOrEmpty(n.Host) ? FirstNonEmpty(n.SNI, n.Address) : n.Host
                    };
                    break;
                default:
                    break; // tcp / kcp / quic 等不写额外 settings
            }

            var mode = TlsMode(n);
            var serverName = string.IsNullOrEmpty(n.SNI) ? FirstNonEmpty(n.Host, n.Address) : n.SNI;

            if (mode == "reality")
            {
                ss["security"] = "reality";
                ss["realitySettings"] = new JObject {
                    ["serverName"] = serverName,
                    ["fingerprint"] = string.IsNullOrEmpty(n.Fingerprint) ? "chrome" : n.Fingerprint,
                    ["publicKey"] = n.PublicKey,
                    ["shortId"] = n.ShortId ?? "",
                    ["spiderX"] = "/"
                };
            }
            else if (mode == "tls")
            {
                ss["security"] = "tls";
                var tls = new JObject {
                    ["serverName"] = serverName,
                    ["allowInsecure"] = n.AllowInsecure
                };
                if (!string.IsNullOrEmpty(n.Fingerprint)) tls["fingerprint"] = n.Fingerprint;

                var alpn = NetUtil.SplitAlpn(n.Alpn);
                if (alpn.Count == 0) alpn.AddRange(new[] { "h2", "http/1.1" });
                tls["alpn"] = new JArray(alpn.ToArray());

                ss["tlsSettings"] = tls;
            }
            else
            {
                ss["security"] = "none";
            }
            return ss;
        }

        private static JObject BuildRouting(ProxyMode mode)
        {
            var rules = new JArray();

            // 广告拦截与国内域名直连只在规则模式下加
            if (mode == ProxyMode.Rule)
            {
                rules.Add(new JObject {
                    ["type"] = "field", ["outboundTag"] = "block",
                    ["domain"] = new JArray { "geosite:category-ads-all" }
                });
                rules.Add(new JObject {
                    ["type"] = "field", ["outboundTag"] = "direct",
                    ["domain"] = new JArray { "geosite:cn" }
                });
            }

            // 私有网段与本机始终直连（所有模式）
            rules.Add(new JObject {
                ["type"] = "field", ["outboundTag"] = "direct",
                ["ip"] = new JArray { "geoip:private", "127.0.0.0/8", "::1/128" }
            });

            if (mode == ProxyMode.Rule)
            {
                rules.Add(new JObject {
                    ["type"] = "field", ["outboundTag"] = "direct",
                    ["ip"] = new JArray { "geoip:cn" }
                });
            }

            return new JObject {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            };
        }

        /// <summary>
        /// DNS：规则模式下国内域名走国内 DNS（返回国内 IP 才会被判为直连），其余走境外 DNS，
        /// 避免"域名被污染成国外 IP → 本该直连的流量被送去代理"这类典型问题。
        /// </summary>
        private static JObject BuildDns(ProxyMode mode)
        {
            var servers = new JArray();
            if (mode == ProxyMode.Rule)
            {
                servers.Add(new JObject {
                    ["address"] = "223.5.5.5",
                    ["domains"] = new JArray { "geosite:cn" },
                    ["expectIPs"] = new JArray { "geoip:cn" },
                    ["skipFallback"] = true
                });
                servers.Add(new JObject {
                    ["address"] = "119.29.29.29",
                    ["domains"] = new JArray { "geosite:cn" },
                    ["expectIPs"] = new JArray { "geoip:cn" },
                    ["skipFallback"] = true
                });
            }
            servers.Add("8.8.8.8");
            servers.Add("1.1.1.1");

            return new JObject {
                ["servers"] = servers,
                ["queryStrategy"] = "UseIPv4"
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }
    }
}
