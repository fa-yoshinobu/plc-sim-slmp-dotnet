@echo off
rem PLC Sim (SLMP) ビルド
rem   build.bat                 テスト実行後、GUI・コンソールをそれぞれ 1 ファイルの exe（.NET ランタイム同梱）で dist\PlcSim.Slmp に発行
rem   build.bat notest          テストを省略
rem   build.bat folder          1 ファイルにせず、gui\・host\ フォルダに exe と DLL を並べて発行
rem   build.bat framework       .NET ランタイムを同梱しない（配布先に .NET 8 Desktop Runtime と ASP.NET Core Runtime が必要）
rem   発行後、配布用の dist\PlcSim.Slmp-win-x64.zip を作る（展開すると PlcSim.Slmp フォルダに exe・config・python・scripts が入る）
rem   引数は組み合わせ可（例: build.bat notest folder framework）
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

set RUN_TEST=1
set SELF_CONTAINED=true
set SINGLE_FILE=1
for %%A in (%*) do (
    if /i "%%A"=="notest" set RUN_TEST=0
    if /i "%%A"=="folder" set SINGLE_FILE=0
    if /i "%%A"=="framework" set SELF_CONTAINED=false
)

set CONFIG=Release
set DIST=%~dp0dist\PlcSim.Slmp
set ZIP=%~dp0dist\PlcSim.Slmp-win-x64.zip

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [エラー] dotnet が見つかりません。.NET 8 SDK 以降をインストールしてください。
    exit /b 1
)

echo === 復元 ===
dotnet restore PlcSim.Slmp.slnx || goto :fail

echo === ビルド (%CONFIG%) ===
dotnet build PlcSim.Slmp.slnx -c %CONFIG% --no-restore || goto :fail

if "%RUN_TEST%"=="1" (
    echo === テスト ===
    dotnet test tests\PlcSim.Slmp.Tests -c %CONFIG% --no-build || goto :fail
) else (
    echo === テスト省略 ===
)

echo === 発行 (self-contained=%SELF_CONTAINED%, single-file=%SINGLE_FILE%) ===
if exist "%DIST%" rmdir /s /q "%DIST%"
if exist "%ZIP%" del /q "%ZIP%"
set PUBLISH_OPTS=-c %CONFIG% -r win-x64 --self-contained %SELF_CONTAINED% -p:DebugType=none
if "%SINGLE_FILE%"=="1" (
    rem exe を配布フォルダ直下に置く。ランタイム同梱時は圧縮して小さくする。
    set PUBLISH_OPTS=!PUBLISH_OPTS! -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=%SELF_CONTAINED%
    set GUI_DIR=%DIST%
    set HOST_DIR=%DIST%
) else (
    set GUI_DIR=%DIST%\gui
    set HOST_DIR=%DIST%\host
)
dotnet publish src\PlcSim.Slmp.Gui -o "%GUI_DIR%" %PUBLISH_OPTS% || goto :fail
dotnet publish src\PlcSim.Slmp.Host -o "%HOST_DIR%" %PUBLISH_OPTS% || goto :fail

rem 設定・Python ライブラリ・サンプルスクリプト（実行ファイルの上位フォルダから自動で探す）
xcopy /e /i /y /q config "%DIST%\config" >nul || goto :fail
xcopy /e /i /y /q python "%DIST%\python" >nul || goto :fail
xcopy /e /i /y /q scripts "%DIST%\scripts" >nul || goto :fail
for /d /r "%DIST%" %%D in (__pycache__) do if exist "%%D" rmdir /s /q "%%D"
copy /y README.md "%DIST%\README.md" >nul
copy /y LICENSE "%DIST%\LICENSE" >nul

if not "%SINGLE_FILE%"=="1" (
    > "%DIST%\start_gui.bat" (
        echo @echo off
        echo start "" "%%~dp0gui\PlcSim.Slmp.Gui.exe" %%*
    )
    > "%DIST%\start_console.bat" (
        echo @echo off
        echo chcp 65001 ^>nul
        echo "%%~dp0host\PlcSim.Slmp.Host.exe" %%*
    )
)

echo === 配布 zip ===
powershell -NoProfile -Command "Compress-Archive -Path '%DIST%' -DestinationPath '%ZIP%' -Force" || goto :fail

echo.
echo === 完了 ===
echo   出力先 : %DIST%
echo   配布 zip : %ZIP%
if "%SINGLE_FILE%"=="1" (
    echo   GUI    : %DIST%\PlcSim.Slmp.Gui.exe
    echo   コンソール : %DIST%\PlcSim.Slmp.Host.exe
) else (
    echo   GUI    : %DIST%\start_gui.bat
    echo   コンソール : %DIST%\start_console.bat
)
exit /b 0

:fail

echo === ビルド (%CONFIG%) ===
dotnet build PlcSim.Slmp.slnx -c %CONFIG% --no-restore || goto :fail

if "%RUN_TEST%"=="1" (
    echo === テスト ===
    dotnet test tests\PlcSim.Slmp.Tests -c %CONFIG% --no-build || goto :fail
) else (
    echo === テスト省略 ===
)

echo === 発行 (self-contained=%SELF_CONTAINED%) ===
if exist "%DIST%" rmdir /s /q "%DIST%"
set PUBLISH_OPTS=-c %CONFIG% -r win-x64 --self-contained %SELF_CONTAINED% -p:DebugType=none
dotnet publish src\PlcSim.Slmp.Gui -o "%DIST%\gui" %PUBLISH_OPTS% || goto :fail
dotnet publish src\PlcSim.Slmp.Host -o "%DIST%\host" %PUBLISH_OPTS% || goto :fail

rem 設定・Python ライブラリ・サンプルスクリプト（実行ファイルの上位フォルダから自動で探す）
xcopy /e /i /y /q config "%DIST%\config" >nul || goto :fail
xcopy /e /i /y /q python "%DIST%\python" >nul || goto :fail
xcopy /e /i /y /q scripts "%DIST%\scripts" >nul || goto :fail
copy /y README.md "%DIST%\README.md" >nul

> "%DIST%\start_gui.bat" (
    echo @echo off
    echo start "" "%%~dp0gui\PlcSim.Slmp.Gui.exe" %%*
)
> "%DIST%\start_console.bat" (
    echo @echo off
    echo chcp 65001 ^>nul
    echo "%%~dp0host\PlcSim.Slmp.Host.exe" %%*
)

echo.
echo === 完了 ===
echo   出力先 : %DIST%
echo   GUI    : %DIST%\start_gui.bat
echo   コンソール : %DIST%\start_console.bat
exit /b 0

:fail
echo.
echo [エラー] ビルドに失敗しました（終了コード %errorlevel%）。
exit /b 1
