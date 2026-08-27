#!/usr/bin/env bash
# 用系统自带 csc.exe 编译 we-music-ctl（C#5 / .NET Framework / 零第三方依赖）
set -euo pipefail
cd "$(dirname "$0")"

CSC="C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"
FW="C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319"
G="C:\\Windows\\Microsoft.NET\\assembly\\GAC_MSIL"
G64="C:\\Windows\\Microsoft.NET\\assembly\\GAC_64"

"C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe" \
  /nologo /target:winexe /platform:x64 /optimize+ /utf8output \
  /out:we-music-ctl.exe \
  /win32icon:tray-on.ico \
  /resource:tray-on.ico \
  /resource:tray-off.ico \
  /r:"$FW\\System.dll" /r:"$FW\\System.Core.dll" \
  /r:"$G\\PresentationFramework\\v4.0_4.0.0.0__31bf3856ad364e35\\PresentationFramework.dll" \
  /r:"$G64\\PresentationCore\\v4.0_4.0.0.0__31bf3856ad364e35\\PresentationCore.dll" \
  /r:"$G\\WindowsBase\\v4.0_4.0.0.0__31bf3856ad364e35\\WindowsBase.dll" \
  /r:"$G\\System.Xaml\\v4.0_4.0.0.0__b77a5c561934e089\\System.Xaml.dll" \
  Program.cs

echo "OK: $(pwd)/we-music-ctl.exe"
