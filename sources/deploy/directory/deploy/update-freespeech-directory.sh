#!/bin/bash
# FreeSpeech directory - update on the VM (lives in the home of the deposit user, run with sudo after copying the published files to
# $FTP/directory/publish). appsettings.Local.json stays where it is (not overwritten).

# Deposit folder: "ftp" in the home of the user who runs sudo (the user who copied the files).
FTP="$(getent passwd "${SUDO_USER:-ubuntu}" | cut -d: -f6)/ftp"

# Stop before touching the running service if the published program is not where it is expected.
[ -f "$FTP/directory/publish/FreeSpeech.Directory.dll" ] || { echo "ERROR: $FTP/directory/publish/FreeSpeech.Directory.dll not found. Copy the published files there (the .dll directly in that folder) and run this script with sudo from the user who owns $FTP."; exit 1; }

systemctl stop freespeech-directory.service

# A service file deposited in $FTP/directory/deploy replaces the installed one.
if [ -f $FTP/directory/deploy/freespeech-directory.service ]; then
  cp $FTP/directory/deploy/freespeech-directory.service /etc/systemd/system/freespeech-directory.service
  systemctl daemon-reload
fi

mkdir -p /var/www/freespeech_directory
cp -r $FTP/directory/publish/* /var/www/freespeech_directory/

chown -R www-data:www-data /var/www/freespeech_directory/
chmod -R 755 /var/www/freespeech_directory/
[ -f /var/www/freespeech_directory/appsettings.Local.json ] && chmod 600 /var/www/freespeech_directory/appsettings.Local.json

systemctl start freespeech-directory.service

sleep 2
journalctl -u freespeech-directory.service -n 50 --no-pager

# The alarm: say loudly what is still missing. Secrets live in /var/www/freespeech_directory/appsettings.Local.json (never part of a publication,
# so an update cannot overwrite them); the check reads it with the service's own environment.
echo
( cd /var/www/freespeech_directory && env $(systemctl show freespeech-directory.service -p Environment --value) runuser -u www-data -- /usr/bin/dotnet FreeSpeech.Directory.dll check ) \
  || echo "!!!! NOT CONFIGURED: see the lines above. The service runs, but refuses whatever depends on the missing items. !!!!"
