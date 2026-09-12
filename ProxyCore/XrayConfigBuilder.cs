using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 把统一 Node 模型 + 分流模式转换为 Xray 配置(JSON)。
    /// 具体实现见 <see cref="V2rayStyleConfigBuilder"/>（与 V2Ray 共用，靠能力表区分差异）。
    /// </summary>
    public static class XrayConfigBuilder
    {
        public static XrayConfig Build(Node node, ProxyMode mode)
        {
            return V2rayStyleConfigBuilder.Build(CoreRegistry.Xray, node, mode);
        }

        public static string BuildJson(Node node, ProxyMode mode)
        {
            return Build(node, mode).ToJson();
        }
    }
}
