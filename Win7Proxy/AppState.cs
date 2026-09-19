using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using ProxyCore;
using ProxyCore.Models;

namespace Win7Proxy
{
    /// <summary>应用状态（订阅 / 节点 / 选择 / 模式 / 偏好设置）的持久化。</summary>
    public class AppState
    {
        private readonly object _saveLock = new object();

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

        /// <summary>本地 mixed 端口（HTTP 与 SOCKS 共用）。</summary>
        public int MixedPort { get; set; } = CoreConstants.MixedPort;

        /// <summary>允许局域网内其他设备使用本机代理（入站监听 0.0.0.0）。</summary>
        public bool AllowLan { get; set; } = false;

        [JsonIgnore]
        public string LoadWarning { get; private set; } = "";

        [JsonIgnore]
        public string LastSaveError { get; private set; } = "";

        public event Action<string> PersistenceError;

        private static string FilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Win7Proxy", "app.json");
        }

        public static AppState Load()
        {
            var p = FilePath();
            Exception mainError = null;
            try
            {
                if (File.Exists(p))
                {
                    var state = ReadState(p);
                    Normalize(state);
                    return state;
                }
            }
            catch (Exception ex) { mainError = ex; }

            var backup = p + ".bak";
            try
            {
                if (File.Exists(backup))
                {
                    var state = ReadState(backup);
                    Normalize(state);
                    state.LoadWarning = "主配置文件损坏，已从 app.json.bak 恢复。";
                    return state;
                }
            }
            catch { }

            if (mainError != null)
            {
                var empty = new AppState();
                empty.LoadWarning = "配置文件读取失败：" + mainError.Message;
                return empty;
            }
            return new AppState();
        }

        public bool Save()
        {
            lock (_saveLock)
            {
                var p = FilePath();
                var temp = p + ".tmp";
                var backup = p + ".bak";
                try
                {
                    var dir = Path.GetDirectoryName(p);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));

                    if (File.Exists(p))
                    {
                        try
                        {
                            File.Replace(temp, p, backup, true);
                        }
                        catch
                        {
                            // 某些文件系统不支持 Replace；先保留旧文件，再覆盖主文件。
                            File.Copy(p, backup, true);
                            File.Copy(temp, p, true);
                            File.Delete(temp);
                        }
                    }
                    else
                    {
                        File.Move(temp, p);
                    }

                    LastSaveError = "";
                    return true;
                }
                catch (Exception ex)
                {
                    LastSaveError = "配置保存失败：" + ex.Message;
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    try { PersistenceError?.Invoke(LastSaveError); } catch { }
                    return false;
                }
            }
        }

        private static AppState ReadState(string path)
        {
            var state = JsonConvert.DeserializeObject<AppState>(File.ReadAllText(path));
            if (state == null) throw new InvalidDataException("配置内容为空。");
            return state;
        }

        private static void Normalize(AppState state)
        {
            if (state.Subscriptions == null) state.Subscriptions = new List<Subscription>();
            if (state.Nodes == null) state.Nodes = new List<Node>();
            if (state.MixedPort < 1 || state.MixedPort > 65535) state.MixedPort = CoreConstants.MixedPort;
            // 旧版本保存的订阅没有 Id，补上，否则"重新导入替换"无从匹配。
            foreach (var sub in state.Subscriptions)
                if (string.IsNullOrEmpty(sub.Id)) sub.Id = Guid.NewGuid().ToString("N");
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
