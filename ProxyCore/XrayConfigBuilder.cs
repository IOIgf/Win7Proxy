using System;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 把统一 Node 模型 + 分流模式转换为 xray 配置(JSON)。
    /// 仅生成入站(socks/http) + 出站(proxy/direct/block) + 路由规则。
    /// </summary>
    public static class XrayConfigBuilder
    {
        public static XrayConfig Build(Node node, ProxyMode mode)
        {
            if (node == null) throw new ProxyCoreException("节点为空，无法生成配置。");

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
                        ["settings"] = new JObject { ["udp"] = true, ["auth"] = "noauth" }
                    },
                    new JObject {
                        ["tag"] = "http",
                        ["port"] = CoreConstants.HttpPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "http"
                    }
                },
                Outbounds = new JArray
                {
                    BuildProxyOutbound(node),
                    new JObject { ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JObject() },
                    new JObject { ["tag"] = "block", ["protocol"] = "blackhole",
                                  ["settings"] = new JObject { ["response"] = new JObject { ["type"] = "http" } } }
                }
            };

            cfg.Routing = BuildRouting(mode);
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
                                        ["security"] = string.IsNullOrEmpty(n.Security) ? "aes-128-gcm" : n.Security
                                    }
                                }
                            }
                        }
                    };
                    break;
                case NodeType.Vless:
                    ob["protocol"] = "vless";
                    ob["settings"] = new JObject {
                        ["vnext"] = new JArray {
                            new JObject {
                                ["address"] = n.Address,
                                ["port"] = n.Port,
                                ["users"] = new JArray {
                                    new JObject {
                                        ["id"] = n.UUID,
                                        ["encryption"] = "none",
                                        ["flow"] = string.IsNullOrEmpty(n.Flow) ? "" : n.Flow
                                    }
                                }
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
                    ob["settings"] = BuildAuthServers(n, false);
                    break;
                case NodeType.Http:
                    ob["protocol"] = "http";
                    ob["settings"] = BuildAuthServers(n, true);
                    break;
                default:
                    throw new ProxyCoreException("不支持的节点类型: " + n.Type);
            }
            ob["streamSettings"] = BuildStreamSettings(n);
            return ob;
        }

        private static JObject BuildAuthServers(Node n, bool isHttp)
        {
            var user = new JObject { ["address"] = n.Address, ["port"] = n.Port };
            if (!string.IsNullOrEmpty(n.UUID) || !string.IsNullOrEmpty(n.Password))
            {
                var u = new JObject { ["user"] = n.UUID, ["pass"] = n.Password };
                user["users"] = new JArray { u };
            }
            return new JObject { ["servers"] = new JArray { user } };
        }

        private static string TlsMode(Node n)
        {
            if (n.Type == NodeType.Vless && n.Extra.TryGetValue("security", out var s))
                return s;
            return n.TLS ? "tls" : "none";
        }

        private static JObject BuildStreamSettings(Node n)
        {
            var ss = new JObject();
            ss["network"] = string.IsNullOrEmpty(n.Network) ? "tcp" : n.Network;

            switch (n.Network)
            {
                case "ws":
                case "websocket":
                    ss["wsSettings"] = new JObject {
                        ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                        ["headers"] = new JObject { ["Host"] = string.IsNullOrEmpty(n.Host) ? n.Address : n.Host }
                    };
                    break;
                case "grpc":
                    ss["grpcSettings"] = new JObject {
                        ["serviceName"] = n.ServiceName,
                        ["multiMode"] = false
                    };
                    break;
                case "h2":
                    ss["httpSettings"] = new JObject {
                        ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                        ["host"] = new JArray { string.IsNullOrEmpty(n.Host) ? n.Address : n.Host }
                    };
                    break;
                default:
                    break; // tcp
            }

            var mode = TlsMode(n);
            if (mode == "reality")
            {
                ss["security"] = "reality";
                ss["realitySettings"] = new JObject {
                    ["serverName"] = string.IsNullOrEmpty(n.SNI) ? n.Host : n.SNI,
                    ["fingerprint"] = string.IsNullOrEmpty(n.Fingerprint) ? "chrome" : n.Fingerprint,
                    ["publicKey"] = n.PublicKey,
                    ["shortId"] = n.ShortId,
                    ["spiderX"] = "/"
                };
            }
            else if (mode == "tls")
            {
                ss["security"] = "tls";
                var tls = new JObject {
                    ["serverName"] = string.IsNullOrEmpty(n.SNI) ? n.Host : n.SNI,
                    ["allowInsecure"] = n.AllowInsecure
                };
                if (!string.IsNullOrEmpty(n.Fingerprint)) tls["fingerprint"] = n.Fingerprint;
                tls["alpn"] = new JArray { "h2", "http/1.1" };
                ss["tlsSettings"] = tls;
            }
            return ss;
        }

        private static JObject BuildRouting(ProxyMode mode)
        {
            var rules = new JArray();
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
            // 私有网段始终直连（所有模式）
            rules.Add(new JObject {
                ["type"] = "field", ["outboundTag"] = "direct",
                ["ip"] = new JArray { "geoip:private" }
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
    }
}
