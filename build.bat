@echo off
setlocal EnableExtensions

cd /d "%~dp0"

set "PROJECT=src\RaftSaveEditor.App\RaftSaveEditor.App.csproj"
set "CONNECTOR_PROJECT=src\RaftSaveEditor.Connector\RaftSaveEditor.Connector.csproj"
set "PUBLISH_DIR=publish"
set "OUTPUT_EXE=RaftSaveEditor.exe"

echo.
echo ========================================
echo   Raft Save Editor - Release Build
echo ========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo ERRO: .NET SDK nao foi encontrado no PATH.
    echo Instale o .NET 8 SDK e execute este arquivo novamente.
    exit /b 1
)

echo [1/4] Restaurando dependencias...
dotnet restore "%PROJECT%" --nologo
if errorlevel 1 goto :build_failed

echo [2/4] Compilando conector temporario em memoria...
if not defined RAFT_GAME_DIR (
    for /f "usebackq delims=" %%I in (`powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Find-RaftGame.ps1"`) do set "RAFT_GAME_DIR=%%I"
)
if not exist "%RAFT_GAME_DIR%\Raft_Data\Managed\UnityEngine.dll" (
    echo ERRO: Raft nao encontrado em "%RAFT_GAME_DIR%".
    echo Defina RAFT_GAME_DIR antes de executar o build.
    goto :build_failed
)
dotnet build "%CONNECTOR_PROJECT%" --configuration Release --nologo -p:RaftGameDir="%RAFT_GAME_DIR%"
if errorlevel 1 goto :build_failed

if not exist "src\RaftSaveEditor.App\Assets\Connector" mkdir "src\RaftSaveEditor.App\Assets\Connector"
copy /Y "src\RaftSaveEditor.Connector\bin\Release\netstandard2.0\RaftSaveEditor.LiveConnector.V5.dll" "src\RaftSaveEditor.App\Assets\Connector\RaftSaveEditor.Connector.dll" >nul
if errorlevel 1 goto :build_failed

echo [3/4] Publicando executavel Windows x64...
if exist "%PUBLISH_DIR%" rmdir /S /Q "%PUBLISH_DIR%"
dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained true ^
    --output "%PUBLISH_DIR%" ^
    --nologo ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
if errorlevel 1 goto :build_failed

echo [4/4] Copiando executavel para a raiz...
copy /Y "%PUBLISH_DIR%\RaftSaveEditor.exe" "%OUTPUT_EXE%" >nul
if errorlevel 1 goto :build_failed

if not exist "%OUTPUT_EXE%" goto :build_failed

echo.
echo Build concluido com sucesso.
echo Executavel: %CD%\%OUTPUT_EXE%
echo.
exit /b 0

:build_failed
echo.
echo ERRO: o build nao foi concluido.
exit /b 1
