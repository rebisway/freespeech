#!/bin/bash
# FreeSpeech relay server - update on the VM: run with sudo after copying the published files
# to $FTP/relay/publish.

# Deposit folder: "ftp" in the home of the user who runs sudo (the user who copied the files).
FTP="$(getent passwd "${SUDO_USER:-ubuntu}" | cut -d: -f6)/ftp"

# Stop before touching the running service if the published program is not where it is expected.
[ -f "$FTP/relay/publish/FreeSpeech.Relay.dll" ] || { echo "ERROR: $FTP/relay/publish/FreeSpeech.Relay.dll not found. Copy the published files there (the .dll directly in that folder) and run this script with sudo from the user who owns $FTP."; exit 1; }

systemctl stop freespeech-relay.service

# A service file deposited in $FTP/relay/deploy replaces the installed one.
if [ -f $FTP/relay/deploy/freespeech-relay.service ]; then
  cp $FTP/relay/deploy/freespeech-relay.service /etc/systemd/system/freespeech-relay.service
  systemctl daemon-reload
fi

mkdir -p /var/www/freespeech_relay
cp -r $FTP/relay/publish/* /var/www/freespeech_relay/

chown -R www-data:www-data /var/www/freespeech_relay/
chmod -R 755 /var/www/freespeech_relay/
# appsettings.Local.json (Admin:Secret) is not part of the published files, so the copy above leaves it alone.
[ -f /var/www/freespeech_relay/appsettings.Local.json ] && chmod 600 /var/www/freespeech_relay/appsettings.Local.json

systemctl start freespeech-relay.service

sleep 2
journalctl -u freespeech-relay.service -n 50 --no-pager

# The alarm: say loudly what is still missing. Secrets live in /var/www/freespeech_relay/appsettings.Local.json (never part of a publication,
# so an update cannot overwrite them); the check reads it with the service's own environment.
echo
( cd /var/www/freespeech_relay && env $(systemctl show freespeech-relay.service -p Environment --value) runuser -u www-data -- /usr/bin/dotnet FreeSpeech.Relay.dll check ) \
  || echo "!!!! NOT CONFIGURED: see the lines above. The service runs, but refuses whatever depends on the missing items. !!!!"
