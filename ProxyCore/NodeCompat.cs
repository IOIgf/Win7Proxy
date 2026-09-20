using System.Collections.Generic;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 节点与内核的兼容性检查。
    /// 各内核支持的传输方式并不一致（Xray 已移除 h2，V2Ray 不支持 REALITY），
    /// 这类不兼容的表现是"内核启动即退出"，用户只看到"启动失败"，很难定位。
    /// 这里在启动前就按内核能力表把问题提示出来。
    /// </summary>
    public static class NodeCompat
    {
        /// <summary>默认按 Xray 判断（向后兼容）。</summary>
        public static List<string> Warnings(Node n)
        {
            return Warnings(n, CoreRegistry.Xray);
        }

        /// <summary>返回该节点在指定内核上的已知兼容性提示；没有问题则返回空列表。</summary>
        public static List<string> Warnings(Node n, CoreSpec spec)
        {
            var list = new List<string>();
            if (n == null) return list;
            if (spec == null) spec = CoreRegistry.Xray;

            var net = NetUtil.NormalizeNetwork(n.Network);
            if (n.Type != NodeType.Hysteria2 && !spec.SupportsNetwork(net))
            {
                var alt = new List<string>();
                foreach (var s in CoreRegistry.All)
                    if (s.SupportsNetwork(net)) alt.Add(s.Name);

                list.Add(spec.Name + " 不支持 " + net + " 传输，内核会拒绝启动"
                    + (alt.Count > 0 ? "（" + string.Join(" / ", alt.ToArray()) + " 仍支持）" : "") + "。");
            }

            if (n.Type == NodeType.Hysteria2 && !spec.Hysteria2)
                list.Add(spec.Name + " 不支持 Hysteria2，内核会拒绝启动（Xray 与 sing-box 支持）。");

            if (n.Type == NodeType.Shadowsocks)
            {
                var plugin = SsPluginParser.Parse(n.Extra);
                if (plugin != null && plugin.IsV2rayPlugin && spec.Kind != CoreKind.Singbox)
                    list.Add("Shadowsocks v2ray-plugin 已等价映射为 WebSocket 传输"
                        + (plugin.Tls ? "（TLS）" : "") + "；Xray/V2Ray 本身没有 SIP003 插件字段。");
            }

            if (n.AllowInsecure && spec.Kind == CoreKind.Xray &&
                NetUtil.NormalizeCertPin(n.PinnedCertSha256) == "")
                list.Add("Xray 已移除 allowInsecure（跳过证书校验），将改用严格校验；"
                    + "自签名服务器请提供 pinSHA256，或改用 sing-box。");

            if (!string.IsNullOrEmpty(n.PinnedCertSha256) && spec.Kind == CoreKind.Singbox)
                list.Add("sing-box 只支持证书公钥 pin，无法识别 hysteria2 的证书指纹 pinSHA256，该参数将被忽略。");

            if (n.Type == NodeType.Vmess && !string.IsNullOrEmpty(n.Security) &&
                (n.Security == "none" || n.Security == "zero"))
                list.Add("该 vmess 节点未启用加密（" + n.Security + "），流量特征明显且已被多数内核标记为不推荐。");

            if (string.IsNullOrEmpty(n.Address))
                list.Add("节点没有地址（Address 为空），无法生成可用配置。");

            if (n.Port <= 0 || n.Port > 65535)
                list.Add("节点端口不合法：" + n.Port);

            return list;
        }
    }
}
