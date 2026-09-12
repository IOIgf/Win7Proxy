using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using ProxyCore;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>应用状态（订阅 / 节点 / 选择 / 模式 / 偏好设置）的持久化。</summary>
    public class AppState
    {
        public List<Subscription> Subscriptions { get; set; } = new List<Subscription>();
        public List<Node> Nodes { get; set; } = new List<Node>();
        public int SelectedIndex { get; set; } = -1;
        public ProxyMode Mode { get; set; } = ProxyMode.Rule;

        /// <summary>当前使用的内核。Xray 是默认，也是唯一有 Win7 构建的内核。</summary>
        public CoreKind Core { get; set; } = CoreKind.Xray;

        /// <summary>开机自动启动本程序。</summary>
        public bool AutoStart { get; set; } = false;

        /// <summary>程序启动后自动用上次选中的节点连上。</summary>
        public bool AutoConnect { get; set; } = false;

        /// <summary>导入订阅时自动去除重复节点。</summary>
        public bool DedupeOnImport { get; set; } = true;

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
                    if (s != null)
                    {
                        if (s.Subscriptions == null) s.Subscriptions = new List<Subscription>();
                        if (s.Nodes == null) s.Nodes = new List<Node>();
                        // 旧版本保存的订阅没有 Id，补上，否则"重新导入替换"无从匹配
                        foreach (var sub in s.Subscriptions)
                            if (string.IsNullOrEmpty(sub.Id)) sub.Id = Guid.NewGuid().ToString("N");
                        return s;
                    }
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

        /// <summary>按订阅 Id 查找。</summary>
        public Subscription FindSubscription(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in Subscriptions)
                if (s.Id == id) return s;
            return null;
        }
    }
}
