@echo off
REM Run the demo: plaintext, then local encryptor, then ciphertext, then channel.
setlocal
set DOTNET_CLI_HOME=%~dp0.dotnet
set NUGET_PACKAGES=%~dp0.nuget
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet run --project "%~dp0src\WeChatConnector.Demo\WeChatConnector.Demo.csproj" -m:1 %*
endlocal
