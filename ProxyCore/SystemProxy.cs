using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 设置/清除 Windows 系统代理（HKCU Internet Settings）并立即生效。
    /// 全局与规则模式都把系统代理指向本机 Xray 端口，分流（国内/私有网段直连、其余走代理）
    /// 由 Xray 的 routing 规则完成。另提供开机自启（HKCU Run）读写。
    /// 仅在 Windows 运行时生效；非 Windows 平台调用为 no-op（便于跨平台编译）。
    /// </summary>
    public static class SystemProxy
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Win7Proxy";

        /// <summary>不走系统代理的地址（本机与常见私有网段）。</summary>
        private const string BypassList =
            "<local>;localhost;127.*;10.*;192.168.*;" +
            "172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;" +
            "172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;" +
            "172.28.*;172.29.*;172.30.*;172.31.*";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public static void SetProxy(ProxyMode mode)
        {
            if (!IsWindows()) return;

            using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
            {
                if (key == null) return;
                if (mode == ProxyMode.Direct)
                {
                    Clear(key);
                    return;
                }
                // 分流（国内/私有网段直连、其余走代理）由 Xray 的 routing 规则完成，
                // 不再依赖 file:// 的 PAC 文件——部分浏览器（Chrome/Edge）在 Win7 上
                // 会拒绝加载 file:// PAC，导致"启动了却不走代理"。
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer",
                    $"http=127.0.0.1:{CoreConstants.HttpPort};socks=127.0.0.1:{CoreConstants.SocksPort}",
                    RegistryValueKind.String);
                key.SetValue("ProxyOverride", BypassList, RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", false);
            }
            Apply();
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
                    var v = key.GetValue("ProxyEnable");
                    return v != null && Convert.ToInt32(v) == 1;
                }
            }
            catch { return false; }
        }

        public static void Clear()
        {
            if (!IsWindows()) return;
            using (var key = Registry.CurrentUser.OpenSubKey(RegPath, true))
            {
                if (key == null) return;
                Clear(key);
            }
            Apply();
        }

        private static void Clear(RegistryKey key)
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", false);
            key.DeleteValue("ProxyServer", false);
            key.DeleteValue("ProxyOverride", false);
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
