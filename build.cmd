@echo off
rem Builds Ada.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio, .NET SDK or Windows SDK needed.
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
cd /d "%~dp0"
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:Ada.exe ^
  /r:"%WM%\Windows.Media.winmd" ^
  /r:"%WM%\Windows.Foundation.winmd" ^
  /r:"%WM%\Windows.Storage.winmd" ^
  /r:"%FW%\System.Runtime.dll" ^
  /r:"%FW%\WPF\UIAutomationClient.dll" ^
  /r:"%FW%\WPF\UIAutomationTypes.dll" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  Ada.cs
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built Ada.exe
