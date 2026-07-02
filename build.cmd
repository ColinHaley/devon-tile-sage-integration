@echo off
rem Builds SageStripeSync with the legacy MSBuild that ships in the .NET
rem Framework runtime (no Visual Studio or dotnet SDK required).
rem Output: src\SageStripeSync\bin\Release\SageStripeSync.exe
setlocal
set MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe
"%MSBUILD%" "%~dp0src\SageStripeSync\SageStripeSync.csproj" /p:Configuration=Release /v:minimal /nologo %*
endlocal
