using System;
using System.Net;
using System.Net.Security;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore
{
    /// <summary>拉取订阅原始文本并解析为节点。下载启用 TLS1.2（Win7 兼容），不依赖 System.Net.Http。</summary>
    public static class SubscriptionFetcher
    {
        /// <summary>串行化「临时放宽证书校验」的窗口，避免并发拉取时互相影响。</summary>
        private static readonly object TlsLock = new object();

        static SubscriptionFetcher()
        {
            // Win7 默认未启用 TLS1.2，显式开启以保证 https 订阅可下载。
            // 只开 TLS1.2 —— TLS1.1 已被主流站点淘汰，不再启用。
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;
            // 注意：这里不再设置全局 ServerCertificateValidationCallback。
            // 旧版本为了"能拉到"而全局跳过 TLS 证书校验，等于把订阅内容和节点凭据
            // 暴露给任何能做中间人的网络。现在默认严格校验，只有用户在订阅上显式
            // 勾选「跳过证书校验」时，才对这一次请求临时放宽，用完立即还原。
        }

        /// <summary>下载订阅原始文本。allowInsecure=true 时对本次请求跳过证书校验。</summary>
        public static string FetchRaw(string url, bool allowInsecure = false)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ProxyCoreException("订阅地址为空。");

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new ProxyCoreException("订阅地址必须以 http:// 或 https:// 开头。");

            lock (TlsLock)
            {
                RemoteCertificateValidationCallback previous = null;
                if (allowInsecure)
                {
                    previous = ServicePointManager.ServerCertificateValidationCallback;
                    ServicePointManager.ServerCertificateValidationCallback = (s, cert, chain, err) => true;
                }
                try
                {
                    using (var wc = new TimeoutWebClient(30000))
                    {
                        wc.Headers["User-Agent"] = "Win7Proxy/1.1";
                        return wc.DownloadString(url);
                    }
                }
                catch (Exception ex)
                {
                    var hint = (ex is WebException && !allowInsecure)
                        ? "（若该站点使用自签名证书，可在导入时勾选「跳过证书校验」）"
                        : "";
                    throw new ProxyCoreException("订阅下载失败：" + ex.Message + hint);
                }
                finally
                {
                    if (allowInsecure)
                        ServicePointManager.ServerCertificateValidationCallback = previous;
                }
            }
        }

        /// <summary>下载并解析订阅，写回 Subscription.Nodes。</summary>
        public static Subscription Fetch(Subscription sub)
        {
            var raw = FetchRaw(sub.Url, sub.AllowInsecureTls);
            var nodes = SubscriptionParserFactory.Parse(sub, raw);
            if (nodes == null || nodes.Count == 0)
                throw new ProxyCoreException("订阅内容里没有解析出任何节点，请检查订阅地址或格式。");
            sub.Nodes = nodes;
            sub.Updated = DateTime.Now;
            return sub;
        }

        // WebClient 在 net461 下没有 Timeout 属性，通过重写 GetWebRequest 实现超时控制
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
