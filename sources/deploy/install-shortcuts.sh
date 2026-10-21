#!/bin/bash
# Creates the administration shortcuts of the servers installed on this machine:
#   fsrelay  -> relay      (/var/www/freespeech_relay)
#   fspush   -> push       (/var/www/freespeech_push)
#   fsdir    -> directory  (/var/www/freespeech_directory)
# Each one runs the program's `admin` command as www-data (the user that can read appsettings.Local.json), from any folder:
#   fsrelay status        fsrelay            (interactive prompt `relay>`)
# Run it once with sudo:  sudo bash install-shortcuts.sh   (again after installing another server; it never touches a program)
set -e
[ "$(id -u)" -eq 0 ] || { echo "Run with sudo." >&2; exit 1; }

make() {   # name, folder, dll, local port
  if [ ! -f "/var/www/$2/$3" ]; then echo "skipped $1: /var/www/$2/$3 not found"; return; fi
  cat > "/usr/local/bin/$1" <<SCRIPT
#!/bin/bash
cd /var/www/$2 && exec sudo -u www-data env FS_ADMIN_URL=http://127.0.0.1:$4 /usr/bin/dotnet /var/www/$2/$3 admin "\$@"
SCRIPT
  chmod 755 "/usr/local/bin/$1"
  echo "created $1"
}

make fsrelay freespeech_relay FreeSpeech.Relay.dll 5290
make fspush freespeech_push FreeSpeech.Push.dll 5291
make fsdir freespeech_directory FreeSpeech.Directory.dll 5294
