#!/bin/sh
# Home Assistant writes /data/options.json. tcl-remote reads tv_ip, port and
# client_name from that file unless TCL_TV_IP / TCL_PORT / TCL_NAME override them.
set -eu
exec /usr/bin/tcl-remote serve --host "${TCL_HOST:-0.0.0.0}"
