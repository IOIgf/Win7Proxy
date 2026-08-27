using System;
using System.Collections.Generic;

namespace ProxyCore.Models
{
    /// <summary>一条订阅源（由 URL 拉取并解析得到节点列表）。</summary>
    public class Subscription
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public List<Node> Nodes { get; set; } = new List<Node>();
        public DateTime Updated { get; set; } = DateTime.MinValue;

        /// <summary>订阅格式提示：Auto 表示自动探测。</summary>
        public SubscriptionFormat Format { get; set; } = SubscriptionFormat.Auto;
    }

    public enum SubscriptionFormat
    {
        Auto,
        V2rayN,
        Clash,
        Sip008
    }
}
