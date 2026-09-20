using System;
using System.Collections.Generic;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>支持的内核。</summary>
    public enum CoreKind
    {
        /// <summary>Xray-core：功能最全，唯一提供 Win7 构建的内核。</summary>
        Xray = 0,
        /// <summary>V2Ray-core：仍支持 HTTP/2（h2）与 mKCP，但不支持 REALITY / XTLS。</summary>
        V2ray = 1,
        /// <summary>sing-box：支持 h2、REALITY，geo 规则数据自成一套（1.12+ 起为按标签拆分的 .srs 规则集）。</summary>
        Singbox = 2
    }

    /// <summary>
    /// 一个内核的能力与运行参数。
    /// 之所以要做能力表：同一条节点（例如 h2 传输）在不同内核里的支持情况完全不同，
    /// Xray 自 25.11 起已移除 h2，而 V2Ray / sing-box 仍然支持。
    /// 靠这张表在启动前就判断能不能用，而不是等内核启动失败后再让用户猜。
    /// </summary>
    public class CoreSpec
    {
        public CoreKind Kind;
        /// <summary>显示名。</summary>
        public string Name;
        /// <summary>core 目录下的可执行文件名。</summary>
        public string ExeName;
        /// <summary>进程名（不含 .exe），用于按 PID 清理残留进程时做校验，避免误杀。</summary>
        public string ProcessName;
        /// <summary>是否有可在 Windows 7 上运行的构建。新版 Go 编译的内核已不支持 Win7。</summary>
        public bool Win7Usable;
        /// <summary>是否支持 REALITY。</summary>
        public bool Reality;
        /// <summary>是否支持 vless 的 XTLS flow（xtls-rprx-vision 等）。</summary>
        public bool XtlsFlow;
        /// <summary>是否支持 Hysteria2 出站（QUIC，仅 sing-box）。</summary>
        public bool Hysteria2;
        /// <summary>使用 v2ray 系的 geoip.dat / geosite.dat。</summary>
        public bool NeedsGeoDat;
        /// <summary>使用 sing-box 的 geo 规则数据（1.12+ 起为按标签拆分的 .srs 规则集）。</summary>
        public bool NeedsGeoDb;
        /// <summary>支持的传输方式（值已归一化，h2 即 HTTP/2）。</summary>
        public HashSet<string> Networks;

        public bool SupportsNetwork(string net)
        {
            return Networks.Contains(NetUtil.NormalizeNetwork(net));
        }

        /// <summary>
        /// 判断该节点能否在当前内核上使用；不能则返回原因，可以则返回 null。
        /// 只覆盖"内核明确不支持、写了必然失败"的情况。
        /// </summary>
        public string ReasonUnsupported(Node n)
        {
            if (n == null) return "节点为空。";
            if (string.IsNullOrWhiteSpace(n.Address)) return "节点地址为空。";
            if (n.Port <= 0 || n.Port > 65535) return "节点端口不合法：" + n.Port + "。";

            if ((n.Type == NodeType.Vmess || n.Type == NodeType.Vless) && string.IsNullOrWhiteSpace(n.UUID))
                return n.Type + " 节点缺少 UUID。";
            if (n.Type == NodeType.Trojan && string.IsNullOrEmpty(n.Password))
                return "Trojan 节点缺少密码。";
            if (n.Type == NodeType.Shadowsocks)
            {
                if (string.IsNullOrWhiteSpace(n.EncryptMethod)) return "Shadowsocks 节点缺少加密方法。";
                if (string.IsNullOrEmpty(n.Password)) return "Shadowsocks 节点缺少密码。";
                if (Kind == CoreKind.V2ray && n.EncryptMethod.StartsWith("2022-", StringComparison.OrdinalIgnoreCase))
                    return "V2Ray 不支持 SS2022 加密，请换用 Xray 或 sing-box。";

                // SIP003 插件：sing-box 原生支持 obfs-local / v2ray-plugin；
                // Xray/V2Ray 没有插件字段，只有 v2ray-plugin 的 websocket 模式能等价映射。
                var plugin = SsPluginParser.Parse(n.Extra);
                if (plugin != null)
                {
                    if (Kind == CoreKind.Singbox)
                    {
                        if (!plugin.IsObfs && !plugin.IsV2rayPlugin)
                            return "sing-box 不支持的 Shadowsocks 插件：" + plugin.Name + "（仅支持 obfs-local 与 v2ray-plugin）。";
                    }
                    else
                    {
                        if (plugin.IsObfs)
                            return "Xray/V2Ray 不支持 simple-obfs（" + plugin.Name + "）混淆插件，请切换到 sing-box 内核，或改用不带插件的节点。";
                        if (!plugin.IsV2rayPlugin)
                            return "Xray/V2Ray 不支持的 Shadowsocks 插件：" + plugin.Name + "。";
                        if (!plugin.IsWebsocketV2rayPlugin)
                            return "Xray/V2Ray 只能表达 v2ray-plugin 的 websocket 模式，当前模式为 " + plugin.Mode + "，请改用 sing-box。";
                    }
                }
            }

            if (n.Type == NodeType.Hysteria2)
            {
                if (!Hysteria2)
                    return Name + " 不支持 Hysteria2，请切换到 Xray 或 sing-box 内核。";
                if (string.IsNullOrEmpty(n.Password))
                    return "Hysteria2 节点缺少密码。";
                return null; // Hysteria2 自带 QUIC 传输，不走 v2ray 的 network 概念
            }

            var net = NetUtil.NormalizeNetwork(n.Network);
            if (!SupportsNetwork(net))
            {
                var alt = "";
                if (net == "h2") alt = "（可切换到 V2Ray 或 sing-box，它们仍支持 h2）";
                else if (net == "kcp") alt = "（可切换到 V2Ray）";
                return string.Format("{0} 不支持 {1} 传输{2}。", Name, net, alt);
            }

            if (NetUtil.NormalizeSecurity(n.Extra != null && n.Extra.ContainsKey("security") ? n.Extra["security"] : "") == "reality")
            {
                if (string.IsNullOrWhiteSpace(n.PublicKey)) return "REALITY 节点缺少公钥。";
                if (!Reality) return Name + " 不支持 REALITY，请换用 Xray 或 sing-box。";
            }

            if (!XtlsFlow && !string.IsNullOrEmpty(n.Flow))
                return Name + " 不支持 XTLS flow（" + n.Flow + "），请换用 Xray 或去掉 flow 参数。";

            return null;
        }
    }

    public static class CoreRegistry
    {
        public static bool IsWindows7OrEarlier()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT &&
                Environment.OSVersion.Platform != PlatformID.Win32Windows) return false;
            var v = Environment.OSVersion.Version;
            return v.Major < 6 || (v.Major == 6 && v.Minor <= 1);
        }

        public static readonly CoreSpec Xray = new CoreSpec
        {
            Kind = CoreKind.Xray,
            Name = "Xray",
            ExeName = "xray.exe",
            ProcessName = "xray",
            Win7Usable = true,
            Reality = true,
            XtlsFlow = true,
            Hysteria2 = true,
            NeedsGeoDat = true,
            Networks = new HashSet<string> { "tcp", "ws", "grpc", "httpupgrade", "quic" }
        };

        public static readonly CoreSpec V2ray = new CoreSpec
        {
            Kind = CoreKind.V2ray,
            Name = "V2Ray",
            ExeName = "v2ray.exe",
            ProcessName = "v2ray",
            Win7Usable = false,
            Reality = false,
            XtlsFlow = false,
            Hysteria2 = false,
            NeedsGeoDat = true,
            Networks = new HashSet<string> { "tcp", "ws", "grpc", "h2", "kcp" }
        };

        public static readonly CoreSpec Singbox = new CoreSpec
        {
            Kind = CoreKind.Singbox,
            Name = "sing-box",
            ExeName = "sing-box.exe",
            ProcessName = "sing-box",
            Win7Usable = false,
            Reality = true,
            XtlsFlow = false,
            Hysteria2 = true,
            NeedsGeoDb = true,
            Networks = new HashSet<string> { "tcp", "ws", "grpc", "h2", "httpupgrade", "quic" }
        };

        public static CoreSpec Of(CoreKind kind)
        {
            switch (kind)
            {
                case CoreKind.V2ray: return V2ray;
                case CoreKind.Singbox: return Singbox;
                default: return Xray;
            }
        }

        /// <summary>用于界面枚举。</summary>
        public static CoreSpec[] All => new[] { Xray, V2ray, Singbox };
    }
}
