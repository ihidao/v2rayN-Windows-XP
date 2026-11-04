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
        private string _detectedCorePath = string.Empty;
        private bool _detectedCoreIsV2ray5;
        private bool _coreVersionDetected;

        public V2rayHandler()
        {
        }

        public void LoadV2ray(Config config)
        {
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

            if (Global.reloadV2ray)
            {
                // Detect the core version after the main window has been shown,
                // but before generating configuration and starting the core.
                DetectCoreVersion();

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
                return V2rayStartNew(configStr);
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

        // Detect the core CLI once during the first UI startup, before launching it.
        // Cache the result so reloads and speed tests do not spawn extra probe processes.
        private void DetectCoreVersion()
        {
            string fileName = V2rayFindexe();
            if (string.IsNullOrEmpty(fileName))
                return;

            if (_coreVersionDetected
                && string.Equals(_detectedCorePath, fileName, StringComparison.OrdinalIgnoreCase))
                return;

            _detectedCorePath = fileName;
            _detectedCoreIsV2ray5 = ProbeV2ray5(fileName);
            _coreVersionDetected = true;
        }

        private bool ProbeV2ray5(string fileName)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = fileName;
                info.Arguments = "version";
                info.WorkingDirectory = Utils.StartupPath();
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                StringBuilder versionText = new StringBuilder();
                using (Process probe = new Process())
                {
                    probe.StartInfo = info;
                    probe.OutputDataReceived += (sender, e) =>
                    {
                        if (!String.IsNullOrEmpty(e.Data))
                            lock (versionText) versionText.AppendLine(e.Data);
                    };
                    probe.ErrorDataReceived += (sender, e) =>
                    {
                        if (!String.IsNullOrEmpty(e.Data))
                            lock (versionText) versionText.AppendLine(e.Data);
                    };

                    if (!probe.Start())
                        return false;

                    probe.BeginOutputReadLine();
                    probe.BeginErrorReadLine();

                    if (!probe.WaitForExit(3000))
                    {
                        try { probe.Kill(); } catch { }
                        return false;
                    }
                    probe.WaitForExit();

                    string text;
                    lock (versionText) text = versionText.ToString();
                    return text.IndexOf("V2Ray 5.", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("V2Fly 5.", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool IsV2ray5(string fileName)
        {
            if (!_coreVersionDetected
                || !string.Equals(_detectedCorePath, fileName, StringComparison.OrdinalIgnoreCase))
            {
                _detectedCorePath = fileName;
                _detectedCoreIsV2ray5 = ProbeV2ray5(fileName);
                _coreVersionDetected = true;
            }
            return _detectedCoreIsV2ray5;
        }

        private string GetConfigArguments(string fileName)
        {
            if (IsV2ray5(fileName))
                return "run -c \"" + Utils.GetPath(v2rayConfigRes) + "\"";
            return "-config \"" + Utils.GetPath(v2rayConfigRes) + "\"";
        }

        // Speed-test configurations are written to a temporary file.
        // V2Ray 5.18 on Windows treats "stdin:" as a literal filename.
        private string GetConfigStringArguments(string fileName, string configFile)
        {
            if (IsV2ray5(fileName))
                return "run -c \"" + configFile + "\"";
            return "-config \"" + configFile + "\"";
        }

        private void V2rayStart()
        {
            ShowMsg(false, string.Format(UIRes.I18N("StartService"), DateTime.Now.ToString()));
            try
            {
                string fileName = V2rayFindexe();
                if (fileName == "") return;

                StringBuilder errorText = new StringBuilder();
                Process p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = GetConfigArguments(fileName),
                    WorkingDirectory = Utils.StartupPath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                p.OutputDataReceived += (sender, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                        ShowMsg(false, e.Data + Environment.NewLine);
                };
                p.ErrorDataReceived += (sender, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                        lock (errorText) errorText.AppendLine(e.Data);
                };

                p.Start();
                p.PriorityClass = ProcessPriorityClass.High;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (p.WaitForExit(1500))
                {
                    p.WaitForExit();
                    string error;
                    lock (errorText) error = errorText.ToString();
                    throw new Exception("V2Ray core exited during startup. " + error);
                }

                _process = p;
                Global.processJob.AddProcess(p.Handle);
                ShowMsg(false, "V2Ray core started. PID=" + p.Id);
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                ShowMsg(true, ex.Message);
            }
        }

        private int V2rayStartNew(string configStr)
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

                StringBuilder errorText = new StringBuilder();
                p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = GetConfigStringArguments(fileName, tempConfigFile),
                    WorkingDirectory = Utils.StartupPath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                p.OutputDataReceived += (sender, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                        ShowMsg(false, e.Data + Environment.NewLine);
                };
                p.ErrorDataReceived += (sender, e) =>
                {
                    if (!String.IsNullOrEmpty(e.Data))
                        lock (errorText) errorText.AppendLine(e.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (p.WaitForExit(1500))
                {
                    p.WaitForExit();
                    string error;
                    lock (errorText) error = errorText.ToString();
                    throw new Exception("V2Ray speed-test core exited during startup. " + error);
                }

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