using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore.Parsers
{
    /// <summary>SIP008 订阅解析：Shadowsocks 官方 JSON 格式。</summary>
    public class Sip008Parser : ISubscriptionParser
    {
        public bool CanParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var t = raw.Trim();
            if (!t.StartsWith("{")) return false;
            try
            {
                var o = JObject.Parse(t);
                return o["servers"] is JArray;
            }
            catch { return false; }
        }

        public List<Node> Parse(string raw)
        {
            var result = new List<Node>();
            var o = JObject.Parse(raw);
            if (!(o["servers"] is JArray arr)) return result;
            foreach (var s in arr)
            {
                var n = new Node { Type = NodeType.Shadowsocks };
                n.Remarks = (string)s["remarks"] ?? (string)s["id"] ?? "";
                n.Address = (string)s["server"] ?? "";
                n.Port = (int?)s["server_port"] ?? 0;
                n.EncryptMethod = (string)s["method"] ?? "";
                n.Password = (string)s["password"] ?? "";
                if (s["plugin"] != null)
                {
                    n.Extra["plugin"] = (string)s["plugin"];
                    n.Extra["plugin_opts"] = (string)s["plugin_opts"] ?? "";
                }
                result.Add(n);
            }
            return result;
        }
    }
}
