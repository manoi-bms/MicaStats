#!/bin/sh
# Build MicaStats.Pi in Release and install it for the current user:
#   $PREFIX/opt/micastats-pi/   published app
#   $PREFIX/bin/micastats-pi     launcher (pass --hidden to start as a hidden dropdown)
#   $PREFIX/bin/micastats-toggle show/hide the dropdown, for panel click bindings
# PREFIX defaults to ~/.local. Needs the .NET 8 SDK (`dotnet` on PATH or ~/.dotnet).
set -eu

here=$(cd "$(dirname "$0")" && pwd)
prefix="${PREFIX:-$HOME/.local}"
dotnet="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
dotnet_root=$(dirname "$(readlink -f "$dotnet")")

DOTNET_CLI_TELEMETRY_OPTOUT=1 "$dotnet" publish "$here/MicaStats.Pi.csproj" \
  -c Release -o "$prefix/opt/micastats-pi" --nologo

mkdir -p "$prefix/bin"
cat > "$prefix/bin/micastats-pi" <<EOF
#!/bin/sh
export DOTNET_ROOT="\${DOTNET_ROOT:-$dotnet_root}"
exec "$prefix/opt/micastats-pi/MicaStats.Pi" "\$@"
EOF
chmod 755 "$prefix/bin/micastats-pi"
install -m 755 "$here/scripts/micastats-toggle" "$prefix/bin/micastats-toggle"

cat <<EOF

Installed. To use MicaStats as a menu-bar dropdown on labwc + waybar:

1. Start it hidden at login — add to ~/.config/labwc/autostart:
     $prefix/bin/micastats-pi --hidden &

2. Open it from the bar — add to each waybar stats module (cpu, memory, ...):
     "on-click": "$prefix/bin/micastats-toggle"

3. Keep it out of the taskbar and let it stay on top — inside <labwc_config>
   in ~/.config/labwc/rc.xml (labwc-pi merges it with the system rc.xml):
     <windowRules>
       <windowRule identifier="MicaStats*" skipTaskbar="yes"
                   skipWindowSwitcher="yes" allowAlwaysOnTop="yes" />
     </windowRules>
   then run: labwc --reconfigure
EOF
