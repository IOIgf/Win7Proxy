#!/usr/bin/env bash
# pack.sh — 在 Linux 上构建 Win7Proxy 并打包成可分发的 zip
# 需要：.NET 8 SDK（脚本假定位于 ./.dotnet/dotnet）、curl、unzip
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="$ROOT/.dotnet/dotnet"
STAGE="$ROOT/stage"
VER="${1:-}"
OUT="$ROOT/Win7Proxy-${VER:-v1.1.0}.zip"

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
if [ -z "$VER" ]; then
    for i in 1 2 3; do
        VER="$(curl -s --max-time 30 "https://api.github.com/repos/XTLS/Xray-core/releases/latest" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')"
        [ -n "$VER" ] && break
        sleep 3
    done
fi
if [ -n "$VER" ]; then
    echo "Xray 版本: $VER"
    XRAY_ZIP="$(mktemp -d)/Xray-win7-64.zip"
    if curl -L --max-time 180 -o "$XRAY_ZIP" "https://github.com/XTLS/Xray-core/releases/download/$VER/Xray-win7-64.zip"; then
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
    # geo 数据（尺寸过小则视为失败并移除）
    for pair in "geoip.dat|https://github.com/v2fly/geoip/releases/latest/download/geoip.dat" \
                "geosite.dat|https://github.com/v2fly/domain-list-community/releases/latest/download/geosite.dat"; do
        name="${pair%%|*}"; url="${pair##*|}"
        if curl -L --max-time 180 -o "$STAGE/core/$name" "$url"; then
            SZ="$(stat -c%s "$STAGE/core/$name" 2>/dev/null || echo 0)"
            if [ "${SZ:-0}" -lt 100000 ]; then
                echo "警告: $name 下载异常(尺寸 $SZ)，移除，请在 Windows 上运行 fetch-core.bat 补齐"
                rm -f "$STAGE/core/$name"
            fi
        else
            echo "警告: 下载 $name 失败"
        fi
    done
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
