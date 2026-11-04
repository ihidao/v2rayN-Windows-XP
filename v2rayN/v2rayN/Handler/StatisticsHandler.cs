using System;
using System.Collections.Generic;
using v2rayN.Mode;

namespace v2rayN.Handler
{
    // .NET Framework 4.0 / Windows XP compatibility:
    // live gRPC statistics are disabled because the original Grpc.Core
    // native runtime requires newer Windows/.NET versions. Previously saved
    // statistics remain readable, clearable and persistent.
    class StatisticsHandler
    {
        private ServerStatistics serverStatistics_;
        private readonly Action<ulong, ulong, List<ServerStatItem>> updateFunc_;

        public bool Enable { get; set; }
        public bool UpdateUI { get; set; }

        public List<ServerStatItem> Statistic
        {
            get { return serverStatistics_.server; }
        }

        public StatisticsHandler(Mode.Config config, Action<ulong, ulong, List<ServerStatItem>> update)
        {
            updateFunc_ = update;
            Enable = false;
            UpdateUI = false;
            LoadFromFile();
        }

        public void Close()
        {
            SaveToFile();
        }

        public void Run()
        {
            // Live traffic collection is intentionally unavailable in the XP build.
        }

        public void LoadFromFile()
        {
            try
            {
                string result = Utils.LoadResource(Utils.GetPath(Global.StatisticLogOverall));
                if (!Utils.IsNullOrEmpty(result))
                    serverStatistics_ = Utils.FromJson<ServerStatistics>(result);

                if (serverStatistics_ == null)
                    serverStatistics_ = new ServerStatistics();
                if (serverStatistics_.server == null)
                    serverStatistics_.server = new List<ServerStatItem>();

                long ticks = DateTime.Now.Date.Ticks;
                foreach (ServerStatItem item in serverStatistics_.server)
                {
                    if (item.dateNow != ticks)
                    {
                        item.todayUp = 0;
                        item.todayDown = 0;
                        item.dateNow = ticks;
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                serverStatistics_ = new ServerStatistics();
                serverStatistics_.server = new List<ServerStatItem>();
            }
        }

        public void SaveToFile()
        {
            try
            {
                Utils.ToJsonFile(serverStatistics_, Utils.GetPath(Global.StatisticLogOverall));
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public void ClearAllServerStatistics()
        {
            if (serverStatistics_ == null || serverStatistics_.server == null)
                return;

            foreach (ServerStatItem item in serverStatistics_.server)
            {
                item.todayUp = 0;
                item.todayDown = 0;
                item.totalUp = 0;
                item.totalDown = 0;
            }
            SaveToFile();
        }
    }
}