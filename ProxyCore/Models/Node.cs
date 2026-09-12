using System;
using System.Collections.Generic;

namespace ProxyCore.Models
{
    /// <summary>支持的节点协议类型。</summary>
    public enum NodeType
    {
        Vmess,
        Vless,
        Trojan,
        Shadowsocks,
        Socks,
        Http
    }

    /// <summary>代理分流模式。</summary>
    public enum ProxyMode
    {
        /// <summary>全部流量走代理。</summary>
        Global,
        /// <summary>按规则(PAC / geoip)分流：国内直连，国外代理。</summary>
        Rule,
        /// <summary>全部直连，不启动内核。</summary>
        Direct
    }

    /// <summary>
    /// 统一节点模型。三种订阅格式(v2rayN / Clash / SIP008)解析后都归一为 Node，
    /// 再由 XrayConfigBuilder 转换为 xray 的 outbound。
    /// </summary>
    public class Node
    {
        public string Remarks { get; set; } = "";
        public NodeType Type { get; set; } = NodeType.Vmess;

        public string Address { get; set; } = "";
        public int Port { get; set; } = 0;

        // 认证
        public string UUID { get; set; } = "";          // vmess / vless / trojan 的 id
        public string Security { get; set; } = "";       // vmess: aes-128-gcm / none / zero
        public string EncryptMethod { get; set; } = ""; // ss: 加密方法
        public string Password { get; set; } = "";      // ss / trojan 密码

        // TLS / 传输
        public bool TLS { get; set; } = false;
        public string Flow { get; set; } = "";          // vless flow, 例如 xtls-rprx-vision
        public string Network { get; set; } = "tcp";     // tcp / websocket / grpc / h2 / quic
        public string Path { get; set; } = "";          // ws / grpc / h2 的 path
        public string Host { get; set; } = "";          // ws host / http host
        public string SNI { get; set; } = "";           // TLS ServerName
        public string Fingerprint { get; set; } = "";   // uTLS 指纹, 例如 chrome / firefox
        public string PublicKey { get; set; } = "";     // REALITY publicKey
        public string ShortId { get; set; } = "";       // REALITY shortId
        public string ServiceName { get; set; } = "";   // grpc serviceName
        public bool AllowInsecure { get; set; } = false;
        public string Alpn { get; set; } = "";          // TLS ALPN, 逗号分隔如 h2,http/1.1

        // 额外透传字段(key=value)，用于保留非常用参数
        public Dictionary<string, string> Extra { get; set; } = new Dictionary<string, string>();

        // 运行期字段（会被持久化：延迟结果和原始链接需要跨会话保留，用于排序与"复制链接"）
        public int LatencyMs { get; set; } = -1;
        public string SourceName { get; set; } = "";     // 来自哪个订阅/手动
        public string RawLink { get; set; } = "";        // 原始链接（用于复制分享/调试）
        public string SubscriptionId { get; set; } = ""; // 所属订阅 Id（手动添加为空）
    }
}
