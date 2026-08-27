using System;

namespace ProxyCore
{
    /// <summary>ProxyCore 统一异常。</summary>
    public class ProxyCoreException : Exception
    {
        public ProxyCoreException(string msg) : base(msg) { }
    }
}
