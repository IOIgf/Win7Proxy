using System;
using System.Collections.Generic;
using Xunit;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore.Tests
{
    public class ParserTests
    {
        private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

        [Fact]
        public void VmessLink()
        {
            var json = "{\"v\":\"2\",\"ps\":\"测试节点\",\"add\":\"1.2.3.4\",\"port\":\"443\",\"id\":\"11111111-1111-1111-1111-111111111111\",\"aid\":\"0\",\"scy\":\"aes-128-gcm\",\"net\":\"ws\",\"path\":\"/ray\",\"host\":\"example.com\",\"tls\":\"tls\"}";
            var node = V2rayNParser.ParseLink("vmess://" + B64(json));
            Assert.Equal(NodeType.Vmess, node.Type);
            Assert.Equal("1.2.3.4", node.Address);
            Assert.Equal(443, node.Port);
            Assert.Equal("11111111-1111-1111-1111-111111111111", node.UUID);
            Assert.Equal("aes-128-gcm", node.Security);
            Assert.Equal("ws", node.Network);
            Assert.Equal("/ray", node.Path);
            Assert.Equal("example.com", node.Host);
            Assert.True(node.TLS);
        }

        [Fact]
        public void VlessLink()
        {
            var node = V2rayNParser.ParseLink("vless://11111111-1111-1111-1111-111111111111@1.2.3.4:443?security=tls&type=ws&path=%2Fray&host=example.com&flow=xtls-rprx-vision&sni=example.com#测试");
            Assert.Equal(NodeType.Vless, node.Type);
            Assert.Equal("1.2.3.4", node.Address);
            Assert.Equal(443, node.Port);
            Assert.Equal("11111111-1111-1111-1111-111111111111", node.UUID);
            Assert.True(node.TLS);
            Assert.Equal("ws", node.Network);
            Assert.Equal("/ray", node.Path);
            Assert.Equal("example.com", node.Host);
            Assert.Equal("xtls-rprx-vision", node.Flow);
            Assert.Equal("example.com", node.SNI);
        }

        [Fact]
        public void TrojanLink()
        {
            var node = V2rayNParser.ParseLink("trojan://pass@1.2.3.4:443?security=tls&type=ws&path=%2Fray&host=example.com#测试");
            Assert.Equal(NodeType.Trojan, node.Type);
            Assert.Equal("1.2.3.4", node.Address);
            Assert.Equal(443, node.Port);
            Assert.Equal("pass", node.Password);
            Assert.True(node.TLS);
            Assert.Equal("/ray", node.Path);
            Assert.Equal("example.com", node.Host);
        }

        [Fact]
        public void SsLink()
        {
            var userinfo = B64("aes-256-gcm:password");
            var node = V2rayNParser.ParseLink("ss://" + userinfo + "@1.2.3.4:8388#测试");
            Assert.Equal(NodeType.Shadowsocks, node.Type);
            Assert.Equal("1.2.3.4", node.Address);
            Assert.Equal(8388, node.Port);
            Assert.Equal("aes-256-gcm", node.EncryptMethod);
            Assert.Equal("password", node.Password);
        }

        [Fact]
        public void V2rayNBase64Subscription()
        {
            var vmessJson = "{\"v\":\"2\",\"ps\":\"n1\",\"add\":\"1.2.3.4\",\"port\":\"443\",\"id\":\"11111111-1111-1111-1111-111111111111\",\"aid\":\"0\",\"scy\":\"aes-128-gcm\",\"net\":\"tcp\",\"tls\":\"\"}";
            var vmessLink = "vmess://" + B64(vmessJson);
            var ssLink = "ss://" + B64("aes-256-gcm:password") + "@1.2.3.4:8388#n2";
            var sub = B64(vmessLink + "\n" + ssLink);
            var parser = new V2rayNParser();
            Assert.True(parser.CanParse(sub));
            var nodes = parser.Parse(sub);
            Assert.Equal(2, nodes.Count);
            Assert.Contains(nodes, n => n.Type == NodeType.Vmess);
            Assert.Contains(nodes, n => n.Type == NodeType.Shadowsocks);
        }

        [Fact]
        public void ClashYaml()
        {
            var yaml = @"port: 7890
proxies:
  - name: ""ss1""
    type: ss
    server: 1.1.1.1
    port: 8388
    cipher: aes-256-gcm
    password: pw1
  - name: ""vmess1""
    type: vmess
    server: 2.2.2.2
    port: 443
    uuid: 11111111-1111-1111-1111-111111111111
    cipher: aes-128-gcm
    network: ws
    tls: true
    servername: example.com
    ws-opts:
      path: /ray
      headers:
        Host: example.com
  - name: ""vless1""
    type: vless
    server: 3.3.3.3
    port: 443
    uuid: 22222222-2222-2222-2222-222222222222
    tls: true
    flow: xtls-rprx-vision
    network: ws
    servername: example.com
  - name: ""trojan1""
    type: trojan
    server: 4.4.4.4
    port: 443
    password: tpw
    sni: example.com
";
            var parser = new ClashParser();
            Assert.True(parser.CanParse(yaml));
            var nodes = parser.Parse(yaml);
            Assert.Equal(4, nodes.Count);
            var ss = nodes.Find(n => n.Type == NodeType.Shadowsocks);
            Assert.Equal("aes-256-gcm", ss.EncryptMethod);
            Assert.Equal("pw1", ss.Password);
            var vm = nodes.Find(n => n.Type == NodeType.Vmess);
            Assert.Equal("11111111-1111-1111-1111-111111111111", vm.UUID);
            Assert.True(vm.TLS);
            var vl = nodes.Find(n => n.Type == NodeType.Vless);
            Assert.Equal("xtls-rprx-vision", vl.Flow);
            var tr = nodes.Find(n => n.Type == NodeType.Trojan);
            Assert.Equal("tpw", tr.Password);
        }

        [Fact]
        public void Sip008Json()
        {
            var json = "{\"version\":1,\"servers\":[{\"id\":\"a\",\"remarks\":\"ss1\",\"server\":\"1.2.3.4\",\"server_port\":8388,\"method\":\"aes-256-gcm\",\"password\":\"pw\"}]}";
            var parser = new Sip008Parser();
            Assert.True(parser.CanParse(json));
            var nodes = parser.Parse(json);
            Assert.Single(nodes);
            Assert.Equal(NodeType.Shadowsocks, nodes[0].Type);
            Assert.Equal("1.2.3.4", nodes[0].Address);
            Assert.Equal(8388, nodes[0].Port);
            Assert.Equal("aes-256-gcm", nodes[0].EncryptMethod);
            Assert.Equal("pw", nodes[0].Password);
        }
    }
}
