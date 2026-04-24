@echo off
REM -----------------------------------------------------------
REM  Deploy SpawnDev.WebTorrent.ServerApp to hub.spawndev.com
REM  VM: webhost (192.168.1.113), root mapped to W:
REM  Service: spawndev_hub | SSH config: webhost -> zed@192.168.1.113
REM
REM  The tracked unit file at deploy\spawndev_hub\spawndev_hub.service is
REM  the source of truth for the hub's environment (STUN/TURN config,
REM  Origin allowlist, ephemeral-credential secret, etc.). Any change
REM  there flows to /etc/systemd/system/ on the VM automatically.
REM -----------------------------------------------------------

setlocal

set HOST=webhost
set SERVICE=spawndev_hub
set DEPLOY_DIR=W:\srv\spawndev_hub
set PROJECT=SpawnDev.WebTorrent.ServerApp\SpawnDev.WebTorrent.ServerApp.csproj
set PUBLISH_DIR=SpawnDev.WebTorrent.ServerApp\bin\publish
set UNIT_SRC=deploy\spawndev_hub\spawndev_hub.service
set UNIT_DEST_REMOTE=/etc/systemd/system/spawndev_hub.service

echo -----------------------------------------------------------
echo   Deploy SpawnDev.WebTorrent.ServerApp
echo   Target: %DEPLOY_DIR% (via mapped W: drive)
echo   Service: %SERVICE%
echo -----------------------------------------------------------
echo.

REM -- Step 1: Build and Publish --
echo [1/5] Publishing release build for linux-x64...
dotnet publish "%PROJECT%" -c Release -r linux-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo PUBLISH FAILED
    exit /b 1
)
echo       Published to %PUBLISH_DIR%
echo.

REM -- Step 2: Stop remote service --
echo [2/5] Stopping %SERVICE%...
ssh %HOST% "sudo systemctl stop %SERVICE%"
echo       Service stopped.
echo.

REM -- Step 3: Sync systemd unit file + daemon-reload --
REM     Direct scp to /etc/systemd/system (user 'zed' must own that specific
REM     file - one-time setup on the VM: `sudo chown zed:zed %UNIT_DEST_REMOTE%`).
REM     daemon-reload is NOPASSWD-sudo for systemctl, matching the stop/start
REM     commands below. systemd does not care about unit-file ownership.
echo [3/5] Syncing systemd unit file...
scp "%UNIT_SRC%" %HOST%:%UNIT_DEST_REMOTE%
if errorlevel 1 (
    echo.
    echo UNIT FILE COPY FAILED
    echo ---------------------------------------------------------
    echo If "Permission denied", the unit file is not owned by the
    echo SSH user. One-time setup on the VM:
    echo.
    echo     ssh %HOST% "sudo chown zed:zed %UNIT_DEST_REMOTE%"
    echo.
    echo Then re-run this bat. systemd does not care about unit-file
    echo ownership - it only reads the content.
    echo ---------------------------------------------------------
    exit /b 1
)
ssh %HOST% "sudo /usr/bin/systemctl daemon-reload"
if errorlevel 1 (
    echo DAEMON-RELOAD FAILED
    exit /b 1
)
echo       Unit file synced + daemon reloaded.
echo.

REM -- Step 4: Copy binaries via mapped drive --
echo [4/5] Copying to %DEPLOY_DIR%...
xcopy /s /y /q "%PUBLISH_DIR%\*" "%DEPLOY_DIR%\"
echo       Files copied.
echo.

REM -- Step 5: Start service --
echo [5/5] Starting %SERVICE%...
ssh %HOST% "chmod +x /srv/spawndev_hub/SpawnDev.WebTorrent.ServerApp"
ssh %HOST% "sudo /usr/bin/systemctl start %SERVICE%"
timeout /t 2 /nobreak >nul
ssh %HOST% "sudo /usr/bin/systemctl status %SERVICE% --no-pager -l"
echo.

echo -----------------------------------------------------------
echo   Deploy complete!
echo   https://hub.spawndev.com:44365/
echo   (The `/` endpoint now reports stunTurn.enabled + authMode.)
echo -----------------------------------------------------------
