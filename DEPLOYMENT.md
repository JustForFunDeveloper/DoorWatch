# Deployment Guide

Step-by-step instructions for setting up DoorWatch on a dedicated Ubuntu server.

**How it works:** the Docker image is built locally on your Windows PC (using your warm build cache), pushed to a private Docker registry running on the server, and the server simply pulls and runs it. The server never builds anything and never sees the source code — deploys take seconds instead of minutes.

```
Windows PC                          Ubuntu server
──────────                          ─────────────
docker build  ──►  docker push ──►  registry:2 (:5000)
                                         │ docker compose pull
                                         ▼
                                    doorwatch container
                                    (managed/visible in Portainer)
```

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

## Step 5 — Run a private Docker registry (one-time)

The registry is a single lightweight container that stores the DoorWatch images on the server:

```bash
docker volume create registry-data

docker run -d \
  -p 5000:5000 \
  --name registry \
  --restart=always \
  -v registry-data:/var/lib/registry \
  registry:2
```

Verify it responds:

```bash
curl http://localhost:5000/v2/_catalog
# → {"repositories":[]}
```

---

## Step 6 — Trust the registry (one-time, both machines)

The registry speaks plain HTTP on your LAN, so Docker must be told to accept it as an "insecure registry".

**On the Ubuntu server** — create/edit `/etc/docker/daemon.json`:

```json
{
  "insecure-registries": ["192.168.1.x:5000"]
}
```

```bash
sudo systemctl restart docker
```

> ⚠️ Restarting the Docker daemon briefly restarts all running containers (Portainer, the registry, etc. come back automatically thanks to `--restart=always`).

**On your Windows PC** — Docker Desktop → Settings → **Docker Engine** → add the same key to the JSON:

```json
{
  "insecure-registries": ["192.168.1.x:5000"]
}
```

Click **Apply & Restart**.

---

## Step 7 — Set up SSH key (one-time)

On your Windows PC use git bash to run the following commands:

```powershell
ssh-keygen -t ed25519 -C "rider-deploy"
ssh-copy-id user@192.168.1.x
```

After this, SSH connections to the server require no password.

---

## Step 8 — Create deploy.local

`deploy.sh` contains no machine-specific values — it refuses to run until you create a git-ignored `deploy.local` file next to it with your real server address:

```bash
# deploy.local — git-ignored, never committed
SERVER="myuser@192.168.1.x"
REGISTRY="192.168.1.x:5000"
# REMOTE_DIR="/opt/doorwatch"   # optional, this is the default
```

The script builds the image locally, pushes it to the registry, copies `docker-compose.server.yml` to the server, and runs `docker compose pull && docker compose up -d` there. The image reference is passed to the server as the `DOORWATCH_IMAGE` variable, so no addresses live in the compose file either.

The script builds the image locally, pushes it to the registry, copies `docker-compose.server.yml` to the server, and runs `docker compose pull && docker compose up -d` there.

---

## Step 9 — Wire deploy.sh into Rider

**File → Settings → Tools → External Tools → +**

| Field | Value |
|---|---|
| Name | `Deploy DoorWatch` |
| Program | `C:\Program Files\Git\bin\bash.exe` |
| Arguments | `deploy.sh` |
| Working directory | `$ProjectFileDir$` |

After saving, the deploy is available under **Tools → External Tools → Deploy DoorWatch**.

---

## Step 10 — Create the .env file on the server

```bash
nano /opt/doorwatch/.env
```

```env
RTSP_URL=rtsp://user:pass@192.168.1.x/stream
HA_TOKEN=your_long_lived_access_token
HA_BASEURL=http://192.168.1.x:8123
DOORWATCH_IMAGE=192.168.1.x:5000/doorwatch:latest
```

`HA_BASEURL` is your Home Assistant address. `DOORWATCH_IMAGE` lets `docker compose` commands run manually on the server resolve the image; during deploys, `deploy.sh` overrides it with the freshly pushed tag.

```bash
chmod 600 /opt/doorwatch/.env
```

---

## Step 11 — First deploy

From Rider: **Tools → External Tools → Deploy DoorWatch**

Or from git bash:

```bash
bash deploy.sh
```

The first local build takes 10–15 minutes because OpenCV is compiled from source; subsequent builds reuse Docker's layer cache and are much faster. The first push uploads the full image; later pushes only transfer changed layers.

---

## Daily workflow

```
Make changes in Rider
  → Tools → External Tools → Deploy DoorWatch  (one click)
  → Builds locally, pushes to the registry, server pulls & restarts
  → Monitor via Portainer at :9000
```

To deploy a tagged version instead of `latest` (useful for rollbacks):

```bash
bash deploy.sh v1.2        # builds & pushes 192.168.1.x:5000/doorwatch:v1.2
```

---

## Optional — Manage the stack in Portainer instead

If you prefer redeploying from the Portainer UI rather than via SSH:

1. Portainer → **Stacks → Add stack** → name it `doorwatch`.
2. Paste the contents of `docker-compose.server.yml`, but remove the `env_file:` section and add `DOORWATCH_IMAGE`, `HA_BASEURL`, `RTSP_URL`, and `HA_TOKEN` as environment variables on the stack instead.
3. Deploy. From then on, after `deploy.sh` (or a plain `docker build` + `docker push`) uploads a new image, open the stack and click **Update the stack → Re-pull image and redeploy**.

Useful registry maintenance commands:

```bash
curl http://192.168.1.x:5000/v2/_catalog            # list repositories
curl http://192.168.1.x:5000/v2/doorwatch/tags/list # list tags
```
