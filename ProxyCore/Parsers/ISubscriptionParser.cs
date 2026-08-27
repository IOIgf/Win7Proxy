using System.Collections.Generic;
using ProxyCore.Models;

namespace ProxyCore.Parsers
{
    /// <summary>订阅解析器统一接口。输入原始文本，输出归一化节点列表。</summary>
    public interface ISubscriptionParser
    {
        /// <summary>该解析器能否处理此原始文本。</summary>
        bool CanParse(string raw);

        /// <summary>解析原始文本为节点列表。解析失败应抛出 ProxyCoreException。</summary>
        List<Node> Parse(string raw);
    }
}
