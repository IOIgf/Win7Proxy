using System.Collections.Generic;
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
            var ports = new HashSet<int>();
            foreach (var ib in inbounds) ports.Add((int)ib["port"]);
            Assert.Contains(CoreConstants.SocksPort, ports);
            Assert.Contains(CoreConstants.HttpPort, ports);
        }

        [Fact]
        public void PacGlobal()
        {
            var pac = PacGenerator.Build(ProxyMode.Global, CoreConstants.PacPort, null);
            Assert.Contains("PROXY 127.0.0.1:" + CoreConstants.HttpPort, pac);
            Assert.Contains("FindProxyForURL", pac);
        }

        [Fact]
        public void PacDirect()
        {
            var pac = PacGenerator.Build(ProxyMode.Direct, CoreConstants.PacPort, null);
            Assert.Contains("DIRECT", pac);
        }

        [Fact]
        public void PacRule()
        {
            var pac = PacGenerator.Build(ProxyMode.Rule, CoreConstants.PacPort, null);
            Assert.Contains("DIRECT", pac);
            Assert.Contains("PROXY 127.0.0.1:" + CoreConstants.HttpPort, pac);
            Assert.Contains("FindProxyForURL", pac);
        }
    }
}
