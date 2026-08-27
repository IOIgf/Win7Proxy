using System;
using System.Collections.Generic;
using System.Text;
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
                    if (t.StartsWith("vmess://") || t.StartsWith("vless://") ||
                        t.StartsWith("trojan://") || t.StartsWith("ss://"))
                        return true;
                }
            }
            // 也可能直接是明文链接（每行一个）
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("vmess://") || t.StartsWith("vless://") ||
                    t.StartsWith("trojan://") || t.StartsWith("ss://"))
                    return true;
            }
            return false;
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
                var link = line.Trim();
                if (string.IsNullOrEmpty(link)) continue;
                if (!link.StartsWith("vmess://") && !link.StartsWith("vless://") &&
                    !link.StartsWith("trojan://") && !link.StartsWith("ss://"))
                    continue;
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
            if (link.StartsWith("vmess://")) return ParseVmess(link);
            if (link.StartsWith("vless://")) return ParseVless(link);
            if (link.StartsWith("trojan://")) return ParseTrojan(link);
            if (link.StartsWith("ss://")) return ParseSs(link);
            return null;
        }

        private static Node ParseVmess(string link)
        {
            // vmess://<base64(json)>
            var b64 = link.Substring("vmess://".Length);
            var json = DecodeBase64Strict(b64);
            var o = Newtonsoft.Json.Linq.JObject.Parse(json);
            var n = new Node { Type = NodeType.Vmess, RawLink = link };
            n.Remarks = (string)o["ps"] ?? "";
            n.Address = (string)o["add"] ?? "";
            n.Port = ToInt(o["port"]);
            n.UUID = (string)o["id"] ?? "";
            n.Security = (string)o["scy"] ?? "aes-128-gcm";
            n.Network = (string)o["net"] ?? "tcp";
            n.TLS = ((string)o["tls"] ?? "") == "tls";
            n.Host = (string)o["host"] ?? "";
            n.Path = (string)o["path"] ?? "";
            n.SNI = (string)o["sni"] ?? n.Host;
            if (n.Network == "ws" || n.Network == "h2")
            {
                // v2rayN 中 ws 的 path/host 可能在 path 字段里用逗号分隔，或 host 单独
                if (string.IsNullOrEmpty(n.Path) && !string.IsNullOrEmpty((string)o["path"]))
                    n.Path = (string)o["path"];
            }
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
            n.TLS = (q["security"] == "tls" || q["security"] == "reality" || q["tls"] == "tls");
            if (q.ContainsKey("security")) n.Extra["security"] = q["security"];
            n.Flow = q.ContainsKey("flow") ? q["flow"] : "";
            n.Network = q.ContainsKey("type") ? q["type"] : "tcp";
            n.Path = q.ContainsKey("path") ? UriUnescape(q["path"]) : "";
            n.Host = q.ContainsKey("host") ? UriUnescape(q["host"]) : "";
            n.SNI = q.ContainsKey("sni") ? q["sni"] : n.Host;
            n.Fingerprint = q.ContainsKey("fp") ? q["fp"] : "";
            n.PublicKey = q.ContainsKey("pbk") ? q["pbk"] : "";
            n.ShortId = q.ContainsKey("sid") ? q["sid"] : "";
            n.AllowInsecure = q.ContainsKey("allowInsecure") && q["allowInsecure"] == "1";
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
            var q = ParseQuery(uri);
            n.TLS = true;
            n.Network = q.ContainsKey("type") ? q["type"] : "tcp";
            n.Path = q.ContainsKey("path") ? UriUnescape(q["path"]) : "";
            n.Host = q.ContainsKey("host") ? UriUnescape(q["host"]) : "";
            n.SNI = q.ContainsKey("sni") ? q["sni"] : n.Host;
            n.Fingerprint = q.ContainsKey("fp") ? q["fp"] : "";
            n.AllowInsecure = q.ContainsKey("allowInsecure") && q["allowInsecure"] == "1";
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

            // userinfo 可能是 base64(method:password)
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
            var ci = userinfo.IndexOf(':');
            if (ci >= 0) { n.EncryptMethod = userinfo.Substring(0, ci); n.Password = userinfo.Substring(ci + 1); }
            ParseHostPort(hp, n);
        }

        private static void ParseHostPort(string hp, Node n)
        {
            // hp = host:port （host 可能含 : 若是 IPv6，但此处简化按最后 ':' 拆分）
            int colon = hp.LastIndexOf(':');
            if (colon < 0) { n.Address = hp; return; }
            n.Address = hp.Substring(0, colon);
            n.Port = ToInt(hp.Substring(colon + 1));
        }

        private static Dictionary<string, string> ParseQuery(string uri)
        {
            var d = new Dictionary<string, string>();
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
                var trimmed = s.Trim();
                // 去除可能的换行
                trimmed = trimmed.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                var bytes = Convert.FromBase64String(FixPadding(trimmed));
                return Encoding.UTF8.GetString(bytes);
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
