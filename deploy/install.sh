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
mkdir -p /opt/teamscop/api /var/lib/teamscop/avatars /etc/teamscop /var/www/certbot
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
Jwt__AccessTokenMinutes=720
CompanyToken__Key=${COMPANY_KEY}
Storage__AvatarRoot=/var/lib/teamscop/avatars
Storage__PublicAvatarBasePath=/media/avatars
EOF
  chmod 600 /etc/teamscop/api.env
  echo "Wrote /etc/teamscop/api.env"
  echo "IMPORTANT: embed CompanyToken__Key into the Windows agent build."
fi

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

server {
    listen 80;
    listen [::]:80;
    server_name teamscop.com www.teamscop.com;

    location /.well-known/acme-challenge/ {
        root /var/www/certbot;
    }

    location /media/avatars/ {
        alias /var/lib/teamscop/avatars/;
    }

    location /api/ {
        limit_req zone=auth_limit burst=20 nodelay;
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
