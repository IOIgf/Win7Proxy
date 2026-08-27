using System;
using System.Net;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore
{
    /// <summary>拉取订阅原始文本并解析为节点。下载启用 TLS1.2（Win7 兼容），不依赖 System.Net.Http。</summary>
    public static class SubscriptionFetcher
    {
        static SubscriptionFetcher()
        {
            // Win7 默认可能未启用 TLS1.2，显式开启以保证 https 订阅可下载
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, err) => true;
        }

        /// <summary>下载订阅原始文本。</summary>
        public static string FetchRaw(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ProxyCoreException("订阅地址为空。");
            try
            {
                using (var wc = new TimeoutWebClient(30000))
                {
                    wc.Headers["User-Agent"] = "Win7Proxy/1.0";
                    return wc.DownloadString(url);
                }
            }
            catch (Exception ex)
            {
                throw new ProxyCoreException("订阅下载失败：" + ex.Message);
            }
        }

        /// <summary>下载并解析订阅，写回 Subscription.Nodes。</summary>
        public static Subscription Fetch(Subscription sub)
        {
            var raw = FetchRaw(sub.Url);
            var nodes = SubscriptionParserFactory.Parse(sub, raw);
            sub.Nodes = nodes;
            sub.Updated = DateTime.Now;
            return sub;
        }

        // WebClient 在 net48 下没有 Timeout 属性，通过重写 GetWebRequest 实现超时控制
        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;
            public TimeoutWebClient(int timeoutMs) { _timeoutMs = timeoutMs; }
            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = _timeoutMs;
                return req;
            }
        }
    }
}
