using System;
using System.Net;
using System.Text;
using ProxyCore.Models;
using ProxyCore.Parsers;

namespace ProxyCore
{
    /// <summary>拉取订阅原始文本并解析为节点。下载启用 TLS1.2（Win7 兼容），不依赖 System.Net.Http。</summary>
    public static class SubscriptionFetcher
    {
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
            return Download(url, allowInsecure).Raw;
        }

        private sealed class DownloadResult
        {
            public string Raw;
            public string SuggestedName;
        }

        private static DownloadResult Download(string url, bool allowInsecure)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ProxyCoreException("订阅地址为空。");

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new ProxyCoreException("订阅地址必须以 http:// 或 https:// 开头。");

            try
            {
                using (var wc = new TimeoutWebClient(30000, allowInsecure))
                {
                    wc.Headers["User-Agent"] = "Win7Proxy/1.4";
                    var raw = wc.DownloadString(url);
                    return new DownloadResult { Raw = raw, SuggestedName = SuggestName(wc.ResponseHeaders, url) };
                }
            }
            catch (Exception ex)
            {
                var hint = (ex is WebException && !allowInsecure)
                    ? "（若该站点使用自签名证书，可在导入时勾选「跳过证书校验」）"
                    : "";
                throw new ProxyCoreException("订阅下载失败：" + ex.Message + hint);
            }
        }

        /// <summary>下载并解析订阅，写回 Subscription.Nodes；订阅名为空时用响应头/URL 推断。</summary>
        public static Subscription Fetch(Subscription sub)
        {
            var result = Download(sub.Url, sub.AllowInsecureTls);
            if (string.IsNullOrWhiteSpace(sub.Name))
                sub.Name = string.IsNullOrWhiteSpace(result.SuggestedName) ? "订阅" : result.SuggestedName;
            var nodes = SubscriptionParserFactory.Parse(sub, result.Raw);
            if (nodes == null || nodes.Count == 0)
                throw new ProxyCoreException("订阅内容里没有解析出任何节点，请检查订阅地址或格式。");
            sub.Nodes = nodes;
            sub.Updated = DateTime.Now;
            return sub;
        }

        // 订阅站点常用 profile-title（Clash 系，可能带 base64: 前缀）与
        // Content-Disposition 文件名来标注机场名，取不到再退回 URL 主机名。
        private static string SuggestName(WebHeaderCollection headers, string url)
        {
            try
            {
                if (headers != null)
                {
                    var title = DecodeProfileTitle(headers["profile-title"]);
                    if (!string.IsNullOrWhiteSpace(title)) return title.Trim();

                    var filename = ExtractFilename(headers["Content-Disposition"]);
                    if (!string.IsNullOrWhiteSpace(filename)) return filename.Trim();
                }
            }
            catch { }
            return NameFromUrl(url);
        }

        private static string DecodeProfileTitle(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                try { return Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring("base64:".Length).Trim())); }
                catch { return ""; }
            }
            return value;
        }

        private static string ExtractFilename(string disposition)
        {
            if (string.IsNullOrWhiteSpace(disposition)) return "";
            foreach (var raw in disposition.Split(';'))
            {
                var part = raw.Trim();
                if (part.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = part.Substring("filename*=".Length).Trim().Trim('"');
                    var idx = v.IndexOf("''", StringComparison.Ordinal);
                    if (idx >= 0) v = v.Substring(idx + 2);
                    try { return Uri.UnescapeDataString(v); } catch { return v; }
                }
                if (part.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                    return part.Substring("filename=".Length).Trim().Trim('"');
            }
            return "";
        }

        public static string NameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            try
            {
                var host = new Uri(url).Host;
                return string.IsNullOrWhiteSpace(host) ? "" : host;
            }
            catch { return ""; }
        }

        // WebClient 在 net461 下没有 Timeout 属性，通过重写 GetWebRequest 实现超时控制
        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;
            private readonly bool _allowInsecure;

            public TimeoutWebClient(int timeoutMs, bool allowInsecure)
            {
                _timeoutMs = timeoutMs;
                _allowInsecure = allowInsecure;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = _timeoutMs;
                var http = req as HttpWebRequest;
                if (http != null)
                {
                    http.ReadWriteTimeout = _timeoutMs;
                    // 逐请求放宽证书校验，不影响进程里的内核下载等其他 HTTPS 请求。
                    if (_allowInsecure)
                        http.ServerCertificateValidationCallback = (s, cert, chain, errors) => true;
                }
                return req;
            }
        }
    }
}
