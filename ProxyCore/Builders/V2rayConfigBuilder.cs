using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// V2Ray-core 配置生成。格式与 Xray 基本一致，关键差异：
    /// HTTP/2 的 network 名字是 "http"（Xray 是 "h2"），且不支持 REALITY / XTLS flow。
    /// 这也是目前唯一能在新版 Xray 之外跑 h2 节点的选择之一。
    /// </summary>
    public static class V2rayConfigBuilder
    {
        public static XrayConfig Build(Node node, ProxyMode mode, InboundOptions inbound = null)
        {
            return V2rayStyleConfigBuilder.Build(CoreRegistry.V2ray, node, mode, inbound);
        }

        public static string BuildJson(Node node, ProxyMode mode, InboundOptions inbound = null)
        {
            return Build(node, mode, inbound).ToJson();
        }
    }
}
