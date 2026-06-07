Hier ist die komplette Schritt-für-Schritt Anleitung:Klicke auf die Boxen für Details. Hier ist die vollständige Schritt-für-Schritt Anleitung:

---

## Schritt 1 — Ubuntu vorbereiten

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y curl git rsync
```

---

## Schritt 2 — Docker installieren

```bash
curl -fsSL https://get.docker.com | sh

#Enter correct user instead of $USER
sudo usermod -aG docker $USER
newgrp docker

# Prüfen ob es funktioniert
docker compose version
```

---

## Schritt 3 — Projektordner anlegen

```bash
sudo mkdir -p /opt/doorwatch
sudo chown $USER:$USER /opt/doorwatch
```

---

## Schritt 4 — Portainer installieren

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

Dann im Browser auf deinem Windows-PC: `http://<ubuntu-ip>:9000` → Admin-User anlegen → "Get Started" → Local.

---

## Schritt 5 — SSH Key einrichten (einmalig)

Auf dem Windows-PC in PowerShell:

```powershell
ssh-keygen -t ed25519 -C "rider-deploy"
ssh-copy-id user@192.168.1.x
```

Ab jetzt kein Passwort mehr beim SSH nötig.

---

## Schritt 6 — deploy.sh im Projekt anlegen

Datei `deploy.sh` im Root deines Projekts (neben `docker-compose.yml`):

```bash
#!/bin/bash
set -e

SERVER="user@192.168.1.x"
REMOTE_DIR="/opt/doorwatch"

echo "📦 Erstelle Archiv ohne .git..."
tar --exclude='.git' \
    --exclude='**/bin' \
    --exclude='**/obj' \
    --exclude='**/.vs' \
    --exclude='*.user' \
    -czf /tmp/doorwatch.tar.gz .

echo "📤 Übertrage zum Server..."
scp /tmp/doorwatch.tar.gz "$SERVER:/tmp/doorwatch.tar.gz"

echo "📂 Entpacken auf Server..."
ssh "$SERVER" "mkdir -p $REMOTE_DIR && tar -xzf /tmp/doorwatch.tar.gz -C $REMOTE_DIR"

echo "🐳 Docker Build & Start..."
ssh "$SERVER" "cd $REMOTE_DIR && docker compose up -d --build"

echo "📋 Letzte Logs:"
ssh "$SERVER" "docker compose -f $REMOTE_DIR/docker-compose.yml logs --tail=20 doorwatch"

echo "✅ Deploy fertig!"
```

---

## Schritt 7 — deploy.sh in Rider einbinden

**File → Settings → Tools → External Tools → +**

| Feld | Wert |
|---|---|
| Name | `Deploy DoorWatch` |
| Program | `C:\Program Files\Git\bin\bash.exe` |
| Arguments | `deploy.sh` |
| Working directory | `$ProjectFileDir$` |

Danach erreichbar über **Tools → External Tools → Deploy DoorWatch**.

---

## Schritt 8 — .env auf dem Server anlegen

```bash
nano /opt/doorwatch/.env
```

```env
RTSP_URL=rtsp://user:pass@192.168.1.x/stream
HA_TOKEN=dein_long_lived_access_token
```

```bash
chmod 600 /opt/doorwatch/.env
```

---

## Schritt 9 — Erster Deploy

Aus Rider: **Tools → External Tools → Deploy DoorWatch**

Oder manuell auf Ubuntu:

```bash
cd /opt/doorwatch
docker compose up -d --build
docker compose logs -f
```

Der erste Build dauert 10-15 Minuten wegen OpenCV kompilieren — danach ist alles gecacht.

---

## Täglicher Workflow danach

```
Code in Rider ändern
  → Tools → Deploy DoorWatch  (ein Klick)
  → Script synct + baut auf Ubuntu neu
  → Portainer unter :9000 zum Überwachen
```