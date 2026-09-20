using System;
using Newtonsoft.Json.Linq;
using Xunit;
using ProxyCore;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore.Tests
{
    /// <summary>
    /// TLS 相关的回归测试：这些缺陷会让 trojan / hysteria2 节点在 Xray 26.x 上直接连不上
    /// （表现为 TLS 校验失败、连接被内核切断）。
    /// </summary>
    public class TlsRegressionTests
    {
        private static JObject StreamOf(string json)
        {
            var ob = (JObject)JObject.Parse(json)["outbounds"][0];
            return (JObject)ob["streamSettings"];
        }

        // ---------- security / tls 标志归一化 ----------

        [Fact]
        public void 安全标志_数字与合作写法都归一化()
        {
            Assert.Equal("tls", NetUtil.NormalizeSecurity("1"));
            Assert.Equal("tls", NetUtil.NormalizeSecurity("xtls"));
            Assert.Equal("tls", NetUtil.NormalizeSecurity("true"));
            Assert.Equal("none", NetUtil.NormalizeSecurity("0"));
            Assert.Equal("none", NetUtil.NormalizeSecurity("false"));
            Assert.Equal("reality", NetUtil.NormalizeSecurity("reality"));
        }

        [Fact]
        public void trojan用tls等于1时_不能退化成明文security_none()
        {
            // 旧行为：NormalizeSecurity("1") 原样返回 "1"，生成器落到 else 分支写出 security:none,
            // 于是以明文去连 TLS 端口，连接立即被对端切断。
            var node = V2rayNParser.ParseLink("trojan://pw@t.example.com:443?tls=1&sni=t.example.com#t");
            Assert.NotNull(node);
            Assert.Equal("tls", NetUtil.NormalizeSecurity(node.Extra["security"]));

            var stream = StreamOf(XrayConfigBuilder.BuildJson(node, ProxyMode.Global));
            Assert.Equal("tls", (string)stream["security"]);
            Assert.NotNull(stream["tlsSettings"]);
        }

        [Fact]
        public void vless用security等于1时_也应走TLS()
        {
            var node = V2rayNParser.ParseLink(
                "vless://11111111-1111-1111-1111-111111111111@v.example.com:443?security=1&sni=v.example.com#v");
            var stream = StreamOf(XrayConfigBuilder.BuildJson(node, ProxyMode.Global));
            Assert.Equal("tls", (string)stream["security"]);
        }

        // ---------- 证书 pin 格式 ----------

        [Fact]
        public void 证书pin_十六进制带分隔符仍可用()
        {
            var hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            Assert.Equal(hex, NetUtil.NormalizeCertPin(hex));
            var separated = "E3:B0:C4:42:98:FC:1C:14:9A:FB:F4:C8:99:6F:B9:24:27:AE:41:E4:64:9B:93:4C:A4:95:99:1B:78:52:B8:55";
            Assert.Equal(hex, NetUtil.NormalizeCertPin(separated));
        }

        [Fact]
        public void 证书pin_base64要转成Xray要求的十六进制()
        {
            // hysteria2 分享链接里的 pinSHA256 基本是 base64；Xray 的 pinnedPeerCertSha256 只认十六进制，
            // 旧实现遇到 base64 直接返回空串，pin 被静默丢弃，随后走严格校验必然失败。
            const string b64 = "HiUzJOgqG+ABAIc4bE4bewEuoY81qOlHLuNIySgmPTQ=";
            Assert.Equal("1e253324e82a1be0010087386c4e1b7b012ea18f35a8e9472ee348c928263d34",
                NetUtil.NormalizeCertPin(b64));

            // URL-safe 无填充
            var urlSafe = b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Assert.Equal("1e253324e82a1be0010087386c4e1b7b012ea18f35a8e9472ee348c928263d34",
                NetUtil.NormalizeCertPin(urlSafe));
        }

        [Fact]
        public void 证书pin_非法值返回空串()
        {
            Assert.Equal("", NetUtil.NormalizeCertPin(""));
            Assert.Equal("", NetUtil.NormalizeCertPin("deadbeef"));
            Assert.Equal("", NetUtil.NormalizeCertPin("not-a-pin"));
        }

        // ---------- trojan / vless / clash 的 pin 解析 ----------

        [Fact]
        public void trojan链接的pinSHA256_应被解析并写入配置()
        {
            var hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            var node = V2rayNParser.ParseLink(
                "trojan://pw@t.example.com:443?security=tls&pinSHA256=" + hex + "&sni=t.example.com#t");

            Assert.NotNull(node);
            Assert.Equal(hex, node.PinnedCertSha256);

            var tls = (JObject)StreamOf(XrayConfigBuilder.BuildJson(node, ProxyMode.Global))["tlsSettings"];
            Assert.Equal(hex, (string)tls["pinnedPeerCertSha256"]);
            // Xray 26.x 已移除 allowInsecure，配置里绝不能出现
            Assert.Null(tls["allowInsecure"]);
        }

        [Fact]
        public void hysteria2的base64_pin_应转成十六进制写进配置()
        {
            var b64 = "HiUzJOgqG+ABAIc4bE4bewEuoY81qOlHLuNIySgmPTQ=";
            var node = V2rayNParser.ParseLink(
                "hysteria2://pw@h.example.com:443?sni=h.example.com&pinSHA256=" + Uri.EscapeDataString(b64) + "#h");

            var tls = (JObject)StreamOf(XrayConfigBuilder.BuildJson(node, ProxyMode.Global))["tlsSettings"];
            Assert.Equal("1e253324e82a1be0010087386c4e1b7b012ea18f35a8e9472ee348c928263d34",
                (string)tls["pinnedPeerCertSha256"]);
        }

        [Fact]
        public void Clash的trojan带pinSHA256_也要解析()
        {
            var yaml = @"
proxies:
  - name: ""t1""
    type: trojan
    server: 6.6.6.6
    port: 443
    password: pw
    sni: example.com
    skip-cert-verify: true
    pinSHA256: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
";
            var node = Assert.Single(new ClashParser().Parse(yaml));
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", node.PinnedCertSha256);

            var tls = (JObject)StreamOf(XrayConfigBuilder.BuildJson(node, ProxyMode.Global))["tlsSettings"];
            Assert.NotNull(tls["pinnedPeerCertSha256"]);
        }
    }
}
