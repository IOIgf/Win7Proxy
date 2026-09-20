using System;
using System.Collections.Generic;

namespace ProxyCore
{
    /// <summary>
    /// Shadowsocks SIP003 插件（obfs-local / simple-obfs / v2ray-plugin）的解析结果。
    /// 解析器只把原始 plugin / plugin_opts 塞进 Node.Extra，各内核的配置生成器再按自己的能力翻译：
    /// - sing-box 出站原生支持 plugin / plugin_opts（仅 obfs-local 与 v2ray-plugin）；
    /// - Xray / V2Ray 没有插件字段，v2ray-plugin 的 websocket 模式只能等价映射成 ws 传输，
    ///   simple-obfs 则完全无法表达，必须在启动前明确拒绝。
    /// </summary>
    public sealed class SsPlugin
    {
        /// <summary>插件名（小写）：obfs-local / simple-obfs / obfs / v2ray-plugin。</summary>
        public string Name = "";

        /// <summary>obfs 的 http|tls，或 v2ray-plugin 的 websocket|quic。可能为空。</summary>
        public string Mode = "";

        /// <summary>v2ray-plugin 的 host / obfs 的 obfs-host。</summary>
        public string Host = "";

        /// <summary>v2ray-plugin 的 path。</summary>
        public string Path = "";

        /// <summary>v2ray-plugin 是否启用 TLS。</summary>
        public bool Tls;

        /// <summary>插件名之后的原始参数（分号分隔），可直接作为 sing-box 的 plugin_opts。</summary>
        public string Options = "";

        public bool IsObfs
        {
            get { return Name == "obfs-local" || Name == "simple-obfs" || Name == "obfs"; }
        }

        public bool IsV2rayPlugin
        {
            get { return Name == "v2ray-plugin"; }
        }

        /// <summary>Xray / V2Ray 能表达的只有 v2ray-plugin 的 websocket 模式。</summary>
        public bool IsWebsocketV2rayPlugin
        {
            get { return IsV2rayPlugin && (Mode == "" || Mode == "websocket" || Mode == "ws"); }
        }
    }

    /// <summary>从 Node.Extra 解析 SIP003 插件参数。</summary>
    public static class SsPluginParser
    {
        /// <summary>没有插件时返回 null。</summary>
        public static SsPlugin Parse(IDictionary<string, string> extra)
        {
            if (extra == null) return null;

            string raw, opts;
            extra.TryGetValue("plugin", out raw);
            extra.TryGetValue("plugin_opts", out opts);
            // 没有插件名时，单独存在的 plugin_opts 无意义，不能把它当插件名解析。
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var parts = new List<string>();
            AddParts(parts, raw);
            AddParts(parts, opts);
            if (parts.Count == 0) return null;

            // 形式可能是 "v2ray-plugin" + "mode=websocket;host=x"（SIP008 分开存），
            // 也可能是 "obfs-local;obfs=http;obfs-host=x"（v2rayN / Clash 一整串）。
            var plugin = new SsPlugin { Name = parts[0].Trim().ToLowerInvariant() };
            if (plugin.Name == "") return null;

            var optParts = new List<string>();
            for (int i = 1; i < parts.Count; i++)
            {
                var part = parts[i].Trim();
                if (part == "") continue;
                optParts.Add(part);

                int eq = part.IndexOf('=');
                var key = (eq >= 0 ? part.Substring(0, eq) : part).Trim().ToLowerInvariant();
                var val = eq >= 0 ? part.Substring(eq + 1).Trim() : "";
                switch (key)
                {
                    case "obfs":
                    case "mode": plugin.Mode = val.ToLowerInvariant(); break;
                    case "obfs-host":
                    case "host": plugin.Host = val; break;
                    case "path": plugin.Path = val; break;
                    case "tls": plugin.Tls = val == "" || IsTruthy(val); break;
                    case "obfs-uri": if (plugin.Path == "") plugin.Path = val; break;
                }
            }
            plugin.Options = string.Join(";", optParts.ToArray());
            return plugin;
        }

        private static void AddParts(List<string> parts, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (var p in value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = p.Trim();
                if (t != "") parts.Add(t);
            }
        }

        private static bool IsTruthy(string v)
        {
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}
