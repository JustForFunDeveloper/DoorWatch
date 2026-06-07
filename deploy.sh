#!/bin/bash
set -e

SERVER="atappler@192.168.1.143"
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