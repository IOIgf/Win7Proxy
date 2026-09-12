using System.Collections.Generic;
using ProxyCore;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore.Parsers
{
    /// <summary>按格式自动选择解析器，并把解析结果写回 Subscription。</summary>
    public static class SubscriptionParserFactory
    {
        private static readonly ISubscriptionParser[] Parsers =
        {
            new V2rayNParser(),
            new ClashParser(),
            new Sip008Parser()
        };

        public static ISubscriptionParser Detect(string raw)
        {
            foreach (var p in Parsers)
                if (p.CanParse(raw)) return p;
            return null;
        }

        /// <summary>解析原始订阅文本为节点列表。format=Auto 时自动探测。</summary>
        public static List<Node> Parse(Subscription sub, string raw)
        {
            ISubscriptionParser parser = null;
            if (sub.Format == SubscriptionFormat.V2rayN) parser = new V2rayNParser();
            else if (sub.Format == SubscriptionFormat.Clash) parser = new ClashParser();
            else if (sub.Format == SubscriptionFormat.Sip008) parser = new Sip008Parser();
            else parser = Detect(raw);

            if (parser == null)
                throw new ProxyCoreException("无法识别订阅格式：既不是 v2rayN/Clash/SIP008。");

            var nodes = parser.Parse(raw);
            foreach (var n in nodes)
            {
                n.SourceName = sub.Name;
                // 记录归属订阅，重新拉取同一订阅时才能精确替换旧节点而不是无脑追加
                n.SubscriptionId = sub.Id ?? "";
            }
            return nodes;
        }
    }
}
