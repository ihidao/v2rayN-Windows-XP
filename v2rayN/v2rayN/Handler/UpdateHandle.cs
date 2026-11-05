using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    class UpdateHandle
    {
        Action<bool, string> _updateFunc;
        private Config _config;

        public event EventHandler<ResultEventArgs> AbsoluteCompleted;

        public class ResultEventArgs : EventArgs
        {
            public bool Success;
            public string Msg;

            public ResultEventArgs(bool success, string msg)
            {
                this.Success = success;
                this.Msg = msg;
            }
        }

        private readonly string nLatestUrl = Global.NUrl + "/latest";
        private const string nUrl = Global.NUrl + "/download/{0}/v2rayN.zip";
        private readonly string v2flyCoreLatestUrl = Global.v2flyCoreUrl + "/latest";
        private const string v2flyCoreUrl = Global.v2flyCoreUrl + "/download/{0}/v2ray-windows-{1}.zip";
        private readonly string xrayCoreLatestUrl = Global.xrayCoreUrl + "/latest";
        private const string xrayCoreUrl = Global.xrayCoreUrl + "/download/{0}/Xray-windows-{1}.zip";
        private const string geoUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/{0}.dat";

        public void CheckUpdateGuiN(Config config, Action<bool, string> update)
        {
            _config = config;
            _updateFunc = update;

            DownloadHandle downloadHandle = null;
            if (downloadHandle == null)
            {
                downloadHandle = new DownloadHandle();

                downloadHandle.UpdateCompleted += (sender2, args) =>
                {
                    if (args.Success)
                    {
                        _updateFunc(false, UIRes.I18N("MsgDownloadV2rayCoreSuccessfully"));

                        try
                        {
                            string fileName = Utils.GetPath(Global.DownloadFileName);
                            Process process = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "v2rayUpgrade.exe",
                                    Arguments = "\"" + fileName + "\"",
                                    WorkingDirectory = Utils.StartupPath()
                                }
                            };
                            process.Start();
                            if (process.Id > 0)
                            {
                                _updateFunc(true, "");
                            }
                        }
                        catch (Exception ex)
                        {
                            _updateFunc(false, ex.Message);
                        }
                    }
                    else
                    {
                        _updateFunc(false, args.Msg);
                    }
                };
                downloadHandle.Error += (sender2, args) =>
                {
                    _updateFunc(false, args.GetException().Message);
                };
            }
            AbsoluteCompleted += (sender2, args) =>
            {
                if (args.Success)
                {
                    _updateFunc(false, string.Format(UIRes.I18N("MsgParsingSuccessfully"), "v2rayN"));

                    string url = args.Msg;
                    askToDownload(downloadHandle, url);
                }
                else
                {
                    _updateFunc(false, args.Msg);
                }
            };
            _updateFunc(false, string.Format(UIRes.I18N("MsgStartUpdating"), "v2rayN"));
            CheckUpdateAsync("v2rayN");
        }


        public void CheckUpdateCore(string type, Config config, Action<bool, string> update)
        {
            _config = config;
            _updateFunc = update;

            DownloadHandle downloadHandle = null;
            if (downloadHandle == null)
            {
                downloadHandle = new DownloadHandle();
                downloadHandle.UpdateCompleted += (sender2, args) =>
                {
                    if (args.Success)
                    {
                        _updateFunc(false, UIRes.I18N("MsgDownloadV2rayCoreSuccessfully"));
                        _updateFunc(false, UIRes.I18N("MsgUnpacking"));

                        try
                        {
                            _updateFunc(true, "");
                        }
                        catch (Exception ex)
                        {
                            _updateFunc(false, ex.Message);
                        }
                    }
                    else
                    {
                        _updateFunc(false, args.Msg);
                    }
                };
                downloadHandle.Error += (sender2, args) =>
                {
                    _updateFunc(true, args.GetException().Message);
                };
            }

            AbsoluteCompleted += (sender2, args) =>
            {
                if (args.Success)
                {
                    _updateFunc(false, string.Format(UIRes.I18N("MsgParsingSuccessfully"), "Core"));
                    string url = args.Msg;
                    askToDownload(downloadHandle, url);
                }
                else
                {
                    _updateFunc(false, args.Msg);
                }
            };
            _updateFunc(false, string.Format(UIRes.I18N("MsgStartUpdating"), "Core"));
            CheckUpdateAsync(type);
        }


        public void UpdateSubscriptionProcess(Config config, Action<bool, string> update)
        {
            _config = config;
            _updateFunc = update;

            _updateFunc(false, UIRes.I18N("MsgUpdateSubscriptionStart"));

            if (config.subItem == null || config.subItem.Count <= 0)
            {
                _updateFunc(false, UIRes.I18N("MsgNoValidSubscription"));
                return;
            }

            for (int k = 1; k <= config.subItem.Count; k++)
            {
                string id = config.subItem[k - 1].id.Trim();
                string url = config.subItem[k - 1].url.Trim();
                string hashCode = $"{k}->";
                if (config.subItem[k - 1].enabled == false)
                {
                    continue;
                }
                if (Utils.IsNullOrEmpty(id) || Utils.IsNullOrEmpty(url))
                {
                    _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgNoValidSubscription")}");
                    continue;
                }

                DownloadHandle downloadHandle3 = new DownloadHandle();
                downloadHandle3.UpdateCompleted += (sender2, args) =>
                {
                    if (args.Success)
                    {
                        _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgGetSubscriptionSuccessfully")}");
                        string result = Utils.Base64Decode(args.Msg);
                        if (Utils.IsNullOrEmpty(result))
                        {
                            string raw = args.Msg == null ? string.Empty : args.Msg.Trim();
                            if (raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
                                || raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
                                || raw.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
                                || raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
                                || raw.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
                                || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            {
                                result = raw;
                            }
                            else
                            {
                                _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgSubscriptionDecodingFailed")}");
                                return;
                            }
                        }

                        ConfigHandler.RemoveServerViaSubid(ref config, id);
                        _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgClearSubscription")}");
                        //  RefreshServers();
                        int ret = MainFormHandler.Instance.AddBatchServers(config, result, id);
                        if (ret > 0)
                        {
                            // RefreshServers();
                        }
                        else
                        {
                            _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgFailedImportSubscription")}");
                        }
                        _updateFunc(true, $"{hashCode}{UIRes.I18N("MsgUpdateSubscriptionEnd")}");
                    }
                    else
                    {
                        _updateFunc(false, args.Msg);
                    }
                };
                downloadHandle3.Error += (sender2, args) =>
                {
                    _updateFunc(false, args.GetException().Message);
                };

                downloadHandle3.WebDownloadString(url);
                _updateFunc(false, $"{hashCode}{UIRes.I18N("MsgStartGettingSubscriptions")}");
            }

        }


        public void UpdateGeoFile(string geoName, Config config, Action<bool, string> update)
        {
            _config = config;
            _updateFunc = update;

            string url = string.Format(geoUrl, geoName);
            if (UI.ShowYesNo(string.Format(UIRes.I18N("DownloadYesNo"), url)) != DialogResult.Yes)
            {
                return;
            }

            // Do not use Google's availability check to decide whether GitHub
            // should be accessed through the local proxy. Different destinations
            // can have different reachability in the user's network.
            int httpPort = _config.GetLocalPort(Global.InboundHttp);
            WebProxy localProxy = httpPort > 0
                ? new WebProxy(Global.Loopback, httpPort) : null;

            string proxyTempPath = Utils.GetPath(geoName + ".dat.proxy.tmp");
            string directTempPath = Utils.GetPath(geoName + ".dat.direct.tmp");

            try
            {
                if (File.Exists(proxyTempPath))
                    File.Delete(proxyTempPath);
                if (File.Exists(directTempPath))
                    File.Delete(directTempPath);
            }
            catch (Exception ex)
            {
                Utils.SaveLog("GeoIP temporary-file cleanup failed: " + ex.Message, ex);
            }

            if (localProxy != null)
            {
                Utils.SaveLog("Geo file download: trying local HTTP proxy, port=" + httpPort);
                DownloadGeoFileAttempt(geoName, url, localProxy, proxyTempPath, directTempPath, true);
            }
            else
            {
                Utils.SaveLog("Geo file download: no local HTTP inbound configured; trying direct connection.");
                DownloadGeoFileAttempt(geoName, url, null, directTempPath, directTempPath, false);
            }
        }

        private void DownloadGeoFileAttempt(string geoName, string url, WebProxy proxy,
            string downloadPath, string directTempPath, bool retryDirect)
        {
            DownloadHandle downloadHandle = new DownloadHandle();
            downloadHandle.UpdateCompleted += (sender, args) =>
            {
                if (args.Success)
                {
                    try
                    {
                        if (!File.Exists(downloadPath) || new FileInfo(downloadPath).Length <= 0)
                            throw new IOException("Geo file download completed but the temporary file is empty.");

                        string targetPath = Utils.GetPath(geoName + ".dat");
                        ReplaceGeoFileSafely(downloadPath, targetPath);
                        Utils.SaveLog("Geo file downloaded successfully: " + geoName
                            + (proxy == null ? " (direct)" : " (local HTTP proxy)"));
                        _updateFunc(false, string.Format(UIRes.I18N("MsgDownloadGeoFileSuccessfully"), geoName));
                        _updateFunc(true, "");
                    }
                    catch (Exception ex)
                    {
                        Utils.SaveLog("Geo file installation failed: " + ex.Message, ex);
                        _updateFunc(false, ex.Message);
                    }
                }
                else if (!string.IsNullOrEmpty(args.Msg)
                    && args.Msg.StartsWith("测速失败", StringComparison.Ordinal))
                {
                    HandleGeoFileDownloadFailure(geoName, url, proxy, directTempPath,
                        retryDirect, new IOException(args.Msg));
                }
                else if (!string.IsNullOrEmpty(args.Msg)
                    && !string.Equals(args.Msg, UIRes.I18N("Downloading"), StringComparison.Ordinal)
                    && !args.Msg.StartsWith("...", StringComparison.Ordinal))
                {
                    _updateFunc(false, args.Msg);
                }
            };

            downloadHandle.Error += (sender, args) =>
            {
                HandleGeoFileDownloadFailure(geoName, url, proxy, directTempPath,
                    retryDirect, args.GetException());
            };

            downloadHandle.DownloadFileAsync(url, proxy, 600, downloadPath, false);
        }



        private void HandleGeoFileDownloadFailure(string geoName, string url, WebProxy proxy,
            string directTempPath, bool retryDirect, Exception error)
        {
            Utils.SaveLog("Geo file download failed (" + (proxy == null ? "direct" : "local HTTP proxy")
                + "): " + error.Message, error);

            if (retryDirect)
            {
                _updateFunc(false, "通过本地代理下载 Geo 文件失败，正在尝试直连...");
                try
                {
                    if (File.Exists(directTempPath))
                        File.Delete(directTempPath);
                }
                catch (Exception cleanupException)
                {
                    Utils.SaveLog(cleanupException.Message, cleanupException);
                }
                DownloadGeoFileAttempt(geoName, url, null, directTempPath, directTempPath, false);
            }
            else
            {
                _updateFunc(false, "Geo 文件下载失败（代理和直连均未成功）：" + error.Message);
            }
        }

        private void ReplaceGeoFileSafely(string downloadedPath, string targetPath)
        {
            string backupPath = targetPath + ".bak";
            bool movedOriginal = false;

            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                if (File.Exists(targetPath))
                {
                    File.Move(targetPath, backupPath);
                    movedOriginal = true;
                }

                File.Move(downloadedPath, targetPath);

                if (movedOriginal && File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch
            {
                // Restore the previous working file if installing the new one fails.
                try
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    if (movedOriginal && File.Exists(backupPath))
                        File.Move(backupPath, targetPath);
                }
                catch (Exception restoreException)
                {
                    Utils.SaveLog("Geo file rollback failed: " + restoreException.Message, restoreException);
                }
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(downloadedPath))
                        File.Delete(downloadedPath);
                }
                catch (Exception cleanupException)
                {
                    Utils.SaveLog(cleanupException.Message, cleanupException);
                }
            }
        }

        #region private

        private void CheckUpdateAsync(string type)
        {
            SynchronizationContext uiContext = SynchronizationContext.Current;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Utils.SetSecurityProtocol();
                    string url;
                    if (type == "v2fly")
                        url = v2flyCoreLatestUrl;
                    else if (type == "xray")
                        url = xrayCoreLatestUrl;
                    else if (type == "v2rayN")
                        url = nLatestUrl;
                    else
                        throw new ArgumentException("Type");

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                    request.AllowAutoRedirect = false;
                    request.Method = "GET";
                    request.Timeout = 15000;
                    request.ReadWriteTimeout = 15000;

                    string redirectUrl = null;
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400)
                            redirectUrl = response.Headers[HttpResponseHeader.Location];
                    }

                    if (!string.IsNullOrEmpty(redirectUrl))
                    {
                        if (uiContext != null)
                            uiContext.Post(delegate(object state) { responseHandler(type, redirectUrl); }, null);
                        else
                            responseHandler(type, redirectUrl);
                    }
                    else
                    {
                        Utils.SaveLog("StatusCode error: " + url);
                    }
                }
                catch (Exception ex)
                {
                    Utils.SaveLog(ex.Message, ex);
                    if (uiContext != null)
                        uiContext.Post(delegate(object state) { _updateFunc(false, ex.Message); }, null);
                    else
                        _updateFunc(false, ex.Message);
                }
            });
        }

        /// <summary>
        /// 获取V2RayCore版本
        /// </summary>
        private string getCoreVersion(string type)
        {
            try
            {
                var core = string.Empty;
                var match = string.Empty;
                if (type == "v2fly")
                {
                    core = "v2ray.exe";
                    match = "V2Ray";
                }
                else if (type == "xray")
                {
                    core = "xray.exe";
                    match = "Xray";
                }
                string filePath = Utils.GetPath(core);
                if (!File.Exists(filePath))
                {
                    string msg = string.Format(UIRes.I18N("NotFoundCore"), @"");
                    //ShowMsg(true, msg);
                    return "";
                }

                Process p = new Process();
                p.StartInfo.FileName = filePath;
                p.StartInfo.Arguments = "-version";
                p.StartInfo.WorkingDirectory = Utils.StartupPath();
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                p.Start();
                p.WaitForExit(5000);
                string echo = p.StandardOutput.ReadToEnd();
                string version = Regex.Match(echo, $"{match} ([0-9.]+) \\(").Groups[1].Value;
                return version;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                _updateFunc(false, ex.Message);
                return "";
            }
        }
        private void responseHandler(string type, string redirectUrl)
        {
            try
            {
                string version = redirectUrl.Substring(redirectUrl.LastIndexOf("/", StringComparison.Ordinal) + 1);

                string curVersion;
                string message;
                string url;
                if (type == "v2fly")
                {
                    curVersion = "v" + getCoreVersion(type);
                    message = string.Format(UIRes.I18N("IsLatestCore"), curVersion);
                    string osBit = Environment.Is64BitProcess ? "64" : "32";
                    url = string.Format(v2flyCoreUrl, version, osBit);
                }
                else if (type == "xray")
                {
                    curVersion = "v" + getCoreVersion(type);
                    message = string.Format(UIRes.I18N("IsLatestCore"), curVersion);
                    string osBit = Environment.Is64BitProcess ? "64" : "32";
                    url = string.Format(xrayCoreUrl, version, osBit);
                }
                else if (type == "v2rayN")
                {
                    curVersion = FileVersionInfo.GetVersionInfo(Utils.GetExePath()).FileVersion.ToString();
                    message = string.Format(UIRes.I18N("IsLatestN"), curVersion);
                    url = string.Format(nUrl, version);
                }
                else
                {
                    throw new ArgumentException("Type");
                }

                if (curVersion == version)
                {
                    AbsoluteCompleted?.Invoke(this, new ResultEventArgs(false, message));
                    return;
                }

                AbsoluteCompleted?.Invoke(this, new ResultEventArgs(true, url));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                _updateFunc(false, ex.Message);
            }
        }

        private void askToDownload(DownloadHandle downloadHandle, string url)
        {
            if (UI.ShowYesNo(string.Format(UIRes.I18N("DownloadYesNo"), url)) == DialogResult.Yes)
            {
                if (httpProxyTest() > 0)
                {
                    int httpPort = _config.GetLocalPort(Global.InboundHttp);
                    WebProxy webProxy = new WebProxy(Global.Loopback, httpPort);
                    downloadHandle.DownloadFileAsync(url, webProxy, 600);
                }
                else
                {
                    downloadHandle.DownloadFileAsync(url, null, 600);
                }
            }
        }

        private int httpProxyTest()
        {
            SpeedtestHandler statistics = new SpeedtestHandler(ref _config);
            return statistics.RunAvailabilityCheck();
        }
        #endregion
    }
}
