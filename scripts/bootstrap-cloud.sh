#!/usr/bin/env bash
# Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
# All rights reserved.
# This file is a part of the Sarafan application

set -euo pipefail

readonly DEPLOYMENT_TARGET="${1:-${SARAFAN_DEPLOYMENT_TARGET:-production}}"
readonly ENV_FILE="${SARAFAN_ENV_FILE:-sarafan.env}"

fail() { printf '%s\n' "$1" >&2; exit 1; }
[[ -f "$ENV_FILE" ]] || fail "Environment file not found: $ENV_FILE"

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

readonly PROJECT_NAME="${COMPOSE_PROJECT_NAME:-sarafan}"
readonly CERTIFICATE_DIR="${SARAFAN_CERTIFICATE_DIR:-/srv/sarafan/certificate}"
readonly DEPLOYMENT_WAIT_TIMEOUT="${SARAFAN_DEPLOYMENT_WAIT_TIMEOUT:-180}"
[[ "$DEPLOYMENT_WAIT_TIMEOUT" =~ ^[1-9][0-9]*$ ]] \
  || fail "SARAFAN_DEPLOYMENT_WAIT_TIMEOUT must be a positive number of seconds"
if ! compose_up_help="$(docker compose up --help)"; then
  fail "Docker Compose v2 with up --wait and --wait-timeout support is required"
fi
[[ "$compose_up_help" == *"--wait-timeout"* ]] \
  || fail "Upgrade Docker Compose: up --wait and --wait-timeout support is required"

ensure_durable_directory() {
  local variable_name="$1"
  local path="${!variable_name:-}"
  [[ -n "$path" ]] || fail "$variable_name must be configured"
  [[ "$path" = /* && "$path" != "/" ]] || fail "$variable_name must be an absolute non-root path"
  mkdir -p -- "$path"
  [[ -d "$path" && -w "$path" ]] || fail "$variable_name is not a writable directory: $path"
}

ensure_durable_directory SARAFAN_POSTGRES_DATA_DIR
ensure_durable_directory SARAFAN_BACKUP_DATA_DIR
ensure_durable_directory SARAFAN_BACKUP_LOG_DIR
[[ -n "${SARAFAN_POSTGRES_PASSWORD:-}" && "${SARAFAN_POSTGRES_PASSWORD}" != "postgres" ]] \
  || fail "SARAFAN_POSTGRES_PASSWORD must be set to a non-default value"
readonly JWT_SECRET="${SARAFAN_JWT_SECRET:-}"
[[ ${#JWT_SECRET} -ge 32 ]] || fail "SARAFAN_JWT_SECRET must contain at least 32 characters"
readonly BACKOFFICE_JWT_SECRET="${SARAFAN_BACKOFFICE_JWT_SECRET:-}"
[[ ${#BACKOFFICE_JWT_SECRET} -ge 32 ]] \
  || fail "SARAFAN_BACKOFFICE_JWT_SECRET must contain at least 32 characters"
[[ "$BACKOFFICE_JWT_SECRET" != "$JWT_SECRET" ]] \
  || fail "SARAFAN_BACKOFFICE_JWT_SECRET must differ from SARAFAN_JWT_SECRET"
if [[ "${SARAFAN_BACKOFFICE_BOOTSTRAP_ENABLED:-false}" == true ]]; then
  [[ "${SARAFAN_REAL_ORDERS_ENABLED:-false}" != true ]] \
    || fail "Back-office demo bootstrap cannot run with real orders enabled"
  [[ "${SARAFAN_REAL_PAYMENT_INTEGRATION_ENABLED:-false}" != true ]] \
    || fail "Back-office demo bootstrap cannot run with real payment integration enabled"
  [[ -n "${SARAFAN_BACKOFFICE_BOOTSTRAP_EMAIL:-}" ]] \
    || fail "SARAFAN_BACKOFFICE_BOOTSTRAP_EMAIL must be set while bootstrap is enabled"
  readonly BACKOFFICE_BOOTSTRAP_PASSWORD="${SARAFAN_BACKOFFICE_BOOTSTRAP_PASSWORD:-}"
  [[ ${#BACKOFFICE_BOOTSTRAP_PASSWORD} -ge 12 && ${#BACKOFFICE_BOOTSTRAP_PASSWORD} -le 72 ]] \
    || fail "SARAFAN_BACKOFFICE_BOOTSTRAP_PASSWORD must contain 12 to 72 characters"
fi

case "$DEPLOYMENT_TARGET" in
  edge)
    readonly OVERLAY_FILE=docker-compose.edge.yml
    docker network inspect "${SW_CONSULTING_EDGE_NETWORK:-sw-consulting-edge}" >/dev/null 2>&1 \
      || fail "Shared edge network does not exist; start sw-consulting-edge first"
    ;;
  production)
    readonly OVERLAY_FILE=docker-compose.production.yml
    [[ -f "$CERTIFICATE_DIR/s.crt" && -f "$CERTIFICATE_DIR/s.key" ]] \
      || fail "TLS certificate files s.crt and s.key are required in $CERTIFICATE_DIR"
    openssl x509 -in "$CERTIFICATE_DIR/s.crt" -noout -checkhost sarafan.sw.consulting >/dev/null \
      || fail "Certificate does not cover sarafan.sw.consulting: $CERTIFICATE_DIR/s.crt"
    openssl x509 -in "$CERTIFICATE_DIR/s.crt" -noout -checkhost sb.sw.consulting >/dev/null \
      || fail "Certificate does not cover sb.sw.consulting: $CERTIFICATE_DIR/s.crt"
    ;;
  *) fail "Deployment target must be 'edge' or 'production'" ;;
esac

readonly COMPOSE=(docker compose --project-name "$PROJECT_NAME" --env-file "$ENV_FILE" -f docker-compose-ghrc.yml -f "$OVERLAY_FILE")
"${COMPOSE[@]}" config --quiet
"${COMPOSE[@]}" pull
"${COMPOSE[@]}" up -d backup api
"${COMPOSE[@]}" up -d --wait --wait-timeout "$DEPLOYMENT_WAIT_TIMEOUT" ui
if [[ "$DEPLOYMENT_TARGET" == production ]]; then
  "${COMPOSE[@]}" up -d --wait --wait-timeout "$DEPLOYMENT_WAIT_TIMEOUT" production-edge
fi
"${COMPOSE[@]}" up -d --wait --wait-timeout "$DEPLOYMENT_WAIT_TIMEOUT" backoffice
"${COMPOSE[@]}" ps
