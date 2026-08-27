# Win7Proxy — Windows 7 代理客户端 设计文档 (Spec)

- **日期**: 2026-08-27
- **作者**: Sisyphus (编排) + 用户
- **状态**: 待用户评审
- **目标平台**: Windows 7 SP1 (x64)，需安装 .NET Framework 4.8

## 1. 目标与范围

构建一个能在 Windows 7 上运行的代理客户端，满足：

- 导入订阅（三种主流格式：v2rayN base64、`Clash YAML`、`SIP008`）。
- 支持主流协议：VMess、VLESS(+XTLS/REALITY)、Trojan、Shadowsocks/SS2022、Socks、HTTP。
- 流量接管方式：**系统代理 + PAC 分流**（无驱动、无 TUN，Win7 最稳）。
- 内核：**Xray-core 官方 `-win7` 构建**（用 XTLS/go-win7 补丁 Go 编译，唯一能在 Win7 跑的 Xray 版本）。
- 交付：在 Linux 上交叉编译出的 `Win7Proxy.exe`（.NET Framework 4.8 程序集）+ 打包内核与资源，用户装好 .NET 4.8 后双击即用。

**MVP（首版）功能**：托盘图标 + 主窗口；添加/刷新订阅；节点列表 + TCP 延迟测试；选节点 → 启动/停止；模式切换（全局 / 规则(PAC) / 直连）；实时日志；手动「更新内核」+ 首次运行自动拉取 xray-win7/geo 数据。

**明确不在首版范围**：TUN 全局接管、扫码导入、后台自动更新调度、可视化路由规则编辑器、测速曲线、多内核切换（sing-box）、订阅分组的高级编辑（订阅分组数据可存储，但不提供复杂编辑 UI）。

## 2. 关键约束

1. **Windows 7**：标准 Go 1.21+ 已不支持 Win7 运行；Xray 必须用带 `-win7` 后缀的发布包。GUI 必须基于 .NET Framework（Win7 最高支持到 **4.8**；**4.8.1 起仅支持 Win10/11**，不能用于 Win7）。本项目编译目标为 `net461`，可在装有 .NET Framework 4.6.1 及以上的 Win7（含 4.8）上原生运行。
2. **交叉编译**：开发机是 Linux，无 Windows。C#/.NET Framework 的 WinForms 程序可通过 .NET SDK + `Microsoft.NETFramework.ReferenceAssemblies.net461` 在 Linux 交叉编译为 `.exe`（输出为 .NET Framework 4.6.1 程序集，在装有 .NET 4.6.1+ 的 Win7 上直接运行）。UI 必须用**纯代码**编写（不用 `.resx` 设计器），以便 Linux 编译。
3. **无法在本机运行 Win7 GUI**：逻辑层（ProxyCore）可编译并跑单元测试；GUI 与整链路集成必须在用户 Win7 上冒烟测试。
4. **Win7 TLS/根证书**：Win7 默认可能缺 TLS1.2/新根证书，导致 https 订阅拉取或内核下载失败。对策：代码中 `ServicePointManager.SecurityProtocol = Tls12`；README 提示安装相关 KB；首次拉取失败时可手动用 `fetch-core.ps1` 或浏览器下载后放入 `core/`。

## 3. 架构

### 3.1 进程模型
- 主程序 `Win7Proxy.exe`（WinForms）是编排者，不含代理逻辑本身。
- 启动时：根据所选节点生成 `core/config.json` → 以子进程启动 `core/xray.exe run -c core/config.json` → 设置系统代理/PAC 指向 xray 本机入站 → 开始抓取日志。
- 停止时：结束 xray 进程树 → 清除系统代理/PAC。

