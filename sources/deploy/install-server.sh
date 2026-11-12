#!/bin/bash
# *******************************************************************
# FreeSpeech - P2P Encrypted Messaging
# -----------------------------------------------------------------------
# Copyright (c) 2024-2026 Thomas Gourgnier
# Licensed under the GNU Affero General Public License v3.0 or later.
# SPDX-License-Identifier: AGPL-3.0-or-later
# *******************************************************************
#
# Installs or updates a FreeSpeech server from its archive (freespeech-<relay|push|directory>-<version>.tar.gz, built by scripts/package-server.ps1).
# Debian 12/13 and Ubuntu 22.04/24.04. Run it with sudo from the extracted folder:
#
#   sudo bash install.sh <domain> --email <address> [--contact <how to reach you>] [--no-certificate] [--no-firewall]
#
# What it does, in this order (every step can be run again without harm):
#   1. installs nginx, certbot, ufw, curl, openssl and the ASP.NET Core 10 runtime when missing;
#   2. opens SSH (on the port sshd listens on), 80 and 443 in the firewall;
#   3. copies the program to /var/www/freespeech_<server> and installs the systemd service (enabled, not started);
#   4. writes appsettings.Local.json with a new admin secret, only when the file does not exist (an update never touches it);
#   5. sets up the nginx site for <domain> and obtains the HTTPS certificate, unless --no-certificate;
#   6. runs `check`; only when it passes, starts the service. Otherwise it says what is missing: edit appsettings.Local.json, then
#      `sudo systemctl start <service>`;
#   7. creates the administration shortcuts (fsrelay, fspush, fsdir).
#
# --email         address Let's Encrypt keeps for expiry notices; giving it accepts the Let's Encrypt subscriber agreement.
# --contact       how another administrator can reach you (Peer:Contact), written in appsettings.Local.json when given.
# --no-certificate  no certificate request (a server on a local network gets its certificate by DNS validation: see the installation guide).
# --no-firewall   leaves ufw alone.
set -euo pipefail

die() { echo "ERROR: $*" >&2; exit 1; }
say() { echo; echo "==> $*"; }

usage() { sed -n '/^# Installs or updates/,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; }

DOMAIN=""; EMAIL=""; CONTACT=""; NO_CERT=0; NO_FW=0
while [ $# -gt 0 ]; do
  case "$1" in
    --email)          [ $# -ge 2 ] || die "--email needs an address"; EMAIL="$2"; shift 2 ;;
    --contact)        [ $# -ge 2 ] || die "--contact needs a value"; CONTACT="$2"; shift 2 ;;
    --no-certificate) NO_CERT=1; shift ;;
    --no-firewall)    NO_FW=1; shift ;;
    -h|--help)        usage; exit 0 ;;
    -*)               die "unknown option $1 (see --help)" ;;
    *)                [ -z "$DOMAIN" ] || die "only one domain"; DOMAIN="$1"; shift ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "run with sudo."
[ -n "$DOMAIN" ] || { usage; exit 1; }
echo "$DOMAIN" | grep -Eq '^([A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z]{2,}$' || die "'$DOMAIN' is not a domain name."
if [ "$NO_CERT" -eq 0 ] && [ -z "$EMAIL" ]; then die "--email <address> is needed for the certificate (or --no-certificate)."; fi

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -f "$HERE/VERSION" ] || die "VERSION not found next to this script: run it from the extracted archive."
read -r COMPONENT PKG_VERSION < "$HERE/VERSION"

case "$COMPONENT" in
  relay)     NAME=freespeech-relay;     APP=/var/www/freespeech_relay;     DLL=FreeSpeech.Relay.dll;     NGINX=freespeech-relay ;;
  push)      NAME=freespeech-push;      APP=/var/www/freespeech_push;      DLL=FreeSpeech.Push.dll;      NGINX=freespeech-push ;;
  directory) NAME=freespeech-directory; APP=/var/www/freespeech_directory; DLL=FreeSpeech.Directory.dll; NGINX=freespeech-directory ;;
  *) die "unknown server '$COMPONENT' in VERSION." ;;
esac
[ -f "$HERE/publish/$DLL" ] || die "$HERE/publish/$DLL not found."
[ -f "$HERE/deploy/$NAME.service" ] || die "$HERE/deploy/$NAME.service not found."
echo "FreeSpeech $COMPONENT $PKG_VERSION on $DOMAIN"

. /etc/os-release
case "${ID:-}" in ubuntu|debian) ;; *) die "Debian or Ubuntu only (this is '${ID:-unknown}')." ;; esac

# ---------------------------------------------------------------- 1. packages
say "Packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y nginx certbot python3-certbot-nginx ufw curl openssl ca-certificates

have_runtime() { command -v dotnet >/dev/null 2>&1 && [[ "$(dotnet --list-runtimes)" == *"Microsoft.AspNetCore.App 10."* ]]; }
if ! have_runtime; then
  say "ASP.NET Core 10 runtime"
  if [ "$ID" = ubuntu ]; then
    apt-get install -y software-properties-common
    add-apt-repository -y ppa:dotnet/backports
  else
    curl -fsSL -o /tmp/packages-microsoft-prod.deb "https://packages.microsoft.com/config/debian/${VERSION_ID%%.*}/packages-microsoft-prod.deb"
    dpkg -i /tmp/packages-microsoft-prod.deb
    rm -f /tmp/packages-microsoft-prod.deb
  fi
  apt-get update -y
  apt-get install -y aspnetcore-runtime-10.0
fi
have_runtime || die "the ASP.NET Core 10 runtime is not available after the installation."

