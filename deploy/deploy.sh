#!/usr/bin/env bash
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/bottelegram}"
REPO_URL="${REPO_URL:-https://github.com/leonardomendes201704/BotAgendamentoAI.git}"
BRANCH="${BRANCH:-main}"

mkdir -p "$(dirname "$APP_DIR")"

if [ ! -d "$APP_DIR/.git" ]; then
  git clone --branch "$BRANCH" "$REPO_URL" "$APP_DIR"
fi

cd "$APP_DIR"

git fetch origin "$BRANCH"
git checkout "$BRANCH"
git pull --ff-only origin "$BRANCH"

if [ ! -f .env ]; then
  echo "Arquivo .env nao encontrado em $APP_DIR. Copie .env.example para .env e preencha os valores." >&2
  exit 1
fi

docker compose up -d --build --remove-orphans
docker image prune -f
