using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 生成 proxy.pac 内容。Rule 模式下匹配直连域名列表返回 DIRECT，否则走本机 HTTP 代理。
    /// 直连域名可从 Rules/pac-rules.txt 加载（每行一个，支持 "DOMAIN-SUFFIX,xxx" 或纯域名），
    /// 文件不存在时使用内置常用国内域名种子。
    /// </summary>
    public static class PacGenerator
    {
        private static readonly string[] BuiltinDirectDomains =
        {
            "cn","qq.com","baidu.com","taobao.com","tmall.com","jd.com","sina.com.cn","weibo.com",
            "163.com","126.net","sohu.com","360.cn","tencent.com","aliyun.com","alipay.com",
            "www.gov.cn","bilibili.com","youku.com","iqiyi.com","douyin.com","zhihu.com","csdn.net",
            "oschina.net","cnbeta.com","hao123.com","sogou.com","sm.cn","weather.com.cn","w3.org",
            "chinaz.com","ip.cn","ipip.net","apnic.net","cnnic.cn","mi.com","xiaomi.com","meituan.com",
            "dianping.com","ctrip.com","qunar.com","12306.cn","gitcode.net","gitee.com"
        };

        public static string Build(ProxyMode mode, int pacPort, string rulesFilePath)
        {
            if (mode == ProxyMode.Direct)
                return Wrap("DIRECT");

            if (mode == ProxyMode.Global)
                return Wrap($"PROXY 127.0.0.1:{CoreConstants.MixedPort}");

            var domains = LoadDirectDomains(rulesFilePath);
            var list = string.Join(",", domains.Select(d => "\"" + d.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
            var proxy = $"PROXY 127.0.0.1:{CoreConstants.MixedPort}";
            var js = string.Format(PacTemplate, list, proxy);
            return js;
        }

        private static string Wrap(string action)
        {
            return string.Format(PacTemplate, "[]", action);
        }

        private static List<string> LoadDirectDomains(string rulesFilePath)
        {
            var set = new HashSet<string>(BuiltinDirectDomains, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(rulesFilePath) && File.Exists(rulesFilePath))
            {
                foreach (var raw in File.ReadAllLines(rulesFilePath))
                {
                    var line = raw.Trim();
                    if (line == "" || line.StartsWith("#")) continue;
                    if (line.StartsWith("DOMAIN-SUFFIX,", StringComparison.OrdinalIgnoreCase))
                        line = line.Substring("DOMAIN-SUFFIX,".Length).Trim();
                    else if (line.StartsWith("DOMAIN,", StringComparison.OrdinalIgnoreCase))
                        line = line.Substring("DOMAIN,".Length).Trim();
                    if (line != "") set.Add(line.TrimStart('.'));
                }
            }
            return set.ToList();
        }

        private const string PacTemplate = @"function FindProxyForURL(url, host) {{
    if (host === '' || host === null) return 'DIRECT';
    if (isPlainHostName(host) || host.indexOf('localhost') === 0 ||
        host.indexOf('127.') === 0 || host.indexOf('[::') === 0 ||
        /^10\./.test(host) || /^192\.168\./.test(host) ||
        /^172\.(1[6-9]|2[0-9]|3[01])\./.test(host)) {{
        return 'DIRECT';
    }}
    var directDomains = [{0}];
    for (var i = 0; i < directDomains.length; i++) {{
        if (host === directDomains[i] || host.endsWith('.' + directDomains[i])) return 'DIRECT';
    }}
    return '{1}';
}}";
    }
}
