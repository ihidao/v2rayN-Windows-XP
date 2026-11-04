using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using v2rayN.Base;

namespace v2rayN.Handler
{
    /// <summary>
    ///Download
    /// </summary>
    class DownloadHandle
    {
        public event EventHandler<ResultEventArgs> UpdateCompleted;

        public event ErrorEventHandler Error;
         

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

        private int progressPercentage = -1;
        private long bytesReceived = 0;
        private Stopwatch downloadStopwatch = new Stopwatch();
        private int DownloadTimeout = -1;
          
        public WebClientEx DownloadFileAsync(string url, WebProxy webProxy, int downloadTimeout)
        {
            WebClientEx ws = new WebClientEx();
            try
            {
                Utils.SetSecurityProtocol();
                UpdateCompleted?.Invoke(this, new ResultEventArgs(false, UIRes.I18N("Downloading")));

                progressPercentage = -1;
                bytesReceived = 0;
                downloadStopwatch.Reset();
                downloadStopwatch.Start();

                //WebClientEx ws = new WebClientEx();
                DownloadTimeout = downloadTimeout;
                if (webProxy != null)
                {
                    ws.Proxy = webProxy;// new WebProxy(Global.Loopback, Global.httpPort);
                }

                ws.DownloadFileCompleted += ws_DownloadFileCompleted;
                ws.DownloadProgressChanged += ws_DownloadProgressChanged;
                ws.DownloadFileAsync(new Uri(url), Utils.GetPath(Global.DownloadFileName));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);

                Error?.Invoke(this, new ErrorEventArgs(ex));
            }
            return ws;
        }

        void ws_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            if (UpdateCompleted != null)
            {
                bytesReceived = e.BytesReceived;

                if (DownloadTimeout != -1)
                {
                    if (downloadStopwatch.Elapsed.TotalSeconds > DownloadTimeout)
                    {
                        ((WebClientEx)sender).CancelAsync();
                    }
                }
                if (progressPercentage != e.ProgressPercentage && e.ProgressPercentage % 10 == 0)
                {
                    progressPercentage = e.ProgressPercentage;
                    string msg = string.Format("...{0}%", e.ProgressPercentage);
                    UpdateCompleted(this, new ResultEventArgs(false, msg));
                }
            }
        }
        void ws_DownloadFileCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            try
            {
                if (UpdateCompleted != null)
                {
                    if (e.Cancelled)
                    {
                        ((WebClientEx)sender).Dispose();
                        downloadStopwatch.Stop();
                        if (bytesReceived <= 0)
                            UpdateCompleted(this, new ResultEventArgs(false, "测速失败：未收到数据"));
                        else
                            UpdateCompleted(this, new ResultEventArgs(true, GetDownloadSpeed()));
                        return;
                    }

                    if (e.Error == null)
                    {
                        downloadStopwatch.Stop();
                        UpdateCompleted(this, new ResultEventArgs(bytesReceived > 0, GetDownloadSpeed()));
                    }
                    else
                    {
                        throw e.Error;
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);

                Error?.Invoke(this, new ErrorEventArgs(ex));
            }
        }

        private string GetDownloadSpeed()
        {
            double elapsedSeconds = downloadStopwatch.Elapsed.TotalSeconds;
            if (bytesReceived <= 0 || elapsedSeconds <= 0)
                return "测速失败：未收到数据";

            double speed = bytesReceived / elapsedSeconds / 1000000.0;
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < 0)
                return "测速失败：速度计算异常";

            return string.Format("{0} MB/s", speed.ToString("#0.0"));
        }

        /// <summary>
        /// DownloadString
        /// </summary> 
        /// <param name="url"></param>
        public void WebDownloadString(string url)
        {
            string source = string.Empty;
            try
            {
                Utils.SetSecurityProtocol();

                WebClientEx ws = new WebClientEx();
                ws.DownloadStringCompleted += Ws_DownloadStringCompleted;
                ws.DownloadStringAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                Error?.Invoke(this, new ErrorEventArgs(ex));
            }
        }

        private void Ws_DownloadStringCompleted(object sender, DownloadStringCompletedEventArgs e)
        {
            try
            {
                if (e.Cancelled)
                {
                    throw new WebException("Subscription download was cancelled.");
                }
                if (e.Error != null)
                {
                    throw e.Error;
                }

                string source = e.Result;
                UpdateCompleted?.Invoke(this, new ResultEventArgs(true, source));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);

                Error?.Invoke(this, new ErrorEventArgs(ex));
            }
        }

        public string WebDownloadStringSync(string url)
        {
            string source = string.Empty;
            try
            {
                Utils.SetSecurityProtocol();

                WebClientEx ws = new WebClientEx();

                return ws.DownloadString(new Uri(url));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return string.Empty;
            }
        }

    }
}
