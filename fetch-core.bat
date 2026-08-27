@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "BASE=%~dp0"
set "CORE=%BASE%core"
if not exist "%CORE%" mkdir "%CORE%"

echo [Win7Proxy] Downloading Xray-core (win7) kernel and geo data ...
echo Target: %CORE%
echo.

call :dl "XTLS/Xray-core/releases/latest/download/Xray-win7-64.zip" "%TEMP%\Xray-win7-64.zip"
if not exist "%TEMP%\Xray-win7-64.zip" goto :fail
call :unzip "%TEMP%\Xray-win7-64.zip" "%CORE%"

call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat" "%CORE%\geoip.dat"
call :dl "Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat" "%CORE%\geosite.dat"

echo.
echo [Win7Proxy] Done. core folder contents:
dir "%CORE%"
echo.
echo You can close this window and double-click Win7Proxy.exe to start.
goto :eof

:dl
set "REL=%~1"
set "OUT=%~2"
if exist "%OUT%" del /f /q "%OUT%" >nul 2>&1
set "OK=0"
for %%P in ("https://gh-proxy.org/" "https://ghproxy.com/" "https://ghproxy.net/" "https://mirror.ghproxy.com/" "") do (
  if "!OK!"=="0" (
    set "URL=%%Phttps://github.com/%REL%"
    echo.
    echo Trying: !URL!
    REM method 1: bitsadmin shows live progress (percent + speed)
    bitsadmin /transfer "w7pdl!RANDOM!" /download /priority normal "!URL!" "%OUT%"
    if exist "%OUT%" for %%S in ("%OUT%") do if %%~zS GTR 500 set "OK=1"
    if "!OK!"=="0" (
      REM method 2: certutil fallback (silent)
      echo   bitsadmin failed, retry with certutil ...
      certutil -urlcache -split -f "!URL!" "%OUT%" >nul 2>&1
      if exist "%OUT%" for %%S in ("%OUT%") do if %%~zS GTR 500 set "OK=1"
    )
  )
)
if "!OK!"=="1" (echo   OK: %OUT%) else (echo   FAILED: %REL%)
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

:fail
echo.
echo [Win7Proxy] Failed to download Xray kernel from all sources.
echo Possible causes:
echo   1. Network cannot reach GitHub/mirrors. Try a VPN or manual download.
echo   2. Windows 7 lacks TLS 1.2 support. Install KB4474419 / KB4490628,
echo      or the .NET/TLS update, then retry.
echo.
echo Manual: download Xray-win7-64.zip from
echo   https://github.com/XTLS/Xray-core/releases
echo and put xray.exe into core\
pause
exit /b 1
