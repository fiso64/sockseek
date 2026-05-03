@echo off
setlocal

set FRAMEWORK=net10.0

set RID=%1

if not exist slsk-batchdl.Cli\bin\zips mkdir slsk-batchdl.Cli\bin\zips

if "%RID%"=="" goto all
if "%RID%"=="win-x86" goto win-x86
if "%RID%"=="linux-x64" goto linux-x64
if "%RID%"=="linux-arm" goto linux-arm
if "%RID%"=="osx-x64" goto osx-x64
if "%RID%"=="osx-arm64" goto osx-arm64

echo Unknown RID: %RID%
exit /b 1

:all
call :publish_and_zip win-x86     false   sldl_win-x86.zip
call :publish_and_zip win-x86     true    sldl_win-x86_self-contained.zip
call :publish_and_zip linux-x64   true    sldl_linux-x64.zip
call :publish_and_zip linux-arm   true    sldl_linux-arm.zip
call :publish_and_zip osx-x64     true    sldl_osx-x64.zip
call :publish_and_zip osx-arm64   true    sldl_osx-arm64.zip
goto end

:win-x86
call :publish_and_zip win-x86     false   sldl_win-x86.zip
call :publish_and_zip win-x86     true    sldl_win-x86_self-contained.zip
goto end

:linux-x64
call :publish_and_zip linux-x64   true    sldl_linux-x64.zip
goto end

:linux-arm
call :publish_and_zip linux-arm   true    sldl_linux-arm.zip
goto end

:osx-x64
call :publish_and_zip osx-x64     true    sldl_osx-x64.zip
goto end

:osx-arm64
call :publish_and_zip osx-arm64   true    sldl_osx-arm64.zip
goto end

:end
endlocal
exit /b

:publish_and_zip
dotnet publish slsk-batchdl.Cli\slsk-batchdl.Cli.csproj -c Release -r %1 -p:PublishSingleFile=true -p:PublishTrimmed=%2 --self-contained=%2

if exist LICENSE copy /Y LICENSE "slsk-batchdl.Cli\bin\Release\%FRAMEWORK%\%1\publish\"

if exist slsk-batchdl.Cli\bin\zips\%3 del /F /Q slsk-batchdl.Cli\bin\zips\%3
powershell.exe -nologo -noprofile -command "& { Add-Type -A 'System.IO.Compression.FileSystem'; [IO.Compression.ZipFile]::CreateFromDirectory('slsk-batchdl.Cli\bin\Release\%FRAMEWORK%\%1\publish', 'slsk-batchdl.Cli\bin\zips\%3'); }"
exit /b
