using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Xunit;
using ProxyCore;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore.Tests
{
    /// <summary>
    /// 针对本次修复的回归测试：每一条都对应一个曾经真实出错的场景。
    /// </summary>
    public class RegressionTests
    {
        private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

        // ---------- 解析层 ----------

        [Fact]
        public void Vmess_布尔型tls_不应整条节点丢失()
        {
            // 旧实现用 (string)o["tls"]，遇到 JSON 布尔 true 会抛 InvalidCastException，
            // 被上层 catch 吞掉后节点静默消失。
            var json = "{\"v\":\"2\",\"ps\":\"bool-tls\",\"add\":\"1.2.3.4\",\"port\":\"443\"," +
                       "\"id\":\"11111111-1111-1111-1111-111111111111\",\"scy\":\"auto\",\"net\":\"tcp\",\"tls\":true}";
            var node = V2rayNParser.ParseLink("vmess://" + B64(json));

            Assert.NotNull(node);
            Assert.Equal("bool-tls", node.Remarks);
            Assert.True(node.TLS);
            Assert.Equal("tls", node.Extra["security"]);
        }

        [Fact]
        public void Vmess_数字型port与布尔型tls_同样可用()
        {
            var json = "{\"ps\":\"n\",\"add\":\"a.b.c\",\"port\":8388,\"id\":\"uuid-1\",\"tls\":false}";
            var node = V2rayNParser.ParseLink("vmess://" + B64(json));
            Assert.NotNull(node);
            Assert.Equal(8388, node.Port);
            Assert.False(node.TLS);
        }

        [Fact]
        public void Websocket_应归一化为_ws()
        {
            var link = "vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls&type=websocket&path=%2Fray#n";
            var node = V2rayNParser.ParseLink(link);
            Assert.Equal("ws", node.Network);

            var cfg = XrayConfigBuilder.Build(node, ProxyMode.Global).ToJson();
            Assert.Contains("\"network\": \"ws\"", cfg);
            Assert.Contains("wsSettings", cfg);
        }

        [Fact]
        public void Trojan_密码放在Password_而不是留在UUID()
        {
            var node = V2rayNParser.ParseLink("trojan://mypassword@1.2.3.4:443?security=tls&sni=example.com#n");
            Assert.Equal("mypassword", node.Password);
            Assert.Equal("", node.UUID);
            Assert.Equal("example.com", node.SNI);
        }

        [Fact]
        public void Ss_无填充的URLSafeBase64_仍可解析()
        {
            // 去掉 '=' 填充、并把 +/ 换成 -_ 的 SIP002 写法
            var raw = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gcm:pa55word"))
                        .Replace("=", "").Replace('+', '-').Replace('/', '_');
            var node = V2rayNParser.ParseLink("ss://" + raw + "@1.2.3.4:8388#n");
            Assert.Equal("aes-256-gcm", node.EncryptMethod);
            Assert.Equal("pa55word", node.Password);
            Assert.Equal(8388, node.Port);
        }

        [Fact]
        public void Clash_Reality节点_解析出公钥与shortId()
        {
            var yaml = @"
port: 7890
proxies:
  - name: ""reality1""
    type: vless
    server: 5.5.5.5
    port: 443
    uuid: 33333333-3333-3333-3333-333333333333
    network: tcp
    tls: true
    servername: www.microsoft.com
    client-fingerprint: chrome
    flow: xtls-rprx-vision
    reality-opts:
      public-key: abc-public-key
      short-id: 0123abcd
";
            var parser = new ClashParser();
            Assert.True(parser.CanParse(yaml));
            var nodes = parser.Parse(yaml);
            var n = Assert.Single(nodes);
            Assert.Equal("abc-public-key", n.PublicKey);
            Assert.Equal("0123abcd", n.ShortId);
            Assert.Equal("reality", n.Extra["security"]);

            var cfg = XrayConfigBuilder.Build(n, ProxyMode.Rule).ToJson();
            Assert.Contains("realitySettings", cfg);
            Assert.Contains("abc-public-key", cfg);
        }

        [Fact]
        public void Clash_Grpc与H2与跳过证书校验()
        {
            var yaml = @"
proxies:
  - name: ""g1""
    type: trojan
    server: 6.6.6.6
    port: 443
    password: pw
    sni: example.com
    skip-cert-verify: true
    network: grpc
    grpc-opts:
      grpc-service-name: GunService
  - name: ""h1""
    type: vmess
    server: 7.7.7.7
    port: 443
    uuid: uuid-h
    cipher: auto
    network: h2
    h2-opts:
      path: /h2path
      host:
        - example.com
";
            var nodes = new ClashParser().Parse(yaml);
            Assert.Equal(2, nodes.Count);

            var g = nodes.Find(x => x.Remarks == "g1");
            Assert.Equal("grpc", g.Network);
            Assert.Equal("GunService", g.ServiceName);
            Assert.True(g.AllowInsecure);

            var h = nodes.Find(x => x.Remarks == "h1");
            Assert.Equal("h2", h.Network);
            Assert.Equal("/h2path", h.Path);
            Assert.Equal("example.com", h.Host);
        }

        [Fact]
        public void Clash_只有proxies字段的极简订阅_也能识别()
        {
            // 旧判定要求同时出现 proxy-groups / rules / port，会漏掉这种
            var yaml = @"
proxies:
  - name: ""only""
    type: ss
    server: 8.8.8.8
    port: 8388
    cipher: aes-256-gcm
    password: pw
";
            Assert.True(new ClashParser().CanParse(yaml));
            Assert.Single(new ClashParser().Parse(yaml));
        }

        // ---------- 归一化 / 去重 ----------

        [Fact]
        public void NetUtil_网络与加密名归一化()
        {
            Assert.Equal("ws", NetUtil.NormalizeNetwork("websocket"));
            Assert.Equal("h2", NetUtil.NormalizeNetwork("http"));
            Assert.Equal("grpc", NetUtil.NormalizeNetwork("gun"));
            Assert.Equal("tcp", NetUtil.NormalizeNetwork(""));
            Assert.Equal("reality", NetUtil.NormalizeSecurity("REALITY"));
            Assert.Equal("none", NetUtil.NormalizeSecurity(null));
        }

        [Fact]
        public void NetUtil_备注不同但配置相同_指纹应一致()
        {
            var a = new Node { Type = NodeType.Vless, Address = "1.2.3.4", Port = 443, UUID = "u", Network = "ws", Path = "/p" };
            var b = new Node { Type = NodeType.Vless, Address = "1.2.3.4", Port = 443, UUID = "u", Network = "websocket", Path = "/p" };
            b.Remarks = "换个名字";

            Assert.Equal(NetUtil.Fingerprint(a), NetUtil.Fingerprint(b));

            var c = new Node { Type = NodeType.Vless, Address = "1.2.3.4", Port = 443, UUID = "u", Network = "ws", Path = "/other" };
            Assert.NotEqual(NetUtil.Fingerprint(a), NetUtil.Fingerprint(c));
        }

        // ---------- 生成的 xray 配置 ----------

        [Fact]
        public void 配置_入站开启嗅探_便于域名规则命中()
        {
            var node = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Password = "pw", TLS = true };
            node.Extra["security"] = "tls";
            var root = JObject.Parse(XrayConfigBuilder.Build(node, ProxyMode.Rule).ToJson());

            foreach (var ib in (JArray)root["inbounds"])
            {
                Assert.True((bool)ib["sniffing"]["enabled"]);
                Assert.True((bool)ib["sniffing"]["routeOnly"]);
            }
        }

        [Fact]
        public void 配置_规则模式带国内DNS_全局模式不带()
        {
            var node = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Password = "pw", TLS = true };
            node.Extra["security"] = "tls";

            var rule = JObject.Parse(XrayConfigBuilder.Build(node, ProxyMode.Rule).ToJson());
            Assert.Contains("geosite:cn", rule["dns"].ToString());

            var global = JObject.Parse(XrayConfigBuilder.Build(node, ProxyMode.Global).ToJson());
            Assert.DoesNotContain("geosite:cn", global["dns"].ToString());
        }

        [Fact]
        public void 配置_未指定alpn时使用默认值()
        {
            var node = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Password = "pw", TLS = true };
            node.Extra["security"] = "tls";
            var root = JObject.Parse(XrayConfigBuilder.Build(node, ProxyMode.Global).ToJson());
            var ob = (JObject)((JArray)root["outbounds"])[0];
            var alpn = (JArray)ob["streamSettings"]["tlsSettings"]["alpn"];
            Assert.Contains("h2", alpn.ToObject<List<string>>());
        }

        [Fact]
        public void 订阅解析_会标记所属订阅Id()
        {
            var vmessJson = "{\"ps\":\"n1\",\"add\":\"1.2.3.4\",\"port\":\"443\",\"id\":\"u1\",\"tls\":false}";
            var sub = new Subscription { Id = "sub-1", Name = "测试订阅" };
            var nodes = SubscriptionParserFactory.Parse(sub,
                "vmess://" + B64(vmessJson));

            var n = Assert.Single(nodes);
            Assert.Equal("sub-1", n.SubscriptionId);
            Assert.Equal("测试订阅", n.SourceName);
        }

        [Fact]
        public void 订阅Fetcher_拒绝非http地址()
        {
            Assert.Throws<ProxyCoreException>(() => SubscriptionFetcher.FetchRaw("ftp://example.com/sub"));
            Assert.Throws<ProxyCoreException>(() => SubscriptionFetcher.FetchRaw(""));
        }

        [Fact]
        public void 兼容性提示_能识别已被内核移除的h2传输()
        {
            // 新版 xray 遇到 h2 传输会直接拒绝启动，必须提前告诉用户
            var h2 = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Network = "h2" };
            Assert.Contains(NodeCompat.Warnings(h2), w => w.Contains("h2"));

            var ws = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 443, Network = "ws" };
            Assert.Empty(NodeCompat.Warnings(ws));

            var badPort = new Node { Type = NodeType.Trojan, Address = "1.2.3.4", Port = 0 };
            Assert.NotEmpty(NodeCompat.Warnings(badPort));
        }
    }
}
