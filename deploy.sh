#!/bin/bash
set -e

SERVER="user@192.168.1.143"
REMOTE_DIR="/opt/doorwatch"

echo "📦 Creating archive (excluding .git)..."
tar --exclude='.git' \
    --exclude='**/bin' \
    --exclude='**/obj' \
    --exclude='**/.vs' \
    --exclude='*.user' \
    -czf /tmp/doorwatch.tar.gz .

echo "📤 Transferring to server..."
scp /tmp/doorwatch.tar.gz "$SERVER:/tmp/doorwatch.tar.gz"

echo "📂 Extracting on server..."
ssh "$SERVER" "mkdir -p $REMOTE_DIR && tar -xzf /tmp/doorwatch.tar.gz -C $REMOTE_DIR"

echo "🐳 Building and starting Docker container..."
ssh "$SERVER" "cd $REMOTE_DIR && docker compose up -d --build"

echo "📋 Recent logs:"
ssh "$SERVER" "docker compose -f $REMOTE_DIR/docker-compose.yml logs --tail=20 doorwatch"

echo "✅ Deploy complete!"
