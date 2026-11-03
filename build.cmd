@echo off
rem Builds K2Remote.exe with the C# compiler included in Windows (.NET Framework 4).
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /out:"%~dp0K2Remote.exe" "%~dp0K2Remote.cs"