### 3.2 解决方案布局
```
Win7Proxy.sln
├─ Win7Proxy/            (net48 exe, WinForms 纯代码 UI) — 托盘、主窗口、编排
├─ ProxyCore/           (net48 + net8.0 多目标类库) — 纯逻辑，无 UI 依赖
│  ├─ Models/           Node.cs, Subscription.cs, ProxyMode.cs, XrayConfig.cs(POCO)
│  ├─ Parsers/          ISubscriptionParser.cs, V2rayNParser.cs, ClashParser.cs, Sip008Parser.cs
│  ├─ XrayConfigBuilder.cs
│  ├─ PacGenerator.cs
│  ├─ SystemProxy.cs
│  ├─ CoreProcess.cs
│  └─ SubscriptionFetcher.cs
├─ ProxyCore.Tests/     (net8.0 xUnit) — 解析器/配置/PAC 单元测试（Linux 可跑）
├─ core/                xray.exe(win7), geoip.dat, geosite.dat  (二进制资源，下载得到)
├─ Rules/               pac-rules.txt (CN 直连域名/广告域名种子)
├─ fetch-core.ps1       拉取最新 xray-win7 + geoip.dat/geosite.dat
└─ README.md            使用说明 + .NET 4.8 前提 + 排错
```
`ProxyCore` 与 UI 拆分，使其可在 Linux 用 net8.0 跑测试。

## 4. 数据模型 (ProxyCore/Models)

```csharp
enum NodeType { Vmess, Vless, Trojan, Shadowsocks, Socks, Http }
enum ProxyMode { Global, Rule, Direct }

class Node {
    string Remarks; NodeType Type;
    string Address; int Port;
    string UUID;            // vmess/vless/trojan 的 id
    string Security;        // vmess: aes-128-gcm / none / zero
    string EncryptMethod;   // ss: 加密方法
    string Password;        // ss/trojan 密码
    bool TLS;               // 是否启用了 TLS
    string Flow;            // vless flow (如 xtls-rprx-vision)
    string Network;         // tcp / websocket / grpc / h2 / quic
    string Path;            // ws/grpc/h2 path
    string Host;            // ws host / http host
    string SNI;
    string Fingerprint;     // uTLS 指纹
    string PublicKey;       // REALITY pubkey
    string ShortId;         // REALITY shortId
    string ServiceName;     // grpc serviceName
    bool AllowInsecure;
    int LatencyMs;
    Dictionary<string,string> Extra;
}

class Subscription { string Url; string Name; List<Node> Nodes; DateTime Updated; }
```

## 5. 订阅解析 (ProxyCore/Parsers)

统一接口 `ISubscriptionParser { bool CanParse(string raw); List<Node> Parse(string raw); }`。`SubscriptionFetcher` 按优先级探测格式：
1. 去掉空白后若可 base64 解码且解码文本每行以 `vmess://`/`vless://`/`trojan://`/`ss://` 开头 → **V2rayN**。
2. 若含 `proxies:` 或顶层为 YAML 映射 → **Clash**。
3. 若以 `{` 开头且含 `"servers"` → **SIP008**。

- **V2rayNParser.ParseSubscription(base64Text)**：URL-safe base64 解码（补 padding）→ 按行 `ParseLink`。
  - `vmess://`：base64(JSON)，字段 `{v,ps,add,port,id,aid,scy,net,type,host,path,tls,sni,...}` → Node。
  - `vless://`：`vless://uuid@host:port?encryption=none&security=tls&type=ws&path=&host=&flow=&sni=&fp=&pbk=&sid=#remarks`（remarks 为 URL 编码）→ 解析 query。
  - `trojan://`：`trojan://password@host:port?security=tls&type=&path=&host=&sni=#remarks`。
  - `ss://`：兼容 SIP002（`ss://base64(method:password)@host:port#remarks` / `ss://method:password@host:port#remarks`）与旧式（`ss://base64(method:password@host:port)#remarks`）。
- **ClashParser**：用 `YamlDotNet` 解析 `proxies:` 下各节点（`type: ss/vmess/vless/trojan/socks/http`）→ Node；提取 `proxy-groups`/`rules` 存为路由提示（首版主要用节点列表）。
- **Sip008Parser**：JSON `{version, servers:[{id,remarks,server,server_port,method,password,plugin,plugin_opts}]}` → Shadowsocks Node。

## 6. Xray 配置生成 (ProxyCore/XrayConfigBuilder)

`BuildConfig(Node node, ProxyMode mode, string geoAssetDir) → XrayConfig(POCO)`，用 `Newtonsoft.Json` 序列化为 `core/config.json`。

