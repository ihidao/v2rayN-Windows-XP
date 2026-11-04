using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        public int LoadV2rayConfigString(Config config, List<int> selecteds)
        {
            string msg;
            string configStr = V2rayConfigHandler.GenerateClientSpeedtestConfigString(config, selecteds, out msg);
            if (configStr == "")
            {
                ShowMsg(false, msg);
                return -1;
            }
            ShowMsg(false, msg);
            return V2rayStartNew(configStr);
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

        // V2Ray 5.x changed the CLI to "run -c". Older 4.x uses "-config".
        private bool IsV2ray5(string fileName)
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

                using (Process probe = Process.Start(info))
                {
                    if (probe == null) return false;
                    string output = probe.StandardOutput.ReadToEnd();
                    string error = probe.StandardError.ReadToEnd();
                    if (!probe.WaitForExit(3000))
                    {
                        try { probe.Kill(); } catch { }
                        return false;
                    }
                    string versionText = output + Environment.NewLine + error;
                    return versionText.IndexOf("V2Ray 5.", StringComparison.OrdinalIgnoreCase) >= 0
                        || versionText.IndexOf("V2Fly 5.", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
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