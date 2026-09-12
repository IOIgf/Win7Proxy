using System.Collections.Generic;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 节点与当前 xray 内核的兼容性检查。
    /// xray 每个大版本都会移除一些老传输方式，而这类不兼容的表现是"内核启动即退出"，
    /// 用户只看到"启动失败"，很难定位。这里在启动前就把已知的不兼容点提示出来。
    /// </summary>
    public static class NodeCompat
    {
        /// <summary>当前 xray（25.11+）已移除、写了会直接拒绝启动的传输方式。</summary>
        private static readonly Dictionary<string, string> RemovedNetworks = new Dictionary<string, string>
        {
            { "h2", "HTTP/2（h2）传输已在 Xray 25.11 起移除，内核会拒绝启动。请换用 ws / grpc 节点。" },
            { "kcp", "mKCP 传输已在新版 Xray 中移除，内核会拒绝启动。请换用 ws / grpc 节点。" }
        };

        /// <summary>返回该节点的已知兼容性提示；没有问题则返回空列表。</summary>
        public static List<string> Warnings(Node n)
        {
            var list = new List<string>();
            if (n == null) return list;

            var net = NetUtil.NormalizeNetwork(n.Network);
            string msg;
            if (RemovedNetworks.TryGetValue(net, out msg))
                list.Add(msg);

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
