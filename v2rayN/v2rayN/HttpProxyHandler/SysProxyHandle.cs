using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace v2rayN.HttpProxyHandler
{
    /// <summary>
    /// Directly manages the current user's Windows Internet (WinINet) proxy settings.
    /// This avoids depending on sysproxy.exe during application shutdown.
    /// </summary>
    public static class SysProxyHandle
    {
        private const string InternetSettingsPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private static RegistryKey OpenSettingsKey()
        {
            RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, true);
            if (key == null)
                key = Registry.CurrentUser.CreateSubKey(InternetSettingsPath);

            if (key == null)
                throw new InvalidOperationException("Cannot open the current user's Internet Settings registry key.");

            return key;
        }

        private static void NotifyInternetSettingsChanged()
        {
            // INTERNET_OPTION_SETTINGS_CHANGED = 39; INTERNET_OPTION_REFRESH = 37.
            bool changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            bool refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            if (!changed || !refreshed)
                Utils.SaveLog("[SysProxy] InternetSetOption notification failed. Win32 error: " + Marshal.GetLastWin32Error());
        }

        private static void ApplyGlobalProxy(string proxyServer, string bypassList)
        {
            if (String.IsNullOrEmpty(proxyServer))
                throw new ArgumentException("Proxy server address is empty.", "proxyServer");

            using (RegistryKey key = OpenSettingsKey())
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", proxyServer, RegistryValueKind.String);
                key.SetValue("ProxyOverride", bypassList ?? String.Empty, RegistryValueKind.String);
                key.DeleteValue("AutoConfigURL", false);
            }

            NotifyInternetSettingsChanged();
        }

        private static void ApplyPac(string pacUrl)
        {
            if (String.IsNullOrEmpty(pacUrl))
                throw new ArgumentException("PAC URL is empty.", "pacUrl");

            using (RegistryKey key = OpenSettingsKey())
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            }

            NotifyInternetSettingsChanged();
        }

        public static void SetIEProxy(bool enable, bool global, string strProxy)
        {
            if (!enable)
            {
                ResetIEProxy();
                return;
            }

            if (global)
                ApplyGlobalProxy(strProxy, Global.IEProxyExceptions);
            else
                ApplyPac(strProxy);
        }

        public static void SetIEProxy(bool global, string strProxy, string strExceptions)
        {
            string bypass = String.IsNullOrEmpty(strExceptions)
                ? Global.IEProxyExceptions
                : Global.IEProxyExceptions + ";" + strExceptions;

            if (global)
                ApplyGlobalProxy(strProxy, bypass);
            else
                ApplyPac(strProxy);
        }

        /// <summary>
        /// Disable and remove the current user's system proxy and PAC settings.
        /// </summary>
        public static bool ResetIEProxy()
        {
            try
            {
                using (RegistryKey key = OpenSettingsKey())
                {
                    key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    key.DeleteValue("ProxyServer", false);
                    key.DeleteValue("ProxyOverride", false);
                    key.DeleteValue("AutoConfigURL", false);
                }

                NotifyInternetSettingsChanged();
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog("[SysProxy] Reset system proxy failed: " + ex.Message, ex);
                return false;
            }
        }
    }
}
