namespace ProxyCore
{
    /// <summary>内核入站：一个 mixed 端口同时服务 SOCKS 与 HTTP，可选允许局域网访问。</summary>
    public sealed class InboundOptions
    {
        public int MixedPort { get; set; } = CoreConstants.MixedPort;
        public bool AllowLan { get; set; }

        public string ListenAddress => AllowLan ? "0.0.0.0" : "127.0.0.1";

        public static InboundOptions Default => new InboundOptions();
    }
}
