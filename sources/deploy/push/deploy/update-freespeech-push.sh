#!/bin/bash
# FreeSpeech push gateway - update on the VM (lives in the home of the deposit user, run with sudo after copying the published files to
# $FTP/push/publish). appsettings.Local.json and secrets/ stay where they are (not overwritten).

# Deposit folder: "ftp" in the home of the user who runs sudo (the user who copied the files).
FTP="$(getent passwd "${SUDO_USER:-ubuntu}" | cut -d: -f6)/ftp"

# Stop before touching the running service if the published program is not where it is expected.
[ -f "$FTP/push/publish/FreeSpeech.Push.dll" ] || { echo "ERROR: $FTP/push/publish/FreeSpeech.Push.dll not found. Copy the published files there (the .dll directly in that folder) and run this script with sudo from the user who owns $FTP."; exit 1; }

systemctl stop freespeech-push.service

# A service file deposited in $FTP/push/deploy replaces the installed one.
if [ -f $FTP/push/deploy/freespeech-push.service ]; then
  cp $FTP/push/deploy/freespeech-push.service /etc/systemd/system/freespeech-push.service
  systemctl daemon-reload
fi

mkdir -p /var/www/freespeech_push
cp -r $FTP/push/publish/* /var/www/freespeech_push/

chown -R www-data:www-data /var/www/freespeech_push/
chmod -R 755 /var/www/freespeech_push/
[ -f /var/www/freespeech_push/appsettings.Local.json ] && chmod 600 /var/www/freespeech_push/appsettings.Local.json
[ -d /var/www/freespeech_push/secrets ] && chmod 700 /var/www/freespeech_push/secrets

systemctl start freespeech-push.service

sleep 2
journalctl -u freespeech-push.service -n 50 --no-pager

# The alarm: say loudly what is still missing. Secrets live in /var/www/freespeech_push/appsettings.Local.json (never part of a publication,
# so an update cannot overwrite them); the check reads it with the service's own environment.
echo
( cd /var/www/freespeech_push && env $(systemctl show freespeech-push.service -p Environment --value) runuser -u www-data -- /usr/bin/dotnet FreeSpeech.Push.dll check ) \
  || echo "!!!! NOT CONFIGURED: see the lines above. The service runs, but refuses whatever depends on the missing items. !!!!"
