#!/bin/bash
# Read-only survey of how this Steam Frame installs apps, for FrameUpload v7.
# Changes nothing: it only lists, reads and asks tools for their --help.
# Run from the PC with probe-sideload.cmd; the output lands next to it.

sec() { printf '\n===== %s =====\n' "$1"; }
run() { printf '$ %s\n' "$*"; timeout 10 bash -c "$*" 2>&1 | head -n "${LINES_MAX:-40}"; }

sec "system"
run 'cat /etc/os-release'
run 'uname -a'
run 'id'
run 'sudo -n true && echo "sudo: passwordless" || echo "sudo: needs a password"'
run 'steamos-readonly status'
run 'df -h / /home /var 2>/dev/null'

sec "app tools on PATH"
for t in flatpak adb waydroid pm cmd appimagetool steamos-add-to-steam steam \
         FEXInterpreter FEXBash box64 proton umu-run wine ar dpkg pacman rpm \
         xdg-mime kioclient5 kioclient6 kde-open5 kde-open; do
  p=$(command -v "$t" 2>/dev/null) && echo "$t -> $p"
done
run 'ls /usr/bin /usr/lib/steamos /usr/libexec 2>/dev/null | grep -iE "android|apk|sideload|install|waydroid|flatpak|steamos-" | sort -u'

sec "what the right-click Install runs (.apk and others)"
run 'xdg-mime query default application/vnd.android.package-archive'
run 'grep -rlsiE "apk|android|sideload|steamos-add" /usr/share/kio/servicemenus /usr/share/kservices5/ServiceMenus /usr/share/kservices6 ~/.local/share/kio/servicemenus /usr/share/applications ~/.local/share/applications 2>/dev/null'
LINES_MAX=80 run 'for f in $(grep -rlsiE "apk|android|sideload|steamos-add" /usr/share/kio/servicemenus /usr/share/kservices5/ServiceMenus /usr/share/kservices6 ~/.local/share/kio/servicemenus /usr/share/applications ~/.local/share/applications 2>/dev/null | head -n 8); do echo "--- $f"; cat "$f"; done'
run 'grep -sE "apk|android|appimage|flatpakref|x-ms-dos|x-executable" /usr/share/applications/mimeapps.list ~/.config/mimeapps.list /etc/xdg/mimeapps.list'

sec "android side"
run 'systemctl list-units --all --no-pager 2>/dev/null | grep -iE "android|waydroid|anbox|apk|container"'
run 'systemctl --user list-units --all --no-pager 2>/dev/null | grep -iE "android|waydroid|anbox|apk"'
run 'ps -eo pid,user,comm,args --no-headers | grep -iE "android|waydroid|anbox|binder|lxc" | grep -v grep'
run 'ls -la /dev/binder* /dev/anbox* 2>/dev/null; ls /var/lib/waydroid ~/.local/share/waydroid 2>/dev/null'
run 'adb devices 2>&1'
run 'ls -la ~/.local/share/android* ~/.var/app 2>/dev/null'

sec "flatpak"
run 'flatpak --version; flatpak remotes --show-details; flatpak list --app --columns=application,origin,installation | head -n 30'

sec "steam library shortcuts (non-Steam games)"
run 'ls -d ~/.steam/steam/userdata/*/config 2>/dev/null'
run 'ls -la ~/.steam/steam/userdata/*/config/shortcuts.vdf 2>/dev/null'
run 'strings ~/.steam/steam/userdata/*/config/shortcuts.vdf 2>/dev/null | head -n 40'

sec "windows .exe and x86 support"
run 'ls ~/.steam/steam/steamapps/common 2>/dev/null | grep -iE "proton|fex|steam linux runtime"'
run 'ls /usr/share/fex-emu /usr/lib/fex-emu ~/.fex-emu 2>/dev/null | head'
run 'cat /proc/sys/fs/binfmt_misc/status 2>/dev/null; ls /proc/sys/fs/binfmt_misc 2>/dev/null'

sec "help text of anything install-related"
for t in $(ls /usr/bin /usr/lib/steamos 2>/dev/null | grep -iE "android|apk|sideload|steamos-add|install-app" | sort -u | head -n 6); do
  p=$(command -v "$t" || echo "/usr/lib/steamos/$t")
  run "file '$p'"
  run "'$p' --help"
done

sec "recent log lines about apk / install"
run 'journalctl --user -n 4000 --no-pager 2>/dev/null | grep -iE "apk|android|sideload|install" | tail -n 30'
run 'journalctl -n 4000 --no-pager 2>/dev/null | grep -iE "apk|android|sideload" | tail -n 30'

sec "done"
