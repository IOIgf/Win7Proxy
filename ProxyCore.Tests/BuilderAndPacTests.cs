using Newtonsoft.Json.Linq;
using Xunit;
using ProxyCore;
using ProxyCore.Models;

namespace ProxyCore.Tests
{
    public class BuilderAndPacTests
    {
        private static Node RealityNode()
        {
            return new Node
            {
                Type = NodeType.Vless,
                Remarks = "r",
                Address = "1.2.3.4",
                Port = 443,
                UUID = "11111111-1111-1111-1111-111111111111",
                TLS = true,
                Extra = { ["security"] = "reality" },
                Flow = "xtls-rprx-vision",
                Network = "tcp",
                SNI = "example.com",
                Fingerprint = "chrome",
                PublicKey = "pk",
                ShortId = "sid"
            };
        }

        [Fact]
        public void XrayConfigReality()
        {
            var cfg = XrayConfigBuilder.Build(RealityNode(), ProxyMode.Rule).ToJson();
            Assert.Contains("vless", cfg);
            Assert.Contains("realitySettings", cfg);
            Assert.Contains("\"publicKey\"", cfg);
            Assert.Contains("freedom", cfg);
            Assert.Contains("blackhole", cfg);
            Assert.Contains("geosite:cn", cfg);

            var root = JObject.Parse(cfg);
            var inbounds = (JArray)root["inbounds"];
            Assert.Single(inbounds);
            Assert.Equal("socks", (string)inbounds[0]["protocol"]);
            Assert.Equal(CoreConstants.MixedPort, (int)inbounds[0]["port"]);
            Assert.Equal("127.0.0.1", (string)inbounds[0]["listen"]);
        }

        [Fact]
        public void 入站选项_自定义端口与局域网监听()
        {
            var lan = new InboundOptions { MixedPort = 7890, AllowLan = true };
            var xray = (JObject)JObject.Parse(CoreConfigFactory.BuildJson(CoreKind.Xray, RealityNode(), ProxyMode.Global, null, lan))["inbounds"][0];
            Assert.Equal(7890, (int)xray["port"]);
            Assert.Equal("0.0.0.0", (string)xray["listen"]);

            var sing = (JObject)JObject.Parse(CoreConfigFactory.BuildJson(CoreKind.Singbox, RealityNode(), ProxyMode.Global, null, lan))["inbounds"][0];
            Assert.Equal("mixed", (string)sing["type"]);
            Assert.Equal(7890, (int)sing["listen_port"]);
            Assert.Equal("0.0.0.0", (string)sing["listen"]);
        }

        [Fact]
        public void 订阅名_从URL推断主机名()
        {
            Assert.Equal("sub.example.com",
                SubscriptionFetcher.NameFromUrl("https://sub.example.com/api/v1/client/subscribe?token=abc"));
            Assert.Equal("", SubscriptionFetcher.NameFromUrl("not a url"));
            Assert.Equal("", SubscriptionFetcher.NameFromUrl(""));
        }

        [Fact]
        public void PacGlobal()
        {
            var pac = PacGenerator.Build(ProxyMode.Global, null);
            Assert.Contains("PROXY 127.0.0.1:" + CoreConstants.MixedPort, pac);
            Assert.Contains("FindProxyForURL", pac);
        }

        [Fact]
        public void PacDirect()
        {
            var pac = PacGenerator.Build(ProxyMode.Direct, null);
            Assert.Contains("DIRECT", pac);
        }

        [Fact]
        public void PacRule()
        {
            var pac = PacGenerator.Build(ProxyMode.Rule, null);
            Assert.Contains("DIRECT", pac);
            Assert.Contains("PROXY 127.0.0.1:" + CoreConstants.MixedPort, pac);
            Assert.Contains("FindProxyForURL", pac);
        }
    }
}