# ---------------------------------------------------------------- 2. firewall
if [ "$NO_FW" -eq 0 ]; then
  say "Firewall"
  SSH_PORT="$(sshd -T 2>/dev/null | awk '$1=="port" { print $2; exit }' || true)"
  ufw allow "${SSH_PORT:-22}/tcp"
  ufw allow 80/tcp
  ufw allow 443/tcp
  ufw --force enable
fi

# ---------------------------------------------------------------- 3. program and service
say "Program and service"
systemctl stop "$NAME.service" 2>/dev/null || true
mkdir -p "$APP"
cp -r "$HERE/publish/." "$APP/"
if [ "$COMPONENT" = push ]; then mkdir -p "$APP/secrets"; fi
chown -R www-data:www-data "$APP"
chmod -R 755 "$APP"
if [ "$COMPONENT" = push ]; then chmod 700 "$APP/secrets"; fi

UNIT="/etc/systemd/system/$NAME.service"
if [ -f "$UNIT" ] && ! cmp -s "$HERE/deploy/$NAME.service" "$UNIT"; then
  cp "$UNIT" "$UNIT.previous"
  echo "The installed service file differed: the old one is kept as $UNIT.previous."
fi
cp "$HERE/deploy/$NAME.service" "$UNIT"
systemctl daemon-reload
systemctl enable "$NAME.service"

# ---------------------------------------------------------------- 4. configuration
SETTINGS="$APP/appsettings.Local.json"
if [ -f "$SETTINGS" ]; then
  say "Configuration: $SETTINGS exists, left as it is"
else
  say "Configuration: writing $SETTINGS"
  SECRET="$(openssl rand -hex 32)"
  CONTACT_JSON=""
  if [ -n "$CONTACT" ]; then
    CONTACT_ESC="$(printf '%s' "$CONTACT" | sed 's/\\/\\\\/g; s/"/\\"/g')"
    CONTACT_JSON=", \"Contact\": \"$CONTACT_ESC\""
  fi
  umask 077
  case "$COMPONENT" in
    relay)
      cat > "$SETTINGS" <<EOF
{
  "Admin": { "Secret": "$SECRET" },
  "Peer": { "Name": "$DOMAIN", "PublicUrl": "https://$DOMAIN"$CONTACT_JSON }
}
EOF
      ;;
    push)
      cat > "$SETTINGS" <<EOF
{
  "Admin": { "Secret": "$SECRET" },
  "Fcm": { "ServiceAccountFile": "$APP/secrets/firebase-service-account.json" },
  "Peer": { "Name": "$DOMAIN", "PublicUrl": "https://$DOMAIN"$CONTACT_JSON }
}
EOF
      ;;
    directory)
      cat > "$SETTINGS" <<EOF
{
  "Admin": { "Secret": "$SECRET" },
  "Peer": { "Name": "$DOMAIN", "PublicUrl": "https://$DOMAIN"$CONTACT_JSON }
}
EOF
      ;;
  esac
  unset SECRET
  chown www-data:www-data "$SETTINGS"
  chmod 600 "$SETTINGS"
fi

# ---------------------------------------------------------------- 5. nginx and certificate
say "nginx"
SITE="/etc/nginx/sites-available/$NGINX"
if [ -f "$SITE" ]; then
  echo "$SITE exists (certbot may have rewritten it): left as it is."
else
  PLACEHOLDER="$(awk '$1=="server_name" { gsub(";", "", $2); print $2; exit }' "$HERE/deploy/nginx-$NAME")"
  [ -n "$PLACEHOLDER" ] || die "no server_name in deploy/nginx-$NAME."
  sed "s/$PLACEHOLDER/$DOMAIN/g" "$HERE/deploy/nginx-$NAME" > "$SITE"
fi
ln -sf "$SITE" "/etc/nginx/sites-enabled/$NGINX"
nginx -t
systemctl reload nginx

CERT_NOTE=""
if [ "$NO_CERT" -eq 1 ]; then
  CERT_NOTE="No certificate requested (--no-certificate): get one for $DOMAIN, then: sudo certbot install --nginx --cert-name $DOMAIN"
elif [ -d "/etc/letsencrypt/live/$DOMAIN" ]; then
  echo "A certificate for $DOMAIN already exists."
else
  say "HTTPS certificate"
  if ! certbot --nginx -d "$DOMAIN" --non-interactive --agree-tos -m "$EMAIL" --redirect; then
    CERT_NOTE="The certificate request failed (is the DNS record of $DOMAIN pointing to this machine, and ports 80/443 reachable?). Once fixed: sudo certbot --nginx -d $DOMAIN"
  fi
fi

# ---------------------------------------------------------------- 6. check and start
say "Check"
run_check() { ( cd "$APP" && env $(systemctl show "$NAME.service" -p Environment --value) runuser -u www-data -- /usr/bin/dotnet "$DLL" check ); }
STARTED=0
if run_check; then
  systemctl start "$NAME.service"
  STARTED=1
  sleep 2
  journalctl -u "$NAME.service" -n 20 --no-pager || true
fi

# ---------------------------------------------------------------- 7. shortcuts
if [ -f "$HERE/deploy/install-shortcuts.sh" ]; then bash "$HERE/deploy/install-shortcuts.sh"; fi

echo
echo "=========================================================="
if [ "$STARTED" -eq 1 ]; then
  echo "FreeSpeech $COMPONENT $PKG_VERSION is running: https://$DOMAIN"
else
  echo "FreeSpeech $COMPONENT $PKG_VERSION is installed but NOT started: the check above lists what is missing."
  echo "Edit $SETTINGS, then: sudo systemctl start $NAME"
fi
[ -z "$CERT_NOTE" ] || echo "$CERT_NOTE"
echo "=========================================================="
