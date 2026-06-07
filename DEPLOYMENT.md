# Deployment Guide

Step-by-step instructions for setting up DoorWatch on a dedicated Ubuntu server.

---

## Step 1 — Prepare Ubuntu

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y curl git rsync
```

---

## Step 2 — Install Docker

```bash
curl -fsSL https://get.docker.com | sh

# Replace $USER with your actual username if needed
sudo usermod -aG docker $USER
newgrp docker

# Verify it works
docker compose version
```

---

## Step 3 — Create the project directory

```bash
sudo mkdir -p /opt/doorwatch
sudo chown $USER:$USER /opt/doorwatch
```

---

## Step 4 — Install Portainer

```bash
docker volume create portainer_data

docker run -d \
  -p 9000:9000 \
  --name portainer \
  --restart=always \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v portainer_data:/data \
  portainer/portainer-ce:latest
```

Open `http://<ubuntu-ip>:9000` in your browser → create an admin user → Get Started → Local.

---

## Step 5 — Set up SSH key (one-time)

On your Windows PC in PowerShell:

```powershell
ssh-keygen -t ed25519 -C "rider-deploy"
ssh-copy-id user@192.168.1.x
```

After this, SSH connections to the server require no password.

---

## Step 6 — Configure deploy.sh

Edit `deploy.sh` at the project root and set the `SERVER` and `REMOTE_DIR` variables at the top to match your server:

```bash
SERVER="user@192.168.1.x"
REMOTE_DIR="/opt/doorwatch"
```

The script packages the project, transfers it via SCP, extracts it on the server, and runs `docker compose up -d --build`.

---

## Step 7 — Wire deploy.sh into Rider

**File → Settings → Tools → External Tools → +**

| Field | Value |
|---|---|
| Name | `Deploy DoorWatch` |
| Program | `C:\Program Files\Git\bin\bash.exe` |
| Arguments | `deploy.sh` |
| Working directory | `$ProjectFileDir$` |

After saving, the deploy is available under **Tools → External Tools → Deploy DoorWatch**.

---

## Step 8 — Create the .env file on the server

```bash
nano /opt/doorwatch/.env
```

```env
RTSP_URL=rtsp://user:pass@192.168.1.x/stream
HA_TOKEN=your_long_lived_access_token
```

```bash
chmod 600 /opt/doorwatch/.env
```

---

## Step 9 — First deploy

From Rider: **Tools → External Tools → Deploy DoorWatch**

Or manually on the server:

```bash
cd /opt/doorwatch
docker compose up -d --build
docker compose logs -f
```

The first build takes 10–15 minutes because OpenCV is compiled from source. Subsequent builds use Docker's layer cache and are much faster.

---

## Daily workflow

```
Make changes in Rider
  → Tools → External Tools → Deploy DoorWatch  (one click)
  → Script syncs and rebuilds on the server
  → Monitor via Portainer at :9000
```