固定结构：
```json
{
  "log": { "loglevel": "warning", "access": "core/access.log", "error": "core/error.log" },
  "inbounds": [
    { "tag":"socks", "port":10808, "listen":"127.0.0.1", "protocol":"socks",
      "settings": { "udp":true, "auth":"noauth" } },
    { "tag":"http", "port":10809, "listen":"127.0.0.1", "protocol":"http" }
  ],
  "outbounds": [
    { "tag":"proxy", "protocol":<协议>, "settings":{...}, "streamSettings":{...}, "mux":{...} },
    { "tag":"direct", "protocol":"freedom", "settings":{} },
    { "tag":"block", "protocol":"blackhole", "settings":{ "response":{ "type":"http" } } }
  ],
  "routing": { "domainStrategy":"IPIfNonMatch", "rules": [ ... ] }
}
```
- `proxy` outbound 由 Node 按协议/传输/TLS 映射（含 streamSettings：network、ws 的 path/host、grpc 的 serviceName、tls 的 alpn/sni/fingerprint、REALITY 的 pbk/sid、vmess 的 security/aid 等）。
- **Global 模式**：routing 默认走 proxy，仅保留私有网段直连。
- **Rule 模式**：追加 `geosite:category-ads → block`、`geoip:cn` + `geoip:private` + `geosite:cn → direct`。
- **Direct 模式**：系统代理关闭，routing 全走 direct（仅用于「直连」开关，不启动 xray 也可）。

## 7. PAC 与系统代理 (ProxyCore/PacGenerator + SystemProxy)

- **PacGenerator.Build(mode, rules, pacPort)**：生成 `proxy.pac`（JS），`FindProxyForURL(url,host)` 返回 `PROXY 127.0.0.1:10809` 或 `DIRECT`。
  - Global：恒 `PROXY`。
  - Rule：内置 CN 直连域名种子（来自 `Rules/pac-rules.txt`，DOMAIN-SUFFIX/KEYWORD/CIDR）→ 匹配直连，否则代理；广告域名 → 走 block（或直接 DIRECT）。
  - Direct：恒 `DIRECT`（同时系统代理关闭）。
- **SystemProxy.SetProxy()**：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`：`ProxyEnable=1`，`ProxyServer="http=127.0.0.1:10809;socks=127.0.0.1:10808"`；Rule 模式改为设置 `AutoConfigURL="http://127.0.0.1:<pacPort>/proxy.pac"` 并清空 ProxyServer。随后 P/Invoke `InternetSetOption(0, INTERNET_OPTION_SETTINGS_CHANGED|INTERNET_OPTION_REFRESH, ...)` 立即生效。
- **SystemProxy.Clear()**：`ProxyEnable=0`，删除 `AutoConfigURL`。
- PAC 由本机 `HttpListener`（`http://127.0.0.1:<pacPort>/proxy.pac`）提供；pacPort 默认取如 `10810`（可配置，冲突时自增）。

## 8. 内核进程管理 (ProxyCore/CoreProcess)

- `Start(xrayPath, configPath, Action<string> onLog)`：启动 `xray.exe run -c config.json`，重定向 stdout/stderr 逐行回调（供日志框）；记录 PID。
- 启动后 TCP 探测 `127.0.0.1:10808/10809` 确认监听（健康检查）。
- `Stop()`：结束进程树（含子进程）。
- 解析日志中的启动成功/致命错误，向 UI 回报状态。

## 9. 订阅拉取 (ProxyCore/SubscriptionFetcher)

- `Fetch(Subscription sub)`：`HttpClient`（启用 Tls12）下载 → 按格式分发解析 → 合并/替换节点列表 → 更新 `Updated`。
- 失败（TLS/证书/网络）时抛出明确异常，UI 提示用 `fetch-core.ps1` 或浏览器手动获取后放 `core/`。

## 10. GUI (Win7Proxy, net48 WinForms 纯代码)

