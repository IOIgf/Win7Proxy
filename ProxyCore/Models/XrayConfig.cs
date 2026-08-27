using Newtonsoft.Json.Linq;

namespace ProxyCore.Models
{
    /// <summary>
    /// xray 配置文件的顶层结构。为了兼容各协议差异巨大的 settings / streamSettings，
    /// 内部使用 JObject 承载，序列化时直接输出为 JSON。
    /// </summary>
    public class XrayConfig
    {
        public JObject Log { get; set; }
        public JArray Inbounds { get; set; }
        public JArray Outbounds { get; set; }
        public JObject Routing { get; set; }
        public JObject Dns { get; set; }

        public JObject ToJObject()
        {
            var root = new JObject();
            if (Log != null) root["log"] = Log;
            if (Inbounds != null) root["inbounds"] = Inbounds;
            if (Outbounds != null) root["outbounds"] = Outbounds;
            if (Routing != null) root["routing"] = Routing;
            if (Dns != null) root["dns"] = Dns;
            return root;
        }

        public string ToJson()
        {
            return ToJObject().ToString(Newtonsoft.Json.Formatting.Indented);
        }
    }
}
