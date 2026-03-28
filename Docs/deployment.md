# Deployment

## Production Server (hub.spawndev.com)

SpawnDev.WebTorrent.ServerApp runs on hub.spawndev.com providing:
- **Tracker** at `wss://hub.spawndev.com:44365/announce`
- **Web Seed** for cached torrent data
- **HuggingFace Proxy** at `/hf/{org}/{repo}/{filePath}`
- **Stats** at `/stats`

### Ports

| Port | Protocol | Service |
|------|----------|---------|
| 44365 | HTTPS (WSS) | Tracker + Web Seed + HF Proxy |

SSL is terminated by HAProxy with LetsEncrypt certificates.

### Systemd Service

The server runs as a systemd service on Linux:

```ini
[Unit]
Description=SpawnDev.WebTorrent.ServerApp
After=network.target

[Service]
Type=notify
ExecStart=/opt/spawndev-webtorrent/SpawnDev.WebTorrent.ServerApp
WorkingDirectory=/opt/spawndev-webtorrent
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

### Deploying Updates

```bash
# On dev machine:
dotnet publish SpawnDev.WebTorrent.ServerApp -c Release -r linux-x64

# Copy to server:
scp -r bin/Release/net10.0/linux-x64/publish/* user@server:/opt/spawndev-webtorrent/

# On server:
sudo systemctl restart spawndev-webtorrent
```

## GitHub Pages Demo

The Blazor WASM demo deploys to GitHub Pages automatically. The `index.html` uses a dynamic base href script that detects the GitHub Pages path.

Demo URL: `https://lostbeard.github.io/SpawnDev.WebTorrent/`

## Local Development

### Running the Server

```bash
cd SpawnDev.WebTorrent.ServerApp
dotnet run
# Tracker: https://localhost:5560/announce
# HTTP:    http://localhost:5561
```

### Running Tests

```bash
cd PlaywrightMultiTest
dotnet test -- NUnit.NumberOfTestWorkers=1
# Publishes demo, launches Chromium, runs 67 browser tests
# Port 5562 for the test server
```

Tests automatically detect whether the server is running locally (localhost:5561) or fall back to the production server (hub.spawndev.com:44365).
