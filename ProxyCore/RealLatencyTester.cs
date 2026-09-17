using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 真实延迟测试：临时拉起一个内核，用被测节点做一次真实的 HTTP 请求并计时。
    /// 与「TCP 连通性测延迟」不同，它对 Hysteria2 这类 UDP/QUIC 协议同样有效——
    /// 因为 hy2 根本不在节点端口上监听 TCP，用 TCP 去连只会得到假的「超时」。
    /// </summary>
    public static class RealLatencyTester
    {
        /// <summary>测速目标：返回 204 的轻量端点，只关心能否通过节点完成一次请求。</summary>
        public const string TestUrl = "http://www.gstatic.com/generate_204";

        /// <summary>
        /// 用指定内核 + 节点做一次真实请求，返回毫秒；失败返回 -1。
        /// coreDir 需包含内核可执行文件；临时配置/日志写在系统临时目录，不污染 core 目录。
        /// </summary>
        public static int Test(CoreKind kind, Node node, string coreDir, int timeoutMs, Action<string> log)
        {
            if (log == null) log = s => { };
            if (node == null) return -1;

            var spec = CoreRegistry.Of(kind);
            var exe = Path.Combine(coreDir, spec.ExeName);
            if (!File.Exists(exe))
            {
                log("找不到内核 " + spec.ExeName + "，无法做真实延迟测试。");
                return -1;
            }

            var unsupported = spec.ReasonUnsupported(node);
            if (unsupported != null)
            {
                log(unsupported);
                return -1;
            }

            var tmp = Path.Combine(Path.GetTempPath(), "w7p-lat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var cfgPath = Path.Combine(tmp, "config.json");
            int port = FreeTcpPort();

            try
            {
                // Global 模式：不依赖 geo 数据，且所有流量都走被测节点
                var json = CoreConfigFactory.BuildJson(kind, node, ProxyMode.Global, coreDir);
                File.WriteAllText(cfgPath, RetargetHttpInbound(json, kind, port));

                using (var core = new CoreProcess())
                {
                    core.LogReceived += log;
                    core.Start(exe, cfgPath, tmp);

                    bool up = false;
                    for (int i = 0; i < 50; i++)
                    {
                        if (!core.IsRunning) break;
                        if (CoreProcess.IsPortListening(port, 150)) { up = true; break; }
                        Thread.Sleep(300);
                    }
                    if (!up)
                    {
                        log("内核未在 15 秒内就绪（节点不可用，或内核版本过旧不支持该协议）。");
                        return -1;
                    }

                    return RequestThroughProxy(port, timeoutMs, log);
                }
            }
            catch (Exception ex)
            {
                log("真实延迟测试出错：" + ex.Message);
                return -1;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        private static int RequestThroughProxy(int port, int timeoutMs, Action<string> log)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(TestUrl);
                req.Proxy = new WebProxy("http://127.0.0.1:" + port);
                req.Method = "GET";
                req.KeepAlive = false;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    // 只关心能否通过节点完成一次请求，不校验响应体
                }
                sw.Stop();
                return (int)sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                log("请求未成功：" + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// 真实测速只需要一个 HTTP 入站；顺便把它改到临时空闲端口，
        /// 避免与正在运行的主代理（10808/10809）冲突。
        /// </summary>
        public static string RetargetHttpInbound(string json, CoreKind kind, int port)
        {
            var root = JObject.Parse(json);
            var inbounds = root["inbounds"] as JArray;
            if (inbounds == null) throw new ProxyCoreException("内核配置里没有入站。");

            var keep = new JArray();
            foreach (var token in inbounds)
            {
                var o = token as JObject;
                if (o == null) continue;
                var isHttp = kind == CoreKind.Singbox
                    ? string.Equals((string)o["type"], "http", StringComparison.OrdinalIgnoreCase)
                    : string.Equals((string)o["protocol"], "http", StringComparison.OrdinalIgnoreCase);
                if (!isHttp) continue;

                if (kind == CoreKind.Singbox) o["listen_port"] = port;
                else o["port"] = port;
                keep.Add(o);
            }

            if (keep.Count == 0) throw new ProxyCoreException("内核配置里没有 HTTP 入站，无法做真实延迟测试。");
            root["inbounds"] = keep;
            return root.ToString(Formatting.Indented);
        }

        private static int FreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
