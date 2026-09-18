@echo off
REM Build the whole solution.
REM The -m:1 flag is required here: multi-node MSBuild uses named pipes,
REM which the sandbox blocks, producing a silent "0 errors but failed" result.
setlocal
set DOTNET_CLI_HOME=%~dp0.dotnet
set NUGET_PACKAGES=%~dp0.nuget
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet build "%~dp0WeChatConnector.sln" -m:1 %*
endlocal
