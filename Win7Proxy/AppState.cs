using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>应用状态（订阅 / 节点 / 选择 / 模式）的持久化。</summary>
    public class AppState
    {
        public List<Subscription> Subscriptions { get; set; } = new List<Subscription>();
        public List<Node> Nodes { get; set; } = new List<Node>();
        public int SelectedIndex { get; set; } = -1;
        public ProxyMode Mode { get; set; } = ProxyMode.Rule;

        private static string FilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win7Proxy", "app.json");
        }

        public static AppState Load()
        {
            try
            {
                var p = FilePath();
                if (File.Exists(p))
                {
                    var s = JsonConvert.DeserializeObject<AppState>(File.ReadAllText(p));
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppState();
        }

        public void Save()
        {
            try
            {
                var p = FilePath();
                var dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }
    }
}
