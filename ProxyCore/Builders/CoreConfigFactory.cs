using System.IO;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>按当前选择的内核分发配置生成。</summary>
    public static class CoreConfigFactory
    {
        /// <summary>
        /// 生成配置 JSON。sing-box 需要知道 geo 数据是否已就位，
        /// 缺文件时自动降级为不含地理规则的配置（而不是生成必然失败的配置）。
        /// </summary>
        public static string BuildJson(CoreKind kind, Node node, ProxyMode mode, string coreDir = null)
        {
            switch (kind)
            {
                case CoreKind.V2ray:
                    return V2rayConfigBuilder.BuildJson(node, mode);

                case CoreKind.Singbox:
                    return SingboxConfigBuilder.BuildJson(node, mode, HasSingboxGeo(coreDir));

                default:
                    return XrayConfigBuilder.BuildJson(node, mode);
            }
        }

        public static bool HasSingboxGeo(string coreDir)
        {
            if (string.IsNullOrEmpty(coreDir)) return false;
            return File.Exists(Path.Combine(coreDir, CoreConstants.GeoIpDb))
                && File.Exists(Path.Combine(coreDir, CoreConstants.GeoSiteDb));
        }
    }
}
