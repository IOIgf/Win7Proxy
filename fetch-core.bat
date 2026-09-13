@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "BASE=%~dp0"
set "CORE=%BASE%core"
if not exist "%CORE%" mkdir "%CORE%"

REM 用法: fetch-core.bat [xray ^| v2ray ^| singbox]   默认 xray
set "KIND=%~1"
if "%KIND%"=="" set "KIND=xray"

echo [Win7Proxy] Fetching %KIND% kernel and geo data ...
echo Target: %CORE%
echo.

if /i "%KIND%"=="v2ray" goto :v2ray
if /i "%KIND%"=="singbox" goto :singbox
if /i "%KIND%"=="sing-box" goto :singbox
goto :xray

REM ---------------- Xray（默认） ----------------
:xray
set "EXE=xray.exe"
REM Windows 7 只能用专门的 win7 构建（Go 1.20 编译），Win10+ 用标准构建
ver | findstr /i "6\.1\." >nul
if not errorlevel 1 (
  set "ZIP=Xray-win7-64.zip"
  echo Detected Windows 7 - using the win7 build.
) else (
  set "ZIP=Xray-windows-64.zip"
)
call :dl "XTLS/Xray-core/releases/latest/download/%ZIP%" "%TEMP%\%ZIP%" 500000
if not exist "%TEMP%\%ZIP%" goto :fail
call :unzip "%TEMP%\%ZIP%" "%CORE%"
call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat" "%CORE%\geoip.dat" 100000
call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat" "%CORE%\geosite.dat" 100000
goto :done

REM ---------------- V2Ray ----------------
:v2ray
set "EXE=v2ray.exe"
set "ZIP=v2ray-windows-64.zip"
call :dl "v2fly/v2ray-core/releases/latest/download/%ZIP%" "%TEMP%\%ZIP%" 500000
if not exist "%TEMP%\%ZIP%" goto :fail
call :unzip "%TEMP%\%ZIP%" "%CORE%"
call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat" "%CORE%\geoip.dat" 100000
call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat" "%CORE%\geosite.dat" 100000
goto :done

REM ---------------- sing-box ----------------
:singbox
set "EXE=sing-box.exe"
REM sing-box 的发布包文件名带版本号，必须先查最新 tag
echo Querying latest sing-box version ...
set "SBJSON=%TEMP%\w7p_singbox.json"
if exist "%SBJSON%" del /f /q "%SBJSON%" >nul 2>&1
bitsadmin /transfer "w7psb!RANDOM!" /download /priority normal "https://api.github.com/repos/SagerNet/sing-box/releases/latest" "%SBJSON%" >nul 2>&1
if not exist "%SBJSON%" (
  certutil -urlcache -split -f "https://api.github.com/repos/SagerNet/sing-box/releases/latest" "%SBJSON%" >nul 2>&1
)
set "SBVER="
if exist "%SBJSON%" (
  for /f "tokens=2 delims=:," %%A in ('findstr /i /c:"tag_name" "%SBJSON%"') do if not defined SBVER set "SBVER=%%A"
)
if defined SBVER set "SBVER=!SBVER: =!"
if defined SBVER set "SBVER=!SBVER:"=!"
if not defined SBVER (
  echo Could not determine the latest sing-box version.
  echo Download it manually from https://github.com/SagerNet/sing-box/releases
  echo and put sing-box.exe into core\
  goto :fail
)
set "SBNUM=%SBVER:~1%"
echo Latest sing-box: %SBVER%
set "ZIP=sing-box-%SBNUM%-windows-amd64.zip"
call :dl "SagerNet/sing-box/releases/download/%SBVER%/%ZIP%" "%TEMP%\%ZIP%" 500000
if not exist "%TEMP%\%ZIP%" goto :fail
call :unzip "%TEMP%\%ZIP%" "%CORE%"
REM sing-box 1.12+ 使用按标签拆分的 .srs 规则集，旧 .db 在 1.14 已失效
call :dl "SagerNet/sing-geoip/raw/rule-set/geoip-cn.srs" "%CORE%\geoip-cn.srs" 5000
call :dl "SagerNet/sing-geosite/raw/rule-set/geosite-cn.srs" "%CORE%\geosite-cn.srs" 5000
call :dl "SagerNet/sing-geosite/raw/rule-set/geosite-category-ads-all.srs" "%CORE%\geosite-category-ads-all.srs" 5000
goto :done

:done
echo.
echo [Win7Proxy] Done. core folder contents:
dir "%CORE%"
echo.
echo Make sure %EXE% is listed above, then start Win7Proxy.exe
echo and pick the matching kernel in the toolbar.
goto :eof

:fail
echo.
echo [Win7Proxy] Failed to download the %KIND% kernel from all sources.
echo Possible causes:
echo   1. Network cannot reach GitHub/mirrors. Try a proxy or manual download.
echo   2. Windows 7 lacks TLS 1.2 support. Install KB4474419 / KB4490628, then retry.
echo.
echo Manual fallback: download the zip from the project's GitHub Releases page
echo and put %EXE% into core\
pause
exit /b 1

:dl
set "REL=%~1"
set "OUT=%~2"
set "MIN=%~3"
if not defined MIN set "MIN=5000"
if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
set "OK=0"
for %%P in ("https://gh-proxy.org/" "https://ghproxy.com/" "https://ghproxy.net/" "https://mirror.ghproxy.com/" "") do (
  if "!OK!"=="0" (
    set "URL=%%Phttps://github.com/%REL%"
    echo.
    echo Trying: !URL!
    if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
    bitsadmin /transfer "w7pdl!RANDOM!" /download /priority normal "!URL!" "%OUT%" >nul 2>&1
    if exist "%OUT%" for %%S in ("%OUT%") do if %%~zS GEQ !MIN! set "OK=1"
    if "!OK!"=="0" (
      echo   bitsadmin failed, retry with certutil ...
      if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
      certutil -urlcache -split -f "!URL!" "%OUT%" >nul 2>&1
      if exist "%OUT%" for %%S in ("%OUT%") do if %%~zS GEQ !MIN! set "OK=1"
    )
  )
)
if "!OK!"=="1" (
  echo   OK: %OUT%
) else (
  if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
  echo   FAILED: %REL%
)
goto :eof

:unzip
set "ZIP=%~1"
set "DST=%~2"
if not exist "%DST%" mkdir "%DST%"
set "VBS=%TEMP%\w7p_unzip.vbs"
(
  echo Set fso=CreateObject^("Scripting.FileSystemObject"^)
  echo Set sa=CreateObject^("Shell.Application"^)
  echo z=WScript.Arguments^(0^)
  echo d=WScript.Arguments^(1^)
  echo If Not fso.FolderExists^(d^) Then fso.CreateFolder^(d^)
  echo Set zf=sa.NameSpace^(z^)
  echo Set df=sa.NameSpace^(d^)
  echo df.CopyHere zf.Items,16
) > "%VBS%"
echo Unzipping %ZIP% ...
cscript //nologo "%VBS%" "%ZIP%" "%DST%"
ping -n 3 127.0.0.1 >nul
goto :eof
