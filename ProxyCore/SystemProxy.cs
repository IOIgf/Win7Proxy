using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 接管/恢复 Windows 系统代理（HKCU Internet Settings）并立即生效。
    /// 全局与规则模式都把系统代理指向本机内核端口，分流（国内/私有网段直连、其余走代理）
    /// 由所选内核的 routing 规则完成。另提供开机自启（HKCU Run）读写。
    /// 仅在 Windows 运行时生效；非 Windows 平台调用为 no-op（便于跨平台编译）。
    /// </summary>
    public static class SystemProxy
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Win7Proxy";
        private const string BackupPath = @"Software\Win7Proxy\SystemProxyBackup";
        private const string BackupMarker = "Captured";
        private static readonly object ProxyLock = new object();

        /// <summary>不走系统代理的地址（本机与常见私有网段）。</summary>
        private const string BypassList =
            "<local>;localhost;127.*;10.*;192.168.*;" +
            "172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;" +
            "172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;" +
            "172.28.*;172.29.*;172.30.*;172.31.*";

        /// <summary>本地 mixed 端口（HTTP 与 SOCKS 共用）。启动/接管前由调用方设置。</summary>
        public static int MixedPort { get; set; } = CoreConstants.MixedPort;

        private const string OwnedServerValue = "OwnedServer";

        private static string OwnedProxyServer =>
            $"http=127.0.0.1:{MixedPort};socks=127.0.0.1:{MixedPort}";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public static void SetProxy(ProxyMode mode)
        {
            if (!IsWindows()) return;

            if (mode == ProxyMode.Direct)
            {
                Restore();
                return;
            }

            lock (ProxyLock)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                    {
                        if (key == null)
                            throw new ProxyCoreException("无法打开 Windows 系统代理设置。");

                        CaptureOriginalSettings(key);
                        RecordOwnedServer();

                        // 分流（国内/私有网段直连、其余走代理）由内核 routing 规则完成，
                        // 不依赖 file:// PAC，兼容 Win7 上的 Chrome/Edge。
                        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                        key.SetValue("ProxyServer", OwnedProxyServer, RegistryValueKind.String);
                        key.SetValue("ProxyOverride", BypassList, RegistryValueKind.String);
                        key.DeleteValue("AutoConfigURL", false);
                    }
                    Apply();
                }
                catch (ProxyCoreException)
                {
                    RestoreCapturedSettings(false);
                    throw;
                }
                catch (Exception ex)
                {
                    RestoreCapturedSettings(false);
                    throw new ProxyCoreException("设置 Windows 系统代理失败：" + ex.Message);
                }
            }
        }

        /// <summary>系统代理当前是否被本程序接管。</summary>
        public static bool IsEnabled()
        {
            if (!IsWindows()) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegPath, false))
                {
                    if (key == null) return false;
                    return IsOwnedSettings(key);
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// 恢复接管前的系统代理。备份持久化在 HKCU，程序崩溃后下次启动仍可恢复。
        /// 如果代理已被其他程序改动，只丢弃旧备份，不覆盖用户的新设置。
        /// </summary>
        public static bool Restore()
        {
            return RestoreCapturedSettings(true);
        }

        private static bool RestoreCapturedSettings(bool requireOwnedSettings)
        {
            if (!IsWindows()) return false;
            lock (ProxyLock)
            {
                try
                {
                    var restored = false;
                    using (var backup = Registry.CurrentUser.OpenSubKey(BackupPath, false))
                    {
                        if (backup == null || Convert.ToInt32(backup.GetValue(BackupMarker, 0)) != 1)
                            return false;

                        using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
                        {
                            if (key == null) return false;
                            if (!requireOwnedSettings || IsOwnedSettings(key))
                            {
                                RestoreValue(key, backup, "ProxyEnable");
                                RestoreValue(key, backup, "ProxyServer");
                                RestoreValue(key, backup, "ProxyOverride");
                                RestoreValue(key, backup, "AutoConfigURL");
                                restored = true;
                            }
                        }
                    }
                    DeleteBackup();
                    if (restored) Apply();
                    return restored;
                }
                catch
                {
                    DeleteBackup();
                    return false;
                }
            }
        }

        /// <summary>兼容旧调用；现在的语义是安全恢复，而不是无条件删除用户配置。</summary>
        public static void Clear()
        {
            Restore();
        }

        private static void CaptureOriginalSettings(RegistryKey key)
        {
            try
            {
                using (var existing = Registry.CurrentUser.OpenSubKey(BackupPath, false))
                    if (existing != null && Convert.ToInt32(existing.GetValue(BackupMarker, 0)) == 1) return;
            }
            catch { DeleteBackup(); }

            try
            {
                using (var backup = Registry.CurrentUser.CreateSubKey(BackupPath))
                {
                    if (backup == null) throw new InvalidOperationException("无法创建代理设置备份。");
                    CaptureValue(key, backup, "ProxyEnable");
                    CaptureValue(key, backup, "ProxyServer");
                    CaptureValue(key, backup, "ProxyOverride");
                    CaptureValue(key, backup, "AutoConfigURL");
                    // 最后写标记，避免把未完成的备份当作有效快照。
                    backup.SetValue(BackupMarker, 1, RegistryValueKind.DWord);
                }
            }
            catch
            {
                DeleteBackup();
                throw;
            }
        }

        private static void CaptureValue(RegistryKey source, RegistryKey backup, string name)
        {
            var value = source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null)
            {
                backup.SetValue(name + ".Exists", 0, RegistryValueKind.DWord);
                return;
            }
            backup.SetValue(name + ".Exists", 1, RegistryValueKind.DWord);
            backup.SetValue(name + ".Value", value, source.GetValueKind(name));
        }

        private static void RestoreValue(RegistryKey target, RegistryKey backup, string name)
        {
            if (Convert.ToInt32(backup.GetValue(name + ".Exists", 0)) == 0)
            {
                target.DeleteValue(name, false);
                return;
            }
            var value = backup.GetValue(name + ".Value", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null) target.SetValue(name, value, backup.GetValueKind(name + ".Value"));
        }

        private static bool IsOwnedSettings(RegistryKey key)
        {
            try
            {
                var enabled = key.GetValue("ProxyEnable");
                var server = key.GetValue("ProxyServer") as string;
                if (enabled == null || Convert.ToInt32(enabled) != 1) return false;
                // 端口可能在两次运行之间被改过，优先拿上次实际写入的值比对，
                // 否则会把"我们写的旧端口"误判成"用户自己改的"而拒绝恢复。
                var owned = ReadOwnedServer();
                if (string.IsNullOrEmpty(owned)) owned = OwnedProxyServer;
                return string.Equals(server, owned, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void RecordOwnedServer()
        {
            try
            {
                using (var backup = Registry.CurrentUser.CreateSubKey(BackupPath))
                {
                    if (backup != null)
                        backup.SetValue(OwnedServerValue, OwnedProxyServer, RegistryValueKind.String);
                }
            }
            catch { }
        }

        private static string ReadOwnedServer()
        {
            try
            {
                using (var backup = Registry.CurrentUser.OpenSubKey(BackupPath, false))
                    return backup == null ? null : backup.GetValue(OwnedServerValue) as string;
            }
            catch { return null; }
        }

        private static void DeleteBackup()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(BackupPath, false); }
            catch { }
        }

        private static void Apply()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }

        // ---------- 开机自启 ----------

        public static bool GetAutoStart()
        {
            if (!IsWindows()) return false;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, false))
                {
                    if (key == null) return false;
                    return key.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>开启/关闭开机自启。exePath 为空时只做关闭。</summary>
        public static void SetAutoStart(bool enable, string exePath)
        {
            if (!IsWindows()) return;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunPath, true))
                {
                    if (key == null) return;
                    if (enable && !string.IsNullOrEmpty(exePath))
                        key.SetValue(RunValue, "\"" + exePath + "\"", RegistryValueKind.String);
                    else
                        key.DeleteValue(RunValue, false);
                }
            }
            catch { }
        }

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ||
                   Environment.OSVersion.Platform == PlatformID.Win32Windows;
        }
    }
}
