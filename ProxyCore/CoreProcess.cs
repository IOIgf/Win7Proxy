using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ProxyCore
{
    /// <summary>管理 xray 内核子进程：启动、日志回传、停止、端口健康检查、残留进程清理。</summary>
    public class CoreProcess : IDisposable
    {
        private Process _proc;
        private bool _disposed;
        private string _pidFile;

        public event Action<string> LogReceived;
        public event Action Exited;

        public int? Pid => _proc?.Id;

        public bool IsRunning => _proc != null && !_proc.HasExited;

        /// <summary>启动 xray： xray.exe run -c configPath。workDir 设为 core 目录。</summary>
        public void Start(string xrayPath, string configPath, string workDir, string pidFile = null)
        {
            if (!File.Exists(xrayPath))
                throw new ProxyCoreException("找不到内核文件：" + xrayPath);

            _pidFile = pidFile;

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
            _proc.Exited += (s, e) => { DeletePidFile(); Exited?.Invoke(); };

            if (!_proc.Start())
                throw new ProxyCoreException("启动内核进程失败。");
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();

            WritePidFile();
        }

        public void Stop()
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill();
                    _proc.WaitForExit(3000);
                }
            }
            catch { }
            DeletePidFile();
        }

        // ---------- PID 文件 / 残留进程 ----------

        private void WritePidFile()
        {
            if (string.IsNullOrEmpty(_pidFile) || _proc == null) return;
            try { File.WriteAllText(_pidFile, _proc.Id.ToString()); }
            catch { }
        }

        private void DeletePidFile()
        {
            if (string.IsNullOrEmpty(_pidFile)) return;
            try { if (File.Exists(_pidFile)) File.Delete(_pidFile); }
            catch { }
        }

        /// <summary>
        /// 清理上次异常退出（进程被强杀、系统崩溃）遗留的 xray 进程。
        /// PID 会复用，所以必须同时校验进程名确实是 xray 才杀，避免误伤别的程序。
        /// </summary>
        public static int KillOrphan(string pidFile)
        {
            if (string.IsNullOrEmpty(pidFile) || !File.Exists(pidFile)) return 0;
            int pid;
            try { pid = int.Parse(File.ReadAllText(pidFile).Trim()); }
            catch { return 0; }

            var killed = 0;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    var name = "";
                    try { name = p.ProcessName; } catch { }
                    if (name.Equals("xray", StringComparison.OrdinalIgnoreCase))
                    {
                        try { p.Kill(); p.WaitForExit(2000); killed = 1; } catch { }
                    }
                }
            }
            catch { /* 进程已经不在了 */ }

            try { File.Delete(pidFile); } catch { }
            return killed;
        }

        // ---------- 静态工具 ----------

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

        /// <summary>读取内核版本号（xray version 的第一行）。失败返回空串。</summary>
        public static string GetCoreVersion(string xrayPath, int timeoutMs = 5000)
        {
            if (string.IsNullOrEmpty(xrayPath) || !File.Exists(xrayPath)) return "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = xrayPath,
                    Arguments = "version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(xrayPath)
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    foreach (var line in (output ?? "").Split('\n'))
                    {
                        var t = line.Trim();
                        if (t != "") return t;
                    }
                    return "";
                }
            }
            catch { return ""; }
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
