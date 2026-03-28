@echo off
REM -----------------------------------------------------------
REM  Deploy SpawnDev.WebTorrent.ServerApp to hub.spawndev.com
REM  VM: webhost (192.168.1.113), root mapped to W:
REM  Service: spawndev_hub | SSH config: webhost -> zed@192.168.1.113
REM -----------------------------------------------------------

setlocal

set HOST=webhost
set SERVICE=spawndev_hub
set DEPLOY_DIR=W:\srv\spawndev_hub
set PROJECT=SpawnDev.WebTorrent.ServerApp\SpawnDev.WebTorrent.ServerApp.csproj
set PUBLISH_DIR=SpawnDev.WebTorrent.ServerApp\bin\publish

echo -----------------------------------------------------------
echo   Deploy SpawnDev.WebTorrent.ServerApp
echo   Target: %DEPLOY_DIR% (via mapped W: drive)
echo   Service: %SERVICE%
echo -----------------------------------------------------------
echo.

REM -- Step 1: Build and Publish --
echo [1/4] Publishing release build for linux-x64...
dotnet publish "%PROJECT%" -c Release -r linux-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo PUBLISH FAILED
    exit /b 1
)
echo       Published to %PUBLISH_DIR%
echo.

REM -- Step 2: Stop remote service --
echo [2/4] Stopping %SERVICE%...
ssh %HOST% "sudo systemctl stop %SERVICE%"
echo       Service stopped.
echo.

REM -- Step 3: Copy files via mapped drive --
echo [3/4] Copying to %DEPLOY_DIR%...
xcopy /s /y /q "%PUBLISH_DIR%\*" "%DEPLOY_DIR%\"
echo       Files copied.
echo.

REM -- Step 4: Start service --
echo [4/4] Starting %SERVICE%...
ssh %HOST% "chmod +x /srv/spawndev_hub/SpawnDev.WebTorrent.ServerApp"
ssh %HOST% "sudo /usr/bin/systemctl start %SERVICE%"
timeout /t 2 /nobreak >nul
ssh %HOST% "sudo /usr/bin/systemctl status %SERVICE% --no-pager -l"
echo.

echo -----------------------------------------------------------
echo   Deploy complete!
echo   https://hub.spawndev.com
echo -----------------------------------------------------------
