#!/bin/bash
set -e

# Machine-specific values live in the git-ignored deploy.local file next to this script:
#
#   SERVER="user@192.168.1.x"      # SSH login for the server
#   REGISTRY="192.168.1.x:5000"    # private Docker registry address
#   REMOTE_DIR="/opt/doorwatch"    # optional, this is the default
#
REMOTE_DIR="/opt/doorwatch"

[ -f "$(dirname "$0")/deploy.local" ] && source "$(dirname "$0")/deploy.local"

: "${SERVER:?SERVER is not set — create a deploy.local file (see DEPLOYMENT.md)}"
: "${REGISTRY:?REGISTRY is not set — create a deploy.local file (see DEPLOYMENT.md)}"

IMAGE="$REGISTRY/doorwatch"
TAG="${1:-latest}"

echo "🐳 Building image locally..."
docker build -t "$IMAGE:$TAG" .

echo "📤 Pushing $IMAGE:$TAG to registry..."
docker push "$IMAGE:$TAG"

echo "📂 Updating compose file on server..."
ssh "$SERVER" "mkdir -p $REMOTE_DIR"
scp docker-compose.server.yml "$SERVER:$REMOTE_DIR/docker-compose.yml"

echo "🚀 Pulling new image and restarting container..."
ssh "$SERVER" "cd $REMOTE_DIR && export DOORWATCH_IMAGE='$IMAGE:$TAG' && docker compose pull doorwatch && docker compose up -d"

echo "📋 Recent logs:"
ssh "$SERVER" "cd $REMOTE_DIR && export DOORWATCH_IMAGE='$IMAGE:$TAG' && docker compose logs --tail=20 doorwatch"

echo "✅ Deploy complete!"
