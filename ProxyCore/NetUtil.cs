using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 协议名归一化与节点去重用的工具方法。
    /// 订阅里同一个传输层有各种写法（websocket / ws、h2 / http、grpc / gun），
    /// 而 xray 只认固定的几个值，必须在解析阶段统一。
    /// </summary>
    public static class NetUtil
    {
        /// <summary>把订阅里的各种别名映射到 xray 支持的 network 值。</summary>
        public static string NormalizeNetwork(string net)
        {
            if (string.IsNullOrWhiteSpace(net)) return "tcp";
            switch (net.Trim().ToLowerInvariant())
            {
                case "ws":
                case "websocket":
                    return "ws";
                case "h2":
                case "http":
                case "http/2":
                    return "h2";
                case "grpc":
                case "gun":
                    return "grpc";
                case "httpupgrade":
                case "http-upgrade":
                    return "httpupgrade";
                case "quic":
                    return "quic";
                case "tcp":
                case "raw":
                case "none":
                    return "tcp";
                case "kcp":
                case "mkcp":
                    return "kcp";
                default:
                    return net.Trim().ToLowerInvariant();
            }
        }

        /// <summary>把 security 参数归一化：tls / reality / none。</summary>
        public static string NormalizeSecurity(string sec)
        {
            if (string.IsNullOrWhiteSpace(sec)) return "none";
            switch (sec.Trim().ToLowerInvariant())
            {
                case "tls":
                case "true":   // 有些订阅直接写 "tls": true
                    return "tls";
                case "reality":
                    return "reality";
                case "none":
                case "false":
                case "":
                    return "none";
                default:
                    return sec.Trim().ToLowerInvariant();
            }
        }

        /// <summary>把 alpn 字符串（"h2,http/1.1"）拆成数组。</summary>
        public static List<string> SplitAlpn(string alpn)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(alpn)) return list;
            foreach (var part in alpn.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (p != "") list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// 节点的去重指纹：协议 + 地址 + 端口 + 凭据 + 传输关键参数。
        /// 用于导入订阅时剔除重复节点。备注名不参与比较（备注变了不算新节点）。
        /// </summary>
        public static string Fingerprint(Node n)
        {
            if (n == null) return "";
            var sb = new StringBuilder();
            sb.Append(n.Type.ToString().ToLowerInvariant()).Append('|');
            sb.Append((n.Address ?? "").Trim().ToLowerInvariant()).Append('|');
            sb.Append(n.Port.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append((n.UUID ?? "").Trim()).Append('|');
            sb.Append((n.Password ?? "").Trim()).Append('|');
            sb.Append((n.EncryptMethod ?? "").Trim().ToLowerInvariant()).Append('|');
            sb.Append(NormalizeNetwork(n.Network)).Append('|');
            sb.Append((n.Path ?? "").Trim()).Append('|');
            sb.Append((n.Host ?? "").Trim().ToLowerInvariant()).Append('|');
            sb.Append((n.SNI ?? "").Trim().ToLowerInvariant()).Append('|');
            sb.Append((n.PublicKey ?? "").Trim()).Append('|');
            sb.Append((n.Flow ?? "").Trim().ToLowerInvariant());
            return sb.ToString();
        }
    }
}
