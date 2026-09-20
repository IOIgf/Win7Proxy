using Newtonsoft.Json.Linq;
using Xunit;
using ProxyCore;
using ProxyCore.Models;

namespace ProxyCore.Tests
{
    /// <summary>
    /// 内核日志必须能被程序日志框看到（捕获 stdout/stderr）：
    /// 一旦把 error 写进文件、或级别设成 warning，真实失败原因（x509、QUIC/ALPN 等）
    /// 就只在文件里或根本不打印，用户拿着"连不上"无从查起。
    /// </summary>
    public class LogConfigTests
    {
        private static Node Trojan() => new Node
        {
            Type = NodeType.Trojan,
            Address = "1.2.3.4",
            Port = 443,
            Password = "pw",
            TLS = true,
            Extra = { ["security"] = "tls" }
        };

        [Fact]
        public void Xray配置_日志不进文件且级别能显示失败原因()
        {
            var log = (JObject)JObject.Parse(XrayConfigBuilder.BuildJson(Trojan(), ProxyMode.Global))["log"];

            // 不能把错误重定向到文件，否则日志框什么都看不到
            Assert.Null(log["error"]);
            Assert.Null(log["access"]);
            // x509 / QUIC 失败原因是 info 级，warning 会把它吞掉
            Assert.Equal("info", (string)log["loglevel"]);
        }

        [Fact]
        public void V2Ray配置_日志同样不进文件()
        {
            var log = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.V2ray, Trojan(), ProxyMode.Global))["log"];
            Assert.Null(log["error"]);
            Assert.Null(log["access"]);
        }

        [Fact]
        public void singbox配置_日志不写output文件()
        {
            var log = (JObject)JObject.Parse(
                CoreConfigFactory.BuildJson(CoreKind.Singbox, Trojan(), ProxyMode.Global))["log"];
            Assert.Null(log["output"]);
            Assert.Equal("warn", (string)log["level"]);
        }
    }
}
