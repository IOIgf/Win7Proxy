using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ProxyCore.Models;

namespace ProxyCore
{
    /// <summary>
    /// 设置/清除 Windows 系统代理（HKCU Internet Settings）并立即生效。
    /// 规则模式使用 PAC 文件（file:// 指向），全局模式直接设置代理服务器地址。
    /// 仅在 Windows 运行时生效；非 Windows 平台调用为 no-op（便于跨平台编译）。
    /// </summary>
    public static class SystemProxy
    {
        private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public static void SetProxy(ProxyMode mode, string pacFilePath)
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
                // 全局与规则模式都直接把系统代理指向本机 Xray 端口；
                // 分流（国内/私有网段直连、其余走代理）由 Xray 的 routing 规则完成。
                // 不再依赖 file:// 的 PAC 文件——部分浏览器（Chrome/Edge）在 Win7 上会拒绝加载 file:// PAC，导致“启动了却不走代理”。
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer",
                    $"http=127.0.0.1:{CoreConstants.HttpPort};socks=127.0.0.1:{CoreConstants.SocksPort}",
                    RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", false);
            }
            Apply();
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
        }

        private static void Apply()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT ||
                   Environment.OSVersion.Platform == PlatformID.Win32Windows;
        }
    }
}
