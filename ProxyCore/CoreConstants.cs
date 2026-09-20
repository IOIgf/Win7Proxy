namespace ProxyCore
{
    /// <summary>全局端口与常量。</summary>
    public static class CoreConstants
    {
        public const int MixedPort = 10808;

        /// <summary>程序版本，用于 User-Agent 等；与 Win7Proxy.csproj 的 &lt;Version&gt; 保持一致。</summary>
        public const string AppVersion = "1.5.2";

        public const string CoreDir = "core";
        public const string XrayExe = "xray.exe";
        public const string V2rayExe = "v2ray.exe";
        public const string SingboxExe = "sing-box.exe";
        public const string ConfigFile = "config.json";

        // v2ray 系（Xray / V2Ray）使用的 v2ray-format geo 数据
        public const string GeoIpFile = "geoip.dat";
        public const string GeoSiteFile = "geosite.dat";

        // sing-box 1.12+ 使用按标签拆分的 .srs 规则集。
        // 旧的单体 geosite.db / geoip.db 在 1.14.0 已无法作为本地 rule-set 加载。
        public const string GeoSiteAdsSrs = "geosite-category-ads-all.srs";
        public const string GeoSiteCnSrs = "geosite-cn.srs";
        public const string GeoIpCnSrs = "geoip-cn.srs";
    }
}
