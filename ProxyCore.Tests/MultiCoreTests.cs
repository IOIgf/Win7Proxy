using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using ProxyCore;
using ProxyCore.Models;

namespace ProxyCore.Tests
{
    /// <summary>多内核支持：能力表判定 + 各内核配置生成格式的差异。</summary>
    public class MultiCoreTests
    {
        private const string Uuid = "11111111-1111-1111-1111-111111111111";

        private static Node H2Node()
        {
            return new Node
            {
                Type = NodeType.Vmess,
                Remarks = "h2",
                Address = "1.2.3.4",
                Port = 443,
                UUID = Uuid,
                Security = "auto",
                Network = "h2",
                Path = "/ray",
                Host = "example.com",
                TLS = true,
                SNI = "example.com"
            };
        }

        private static Node RealityVless()
        {
            return new Node
            {
                Type = NodeType.Vless,
                Remarks = "r",
                Address = "1.2.3.4",
                Port = 443,
                UUID = Uuid,
                TLS = true,
                Extra = { ["security"] = "reality" },
                Flow = "xtls-rprx-vision",
                Network = "tcp",
                SNI = "example.com",
                PublicKey = "pk",
                ShortId = "sid"
            };
        }

        // ---------- 能力表 ----------

        [Fact]
        public void 能力表_h2只有V2Ray和singbox支持()
        {
            Assert.False(CoreRegistry.Xray.SupportsNetwork("h2"));
            Assert.True(CoreRegistry.V2ray.SupportsNetwork("h2"));
            Assert.True(CoreRegistry.Singbox.SupportsNetwork("h2"));

            // 别名也要能归一化后命中
            Assert.True(CoreRegistry.V2ray.SupportsNetwork("http"));
            Assert.True(CoreRegistry.V2ray.SupportsNetwork("HTTP/2"));
        }

        [Fact]
        public void 能力表_h2节点在Xray上给出可操作建议()
        {
            var reason = CoreRegistry.Xray.ReasonUnsupported(H2Node());
            Assert.NotNull(reason);
            Assert.Contains("h2", reason);
            Assert.Contains("V2Ray", reason);   // 应该告诉用户换哪个内核

            Assert.Null(CoreRegistry.V2ray.ReasonUnsupported(H2Node()));
            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(H2Node()));
        }

