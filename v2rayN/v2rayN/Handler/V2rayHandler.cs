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

        public void LoadV2ray(Config config)
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

            if (Global.reloadV2ray)
            {
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

        // Detect the core CLI once during the first UI startup, before launching it.
        // Cache the result so reloads and speed tests do not spawn extra probe processes.
        // Try the V2Ray 5.x command line first, then fall back to the 4.x syntax.
        // A process that remains alive for the startup check is considered started.
        private Process TryStartCore(string fileName, string arguments, List<int> ports, out string failure)
        {
            failure = string.Empty;
            Process p = new Process();
            StringBuilder errorText = new StringBuilder();
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

            try
            {
                if (!p.Start())
                    throw new Exception("Process.Start returned false.");

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // 每 100ms 检查一次：核心监听端口一旦就绪便立即返回，
                // 不再无条件等待完整的 1.5 秒；无端口信息时保留原超时保护。
                // 只在 SOCKS 入站端口真正开始监听后报告启动成功。
                // XP 机器较慢时最多等待 3 秒；超时则停止这个未就绪的进程，
                // 避免 UI 提前设置系统代理并把“进程仍存活”当作“核心可用”。
                for (int elapsed = 0; elapsed < 3000; elapsed += 100)
                {
                    if (p.HasExited) break;
                    if (IsAnyPortReady(ports))
                    {
                        // 避免把其它程序占用的同一端口误判为本核心已就绪。
                        if (p.WaitForExit(100)) break;
                        return p;
                    }
                    if (p.WaitForExit(100)) break;
                }

                if (!p.HasExited)
                {
                    failure = "核心进程仍在运行，但 SOCKS 入站端口在 3 秒内未就绪。";
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

        private Process StartCoreWithFallback(string fileName, string configFile, List<int> ports, out string failure)
        {
            string error5;
            Process p = TryStartCore(fileName, "run -c \"" + configFile + "\"", ports,
                out error5);
            if (p != null)
            {
                failure = string.Empty;
                return p;
            }

            ShowMsg(false, "V2Ray 5.x 启动方式失败，尝试 4.x 参数方式..." + Environment.NewLine
                + error5 + Environment.NewLine);

            string error4;
            p = TryStartCore(fileName, "-config \"" + configFile + "\"", ports,
                out error4);
            if (p != null)
            {
                failure = string.Empty;
                ShowMsg(false, "已通过 V2Ray 4.x 参数方式启动内核。" + Environment.NewLine);
                return p;
            }

            failure = "V2Ray core failed to start with both command-line formats."
                + Environment.NewLine + "[5.x run -c]" + Environment.NewLine + error5
                + Environment.NewLine + "[4.x -config]" + Environment.NewLine + error4;
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
                Process p = StartCoreWithFallback(fileName,
                    Utils.GetPath(v2rayConfigRes), readinessPorts, out failure);
                if (p == null)
                    throw new Exception(failure);

                try { p.PriorityClass = ProcessPriorityClass.High; } catch { }
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
                p = StartCoreWithFallback(fileName, tempConfigFile, ports, out failure);
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