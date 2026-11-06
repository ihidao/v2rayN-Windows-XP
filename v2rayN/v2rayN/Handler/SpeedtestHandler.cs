using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    class SpeedtestHandler
    {
        private Config _config;
        private V2rayHandler _v2rayHandler;
        private List<int> _selecteds;
        Action<int, string> _updateFunc;
        Action<string> _logFunc;

        private void LogProgress(string message, string level = "INFO")
        {
            try
            {
                if (_logFunc != null)
                {
                    string normalizedLevel = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                        ? "Error" : level.Equals("WARNING", StringComparison.OrdinalIgnoreCase)
                            ? "Warning" : "Info";
                    string logLine = string.Format("{0} [{1}] [Speedtest] {2}",
                        DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"), normalizedLevel, message);
                    _logFunc(logLine + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Speedtest", ex);
            }
        }

        public SpeedtestHandler(ref Config config)
        {
            _config = config;
        }

        public SpeedtestHandler(ref Config config, ref V2rayHandler v2rayHandler, List<int> selecteds, string actionType, Action<int, string> update, Action<string> log = null)
        {
            _config = config;
            _v2rayHandler = v2rayHandler;
            _selecteds = Utils.DeepCopy(selecteds);
            _updateFunc = update;
            _logFunc = log;

            if (actionType == "ping")
            {
                Task.Factory.StartNew(() => RunPing());
            }
            if (actionType == "tcping")
            {
                Task.Factory.StartNew(() => RunTcping());
            }
            else if (actionType == "realping")
            {
                Task.Factory.StartNew(() => RunRealPing());
            }
        }

        private void RunPingSub(Action<int> updateFun)
        {
            try
            {
                foreach (int index in _selecteds)
                {
                    if (_config.vmess[index].configType == (int)EConfigType.Custom)
                    {
                        continue;
                    }
                    try
                    {
                        updateFun(index);
                    }
                    catch (Exception ex)
                    {
                        Utils.SaveLog(ex.Message, ex);
                    }
                }

                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }


        private void RunPing()
        {
            RunPingSub((int index) =>
            {
                long time = Utils.Ping(_config.vmess[index].address);
                _updateFunc(index, FormatOut(time, "ms"));
            });
        }

        private void RunTcping()
        {
            RunPingSub((int index) =>
            {
                int time = GetTcpingTime(_config.vmess[index].address, _config.vmess[index].port);
                _updateFunc(index, FormatOut(time, "ms"));
            });
        }

        private void RunRealPing()
        {
            int pid = -1;
            Stopwatch totalWatch = Stopwatch.StartNew();
            try
            {
                Dictionary<int, int> speedTestPorts;
                LogProgress(UIRes.I18N("SpeedtestStartingCore"));
                pid = _v2rayHandler.LoadV2rayConfigString(_config, _selecteds, out speedTestPorts);
                if (pid < 0)
                {
                    string failure = UIRes.I18N("OperationFailed");
                    LogProgress(UIRes.I18N("SpeedtestCoreStartFailedLog") + failure, "ERROR");
                    if (_selecteds.Count > 0)
                        _updateFunc(_selecteds[0], failure);
                    return;
                }
                LogProgress(UIRes.I18N("SpeedtestCoreStarted") + pid);

                List<Task> tasks = new List<Task>();
                foreach (int itemIndex in _selecteds)
                {
                    if (_config.vmess[itemIndex].configType == (int)EConfigType.Custom)
                    {
                        LogProgress(UIRes.I18N("SpeedtestSkipCustomConfig") + _config.vmess[itemIndex].remarks);
                        continue;
                    }

                    int capturedIndex = itemIndex;
                    string serverName = _config.vmess[capturedIndex].remarks;
                    tasks.Add(Task.Factory.StartNew(() =>
                    {
                        Stopwatch itemWatch = Stopwatch.StartNew();
                        try
                        {
                            LogProgress(UIRes.I18N("SpeedtestTestingNode") + serverName);
                            int localTestPort;
                            if (!speedTestPorts.TryGetValue(capturedIndex, out localTestPort))
                            {
                                _updateFunc(capturedIndex, UIRes.I18N("SpeedtestPortAllocationFailed"));
                                LogProgress(String.Format(UIRes.I18N("SpeedtestNodePortFailed"), serverName), "ERROR");
                                return;
                            }
                            WebProxy webProxy = new WebProxy(Global.Loopback, localTestPort);
                            int responseTime = -1;
                            string status = GetRealPingTime(_config.speedPingTestUrl, webProxy, out responseTime);
                            string output = Utils.IsNullOrEmpty(status) ? FormatOut(responseTime, "ms") : FormatOut(status, "");
                            _updateFunc(capturedIndex, output);
                            LogProgress(String.Format(UIRes.I18N("SpeedtestNodeTestResultPrefix"), serverName) + output.Trim()
                                + (Utils.IsNullOrEmpty(status) ? String.Format(UIRes.I18N("SpeedtestElapsedSuffix"), itemWatch.ElapsedMilliseconds)
                                    : String.Format(UIRes.I18N("SpeedtestFailureReasonSuffix"), status)));
                        }
                        catch (Exception ex)
                        {
                            _updateFunc(capturedIndex, UIRes.I18N("SpeedtestErrorShort"));
                            Utils.SaveLog("Real ping task failed for " + serverName + ": " + ex.ToString(), ex);
                            LogProgress(String.Format(UIRes.I18N("SpeedtestNodeError"), serverName, ex.Message), "ERROR");
                        }
                    }));
                }
                Task.WaitAll(tasks.ToArray());
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Real ping failed: " + ex.ToString(), ex);
                LogProgress(UIRes.I18N("SpeedtestUnexpectedError") + ex.Message, "ERROR");
            }
            finally
            {
                if (pid > 0)
                {
                    _v2rayHandler.V2rayStopPid(pid);
                }
                totalWatch.Stop();
                LogProgress(String.Format(UIRes.I18N("SpeedtestCompletedFormat"), totalWatch.ElapsedMilliseconds));
            }
        }

        public int RunAvailabilityCheck() // alias: isLive
        {
            try
            {
                int httpPort = _config.GetLocalPort(Global.InboundHttp);
                if (httpPort <= 0 || !WaitForLocalPort(httpPort, 2500))
                {
                    Utils.SaveLog("Local HTTP inbound is not listening on 127.0.0.1:" + httpPort);
                    return -1;
                }

                Task<int> t = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        WebProxy webProxy = new WebProxy(Global.Loopback, httpPort);
                        int responseTime = -1;
                        string status = GetRealPingTime(Global.AvailabilityTestUrl, webProxy, out responseTime);
                        bool noError = Utils.IsNullOrEmpty(status);
                        return noError ? responseTime : -1;
                    }
                    catch (Exception ex)
                    {
                        Utils.SaveLog(ex.Message, ex);
                        return -1;
                    }
                });
                return t.Result;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return -1;
            }
        }

        private bool WaitForLocalPort(int port, int timeoutMilliseconds)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMilliseconds)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        IAsyncResult result = client.BeginConnect(Global.Loopback, port, null, null);
                        bool connected = result.AsyncWaitHandle.WaitOne(200);
                        if (connected)
                        {
                            client.EndConnect(result);
                            return true;
                        }
                    }
                }
                catch
                {
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private int GetTcpingTime(string url, int port)
        {
            int responseTime = -1;

            try
            {
                if (!IPAddress.TryParse(url, out IPAddress ipAddress))
                {
                    IPHostEntry ipHostInfo = System.Net.Dns.GetHostEntry(url);
                    ipAddress = ipHostInfo.AddressList[0];
                }

                Stopwatch timer = new Stopwatch();
                timer.Start();

                IPEndPoint endPoint = new IPEndPoint(ipAddress, port);
                Socket clientSocket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                IAsyncResult result = clientSocket.BeginConnect(endPoint, null, null);
                if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("connect timeout (5s): " + url);
                clientSocket.EndConnect(result);

                timer.Stop();
                responseTime = (int)timer.Elapsed.TotalMilliseconds;
                clientSocket.Close();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
            return responseTime;
        }

        private string GetRealPingTime(string url, WebProxy webProxy, out int responseTime)
        {
            string msg = string.Empty;
            responseTime = -1;
            Stopwatch timer = new Stopwatch();

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                // XP machines and older networks may need more than the previous 5 seconds.
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.Proxy = webProxy;
                request.KeepAlive = false;

                timer.Start();

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    timer.Stop();

                    if (response.StatusCode != HttpStatusCode.OK
                        && response.StatusCode != HttpStatusCode.NoContent)
                    {
                        msg = response.StatusDescription;
                    }
                    else
                    {
                        responseTime = (int)timer.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch (WebException ex)
            {
                timer.Stop();
                msg = ex.Status + ": " + ex.Message;
                Utils.SaveLog("Real ping failed: " + url
                    + ", elapsed: " + timer.Elapsed.TotalMilliseconds
                    + "ms, error: " + msg, ex);
            }
            catch (Exception ex)
            {
                timer.Stop();
                msg = ex.Message;
                Utils.SaveLog("Real ping failed: " + url
                    + ", elapsed: " + timer.Elapsed.TotalMilliseconds
                    + "ms, error: " + msg, ex);
            }

            return msg;
        }
        private string FormatOut(object time, string unit)
        {
            if (time.ToString().Equals("-1"))
            {
                return "Timeout";
            }
            return string.Format("{0}{1}", time, unit).PadLeft(8, ' ');
        }
    }
}
