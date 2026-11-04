#!/bin/sh
# Opdatér fra GitHub og genstart containeren. Kør på LXC'en:
#   cd /opt/f1-live-replay && sh update.sh
set -e
cd "$(dirname "$0")"

git pull --ff-only

if [ ! -f .env ]; then
  cp .env.example .env
  echo "Oprettede .env fra .env.example — udfyld F1_USERNAME/F1_PASSWORD og kør igen."
  exit 1
fi

# Containeren kører som UID 1654 ("app") og skal kunne skrive i ./data
mkdir -p data
chown 1654:1654 data 2>/dev/null || true

docker compose up -d --build
docker image prune -f >/dev/null 2>&1 || true
docker compose ps
echo "Log: docker compose logs -f --tail 50"
