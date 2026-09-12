namespace ProxyCore
{
    /// <summary>全局端口与常量。</summary>
    public static class CoreConstants
    {
        public const int SocksPort = 10808;
        public const int HttpPort = 10809;
        public const int PacPort = 10810;

        public const string CoreDir = "core";
        public const string XrayExe = "xray.exe";
        public const string V2rayExe = "v2ray.exe";
        public const string SingboxExe = "sing-box.exe";
        public const string ConfigFile = "config.json";

        // v2ray 系（Xray / V2Ray）使用的 v2ray-format geo 数据
        public const string GeoIpFile = "geoip.dat";
        public const string GeoSiteFile = "geosite.dat";

        // sing-box 使用的规则集数据库。注意：sing-box 1.12+ 起 geosite/geoip 改用
        // 按标签拆分的 .srs 规则集（SagerNet/sing-geosite、sing-geoip 的 rule-set 分支），
        // 旧的单体 geosite.db / geoip.db 在 1.14.0 已无法作为本地 rule-set 加载。
        public const string GeoIpDb = "geoip.db";
        public const string GeoSiteDb = "geosite.db";

        // sing-box 规则模式实际使用的 .srs 规则集文件名
        public const string GeoSiteAdsSrs = "geosite-category-ads-all.srs";
        public const string GeoSiteCnSrs = "geosite-cn.srs";
        public const string GeoIpCnSrs = "geoip-cn.srs";
    }
}