        [Fact]
        public void 能力表_REALITY和XTLS只有部分内核支持()
        {
            var n = RealityVless();
            Assert.Null(CoreRegistry.Xray.ReasonUnsupported(n));
            Assert.Contains("REALITY", CoreRegistry.V2ray.ReasonUnsupported(n));
            // sing-box 支持 REALITY，但主线构建不含 XTLS，所以带 flow 的节点仍要拦下
            Assert.Contains("XTLS", CoreRegistry.Singbox.ReasonUnsupported(n));

            // 去掉 flow 之后 sing-box 就能用了，V2Ray 依旧不行
            var noFlow = RealityVless();
            noFlow.Flow = "";
            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(noFlow));
            Assert.Contains("REALITY", CoreRegistry.V2ray.ReasonUnsupported(noFlow));
        }

        // ---------- 配置格式差异 ----------

        [Fact]
        public void V2Ray配置_h2的network写作http()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.V2ray, H2Node(), ProxyMode.Rule);
            var ob = (JObject)JObject.Parse(json)["outbounds"][0];
            Assert.Equal("http", (string)ob["streamSettings"]["network"]);
            Assert.NotNull(ob["streamSettings"]["httpSettings"]);
            Assert.Equal("/ray", (string)ob["streamSettings"]["httpSettings"]["path"]);
        }

        [Fact]
        public void Xray配置_h2仍写作h2()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.Xray, H2Node(), ProxyMode.Rule);
            var ob = (JObject)JObject.Parse(json)["outbounds"][0];
            Assert.Equal("h2", (string)ob["streamSettings"]["network"]);
        }

        [Fact]
        public void singbox配置_h2用transport_http()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.Singbox, H2Node(), ProxyMode.Rule);
            var root = JObject.Parse(json);
            var ob = (JObject)root["outbounds"][0];
            Assert.Equal("vmess", (string)ob["type"]);
            Assert.Equal("http", (string)ob["transport"]["type"]);
            Assert.Equal("/ray", (string)ob["transport"]["path"]);
            Assert.Equal("1.2.3.4", (string)ob["server"]);
            // sing-box 的 tls 是 enabled/insecure，不是 allowInsecure
            Assert.True((bool)ob["tls"]["enabled"]);
            Assert.False((bool)ob["tls"]["insecure"]);
        }

        [Fact]
        public void 三个内核入站端口一致()
        {
            foreach (CoreKind k in Enum.GetValues(typeof(CoreKind)))
            {
                var json = CoreConfigFactory.BuildJson(k, H2Node(), ProxyMode.Rule);
                var root = JObject.Parse(json);
                var ins = (JArray)root["inbounds"];
                var ports = new System.Collections.Generic.HashSet<int>();
                foreach (var ib in ins)
                {
                    // sing-box 用 listen_port，v2ray 系用 port
                    var p = ib["port"] ?? ib["listen_port"];
                    Assert.NotNull(p);
                    ports.Add((int)p);
                }
                Assert.Contains(CoreConstants.SocksPort, ports);
                Assert.Contains(CoreConstants.HttpPort, ports);
            }
        }

        [Fact]
        public void V2Ray不支持REALITY时降级为普通TLS()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.V2ray, RealityVless(), ProxyMode.Global);
            var ss = (JObject)JObject.Parse(json)["outbounds"][0]["streamSettings"];
            Assert.Equal("tls", (string)ss["security"]);
            Assert.Null(ss["realitySettings"]);
            // XTLS flow 也不能写进 v2ray 配置
            Assert.Null(JObject.Parse(json)["outbounds"][0]["settings"]["vnext"][0]["users"][0]["flow"]);
        }

        [Fact]
        public void singbox缺少geo数据时不生成rule_set()
        {
            var noGeo = CoreConfigFactory.BuildJson(CoreKind.Singbox, H2Node(), ProxyMode.Rule);
            Assert.Null(JObject.Parse(noGeo)["route"]["rule_set"]);

            var dir = Path.Combine(Path.GetTempPath(), "w7p-sbgeo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, CoreConstants.GeoIpCnSrs), "x");
                File.WriteAllText(Path.Combine(dir, CoreConstants.GeoSiteCnSrs), "x");
                Assert.False(CoreConfigFactory.HasSingboxGeo(dir));
                File.WriteAllText(Path.Combine(dir, CoreConstants.GeoSiteAdsSrs), "x");

                Assert.True(CoreConfigFactory.HasSingboxGeo(dir));

                var withGeo = CoreConfigFactory.BuildJson(CoreKind.Singbox, H2Node(), ProxyMode.Rule, dir);
                var route = JObject.Parse(withGeo)["route"];
                Assert.NotNull(route["rule_set"]);
                Assert.NotEmpty((JArray)route["rule_set"]);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void V2Ray系规则模式需要完整geo数据()
        {
            var dir = Path.Combine(Path.GetTempPath(), "w7p-geo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.False(CoreConfigFactory.HasV2rayGeo(dir));
                Assert.Throws<ProxyCoreException>(() =>
                    CoreConfigFactory.BuildJson(CoreKind.Xray, H2Node(), ProxyMode.Rule, dir));

                File.WriteAllText(Path.Combine(dir, CoreConstants.GeoIpFile), "x");
                Assert.False(CoreConfigFactory.HasV2rayGeo(dir));
                File.WriteAllText(Path.Combine(dir, CoreConstants.GeoSiteFile), "x");
                Assert.True(CoreConfigFactory.HasV2rayGeo(dir));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void 未知传输在所有内核上都被拦下()
        {
            var n = H2Node();
            n.Network = "quic";
            // quic：Xray / sing-box 支持，V2Ray 已在 v5 移除
            Assert.Null(CoreRegistry.Xray.ReasonUnsupported(n));
            Assert.NotNull(CoreRegistry.V2ray.ReasonUnsupported(n));
            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(n));
        }

        [Fact]
        public void SS2022不能使用V2Ray内核()
        {
            var n = new Node
            {
                Type = NodeType.Shadowsocks,
                Address = "1.2.3.4",
                Port = 443,
                EncryptMethod = "2022-blake3-aes-128-gcm",
                Password = "MDEyMzQ1Njc4OWFiY2RlZg=="
            };

            Assert.Contains("SS2022", CoreRegistry.V2ray.ReasonUnsupported(n));
            Assert.Null(CoreRegistry.Xray.ReasonUnsupported(n));
            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(n));
        }

        [Fact]
        public void 启动前拒绝缺少关键字段的节点()
        {
            var n = H2Node();
            n.Address = "";
            Assert.Contains("地址", CoreRegistry.V2ray.ReasonUnsupported(n));

            n = H2Node();
            n.Port = 70000;
            Assert.Contains("端口", CoreRegistry.V2ray.ReasonUnsupported(n));

            n = H2Node();
            n.UUID = "";
            Assert.Contains("UUID", CoreRegistry.V2ray.ReasonUnsupported(n));

            n = RealityVless();
            n.Flow = "";
            n.PublicKey = "";
            Assert.Contains("公钥", CoreRegistry.Singbox.ReasonUnsupported(n));
        }

        // ---------- Hysteria2 ----------

        private static Node Hysteria2Node()
        {
            return new Node
            {
                Type = NodeType.Hysteria2,
                Remarks = "hy2",
                Address = "1.2.3.4",
                Port = 443,
                Password = "pw",
                SNI = "example.com",
                AllowInsecure = true,
                Obfs = "salamander",
                ObfsPassword = "op",
                UpMbps = 100,
                DownMbps = 200,
                Ports = "20000-30000",
                PinnedCertSha256 = "e3:b0:c4:42:98:fc:1c:14:9a:fb:f4:c8:99:6f:b9:24:27:ae:41:e4:64:9b:93:4c:a4:95:99:1b:78:52:b8:55"
            };
        }

        [Fact]
        public void 能力表_Hysteria2由Xray与singbox支持()
        {
            var n = Hysteria2Node();
            Assert.Null(CoreRegistry.Xray.ReasonUnsupported(n));
            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(n));
            Assert.Contains("Hysteria2", CoreRegistry.V2ray.ReasonUnsupported(n));

            // 缺密码也要拦下
            n.Password = "";
            Assert.Contains("密码", CoreRegistry.Singbox.ReasonUnsupported(n));
        }

        [Fact]
        public void singbox配置_Hysteria2出站带obfs与h3与端口跳跃()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.Singbox, Hysteria2Node(), ProxyMode.Global);
            var ob = (JObject)JObject.Parse(json)["outbounds"][0];

            Assert.Equal("hysteria2", (string)ob["type"]);
            Assert.Equal("1.2.3.4", (string)ob["server"]);
            Assert.Equal(443, (int)ob["server_port"]);
            Assert.Equal("pw", (string)ob["password"]);
            Assert.Equal("salamander", (string)ob["obfs"]["type"]);
            Assert.Equal("op", (string)ob["obfs"]["password"]);
            Assert.Equal(100, (int)ob["up_mbps"]);
            Assert.Equal(200, (int)ob["down_mbps"]);
            Assert.Equal("20000:30000", (string)ob["server_ports"][0]);

            // Hysteria2 基于 QUIC：TLS 恒开启、ALPN 默认 h3（不能是 v2ray 系的 h2）
            Assert.True((bool)ob["tls"]["enabled"]);
            Assert.True((bool)ob["tls"]["insecure"]);
            Assert.Equal("example.com", (string)ob["tls"]["server_name"]);
            Assert.Equal("h3", (string)ob["tls"]["alpn"][0]);
            // sing-box 只能 pin 公钥，识别不了 hysteria2 的证书指纹 pin，故不输出该字段
            Assert.Null(ob["tls"]["certificate_public_key_sha256"]);
            Assert.Null(ob["transport"]);
        }

        [Fact]
        public void V2Ray配置_遇到Hysteria2节点明确报错()
        {
            Assert.Throws<ProxyCoreException>(() =>
                CoreConfigFactory.BuildJson(CoreKind.V2ray, Hysteria2Node(), ProxyMode.Global));
        }

        [Fact]
        public void Xray配置_Hysteria2出站带传输层认证与finalmask()
        {
            var json = CoreConfigFactory.BuildJson(CoreKind.Xray, Hysteria2Node(), ProxyMode.Global);
            var ob = (JObject)JObject.Parse(json)["outbounds"][0];
            var ss = (JObject)ob["streamSettings"];

            Assert.Equal("hysteria", (string)ob["protocol"]);
            Assert.Equal(2, (int)ob["settings"]["version"]);
            Assert.Equal("1.2.3.4", (string)ob["settings"]["address"]);
            Assert.Equal(443, (int)ob["settings"]["port"]);

            Assert.Equal("hysteria", (string)ss["network"]);
            Assert.Equal("tls", (string)ss["security"]);
            Assert.Equal("pw", (string)ss["hysteriaSettings"]["auth"]);
            Assert.Equal("example.com", (string)ss["tlsSettings"]["serverName"]);
            // Xray 已移除 allowInsecure，改用证书 pin（归一化为无分隔小写十六进制）
            Assert.Null(ss["tlsSettings"]["allowInsecure"]);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                (string)ss["tlsSettings"]["pinnedPeerCertSha256"]);
            Assert.Equal("h3", (string)ss["tlsSettings"]["alpn"][0]);

            Assert.Equal("salamander", (string)ss["finalmask"]["udp"][0]["type"]);
            Assert.Equal("op", (string)ss["finalmask"]["udp"][0]["settings"]["password"]);
            Assert.Equal("20000-30000", (string)ss["finalmask"]["quicParams"]["udpHop"]["ports"]);
            // interval 缺失会让 Xray 26.x 在 hopLoop 里 int64 溢出 panic，必须随端口跳跃一起输出
            Assert.Equal(30, (int)ss["finalmask"]["quicParams"]["udpHop"]["interval"]);
            Assert.Equal("100 mbps", (string)ss["finalmask"]["quicParams"]["brutalUp"]);
        }

        [Fact]
        public void Xray不再输出已移除的allowInsecure()
        {
            var n = new Node
            {
                Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Password = "pw",
                TLS = true, SNI = "example.com", AllowInsecure = true
            };
            var ss = (JObject)JObject.Parse(CoreConfigFactory.BuildJson(CoreKind.Xray, n, ProxyMode.Global))["outbounds"][0]["streamSettings"];
            Assert.Null(ss["tlsSettings"]["allowInsecure"]);
            // V2Ray 未移除该字段，仍照常输出
            var v2 = (JObject)JObject.Parse(CoreConfigFactory.BuildJson(CoreKind.V2ray, n, ProxyMode.Global))["outbounds"][0]["streamSettings"];
            Assert.True((bool)v2["tlsSettings"]["allowInsecure"]);
        }

        [Fact]
        public void 证书pin归一化()
        {
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                NetUtil.NormalizeCertPin("e3:b0:c4:42:98:fc:1c:14:9a:fb:f4:c8:99:6f:b9:24:27:ae:41:e4:64:9b:93:4c:a4:95:99:1b:78:52:b8:55"));
            Assert.Equal("", NetUtil.NormalizeCertPin("deadbeef"));
            Assert.Equal("", NetUtil.NormalizeCertPin("not-hex"));
        }

        [Fact]
        public void 真实测速配置_只保留HTTP入站并改到指定端口()
        {
            var xray = CoreConfigFactory.BuildJson(CoreKind.Xray, H2Node(), ProxyMode.Global);
            var r = JObject.Parse(RealLatencyTester.RetargetHttpInbound(xray, CoreKind.Xray, 12345));
            var ins = (JArray)r["inbounds"];
            Assert.Single(ins);
            Assert.Equal("http", (string)ins[0]["protocol"]);
            Assert.Equal(12345, (int)ins[0]["port"]);

            var sing = CoreConfigFactory.BuildJson(CoreKind.Singbox, H2Node(), ProxyMode.Global);
            var r2 = JObject.Parse(RealLatencyTester.RetargetHttpInbound(sing, CoreKind.Singbox, 23456));
            var ins2 = (JArray)r2["inbounds"];
            Assert.Single(ins2);
            Assert.Equal("http", (string)ins2[0]["type"]);
            Assert.Equal(23456, (int)ins2[0]["listen_port"]);
        }
    }
}
