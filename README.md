# Win7Proxy — Windows 7 代理客户端

一个能在 **Windows 7 SP1 (x64)** 上运行的代理客户端，基于 **Xray-core（官方 `-win7` 构建）**，
支持导入 **v2rayN / Clash / SIP008** 三种订阅，覆盖 VMess、VLESS(+XTLS/REALITY)、Trojan、
Shadowsocks/SS2022、Socks、HTTP 等主流协议。流量接管方式为 **系统代理 + Xray 路由分流**（无驱动、无 TUN）：规则模式下系统代理指向本机 Xray 端口，由 Xray 按 geoip/geosite 规则把国内/私有网段直连、其余走代理。

## 一、前置条件（一次性的）

1. **Windows 7 SP1 x64**。
2. 安装 **.NET Framework 4.6.1 及以上**即可（本项目编译目标为 `net461`，已装的 4.6.1 可直接运行；
   Win7 最高支持到 **4.8**，**4.8.1 起仅支持 Win10/11**，请勿在 Win7 上装 4.8.1）。
   若尚无任何 .NET Framework，可运行压缩包内的 `ndp48-x86-x64-allos-chs.exe`，或到
   https://dotnet.microsoft.com/download/dotnet-framework/net48 下载 4.8 离线安装包（Win7 可用）。
3. 建议安装 Windows 更新补丁 **KB4474419 / KB4490628**（让老系统正确支持 TLS 1.2，
   否则 https 订阅可能拉取失败）。

## 二、首次运行

1. 解压 `Win7Proxy-v1.0.zip` 到任意目录（路径不要含中文/空格最佳）。
2. 内核已随包提供：`core\xray.exe` 与 `core\geoip.dat` / `core\geosite.dat` 解压即用，无需手动下载。
   仅当要**更新**内核时，再以管理员身份运行 `fetch-core.bat`（右键 → 以管理员身份运行）拉取最新 xray-win7。
3. 双击 `Win7Proxy.exe`。

## 三、使用

- **导入订阅**：菜单「导入订阅」→ 填写订阅 URL 与名称 → 确定。支持：
  - v2rayN 订阅（base64 包裹的 vmess/vless/trojan/ss 链接）
  - Clash 订阅（YAML `proxies:` 列表）
  - SIP008（Shadowsocks 官方 JSON）
  程序会自动识别格式。
- **添加节点**：菜单「添加节点」→ 粘贴单条 `vmess://` / `vless://` / `trojan://` / `ss://` 链接。
- **测试延迟**：在节点列表里**按住 Ctrl 逐个点选、或 Shift 框选**多个节点，再点「测试延迟」即可批量测延迟
  （点「全选」可一键选中全部；不选任何行则点「测试延迟」会测全部）。测试按最多 16 并发进行，
  按 `Address:Port` 的 TCP 连通性测延迟，结果写回「延迟」列。
- **批量删除**：选中多个节点后点「删除选中」可一次移除（会二次确认）。
- **按列排序**：点击列表表头可按该列排序（再点一次切换升/降序）。延迟列按数值排序，
  未测（显示为 `-`）的节点会排到最后。排序后刷新列表（如测延迟）会保持你当前的滚动位置和选中项。
- **选择节点 + 启动**：在列表**单击选中一个节点**（列表下方「当前节点」会显示你选中的是谁），选好模式后点「启动」即用该节点联网；
  **双击某一行可直接以该节点启动**；想换节点就点另一个节点再点「启动」（会先停掉旧的内核再起新的）。
  - 模式「全局」：所有流量走代理。
  - 模式「规则」：国内/私有网段直连，其余走代理（由 Xray 路由规则根据 geoip/geosite 自动分流，无需 PAC 文件）。
  - 模式「直连」：关闭代理。
- **停止**：点「停止」会结束 xray 并恢复系统代理为「不使用代理」。
- **托盘**：右下角托盘图标可快速「启动 / 停止 / 显示窗口 / 退出」。
- **更新内核**：菜单「更新内核」会尝试运行 `fetch-core.bat` 拉取最新 xray-win7。

## 四、配置文件

- 订阅与节点、当前选择、模式保存在 `%AppData%\Win7Proxy\app.json`，退出时自动保存。
- 运行期生成的 xray 配置在 `core\config.json`，日志在 `core\access.log` / `core\error.log`。

## 五、排错

- **启动报“找不到 xray.exe”**：按第二节放置内核。
- **订阅拉取失败 / TLS 错误**：安装 KB4474419/KB4490628，或手动下载订阅文本后“添加节点”。
- **系统代理未生效**：确认浏览器使用“使用系统代理”。修改模式或节点后建议重启一下浏览器，让它重新读取系统代理设置。
- **无法访问内网/局域网**：私有网段默认直连，一般无需处理。

## 六、从源码构建（开发者）

在 Windows 上用 Visual Studio 2019/2022 打开 `Win7Proxy.sln` 直接生成即可。
也可在 Linux/macOS 用 .NET SDK 交叉编译（编译目标为 `net461`，兼容 Win7 的 .NET 4.6.1+）：

```
dotnet build Win7Proxy/Win7Proxy.csproj -c Release -f net461
```

（需 `Microsoft.NETFramework.ReferenceAssemblies.net461` 提供 net461 引用程序集；
`ProxyCore` 同时多目标 `net461;net8.0`，被 GUI 引用时取 net461。）
逻辑层 `ProxyCore` 可单独用 `dotnet test ProxyCore.Tests/ProxyCore.Tests.csproj` 跑单元测试。

### 自动构建与发布

仓库已配置 GitHub Actions（`.github/workflows/build.yml`）：推送 `main` 或手动触发时，
会在 Linux  runner 上构建 `net461` Release，并把产物打包成 `Win7Proxy-v1.0.zip` 作为构件上传；
打 `v*` 标签（如 `v1.0.0`）推送时，会自动创建 GitHub Release 并附上该 zip，无需本地手工打包。
