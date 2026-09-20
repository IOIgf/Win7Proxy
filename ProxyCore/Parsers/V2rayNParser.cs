using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore.Parsers
{
    /// <summary>
    /// v2rayN 订阅解析：base64 包裹的逐行协议链接。
    /// 支持 vmess:// / vless:// / trojan:// / ss://。
    /// </summary>
    public class V2rayNParser : ISubscriptionParser
    {
        public bool CanParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            // 尝试 base64 解码后判断
            string decoded = TryDecodeBase64(raw.Trim());
            if (decoded != null)
            {
                foreach (var line in decoded.Split('\n'))
                {
                    var t = line.Trim();
                    if (IsSupportedLink(t)) return true;
                }
            }
            // 也可能直接是明文链接（每行一个）
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (IsSupportedLink(t)) return true;
            }
            return false;
        }

        private static bool IsSupportedLink(string t)
        {
            return t.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("ss://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase);
        }

        public List<Node> Parse(string raw)
        {
            var result = new List<Node>();
            string text = raw.Trim();
            // 先尝试整体 base64 解码（v2rayN 标准做法是整段 base64）
            var decoded = TryDecodeBase64(text);
            if (decoded != null) text = decoded;

            foreach (var line in text.Split('\n'))
            {
                var link = line.Trim().Trim('\r');
                if (string.IsNullOrEmpty(link)) continue;
                if (!IsSupportedLink(link)) continue;
                try
                {
                    var node = ParseLink(link);
                    if (node != null) result.Add(node);
                }
                catch
                {
                    // 单条失败不阻断整体
                }
            }
            return result;
        }

        public static Node ParseLink(string link)
        {
            if (link == null) return null;
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(link);
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(link);
            if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(link);
            if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseSs(link);
            if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(link);
            return null;
        }

        /// <summary>
        /// 安全读取 JSON 字段为字符串。
        /// 早期实现写成 (string)o["tls"]，当订阅里 "tls" 是布尔值 true 时会抛
        /// InvalidCastException，被上层 catch 吞掉后整条节点就静默消失了。
        /// 这里统一走 ToString()，布尔/数字/字符串都能吃下。
        /// </summary>
        private static string Str(JObject o, params string[] keys)
        {
            foreach (var k in keys)
            {
                var t = o[k];
                if (t == null || t.Type == JTokenType.Null) continue;
                // 布尔值统一转成小写字符串，否则 JToken.ToString() 会给出 "True"，
                // 后续按小写比较就匹配不上
                if (t.Type == JTokenType.Boolean) return ((bool)t) ? "true" : "false";
                var s = t.ToString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return "";
        }

        private static bool Bool(JObject o, params string[] keys)
        {
            var s = Str(o, keys);
            if (s == "") return false;
            return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }

        private static Node ParseVmess(string link)
        {
            // vmess://<base64(json)>
            var b64 = link.Substring("vmess://".Length);
            var json = DecodeBase64Strict(b64);
            var o = JObject.Parse(json);
            var n = new Node { Type = NodeType.Vmess, RawLink = link };

            n.Remarks = Str(o, "ps", "remarks");
            n.Address = Str(o, "add", "address");
            n.Port = ToInt(o["port"] ?? o["server_port"]);
            n.UUID = Str(o, "id", "uuid");
            n.Security = Str(o, "scy", "encryption", "cipher");
            if (string.IsNullOrEmpty(n.Security)) n.Security = "auto";

            n.Network = NetUtil.NormalizeNetwork(Str(o, "net", "network", "type"));
            n.Host = Str(o, "host", "sni");
            n.Path = Str(o, "path", "ws-path");
            n.SNI = Str(o, "sni", "servername", "host");
            n.Fingerprint = Str(o, "fp", "fingerprint");
            n.Alpn = Str(o, "alpn");
            n.AllowInsecure = Bool(o, "allowInsecure", "allow_insecure", "skip-cert-verify");
            n.PinnedCertSha256 = Str(o, "pinSHA256", "pin-sha256", "pinnedPeerCertSha256", "certSha256");

            var tls = NetUtil.NormalizeSecurity(Str(o, "tls", "security"));
            n.TLS = tls == "tls" || tls == "reality";
            n.Extra["security"] = tls;

            return n;
        }

        private static Node ParseVless(string link)
        {
            // vless://uuid@host:port?query#remarks
            var n = new Node { Type = NodeType.Vless, RawLink = link };
            var uri = SplitLink(link, "vless://", out var remarks);
            n.Remarks = remarks;
            ParseAuthorityAndQuery(uri, n);
            var q = ParseQuery(uri);

            var sec = NetUtil.NormalizeSecurity(Get(q, "security", "tls"));
            n.Extra["security"] = sec;
            n.TLS = sec == "tls" || sec == "reality";
            n.Flow = Get(q, "flow");
            n.Network = NetUtil.NormalizeNetwork(Get(q, "type", "network"));
            n.Path = UriUnescape(Get(q, "path", "serviceName"));
            n.Host = UriUnescape(Get(q, "host"));
            n.SNI = Get(q, "sni", "servername", "host");
            n.Fingerprint = Get(q, "fp", "fingerprint");
            n.PublicKey = Get(q, "pbk", "publicKey");
            n.ShortId = Get(q, "sid", "shortId");
            n.Alpn = UriUnescape(Get(q, "alpn"));
            n.ServiceName = UriUnescape(Get(q, "serviceName", "grpc-service-name"));
            n.AllowInsecure = IsTrue(q, "allowInsecure", "allowInsecureTls", "skip-cert-verify");
            n.PinnedCertSha256 = CertPin(q);
            return n;
        }

        private static Node ParseTrojan(string link)
        {
            // trojan://password@host:port?query#remarks
            var n = new Node { Type = NodeType.Trojan, RawLink = link };
            var uri = SplitLink(link, "trojan://", out var remarks);
            n.Remarks = remarks;
            ParseAuthorityAndQuery(uri, n);
            n.Password = n.UUID; // authority 里 user 部分是密码
            n.UUID = "";
            var q = ParseQuery(uri);

            var sec = NetUtil.NormalizeSecurity(Get(q, "security", "tls"));
            n.TLS = true; // trojan 必经 TLS
            n.Extra["security"] = sec == "none" ? "tls" : sec;
            n.Network = NetUtil.NormalizeNetwork(Get(q, "type", "network"));
            n.Path = UriUnescape(Get(q, "path"));
            n.Host = UriUnescape(Get(q, "host"));
            n.SNI = Get(q, "sni", "peer", "host");
            n.Fingerprint = Get(q, "fp", "fingerprint");
            n.Alpn = UriUnescape(Get(q, "alpn"));
            n.ServiceName = UriUnescape(Get(q, "serviceName", "grpc-service-name"));
            n.AllowInsecure = IsTrue(q, "allowInsecure", "allowInsecureTls", "skip-cert-verify");
            n.PinnedCertSha256 = CertPin(q);
            return n;
        }

        private static Node ParseSs(string link)
        {
            // 形式1 (SIP002): ss://base64(method:password)@host:port#remarks
            // 形式2:          ss://method:password@host:port#remarks
            // 形式3 (旧):     ss://base64(method:password@host:port)#remarks
            var n = new Node { Type = NodeType.Shadowsocks, RawLink = link };
            var body = link.Substring("ss://".Length);
            // 分离 #remarks
            string remarks = "";
            int hash = body.IndexOf('#');
            if (hash >= 0) { remarks = UriUnescape(body.Substring(hash + 1)); body = body.Substring(0, hash); }
            n.Remarks = remarks;

            // 可能带 ?plugin=... 查询串
            int qm = body.IndexOf('?');
            if (qm >= 0)
            {
                var qs = ParseQuery(body);
                var plugin = Get(qs, "plugin");
                if (!string.IsNullOrEmpty(plugin)) n.Extra["plugin"] = plugin;
                var pluginOpts = Get(qs, "plugin-opts", "plugin_opts");
                if (!string.IsNullOrEmpty(pluginOpts)) n.Extra["plugin_opts"] = pluginOpts;
                body = body.Substring(0, qm);
            }

            // 形式3: 整个 body 是 base64 且解码后含 '@'
            var decodedFull = TryDecodeBase64(body);
            if (decodedFull != null && decodedFull.Contains("@"))
            {
                var at = decodedFull.LastIndexOf('@');
                var userinfo = decodedFull.Substring(0, at);
                var hp = decodedFull.Substring(at + 1);
                ParseUserHostPort(userinfo, hp, n);
                return n;
            }

            // 形式1 / 形式2: userinfo@host:port
            int at2 = body.LastIndexOf('@');
            if (at2 < 0) return n;
            var userinfo2 = body.Substring(0, at2);
            var hp2 = body.Substring(at2 + 1);

            // userinfo 可能是 base64(method:password)，也可能是 URL-safe 无填充
            var decUi = TryDecodeBase64(userinfo2);
            if (decUi != null && decUi.Contains(":"))
            {
                var ci = decUi.IndexOf(':');
                n.EncryptMethod = decUi.Substring(0, ci);
                n.Password = decUi.Substring(ci + 1);
            }
            else if (userinfo2.Contains(":"))
            {
                var ci = userinfo2.IndexOf(':');
                n.EncryptMethod = userinfo2.Substring(0, ci);
                n.Password = userinfo2.Substring(ci + 1);
            }
            ParseHostPort(hp2, n);
            return n;
        }

        private static Node ParseHysteria2(string link)
        {
            // hysteria2://auth@host:port/?obfs=salamander&obfs-password=..&sni=..&insecure=1#remarks
            var n = new Node { Type = NodeType.Hysteria2, RawLink = link };
            var body = link.Substring(link.IndexOf("://", StringComparison.Ordinal) + 3);

            int hash = body.IndexOf('#');
            if (hash >= 0) { n.Remarks = UriUnescape(body.Substring(hash + 1)); body = body.Substring(0, hash); }

            int qm = body.IndexOf('?');
            var authority = (qm >= 0 ? body.Substring(0, qm) : body).TrimEnd('/');

            int at = authority.LastIndexOf('@');
            var hp = authority;
            if (at >= 0)
            {
                n.Password = UriUnescape(authority.Substring(0, at));
                hp = authority.Substring(at + 1);
            }

            int colon = hp.LastIndexOf(':');
            n.Address = (colon >= 0 ? hp.Substring(0, colon) : hp).Trim('[', ']');
            // 端口部分可能是 "443" 或 "443,20000-30000"（后者为端口跳跃）
            var portParts = (colon >= 0 ? hp.Substring(colon + 1) : "").Split(',');
            n.Port = ToInt(portParts.Length > 0 ? portParts[0].Trim() : "");
            if (portParts.Length > 1)
                n.Ports = string.Join(",", portParts, 1, portParts.Length - 1).Trim();

            var q = ParseQuery(body);
            n.SNI = Get(q, "sni", "peer", "servername");
            n.Host = Get(q, "host");
            n.Fingerprint = Get(q, "fp", "fingerprint");
            n.Alpn = UriUnescape(Get(q, "alpn"));
            n.Obfs = Get(q, "obfs");
            n.ObfsPassword = Get(q, "obfs-password", "obfs-pwd", "obfsparam");
            n.PinnedCertSha256 = Get(q, "pinSHA256", "pin-sha256", "pinsha256",
                "pinnedPeerCertSha256", "pinned-peer-cert-sha256", "certSha256", "cert-sha256");
            n.AllowInsecure = IsTrue(q, "insecure", "allowInsecure", "skip-cert-verify");
            n.UpMbps = ToInt(Get(q, "up_mbps", "up"));
            n.DownMbps = ToInt(Get(q, "down_mbps", "down"));

            if (string.IsNullOrEmpty(n.Ports))
            {
                var mport = Get(q, "mport", "ports");
                if (!string.IsNullOrEmpty(mport)) n.Ports = mport;
            }
            if (n.Port <= 0) n.Port = 443;

            n.TLS = true; // hysteria2 基于 QUIC，必然 TLS
            n.Extra["security"] = "tls";
            return n;
        }

        // ---------- 工具方法 ----------

        private static void ParseAuthorityAndQuery(string uri, Node n)
        {
            // uri = user@host:port?query  或  host:port?query
            int q = uri.IndexOf('?');
            string auth = q >= 0 ? uri.Substring(0, q) : uri;
            int at = auth.LastIndexOf('@');
            string user = at >= 0 ? auth.Substring(0, at) : "";
            string hp = at >= 0 ? auth.Substring(at + 1) : auth;
            n.UUID = UriUnescape(user);
            ParseHostPort(hp, n);
        }

        private static void ParseUserHostPort(string userinfo, string hp, Node n)
        {
            // SS 的 userinfo 本身可能也是 base64，先试着解一次
            var ui = TryDecodeBase64(userinfo) ?? userinfo;
            // password 里可能含 ':'（base64 常见），所以只按第一个 ':' 切分 method:password
            var ci = ui.IndexOf(':');
            if (ci >= 0) { n.EncryptMethod = ui.Substring(0, ci); n.Password = ui.Substring(ci + 1); }
            ParseHostPort(hp, n);
        }

        private static void ParseHostPort(string hp, Node n)
        {
            // hp = host:port （host 可能含 : 若是 IPv6，但此处简化按最后 ':' 拆分）
            int colon = hp.LastIndexOf(':');
            if (colon < 0) { n.Address = hp; return; }
            n.Address = hp.Substring(0, colon).Trim('[', ']');
            n.Port = ToInt(hp.Substring(colon + 1));
        }

        private static Dictionary<string, string> ParseQuery(string uri)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = uri.IndexOf('?');
            if (q < 0) return d;
            var qs = uri.Substring(q + 1);
            foreach (var pair in qs.Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                int eq = pair.IndexOf('=');
                if (eq < 0) d[UriUnescape(pair)] = "";
                else d[UriUnescape(pair.Substring(0, eq))] = UriUnescape(pair.Substring(eq + 1));
            }
            return d;
        }

        private static string Get(Dictionary<string, string> q, params string[] keys)
        {
            foreach (var k in keys)
                if (q.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v)) return v;
            return "";
        }

        private static bool IsTrue(Dictionary<string, string> q, params string[] keys)
        {
            var v = Get(q, keys);
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>证书 SHA-256 pin：不同订阅/客户端用的字段名不一样，逐个试。</summary>
        private static string CertPin(Dictionary<string, string> q)
        {
            return Get(q, "pinSHA256", "pin-sha256", "pinsha256",
                "pinnedPeerCertSha256", "pinned-peer-cert-sha256", "certSha256", "cert-sha256");
        }

        private static string SplitLink(string link, string scheme, out string remarks)
        {
            var body = link.Substring(scheme.Length);
            int hash = body.IndexOf('#');
            if (hash >= 0) { remarks = UriUnescape(body.Substring(hash + 1)); body = body.Substring(0, hash); }
            else remarks = "";
            return body;
        }

        private static int ToInt(object v)
        {
            if (v == null) return 0;
            if (int.TryParse(v.ToString(), out var i)) return i;
            return 0;
        }

        public static string UriUnescape(string s)
        {
            try { return Uri.UnescapeDataString(s ?? ""); }
            catch { return s; }
        }

        public static string TryDecodeBase64(string s)
        {
            try
            {
                var trimmed = (s ?? "").Trim();
                // 去除可能的换行
                trimmed = trimmed.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                if (trimmed.Length == 0) return null;
                var bytes = Convert.FromBase64String(FixPadding(trimmed));
                var text = Encoding.UTF8.GetString(bytes);
                // 避免把普通字符串误判成 base64：解码结果必须是可打印文本
                foreach (var ch in text)
                    if (ch != '\r' && ch != '\n' && ch != '\t' && (ch < 32 || ch == 127)) return null;
                return text;
            }
            catch { return null; }
        }

        private static string DecodeBase64Strict(string s)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(FixPadding(s.Trim().Replace("\r", "").Replace("\n", ""))));
        }

        private static string FixPadding(string s)
        {
            int mod = s.Length % 4;
            if (mod != 0) s = s.PadRight(s.Length + (4 - mod), '=');
            // 兼容 URL-safe base64
            s = s.Replace('-', '+').Replace('_', '/');
            return s;
        }
    }
}
