using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// Xray 与 V2Ray 共用的配置生成。
    /// 两者格式基本一致，差异只有几处，用 <see cref="CoreSpec"/> 参数化区分：
    /// - HTTP/2 的 network 名字：Xray 写 "h2"，V2Ray 写 "http"；
    /// - 嗅探目标：Xray 支持 quic，V2Ray 只写 http/tls；
    /// - REALITY / XTLS flow：只有 Xray 支持，V2Ray 上要降级成普通 TLS。
    /// </summary>
    internal static class V2rayStyleConfigBuilder
    {
        public static XrayConfig Build(CoreSpec spec, Node node, ProxyMode mode)
        {
            if (node == null) throw new ProxyCoreException("节点为空，无法生成配置。");

            var cfg = new XrayConfig
            {
                Log = new JObject {
                    ["loglevel"] = "warning",
                    ["access"] = "access.log",
                    ["error"] = "error.log"
                },
                Inbounds = new JArray { SocksInbound(spec), HttpInbound(spec) },
                Outbounds = new JArray
                {
                    BuildProxyOutbound(spec, node),
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

        // ---------- 入站 ----------

        private static JObject Sniffing(CoreSpec spec)
        {
            var targets = new JArray { "http", "tls" };
            if (spec.Kind == CoreKind.Xray) targets.Add("quic");
            return new JObject {
                ["enabled"] = true,
                ["destOverride"] = targets,
                // routeOnly=true：嗅探结果只用于路由决策，不改写真实连接目标
                ["routeOnly"] = true
            };
        }

        private static JObject SocksInbound(CoreSpec spec)
        {
            return new JObject {
                ["tag"] = "socks",
                ["port"] = CoreConstants.SocksPort,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "socks",
                ["settings"] = new JObject { ["udp"] = true, ["auth"] = "noauth" },
                ["sniffing"] = Sniffing(spec)
            };
        }

        private static JObject HttpInbound(CoreSpec spec)
        {
            return new JObject {
                ["tag"] = "http",
                ["port"] = CoreConstants.HttpPort,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "http",
                ["sniffing"] = Sniffing(spec)
            };
        }

        // ---------- 出站 ----------

        private static JObject BuildProxyOutbound(CoreSpec spec, Node n)
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
                    var vlessUser = new JObject { ["id"] = n.UUID, ["encryption"] = "none" };
                    // flow（XTLS）只有 Xray 支持，其他内核写了会直接启动失败
                    if (spec.XtlsFlow && !string.IsNullOrEmpty(n.Flow)) vlessUser["flow"] = n.Flow;
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

            ob["streamSettings"] = BuildStreamSettings(spec, n);
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
        private static string TlsMode(CoreSpec spec, Node n)
        {
            string raw = "";
            if (n.Extra != null && n.Extra.TryGetValue("security", out var s)) raw = s;
            var mode = NetUtil.NormalizeSecurity(raw);
            if (mode == "" || mode == "none") mode = n.TLS ? "tls" : "none";
            // REALITY 只有部分内核支持，不支持时降级为普通 TLS（配合启动前的提示）
            if (mode == "reality" && !spec.Reality) mode = "tls";
            return mode;
        }

        /// <summary>HTTP/2 在两个内核里的 network 名字不同。</summary>
        private static string NetworkName(CoreSpec spec, string normalized)
        {
            if (normalized == "h2" && spec.Kind == CoreKind.V2ray) return "http";
            return normalized;
        }

        private static JObject BuildStreamSettings(CoreSpec spec, Node n)
        {
            var ss = new JObject();
            var network = NetUtil.NormalizeNetwork(n.Network);
            ss["network"] = NetworkName(spec, network);

            var path = string.IsNullOrEmpty(n.Path) ? "/" : n.Path;
            var host = string.IsNullOrEmpty(n.Host) ? FirstNonEmpty(n.SNI, n.Address) : n.Host;

            switch (network)
            {
                case "ws":
                    ss["wsSettings"] = new JObject {
                        ["path"] = path,
                        ["headers"] = new JObject { ["Host"] = host }
                    };
                    break;

                case "grpc":
                    ss["grpcSettings"] = new JObject {
                        ["serviceName"] = n.ServiceName ?? "",
                        ["multiMode"] = false
                    };
                    break;

                case "h2":
                    // Xray 写 network:"h2"、V2Ray 写 network:"http"，但 settings 都叫 httpSettings
                    ss["httpSettings"] = new JObject {
                        ["path"] = path,
                        ["host"] = new JArray { host }
                    };
                    break;

                case "httpupgrade":
                    ss["httpupgradeSettings"] = new JObject { ["path"] = path, ["host"] = host };
                    break;

                case "kcp":
                    // 仅 V2Ray 仍支持（Xray 已移除）。不写默认参数 V2Ray 也能跑，
                    // 但显式给出更稳，避免不同版本默认值差异。
                    ss["kcpSettings"] = new JObject {
                        ["mtu"] = 1350,
                        ["tti"] = 50,
                        ["uplinkCapacity"] = 12,
                        ["downlinkCapacity"] = 100,
                        ["congestion"] = false,
                        ["readBufferSize"] = 2,
                        ["writeBufferSize"] = 2,
                        ["header"] = new JObject { ["type"] = "none" }
                    };
                    break;

                default:
                    break; // tcp / quic 等不写额外 settings
            }

            var mode = TlsMode(spec, n);
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

        // ---------- 路由 / DNS ----------

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

            return new JObject { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules };
        }

        /// <summary>
        /// DNS：规则模式下国内域名走国内 DNS（只有返回国内 IP 才会被判为直连），
        /// 避免"域名被污染成国外 IP → 本该直连的流量被送去代理"。
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

            return new JObject { ["servers"] = servers, ["queryStrategy"] = "UseIPv4" };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }
    }
}
