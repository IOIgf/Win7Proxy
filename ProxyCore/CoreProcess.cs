using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ProxyCore
{
    /// <summary>管理 xray 内核子进程：启动、日志回传、停止、端口健康检查。</summary>
    public class CoreProcess : IDisposable
    {
        private Process _proc;
        private bool _disposed;

        public event Action<string> LogReceived;
        public event Action Exited;

        public int? Pid => _proc?.Id;

        public bool IsRunning => _proc != null && !_proc.HasExited;

        /// <summary>启动 xray： xray.exe run -c configPath。workDir 设为 core 目录所在父目录。</summary>
        public void Start(string xrayPath, string configPath, string workDir)
        {
            if (!File.Exists(xrayPath))
                throw new ProxyCoreException("找不到内核文件：" + xrayPath);

            var psi = new ProcessStartInfo
            {
                FileName = xrayPath,
                Arguments = "run -c \"" + configPath + "\"",
                WorkingDirectory = workDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (s, e) => { if (e.Data != null) LogReceived?.Invoke(e.Data); };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) LogReceived?.Invoke(e.Data); };
            _proc.Exited += (s, e) => Exited?.Invoke();

            if (!_proc.Start())
                throw new ProxyCoreException("启动内核进程失败。");
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public void Stop()
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    // 先尝试优雅退出
                    _proc.Kill();
                    if (!_proc.WaitForExit(3000))
                    {
                        try { _proc.Kill(); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>TCP 探测本机端口是否处于监听状态（健康检查）。</summary>
        public static bool IsPortListening(int port, int timeoutMs = 1500)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                        return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            try { _proc?.Dispose(); } catch { }
        }
    }
}
