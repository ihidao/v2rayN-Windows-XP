using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace v2rayN.HttpProxyHandler
{
    public static class SysProxyHandle
    {
        private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private static readonly string[] ProxyValueNames = new string[]
        {
            "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL", "AutoDetect"
        };

        private class SavedValue
        {
            public bool Exists;
            public object Value;
            public RegistryValueKind Kind;
        }

        private static Dictionary<string, SavedValue> _originalValues;
        private static bool _settingsCaptured;

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private static void CaptureOriginalSettings()
        {
            if (_settingsCaptured) return;

            Dictionary<string, SavedValue> values = new Dictionary<string, SavedValue>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, false))
            {
                if (key != null)
                {
                    foreach (string name in ProxyValueNames)
                    {
                        SavedValue saved = new SavedValue();
                        try
                        {
                            saved.Value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            saved.Exists = saved.Value != null;
                            if (saved.Exists)
                                saved.Kind = key.GetValueKind(name);
                        }
                        catch { saved.Exists = false; }
                        values[name] = saved;
                    }
                }
            }
            _originalValues = values;
            _settingsCaptured = true;
        }

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
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }

        private static void ApplyGlobalProxy(string proxyServer, string bypassList)
        {
            if (String.IsNullOrEmpty(proxyServer))
                throw new ArgumentException("Proxy server address is empty.", "proxyServer");

            CaptureOriginalSettings();
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

            CaptureOriginalSettings();
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
                RestoreOriginalSettings();
                return;
            }

            if (global)
                ApplyGlobalProxy(strProxy, Global.IEProxyExceptions);
            else
                ApplyPac(strProxy);
        }

        public static void SetIEProxy(bool global, string strProxy, string strExceptions)
        {
            if (global)
            {
                string bypass = String.IsNullOrEmpty(strExceptions)
                    ? Global.IEProxyExceptions
                    : Global.IEProxyExceptions + ";" + strExceptions;
                ApplyGlobalProxy(strProxy, bypass);
            }
            else
            {
                ApplyPac(strProxy);
            }
        }

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
                Utils.SaveLog("Reset system proxy failed: " + ex.Message, ex);
                return false;
            }
        }

        private static void RestoreOriginalSettings()
        {
            try
            {
                if (!_settingsCaptured || _originalValues == null)
                {
                    ResetIEProxy();
                    return;
                }

                using (RegistryKey key = OpenSettingsKey())
                {
                    foreach (string name in ProxyValueNames)
                    {
                        SavedValue saved;
                        if (!_originalValues.TryGetValue(name, out saved) || saved == null || !saved.Exists)
                            key.DeleteValue(name, false);
                        else
                            key.SetValue(name, saved.Value, saved.Kind);
                    }
                }
                _settingsCaptured = false;
                _originalValues = null;
                NotifyInternetSettingsChanged();
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Restore system proxy failed: " + ex.Message, ex);
                throw;
            }
        }
    }
}