# Compila ReparadorV821.exe (un solo archivo, todo embebido) con el csc de .NET Framework 4.x
# (viene con Windows, no hay que instalar nada). Uso:  powershell -File build.ps1 [-Out nombre.exe]
# Necesita, relativo a la raiz del repo (no se suben a GitHub, ver README):
#   firmware\v0.1.5_extraido\   boot.fex amp_rv0.fex rootfs_nor.fex usr.fex env.fex
#   vendor\xfel-windows-v1.4.0\ xfel.exe libusb-1.0.dll Drivers\zadig-2.9.exe
#   vendor\platform-tools\      adb.exe AdbWinApi.dll AdbWinUsbApi.dll
param([string]$Out = "ReparadorV821.exe")
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$fw   = "$root\firmware\v0.1.5_extraido"
$xf   = "$root\vendor\xfel-windows-v1.4.0"
$pt   = "$root\vendor\platform-tools"
$csc  = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$res = @(
  "$xf\xfel.exe,xfel.exe",
  "$xf\libusb-1.0.dll,libusb-1.0.dll",
  "$xf\Drivers\zadig-2.9.exe,zadig.exe",
  "$fw\boot.fex,boot.fex",
  "$fw\amp_rv0.fex,amp_rv0.fex",
  "$fw\rootfs_nor.fex,rootfs_nor.fex",
  "$fw\usr.fex,usr.fex",
  "$fw\env.fex,env.fex",
  "$pt\adb.exe,adb.exe",
  "$pt\AdbWinApi.dll,AdbWinApi.dll",
  "$pt\AdbWinUsbApi.dll,AdbWinUsbApi.dll",
  "$here\linux\S00autofix,S00autofix"
) | ForEach-Object { "/resource:$_" }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$here\$Out" `
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll /reference:System.Management.dll `
  $res "$here\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw "Fallo la compilacion" }
Get-Item "$here\$Out" | Select-Object Name, Length, LastWriteTime
