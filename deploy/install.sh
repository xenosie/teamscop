#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-/home/ubuntu/.dotnet}"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root: sudo bash deploy/install.sh"
  exit 1
fi

apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y postgresql postgresql-contrib nginx certbot python3-certbot-nginx curl rsync

id -u teamscop >/dev/null 2>&1 || useradd --system --home /opt/teamscop --shell /usr/sbin/nologin teamscop
mkdir -p /opt/teamscop/api /var/lib/teamscop/avatars /var/lib/teamscop/screenshots /etc/teamscop /var/www/certbot
chown -R teamscop:teamscop /opt/teamscop /var/lib/teamscop

if [[ ! -f /etc/teamscop/api.env ]]; then
  DB_PASS="$(openssl rand -base64 24 | tr -d '=+/')"
  JWT_KEY="$(openssl rand -base64 48 | tr -d '\n')"
  COMPANY_KEY="$(openssl rand -base64 32 | tr -d '\n')"

  sudo -u postgres psql -v ON_ERROR_STOP=1 <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'teamscop') THEN
    CREATE ROLE teamscop LOGIN PASSWORD '${DB_PASS}';
  ELSE
    ALTER ROLE teamscop WITH PASSWORD '${DB_PASS}';
  END IF;
END
\$\$;
SELECT 'CREATE DATABASE teamscop OWNER teamscop'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'teamscop')\gexec
GRANT ALL PRIVILEGES ON DATABASE teamscop TO teamscop;
SQL
  sudo -u postgres psql -d teamscop -v ON_ERROR_STOP=1 <<'SQL'
GRANT ALL ON SCHEMA public TO teamscop;
ALTER SCHEMA public OWNER TO teamscop;
SQL

  cat >/etc/teamscop/api.env <<EOF
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
ConnectionStrings__Default=Host=127.0.0.1;Port=5432;Database=teamscop;Username=teamscop;Password=${DB_PASS}
Jwt__Key=${JWT_KEY}
Jwt__Issuer=teamscop
Jwt__Audience=teamscop-agent
CompanyToken__Key=${COMPANY_KEY}
Storage__AvatarRoot=/var/lib/teamscop/avatars
# Must match the authorized route (B12). The old /media/avatars was the unauthenticated
# static alias and no longer exists; setting it here would generate dead avatar URLs.
Storage__PublicAvatarBasePath=/api/media/avatars
Storage__ScreenshotRoot=/var/lib/teamscop/screenshots
Retention__AgentEventsDays=30
EOF
  chmod 600 /etc/teamscop/api.env
  echo "Wrote /etc/teamscop/api.env"
  echo "IMPORTANT: embed CompanyToken__Key into the Windows agent build."
fi

# Repair keys on hosts provisioned before these settings existed — the block above
# only runs on a first install, so without this a re-run leaves them at their defaults.
ensure_env() {
  local key="$1" value="$2"
  if grep -qE "^${key}=" /etc/teamscop/api.env; then
    return
  fi
  printf '%s=%s\n' "$key" "$value" >>/etc/teamscop/api.env
  echo "Added ${key} to /etc/teamscop/api.env"
}

# §13.2 — data expires after 30 days. Note the binding key is Retention__, not Storage__:
# RetentionOptions.SectionName is "Retention", so Storage__AgentEventsDays binds to nothing
# and silently leaves the window at its compiled-in default.
ensure_env Retention__AgentEventsDays 30
# Without this the default is the relative path data/screenshots, which resolves under
# WorkingDirectory=/opt/teamscop/api and is destroyed by the `rsync --delete` below on
# every single deploy. Screenshot blobs must live outside the publish directory.
ensure_env Storage__ScreenshotRoot /var/lib/teamscop/screenshots
chmod 600 /etc/teamscop/api.env

DOTNET_BIN="${DOTNET_ROOT}/dotnet"
if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "dotnet SDK not found at $DOTNET_BIN"
  exit 1
fi

sudo -u ubuntu env DOTNET_ROOT="$DOTNET_ROOT" PATH="$DOTNET_ROOT:$PATH" \
  "$DOTNET_BIN" publish "$ROOT/backend/Teamscop.Api/Teamscop.Api.csproj" \
  -c Release -r linux-x64 --self-contained true -o /tmp/teamscop-api-publish

rsync -a --delete /tmp/teamscop-api-publish/ /opt/teamscop/api/
chown -R teamscop:teamscop /opt/teamscop/api

cp "$ROOT/deploy/teamscop-api.service" /etc/systemd/system/teamscop-api.service

if [[ ! -f /etc/letsencrypt/live/teamscop.com/fullchain.pem ]]; then
  cat >/etc/nginx/sites-available/teamscop <<'NGINX'
limit_req_zone $binary_remote_addr zone=auth_limit:10m rate=10r/s;
limit_req_zone $binary_remote_addr zone=api_limit:10m rate=50r/s;

server {
    listen 80;
    listen [::]:80;
    server_name teamscop.com www.teamscop.com;

    # Ingest batches are capped at 8 MiB of payload server-side; nginx defaults to 1m,
    # which would 413 a full batch long before the service ever validated it.
    client_max_body_size 12m;

    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }

    # No /media/avatars/ alias — avatars are authorized through the API (B12). See
    # deploy/nginx.teamscop.conf; the two configs must stay in step.

    location /api/monitor/ {
        access_log off;
        return 404;
    }

    location /api/auth/ {
        limit_req zone=auth_limit burst=20 nodelay;
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /api/ {
        limit_req zone=api_limit burst=100 nodelay;
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }

    location /health {
        proxy_pass http://127.0.0.1:5080/health;
    }

    location /hubs/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    location / {
        return 200 'Teamscop API is online.\n';
        add_header Content-Type text/plain;
    }
}
NGINX
else
  cp "$ROOT/deploy/nginx.teamscop.conf" /etc/nginx/sites-available/teamscop
fi

ln -sfn /etc/nginx/sites-available/teamscop /etc/nginx/sites-enabled/teamscop
rm -f /etc/nginx/sites-enabled/default

nginx -t
systemctl daemon-reload
systemctl enable --now postgresql
systemctl enable --now teamscop-api
systemctl enable --now nginx
systemctl restart teamscop-api
systemctl reload nginx

PUBLIC_IP="$(curl -4 -s --max-time 5 ifconfig.me || true)"
DOMAIN_IP="$(getent ahostsv4 teamscop.com | awk '{print $1; exit}' || true)"
if [[ -n "$PUBLIC_IP" && -n "$DOMAIN_IP" && "$PUBLIC_IP" == "$DOMAIN_IP" && ! -f /etc/letsencrypt/live/teamscop.com/fullchain.pem ]]; then
  certbot --nginx -d teamscop.com -d www.teamscop.com --non-interactive --agree-tos -m admin@teamscop.com --redirect || true
fi

echo "Deploy complete."
curl -sS http://127.0.0.1:5080/health || true
echo
curl -sS https://teamscop.com/health || true
echo
