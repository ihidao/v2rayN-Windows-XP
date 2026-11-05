using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    public delegate void ProcessDelegate(bool notify, string msg);

    class V2rayHandler
    {
        private static string v2rayConfigRes = Global.v2rayConfigFileName;
        private List<string> lstV2ray;
        private string coreUrl;
        public event ProcessDelegate ProcessEvent;
        private Process _process;
        private List<int> readinessPorts = new List<int>();

        /// <summary>
        /// True only while the process launched by this handler is still alive.
        /// The UI uses this before applying the Windows system proxy.
        /// </summary>
        public bool IsCoreRunning
        {
            get
            {
                try
                {
                    return _process != null && !_process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public V2rayHandler()
        {
        }

        public void LoadV2ray(Config config, bool reloadConfig)
        {
            readinessPorts = new List<int>();
            if (config.inbound != null)
            {
                foreach (var inbound in config.inbound)
                {
                    // 系统代理依赖 SOCKS 入站；不能用其他 inbound 的端口误判核心就绪。
                    if (inbound != null
                        && inbound.protocol == Global.InboundSocks
                        && inbound.localPort > 0
                        && !readinessPorts.Contains(inbound.localPort))
                        readinessPorts.Add(inbound.localPort);
                }
            }

            if (config.coreType == ECoreType.v2fly_core)
            {
                lstV2ray = new List<string> { "wv2ray", "v2ray" };
                coreUrl = Global.v2flyCoreUrl;
            }
            else
            {
                lstV2ray = new List<string> { "xray" };
                coreUrl = Global.xrayCoreUrl;
            }

            if (reloadConfig)
            {
                string conflict;
                if (FindInboundPortConflict(config, out conflict))
                {
                    ShowMsg(false, conflict);
                    Utils.SaveLog(conflict);
                    return;
                }

                string fileName = Utils.GetPath(v2rayConfigRes);
                string msg;
                if (V2rayConfigHandler.GenerateClientConfig(config, fileName, false, out msg) != 0)
                {
                    ShowMsg(false, msg);
                }
                else
                {
                    ShowMsg(true, msg);
                    V2rayRestart();
                }
            }
        }

        /// <summary>
        /// 启动前检查本程序实际使用的本地 TCP 入站端口是否已被占用。
        /// 这里只做预检，不会终止或修改占用端口的其他程序。
        /// </summary>
        private bool FindInboundPortConflict(Config config, out string message)
        {
            message = String.Empty;
            if (config == null || config.inbound == null || config.inbound.Count == 0)
                return false;

            IPAddress listenAddress = config.allowLANConn ? IPAddress.Any : IPAddress.Loopback;
            List<int> portsToCheck = new List<int>();
            foreach (var inbound in config.inbound)
            {
                if (inbound == null || inbound.localPort <= 0)
                    continue;
                if (!portsToCheck.Contains(inbound.localPort))
                    portsToCheck.Add(inbound.localPort);
            }

            // 当前 GUI 的 HTTP 入站端口按 SOCKS 端口 + 1 生成。
            if (config.inbound[0] != null)
            {
                int httpPort = config.inbound[0].localPort + 1;
                if (httpPort > 0 && httpPort <= 65535 && !portsToCheck.Contains(httpPort))
                    portsToCheck.Add(httpPort);
            }

            foreach (int port in portsToCheck)
            {
                TcpListener probe = null;
                try
                {
                    probe = new TcpListener(listenAddress, port);
                    probe.Server.ExclusiveAddressUse = true;
                    probe.Start();
                }
                catch (SocketException ex)
                {
                    message = String.Format(
                        "启动内核失败：本地端口 {0}:{1} 已被占用或无法监听。请关闭占用该端口的程序，或修改 v2rayN 本地入站端口。\r\n详细信息：{2}",
                        listenAddress, port, ex.Message);
                    return true;
                }
                catch (Exception ex)
                {
                    message = String.Format(
                        "启动内核失败：无法检查本地端口 {0}:{1}。\r\n详细信息：{2}",
                        listenAddress, port, ex.Message);
                    return true;
                }
                finally
                {
                    if (probe != null)
                    {
                        try { probe.Stop(); } catch { }
                    }
                }
            }

            // SOCKS 入站启用 UDP 时，也检查同一端口的 UDP 绑定冲突。
            if (config.inbound[0] != null && config.inbound[0].udpEnabled
                && config.inbound[0].localPort > 0)
            {
                UdpClient udpProbe = null;
                try
                {
                    udpProbe = new UdpClient(AddressFamily.InterNetwork);
                    udpProbe.Client.ExclusiveAddressUse = true;
                    udpProbe.Client.Bind(new IPEndPoint(listenAddress, config.inbound[0].localPort));
                }
                catch (SocketException ex)
                {
                    message = String.Format(
                        "启动内核失败：本地 UDP 端口 {0}:{1} 已被占用或无法监听。请关闭占用该端口的程序，或修改 v2rayN 本地入站端口。\\r\\n详细信息：{2}",
                        listenAddress, config.inbound[0].localPort, ex.Message);
                    return true;
                }
                catch (Exception ex)
                {
                    message = String.Format(
                        "启动内核失败：无法检查本地 UDP 端口 {0}:{1}。\\r\\n详细信息：{2}",
                        listenAddress, config.inbound[0].localPort, ex.Message);
                    return true;
                }
                finally
                {
                    if (udpProbe != null)
                    {
                        try { udpProbe.Close(); } catch { }
                    }
                }
            }
            return false;
        }

        public int LoadV2rayConfigString(Config config, List<int> selecteds,
            out Dictionary<int, int> speedTestPorts)
        {
            speedTestPorts = new Dictionary<int, int>();

            try
            {
                // Ask Windows for currently unused ephemeral ports instead of
                // deriving fixed ports from the SOCKS port (which can collide).
                foreach (int index in selecteds)
                {
                    if (index < 0 || index >= config.vmess.Count
                        || config.vmess[index].configType == (int)EConfigType.Custom)
                        continue;

                    int port = GetAvailablePort();
                    while (speedTestPorts.ContainsValue(port))
                        port = GetAvailablePort();

                    speedTestPorts[index] = port;
                }

                string msg;
                string configStr = V2rayConfigHandler.GenerateClientSpeedtestConfigString(
                    config, selecteds, speedTestPorts, out msg);
                if (configStr == "")
                {
                    ShowMsg(false, msg);
                    return -1;
                }

                ShowMsg(false, msg);
                return V2rayStartNew(configStr, new List<int>(speedTestPorts.Values));
            }
            catch (Exception ex)
            {
                Utils.SaveLog("Unable to allocate speed-test ports: " + ex.Message, ex);
                ShowMsg(false, "无法分配测速端口: " + ex.Message);
                return -1;
            }
        }

        private int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private void V2rayRestart()
        {
            V2rayStop();
            V2rayStart();
        }

        public void V2rayStop()
        {
            try
            {
                if (_process != null)
                {
                    KillProcess(_process);
                    _process.Dispose();
                    _process = null;
                }
                else if (lstV2ray != null)
                {
                    foreach (string vName in lstV2ray)
                    {
                        Process[] existing = Process.GetProcessesByName(vName);
                        foreach (Process p in existing)
                        {
                            try
                            {
                                string path = p.MainModule.FileName;
                                if (string.Equals(path, Utils.GetPath(vName + ".exe"), StringComparison.OrdinalIgnoreCase))
                                    KillProcess(p);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public void V2rayStopPid(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                KillProcess(p);
                p.Dispose();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        private string V2rayFindexe()
        {
            if (lstV2ray == null)
            {
                lstV2ray = new List<string> { "wv2ray", "v2ray" };
                coreUrl = Global.v2flyCoreUrl;
            }

            foreach (string name in lstV2ray)
            {
                string fileName = Utils.GetPath(name + ".exe");
                if (File.Exists(fileName))
                    return fileName;
            }

            string msg = string.Format(UIRes.I18N("NotFoundCore"), coreUrl ?? Global.v2flyCoreUrl);
            ShowMsg(false, msg);
            return string.Empty;
        }

        // Wait for the core's own startup message, then briefly verify the SOCKS listener.
        // Both output streams are consumed asynchronously to avoid pipe deadlocks.
        private Process TryStartCore(string fileName, string arguments, List<int> ports,
            out string failure, out long startupElapsedMs, out string coreNameVersion)
        {
            failure = string.Empty;
            startupElapsedMs = 0;
            coreNameVersion = "V2Ray";
            string detectedCoreNameVersion = "V2Ray";
            Stopwatch startupWatch = new Stopwatch();
            Process p = new Process();
            StringBuilder errorText = new StringBuilder();
            object startupLock = new object();
            bool startedMessageSeen = false;
            p.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Utils.StartupPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            DataReceivedEventHandler outputHandler = (sender, e) =>
            {
                if (String.IsNullOrEmpty(e.Data)) return;
                string banner = e.Data.Trim();
                if (banner.StartsWith("V2Ray ", StringComparison.OrdinalIgnoreCase)
                    || banner.StartsWith("Xray ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = banner.Split(new char[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        detectedCoreNameVersion = parts[0] + " " + parts[1];
                }
                // Keep warning/error details in the daily log file; do not forward
                // routine connection chatter to the GUI log window.
                if (!IsRoutineCoreOutput(e.Data) && IsImportantCoreDiagnostic(e.Data))
                {
                    Utils.SaveLogDetail("CORE", e.Data);
                    ShowMsg(false, e.Data + Environment.NewLine);
                }
                if (IsCoreStartedMessage(e.Data))
                {
                    lock (startupLock) startedMessageSeen = true;
                }
            };
            DataReceivedEventHandler errorHandler = (sender, e) =>
            {
                if (String.IsNullOrEmpty(e.Data)) return;
                lock (errorText) errorText.AppendLine(e.Data);
                if (!IsRoutineCoreOutput(e.Data) && IsImportantCoreDiagnostic(e.Data))
                {
                    // Preserve full core diagnostics in guiLogs even when the GUI
                    // displays only the concise warning/error line.
                    Utils.SaveLogDetail("CORE", e.Data);
                    ShowMsg(false, e.Data + Environment.NewLine);
                }
                if (IsCoreStartedMessage(e.Data))
                {
                    lock (startupLock) startedMessageSeen = true;
                }
            };
            p.OutputDataReceived += outputHandler;
            p.ErrorDataReceived += errorHandler;

            try
            {
                if (!p.Start())
                    throw new Exception("Process.Start returned false.");

                startupWatch.Start();
                Utils.SaveLog("[CoreStartup] Process created; PID=" + p.Id
                    + "; time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + "; arguments=" + arguments);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // XP hardware may be slow. Allow up to 10 seconds for the core's
                // explicit startup message; an early process exit fails immediately.
                bool started = false;
                for (int elapsed = 0; elapsed < 10000; elapsed += 100)
                {
                    if (p.HasExited) break;

                    lock (startupLock) started = startedMessageSeen;
                    if (started) break;

                    if (p.WaitForExit(100)) break;
                }

                if (!p.HasExited && started)
                {
                    // The startup message is the primary signal. Verify the configured
                    // SOCKS listener for up to 2 more seconds before enabling the proxy.
                    if (ports == null || ports.Count == 0)
                    {
                        startupElapsedMs = startupWatch.ElapsedMilliseconds;
                        coreNameVersion = detectedCoreNameVersion;
                        return p;
                    }

                    // Poll at 50 ms intervals so the UI can proceed promptly once
                    // the SOCKS listener is available, without adding a fixed startup delay.
                    for (int elapsed = 0; elapsed < 2000; elapsed += 50)
                    {
                        if (p.HasExited) break;
                        if (IsAnyPortReady(ports))
                        {
                            if (!p.WaitForExit(50))
                            {
                                startupElapsedMs = startupWatch.ElapsedMilliseconds;
                                coreNameVersion = detectedCoreNameVersion;
                                return p;
                            }
                            break;
                        }
                        if (p.WaitForExit(50)) break;
                    }
                }

                if (!p.HasExited)
                {
                    lock (startupLock) started = startedMessageSeen;
                    startupElapsedMs = startupWatch.ElapsedMilliseconds;
                    failure = started
                        ? "内核已输出启动成功提示，但 SOCKS 入站端口在 2 秒内未就绪。"
                        : "内核进程仍在运行，但在 10 秒内未输出启动成功提示。";
                    try
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                    catch { }
                    try { p.Dispose(); } catch { }
                    return null;
                }

                p.WaitForExit();
                startupElapsedMs = startupWatch.ElapsedMilliseconds;
                lock (errorText) failure = errorText.ToString();
                if (String.IsNullOrEmpty(failure))
                    failure = "内核进程在启动检查期间退出，退出代码: " + p.ExitCode;
                p.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                lock (errorText) failure = errorText.ToString();
                failure = ex.Message + (String.IsNullOrEmpty(failure) ? "" : Environment.NewLine + failure);
                try { p.Dispose(); } catch { }
                return null;
            }
        }

        private bool IsCoreStartedMessage(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            return line.IndexOf("started", StringComparison.OrdinalIgnoreCase) >= 0
                && (line.IndexOf("V2Ray", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Xray", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private bool IsImportantCoreDiagnostic(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            string value = line.ToLowerInvariant();
            return value.IndexOf("[warning]", StringComparison.Ordinal) >= 0
                || value.IndexOf("[error]", StringComparison.Ordinal) >= 0
                || value.IndexOf("[fatal]", StringComparison.Ordinal) >= 0
                || value.IndexOf("panic:", StringComparison.Ordinal) >= 0
                || value.IndexOf("failed", StringComparison.Ordinal) >= 0;
        }

        private bool IsRoutineCoreOutput(string line)
        {
            if (String.IsNullOrEmpty(line)) return false;
            if (IsCoreStartedMessage(line)) return true;

            string value = line.Trim();
            if (String.Equals(value, "A unified platform for anti-censorship.",
                StringComparison.OrdinalIgnoreCase))
                return true;

            // Standard V2Ray/Xray version banners; do not suppress arbitrary warnings.
            if (value.StartsWith("V2Ray ", StringComparison.OrdinalIgnoreCase)
                && value.IndexOf("(V2Fly", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (value.StartsWith("Xray ", StringComparison.OrdinalIgnoreCase)
                && value.IndexOf("(Xray,", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        private bool IsAnyPortReady(List<int> ports)
        {
            if (ports == null || ports.Count == 0) return false;
            foreach (int port in ports)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        client.Connect(IPAddress.Loopback, port);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private Process StartCoreWithFallback(string fileName, string configFile, List<int> ports,
            out string failure, out long startupElapsedMs, out bool usedLegacyArguments,
            out string coreNameVersion)
        {
            Stopwatch totalWatch = Stopwatch.StartNew();
            startupElapsedMs = 0;
            usedLegacyArguments = false;
            coreNameVersion = "V2Ray";

            string error5;
            long attempt5Ms;
            string version5;
            Process p = TryStartCore(fileName, "run -c \"" + configFile + "\"", ports,
                out error5, out attempt5Ms, out version5);
            if (p != null)
            {
                totalWatch.Stop();
                startupElapsedMs = totalWatch.ElapsedMilliseconds;
                coreNameVersion = version5;
                failure = string.Empty;
                Utils.SaveLog("[CoreStartup] PID=" + p.Id
                    + "; core=" + coreNameVersion
                    + "; config-arg=-c; elapsed=" + startupElapsedMs
                    + " ms; SOCKS ready.");
                return p;
            }

            Utils.SaveLog("[CoreStartup] config-arg=-c failed after " + attempt5Ms
                + " ms; trying config-arg=-config. " + error5);

            string error4;
            long attempt4Ms;
            string version4;
            p = TryStartCore(fileName, "-config \"" + configFile + "\"", ports,
                out error4, out attempt4Ms, out version4);
            totalWatch.Stop();
            startupElapsedMs = totalWatch.ElapsedMilliseconds;
            if (p != null)
            {
                usedLegacyArguments = true;
                coreNameVersion = version4;
                failure = string.Empty;
                Utils.SaveLog("[CoreStartup] PID=" + p.Id
                    + "; core=" + coreNameVersion
                    + "; config-arg=-config; elapsed=" + startupElapsedMs
                    + " ms; SOCKS ready.");
                return p;
            }

            failure = "Core failed to start with both command-line formats."
                + Environment.NewLine + "[-c]" + Environment.NewLine + error5
                + Environment.NewLine + "[-config]" + Environment.NewLine + error4;
            return null;
        }

        private void V2rayStart()
        {
            ShowMsg(false, string.Format(UIRes.I18N("StartService"), DateTime.Now.ToString()));
            try
            {
                string fileName = V2rayFindexe();
                if (fileName == "") return;

                string failure;
                long startupElapsedMs;
                bool usedLegacyArguments;
                string coreNameVersion;
                Process p = StartCoreWithFallback(fileName,
                    Utils.GetPath(v2rayConfigRes), readinessPorts, out failure,
                    out startupElapsedMs, out usedLegacyArguments, out coreNameVersion);
                if (p == null)
                    throw new Exception(failure);

                try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
                _process = p;
                Global.processJob.AddProcess(p.Handle);
                ShowMsg(false, coreNameVersion + " started successfully (PID=" + p.Id
                    + ", SOCKS ready)." + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                ShowMsg(true, ex.Message);
            }
        }

        private int V2rayStartNew(string configStr, List<int> ports)
        {
            ShowMsg(false, string.Format(UIRes.I18N("StartService"), DateTime.Now.ToString()));
            string tempConfigFile = null;
            Process p = null;
            try
            {
                string fileName = V2rayFindexe();
                if (fileName == "") return -1;

                tempConfigFile = Path.Combine(Path.GetTempPath(),
                    "v2rayN-speedtest-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(tempConfigFile, configStr, new UTF8Encoding(false));

                string failure;
                long startupElapsedMs;
                bool usedLegacyArguments;
                string coreNameVersion;
                p = StartCoreWithFallback(fileName, tempConfigFile, ports, out failure,
                    out startupElapsedMs, out usedLegacyArguments, out coreNameVersion);
                if (p == null)
                    throw new Exception("V2Ray speed-test core failed to start. " + failure);

                // The core has loaded its configuration; the temporary file is no longer needed.
                try { File.Delete(tempConfigFile); } catch { }
                tempConfigFile = null;

                Global.processJob.AddProcess(p.Handle);
                return p.Id;
            }
            catch (Exception ex)
            {
                if (p != null)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
                if (!String.IsNullOrEmpty(tempConfigFile))
                {
                    try { File.Delete(tempConfigFile); } catch { }
                }
                Utils.SaveLog(ex.Message, ex);
                ShowMsg(false, ex.Message);
                return -1;
            }
        }

        private void ShowMsg(bool updateToTrayTooltip, string msg)
        {
            ProcessEvent?.Invoke(updateToTrayTooltip, msg);
        }

        private void KillProcess(Process p)
        {
            try
            {
                if (p == null || p.HasExited) return;
                p.CloseMainWindow();
                if (!p.WaitForExit(300))
                {
                    p.Kill();
                    p.WaitForExit(1000);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }
    }
}