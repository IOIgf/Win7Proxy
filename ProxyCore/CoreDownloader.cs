using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using Newtonsoft.Json.Linq;

namespace ProxyCore
{
    /// <summary>
    /// 内核下载与安装。
    /// 各内核的发布包结构不同：Xray/V2Ray 的 zip 里 exe 就在根目录或一层子目录，
    /// sing-box 的 zip 名带版本号（必须先在 GitHub API 查最新 tag），
    /// 且 sing-box 用的是自己的 geoip.db / geosite.db，与 v2ray 系的 .dat 不通用。
    /// </summary>
    public static class CoreDownloader
    {
        /// <summary>GitHub 镜像，依次尝试。空串表示直连。</summary>
        private static readonly string[] Mirrors = {
            "https://gh-proxy.org/",
            "https://ghproxy.com/",
            "https://ghproxy.net/",
            "https://mirror.ghproxy.com/",
            ""
        };

        static CoreDownloader()
        {
            // 老系统（Win7）默认不开 TLS 1.2，不显式打开就会握手失败
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        }

        /// <summary>把指定内核下载并安装到 coreDir；返回是否成功。</summary>
        public static bool Install(CoreKind kind, string coreDir, Action<string> log)
        {
            if (log == null) log = s => { };
            Directory.CreateDirectory(coreDir);

            var spec = CoreRegistry.Of(kind);
            log("准备安装内核：" + spec.Name);

            string zipRel;
            switch (kind)
            {
                case CoreKind.V2ray:
                    zipRel = "v2fly/v2ray-core/releases/latest/download/v2ray-windows-64.zip";
                    break;
                case CoreKind.Singbox:
                    var ver = FetchLatestSingboxVersion(log);
                    if (string.IsNullOrEmpty(ver))
                    {
                        log("无法获取 sing-box 最新版本号，请手动下载后把 sing-box.exe 放进 core\\");
                        return false;
                    }
                    log("sing-box 最新版本：" + ver);
                    zipRel = "SagerNet/sing-box/releases/download/v" + ver + "/sing-box-" + ver + "-windows-amd64.zip";
                    break;
                default:
                    // Win7 只能用专门的 win7 构建（Go 1.20 编译），Win10+ 用标准构建
                    var win7 = Environment.OSVersion.Version.Major < 6 ||
                               (Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor <= 1);
                    zipRel = win7
                        ? "XTLS/Xray-core/releases/latest/download/Xray-win7-64.zip"
                        : "XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip";
                    break;
            }

            var tmpZip = Path.Combine(Path.GetTempPath(), "win7proxy-" + spec.ProcessName + ".zip");
            if (!Download("https://github.com/" + zipRel, tmpZip, 500000, log))
            {
                log("内核下载失败，已尝试全部镜像。");
                return false;
            }

            var extractDir = Path.Combine(Path.GetTempPath(), "win7proxy-extract-" + spec.ProcessName);
            if (Directory.Exists(extractDir)) try { Directory.Delete(extractDir, true); } catch { }
            Directory.CreateDirectory(extractDir);

            try
            {
                log("解压 " + Path.GetFileName(tmpZip));
                ZipFile.ExtractToDirectory(tmpZip, extractDir);
            }
            catch (Exception ex)
            {
                log("解压失败：" + ex.Message);
                return false;
            }

            var found = FindFile(extractDir, spec.ExeName);
            if (found == null)
            {
                log("压缩包里没找到 " + spec.ExeName);
                return false;
            }

            var target = Path.Combine(coreDir, spec.ExeName);
            File.Copy(found, target, true);
            log("已安装 " + spec.ExeName + "（" + new FileInfo(target).Length + " 字节）");

            // 附带的可执行文件（v2ray 需要 v2ctl.exe）
            if (kind == CoreKind.V2ray)
            {
                var ctl = FindFile(extractDir, "v2ctl.exe");
                if (ctl != null) { File.Copy(ctl, Path.Combine(coreDir, "v2ctl.exe"), true); log("已安装 v2ctl.exe"); }
            }

            // geo 数据：v2ray 系用 .dat；sing-box 用按标签拆分的 .srs 规则集
            // （sing-box 1.12+ 起 geosite/geoip 改为 .srs 格式，单体 geosite.db/geoip.db 在 1.14.0 已失效）。
            if (spec.NeedsGeoDb)
            {
                Download("https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/" + CoreConstants.GeoIpCnSrs,
                         Path.Combine(coreDir, CoreConstants.GeoIpCnSrs), 5000, log);
                Download("https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/" + CoreConstants.GeoSiteCnSrs,
                         Path.Combine(coreDir, CoreConstants.GeoSiteCnSrs), 5000, log);
                Download("https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/" + CoreConstants.GeoSiteAdsSrs,
                         Path.Combine(coreDir, CoreConstants.GeoSiteAdsSrs), 5000, log);
            }
            else
            {
                Download("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
                         Path.Combine(coreDir, CoreConstants.GeoIpFile), 100000, log);
                Download("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
                         Path.Combine(coreDir, CoreConstants.GeoSiteFile), 100000, log);
            }

            return true;
        }

        /// <summary>查询 sing-box 最新版本号（去掉前导 v）；失败返回 null。</summary>
        public static string FetchLatestSingboxVersion(Action<string> log)
        {
            const string api = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
            // API 直连通常最快且镜像未必代理 api 域名，所以先试直连
            foreach (var prefix in new[] { "", "https://gh-proxy.org/", "https://ghproxy.com/" })
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Win7Proxy");
                        wc.Headers.Add("Accept", "application/vnd.github+json");
                        var json = wc.DownloadString(prefix + api);
                        var tag = (string)JObject.Parse(json)["tag_name"];
                        if (!string.IsNullOrEmpty(tag)) return tag.TrimStart('v', 'V');
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke("  版本查询失败(" + (prefix == "" ? "直连" : prefix) + ")：" + ex.Message);
                }
            }
            return null;
        }

        /// <summary>依次尝试各镜像下载文件，成功返回 true。</summary>
        private static bool Download(string url, string outFile, long minBytes, Action<string> log)
        {
            if (File.Exists(outFile)) { try { File.Delete(outFile); } catch { } }

            foreach (var m in Mirrors)
            {
                var full = m + url;
                log("尝试：" + full);
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Headers.Add("User-Agent", "Win7Proxy");
                        wc.DownloadFile(full, outFile);
                    }
                    var sz = File.Exists(outFile) ? new FileInfo(outFile).Length : 0;
                    if (sz >= minBytes)
                    {
                        log("  OK：" + Path.GetFileName(outFile) + "（" + sz + " 字节）");
                        return true;
                    }
                    log("  尺寸异常（" + sz + " 字节），换下一个镜像");
                }
                catch (Exception ex)
                {
                    log("  失败：" + ex.Message);
                }
            }
            log("全部镜像均失败：" + url);
            return false;
        }

        private static string FindFile(string dir, string fileName)
        {
            try
            {
                var files = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories);
                if (files.Length > 0) return files[0];
            }
            catch { }
            return null;
        }
    }
}
