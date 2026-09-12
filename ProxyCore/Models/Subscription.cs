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

        /// <summary>
        /// 是否允许该订阅使用无效/自签名 TLS 证书。
        /// 默认 false —— 证书校验是全局默认行为，只有用户显式勾选才对本次请求放宽。
        /// </summary>
        public bool AllowInsecureTls { get; set; } = false;

        /// <summary>唯一标识，用于「重新导入同一订阅时替换旧节点」而不是追加。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
    }

    public enum SubscriptionFormat
    {
        Auto,
        V2rayN,
        Clash,
        Sip008
    }
}
