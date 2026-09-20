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
    /// 针对本轮 bug 修复的回归测试：
    /// SIP003 插件翻译、Hysteria2 端口区间分隔符、PAC 参数签名。
    /// </summary>
    public class BugFixTests
    {
        private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

        private static Node SsNode(string plugin, string opts = null, string method = "aes-256-gcm")
        {
            var n = new Node
            {
                Type = NodeType.Shadowsocks,
                Address = "1.2.3.4",
                Port = 8388,
                EncryptMethod = method,
                Password = "pw"
            };
            if (plugin != null) n.Extra["plugin"] = plugin;
            if (opts != null) n.Extra["plugin_opts"] = opts;
            return n;
        }

        // ---------- SIP003 插件解析 ----------

        [Fact]
        public void SsPlugin_v2rayN的整串plugin_能拆出模式主机路径与TLS()
        {
            var plugin = SsPluginParser.Parse(new Dictionary<string, string>
            {
                ["plugin"] = "v2ray-plugin;mode=websocket;host=example.com;path=/ws;tls"
            });

            Assert.NotNull(plugin);
            Assert.True(plugin.IsV2rayPlugin);
            Assert.True(plugin.IsWebsocketV2rayPlugin);
            Assert.Equal("websocket", plugin.Mode);
            Assert.Equal("example.com", plugin.Host);
            Assert.Equal("/ws", plugin.Path);
            Assert.True(plugin.Tls);
        }

        [Fact]
        public void SsPlugin_SIP008分开存的plugin与plugin_opts_能合并()
        {
            var plugin = SsPluginParser.Parse(new Dictionary<string, string>
            {
                ["plugin"] = "obfs-local",
                ["plugin_opts"] = "obfs=http;obfs-host=www.bing.com"
            });

            Assert.NotNull(plugin);
            Assert.True(plugin.IsObfs);
            Assert.Equal("http", plugin.Mode);
            Assert.Equal("www.bing.com", plugin.Host);
            Assert.Equal("obfs=http;obfs-host=www.bing.com", plugin.Options);
        }

        [Fact]
        public void SsPlugin_没有插件返回null()
        {
            Assert.Null(SsPluginParser.Parse(new Dictionary<string, string>()));
            Assert.Null(SsPluginParser.Parse(null));
            // 只有 plugin_opts、没有插件名时不能把它当插件解析
            Assert.Null(SsPluginParser.Parse(new Dictionary<string, string> { ["plugin_opts"] = "obfs=http" }));
        }

        [Fact]
        public void 无插件SS_两个内核都正常生成且不带plugin字段()
        {
            var node = SsNode(null);
            var xray = (JObject)JObject.Parse(XrayConfigBuilder.BuildJson(node, ProxyMode.Global))["outbounds"][0];
            Assert.Equal("shadowsocks", (string)xray["protocol"]);
            Assert.Equal("tcp", (string)xray["streamSettings"]["network"]);

            var sing = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Singbox, node, ProxyMode.Global))["outbounds"][0];
            Assert.Equal("shadowsocks", (string)sing["type"]);
            Assert.Null(sing["plugin"]);
        }

        [Fact]
        public void Ss订阅链接_plugin参数能解析出来()
        {
            var link = "ss://" + B64("aes-256-gcm:pw") + "@1.2.3.4:8388" +
                       "?plugin=v2ray-plugin%3Bmode%3Dwebsocket%3Bhost%3Dexample.com%3Bpath%3D%2Fws#n";
            var n = V2rayNParser.ParseLink(link);

            Assert.NotNull(n);
            var plugin = SsPluginParser.Parse(n.Extra);
            Assert.NotNull(plugin);
            Assert.True(plugin.IsWebsocketV2rayPlugin);
            Assert.Equal("example.com", plugin.Host);
            Assert.Equal("/ws", plugin.Path);
        }

        // ---------- 能力检查 ----------

        [Fact]
        public void 插件能力_obfs只有singbox支持()
        {
            var obfs = SsNode("obfs-local", "obfs=http;obfs-host=www.bing.com");

            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(obfs));
            var reason = CoreRegistry.Xray.ReasonUnsupported(obfs);
            Assert.NotNull(reason);
            Assert.Contains("obfs", reason);
            Assert.Contains("sing-box", reason);
            Assert.NotNull(CoreRegistry.V2ray.ReasonUnsupported(obfs));
        }

        [Fact]
        public void 插件能力_v2rayPlugin非websocket模式只有singbox支持()
        {
            var quic = SsNode("v2ray-plugin", "mode=quic;host=example.com");

            Assert.Null(CoreRegistry.Singbox.ReasonUnsupported(quic));
            var reason = CoreRegistry.Xray.ReasonUnsupported(quic);
            Assert.NotNull(reason);
            Assert.Contains("websocket", reason);
        }

        [Fact]
        public void 插件能力_未知插件被拒绝()
        {
            var weird = SsNode("shadow-tls", "host=example.com");
            Assert.NotNull(CoreRegistry.Xray.ReasonUnsupported(weird));
            Assert.NotNull(CoreRegistry.Singbox.ReasonUnsupported(weird));
        }

        // ---------- 配置生成 ----------

        [Fact]
        public void Xray配置_v2rayPlugin映射为WebSocket与TLS()
        {
            var node = SsNode("v2ray-plugin", "mode=websocket;host=example.com;path=/ws;tls");
            var ob = (JObject)JObject.Parse(XrayConfigBuilder.BuildJson(node, ProxyMode.Global))["outbounds"][0];

            Assert.Equal("shadowsocks", (string)ob["protocol"]);
            var stream = (JObject)ob["streamSettings"];
            Assert.Equal("ws", (string)stream["network"]);
            Assert.Equal("/ws", (string)stream["wsSettings"]["path"]);
            Assert.Equal("example.com", (string)stream["wsSettings"]["headers"]["Host"]);
            Assert.Equal("tls", (string)stream["security"]);
            Assert.Equal("example.com", (string)stream["tlsSettings"]["serverName"]);
        }

        [Fact]
        public void Xray配置_obfs插件直接报错而不是生成连不上的配置()
        {
            var node = SsNode("obfs-local", "obfs=http;obfs-host=www.bing.com");
            Assert.Throws<ProxyCoreException>(() => XrayConfigBuilder.BuildJson(node, ProxyMode.Global));
        }

        [Fact]
        public void singbox配置_透传plugin与pluginOpts()
        {
            var node = SsNode("obfs-local", "obfs=http;obfs-host=www.bing.com");
            var ob = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Singbox, node, ProxyMode.Global))["outbounds"][0];

            Assert.Equal("shadowsocks", (string)ob["type"]);
            Assert.Equal("obfs-local", (string)ob["plugin"]);
            Assert.Equal("obfs=http;obfs-host=www.bing.com", (string)ob["plugin_opts"]);
        }

        [Fact]
        public void singbox配置_无插件时不写plugin字段()
        {
            var node = SsNode(null);
            var ob = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Singbox, node, ProxyMode.Global))["outbounds"][0];

            Assert.Null(ob["plugin"]);
            Assert.Null(ob["plugin_opts"]);
        }

        // ---------- Hysteria2 端口区间分隔符 ----------

        [Fact]
        public void 端口区间_冒号写法会归一化成Xray的连字符()
        {
            Assert.Equal(new[] { "20000-30000", "443" }, NetUtil.SplitPortsRaw("20000:30000,443"));
            Assert.Equal(new[] { "20000-30000" }, NetUtil.SplitPortsRaw("20000-30000"));
            // sing-box 需要冒号写法，转回来
            Assert.Equal(new[] { "20000:30000", "443" }, NetUtil.SplitPorts("20000-30000,443"));
        }

        [Fact]
        public void Hysteria2配置_Xray用连字符而singbox用冒号()
        {
            var node = new Node
            {
                Type = NodeType.Hysteria2,
                Address = "1.2.3.4",
                Port = 443,
                Password = "pw",
                Ports = "20000:30000"
            };

            var xray = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Xray, node, ProxyMode.Global))["outbounds"][0];
            Assert.Equal("20000-30000", (string)xray["streamSettings"]["finalmask"]["quicParams"]["udpHop"]["ports"]);

            var sing = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Singbox, node, ProxyMode.Global))["outbounds"][0];
            Assert.Equal("20000:30000", (string)sing["server_ports"][0]);
        }
    }
}