- `Program.cs`：`EnableVisualStyles` + `SetCompatibleTextRenderingDefault(false)` + 单实例 `Mutex` + `Application.Run(new MainForm())`。
- `MainForm.cs`：菜单/工具栏 + `DataGridView` 节点表（备注、类型、地址、延迟、来源）；按钮：导入订阅、添加节点、测试延迟、启动、停止、模式(全局/规则/直连)、更新内核；`NotifyIcon` 托盘（显示/启动/停止/退出）；状态栏（运行中/已停 + 当前节点）；日志 `TextBox`（尾部追加入口日志）。
- `SubscriptionForm.cs`：输入 URL + 名称 → 拉取 → 显示节点数 → 加入列表。
- 持久化：订阅与节点、当前选择存 `%AppData%/Win7Proxy/config.json`（Newtonsoft.Json）。
- 首次运行：检查 `core/xray.exe` 是否存在，缺失则提示运行 `fetch-core.ps1` 或自动拉取。
- 延迟测试：对选中/全部节点 `TcpClient` 连接 `Address:Port`，超时 2000ms，记录 `LatencyMs`。

## 11. 构建与交付

- 项目：`Win7Proxy.sln` 含 `ProxyCore`(多目标 `net48;net8.0`)、`Win7Proxy`(net48 exe，引用 ProxyCore)、`ProxyCore.Tests`(net8.0 xUnit)。
- NuGet：`Newtonsoft.Json`、`YamlDotNet`、`Microsoft.NETFramework.ReferenceAssemblies.net48`（构建期，保证 Linux 可解析 net48 引用）。
- Linux 交叉编译：`dotnet build Win7Proxy/Win7Proxy.csproj -c Release -f net48` → 产出 `bin/Release/net48/Win7Proxy.exe`（含 ProxyCore.dll、Newtonsoft、YamlDotNet 等）。
- 打包脚本（实现阶段编写 `pack.sh`）：将 exe + dll + `core/`(xray.exe, geoip.dat, geosite.dat) + `Rules/` + `ndp48-x86-x64-allos-chs.exe`(.NET 4.8 离线安装包) + `fetch-core.ps1` + `README.md` 压成 `Win7Proxy-v1.0.zip`。
- `fetch-core.ps1`：从 GitHub Releases 下载 `Xray-windows-64-win7.zip` 解压得 `xray.exe`；下载 `geoip.dat`(`v2fly/geoip`)、`geosite.dat`(`v2fly/domain-list-community`)。

## 12. 测试

- **ProxyCore.Tests（Linux 可跑，`dotnet test`）**：
  - V2rayN：base64 编解码 + 四种协议链接各一例 → 断言 Node 字段正确。
  - Clash：样例 YAML → 断言节点数/字段。
  - SIP008：样例 JSON → 断言 SS 节点。
  - XrayConfigBuilder：样例 VLESS+Reality 节点 → 断言 config.json 含正确 inbounds/outbounds/routing。
  - PacGenerator：Rule 模式输出含 `PROXY`/`DIRECT` 与预期域名。
- **GUI/集成（用户 Win7 冒烟）**：启动 xray 后本机端口监听、系统代理切换生效、PAC 浏览器分流、订阅导入三种格式、延迟测试、启动/停止状态。

## 13. 风险与兜底

- **net48 WinForms 在 Linux 交叉编译遇不支持 API**（概率低）：兜底改为 Go + Walk（静态单文件 exe，零运行时）或 MinGW C++ 原生 exe；两者均可在 Linux 编出 Win7 原生 exe，但 GUI 代码量更大、观感更朴素。用户已接受此兜底。
- **Win7 TLS/根证书**导致首次下载失败：已用 Tls12 + README 提示 + 手动放置兜底。
- **GUI 无法本机可视验证**：以逻辑单测 + 编译成功 + PE 合法性为证据，最终由用户在 Win7 验证。

## 14. 验收标准

1. `dotnet build -f net48` 在 Linux 成功产出 `Win7Proxy.exe`，`file` 识别为 Mono/.Net assembly。
2. `dotnet test`（ProxyCore.Tests）全绿。
3. 打包 zip 内含 exe、xray-win7、geo 数据、.NET 4.8 安装包、fetch 脚本、README。
4. 用户在 Win7：装 .NET 4.8 → 双击 exe → 导入三种格式订阅各一次成功 → 选节点启动 → 系统代理/PAC 生效 → 停止清除代理。
