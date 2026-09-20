#!/usr/bin/env bash
# pack.sh — 在 Linux 上构建 Win7Proxy 并打包成可分发的 zip
# 需要：.NET 8 SDK（脚本假定位于 ./.dotnet/dotnet）、curl、unzip
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
STAGE="$ROOT/stage"
# 第 1 个参数是"本程序版本号"（只用于产物文件名），
# 第 2 个参数可选，才是要下载的 Xray 版本 tag；不传则查 latest。
# 旧脚本把同一个 VER 既当产物版本又当 Xray tag，传 ./pack.sh v1.5.0 会去下载
# 并不存在的 Xray 发布标签，结果包里的内核和 geo 数据全是空的。
APPVER="${1:-v1.5.3}"
XRAY_VER="${2:-}"
OUT="$ROOT/Win7Proxy-${APPVER}.zip"

echo "==> 1) 构建 Win7Proxy (net461)"
"$DOTNET" build "$ROOT/Win7Proxy/Win7Proxy.csproj" -c Release -f net461 || exit 1

BIN="$ROOT/Win7Proxy/bin/Release/net461"
rm -rf "$STAGE"
mkdir -p "$STAGE/core" "$STAGE/Rules"

echo "==> 2) 复制主程序与依赖"
# 直接把输出目录里的 exe/dll/config 全拷过去，不再手写清单（手写清单容易漏拷依赖）
find "$BIN" -maxdepth 1 -type f \( -name '*.exe' -o -name '*.dll' -o -name '*.config' \) -exec cp {} "$STAGE/" \;
[ -f "$ROOT/Win7Proxy/app.ico" ] && cp "$ROOT/Win7Proxy/app.ico" "$STAGE/app.ico"
for f in Win7Proxy.exe ProxyCore.dll; do
    [ -f "$STAGE/$f" ] || echo "警告: 缺少 $f"
done

echo "==> 3) (尽力) 下载 xray-win7 内核与 geo 数据"
# 网速受限环境可能失败；失败时仍产出可用包，用户在 Windows 上首次运行
# fetch-core.bat 即可补齐内核与 geo 数据。
if [ -z "$XRAY_VER" ]; then
    for i in 1 2 3; do
        XRAY_VER="$(curl -s --max-time 30 "https://api.github.com/repos/XTLS/Xray-core/releases/latest" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')"
        [ -n "$XRAY_VER" ] && break
        sleep 3
    done
fi
if [ -n "$XRAY_VER" ]; then
    echo "Xray 版本: $XRAY_VER"
    XRAY_ZIP="$(mktemp -d)/Xray-win7-64.zip"
    if curl -fL --max-time 180 -o "$XRAY_ZIP" "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VER/Xray-win7-64.zip"; then
        if command -v unzip >/dev/null 2>&1; then
            unzip -o "$XRAY_ZIP" -d "$STAGE/core" >/dev/null 2>&1 && echo "已解压 xray 内核"
        else
            python3 - "$XRAY_ZIP" "$STAGE/core" <<'PY' && echo "已解压 xray 内核"
import sys, zipfile
zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])
PY
        fi
        if [ ! -f "$STAGE/core/xray.exe" ]; then
            echo "警告: 解压后未找到 xray.exe，打包将不含内核，请在 Windows 上运行 fetch-core.bat"
        fi
    else
        echo "警告: 下载 xray 内核失败，打包将不含内核，请在 Windows 上运行 fetch-core.bat"
    fi
    # geo 数据：与 CoreDownloader.cs / CI 保持同一来源（v2fly 的 domain-list-community
    # 已不再提供 geosite.dat 发布资源，会 404），并逐个镜像兜底。
    dl_geo() { # <相对路径> <输出文件> <最小字节>
        local rel="$1" out="$2" min="${3:-100000}"
        for m in "https://gh-proxy.org/" "https://ghproxy.com/" "https://ghproxy.net/" "https://mirror.ghproxy.com/" ""; do
            rm -f "$out"
            if curl -fL --max-time 180 -o "$out" "${m}https://github.com/${rel}"; then
                local sz; sz="$(stat -c%s "$out" 2>/dev/null || echo 0)"
                if [ "${sz:-0}" -ge "$min" ]; then echo "  OK: $out（$sz 字节）"; return 0; fi
                echo "  尺寸异常($sz)，换下一个镜像"
            fi
        done
        rm -f "$out"
        return 1
    }
    dl_geo "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat" "$STAGE/core/geoip.dat" 100000 \
        || echo "警告: geoip.dat 下载失败，请在 Windows 上运行 fetch-core.bat 补齐"
    dl_geo "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat" "$STAGE/core/geosite.dat" 100000 \
        || echo "警告: geosite.dat 下载失败，请在 Windows 上运行 fetch-core.bat 补齐"
else
    echo "警告: 无法获取 Xray 版本号，跳过内核与 geo 下载（将在 Windows 上由 fetch-core.bat 补齐）"
fi

echo "==> 4) 复制规则与脚本"
cp "$ROOT/Rules/pac-rules.txt" "$STAGE/Rules/"
cp "$ROOT/fetch-core.bat" "$STAGE/"
cp "$ROOT/README.md" "$STAGE/"

echo "==> 5) (尽力) 下载 .NET Framework 4.8 离线安装包"
if curl -L --max-time 180 -o "$STAGE/ndp48-x86-x64-allos-chs.exe" "https://go.microsoft.com/fwlink/?linkid=2088631"; then
    SIZE="$(stat -c%s "$STAGE/ndp48-x86-x64-allos-chs.exe" 2>/dev/null || echo 0)"
    if [ "${SIZE:-0}" -lt 50000000 ]; then
        echo "安装包尺寸异常($SIZE)，移除，请在 README 指引下手动安装 .NET 4.8"
        rm -f "$STAGE/ndp48-x86-x64-allos-chs.exe"
    fi
else
    echo "下载 .NET 4.8 失败，跳过（不影响主程序，请按 README 手动安装）"
fi

echo "==> 6) 打包 $OUT"
rm -f "$OUT"
python3 - "$STAGE" "$OUT" <<'PY'
import sys, zipfile, os
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(stage):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, stage))
PY
echo "完成：$(ls -lh "$OUT" | awk '{print $5, $9}')"
echo "stage 内容："; ( cd "$STAGE" && find . -type f | sort )
