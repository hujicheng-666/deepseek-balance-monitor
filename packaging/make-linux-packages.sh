#!/usr/bin/env bash
# Build native Linux packages on Linux.
# Usage: ./packaging/make-linux-packages.sh [linux-x64|linux-arm64]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in
    x86_64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) echo "Unsupported Linux architecture: $(uname -m)" >&2; exit 1 ;;
  esac
fi

case "$RID" in
  linux-x64) DEB_ARCH="amd64"; RPM_ARCH="x86_64" ;;
  linux-arm64) DEB_ARCH="arm64"; RPM_ARCH="aarch64" ;;
  *) echo "RID must be linux-x64 or linux-arm64" >&2; exit 1 ;;
esac

command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required to create .deb packages" >&2; exit 1; }
command -v rpmbuild >/dev/null || { echo "rpmbuild is required to create .rpm packages" >&2; exit 1; }

VERSION="2.0.0"
PUBLISH="$ROOT/.artifacts/publish/$RID"
STAGE="$ROOT/.artifacts/linux-stage"
RPMTOP="$ROOT/.artifacts/rpmbuild"
RELEASE="$ROOT/release"
rm -rf "$PUBLISH" "$STAGE" "$RPMTOP"
mkdir -p "$PUBLISH" "$RELEASE"

dotnet publish "$ROOT/DeepSeekMonitor.Avalonia/DeepSeekMonitor.Avalonia.csproj" \
  -c Release -r "$RID" --self-contained true -o "$PUBLISH" --nologo

mkdir -p "$STAGE/DEBIAN" "$STAGE/usr/lib/deepseek" "$STAGE/usr/bin" \
  "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/256x256/apps"
cp -R "$PUBLISH/." "$STAGE/usr/lib/deepseek/"
chmod +x "$STAGE/usr/lib/deepseek/DeepSeekMonitor"
cat > "$STAGE/usr/bin/deepseek-monitor" <<'LAUNCHER'
#!/usr/bin/env sh
exec /usr/lib/deepseek/DeepSeekMonitor "$@"
LAUNCHER
chmod 755 "$STAGE/usr/bin/deepseek-monitor"
cp "$ROOT/DeepSeekMonitor.Avalonia/Assets/whale.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/deepseek-monitor.png"
cat > "$STAGE/usr/share/applications/deepseek-monitor.desktop" <<'DESKTOP'
[Desktop Entry]
Name=DeepSeek
Comment=DeepSeek balance monitor
Exec=deepseek-monitor
Icon=deepseek-monitor
Terminal=false
Type=Application
Categories=Utility;
DESKTOP
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: deepseek-monitor
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: hujicheng-666
Description: DeepSeek balance monitor
CONTROL

dpkg-deb --root-owner-group --build "$STAGE" "$RELEASE/DeepSeek_${VERSION}_linux_${DEB_ARCH}.deb"

mkdir -p "$RPMTOP/BUILDROOT" "$RPMTOP/RPMS" "$RPMTOP/SPECS"
cat > "$RPMTOP/SPECS/deepseek-monitor.spec" <<SPEC
Name:           deepseek-monitor
Version:        $VERSION
Release:        1
Summary:        DeepSeek balance monitor
License:        Proprietary
BuildArch:      $RPM_ARCH
%description
DeepSeek balance monitor.
%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
cp -a $STAGE/usr %{buildroot}/
%files
/usr/lib/deepseek
/usr/bin/deepseek-monitor
/usr/share/applications/deepseek-monitor.desktop
/usr/share/icons/hicolor/256x256/apps/deepseek-monitor.png
SPEC
rpmbuild --define "_topdir $RPMTOP" --define "_buildrootdir $RPMTOP/BUILDROOT" \
  -bb "$RPMTOP/SPECS/deepseek-monitor.spec"
find "$RPMTOP/RPMS" -name '*.rpm' -exec cp {} "$RELEASE/DeepSeek_${VERSION}_linux_${RPM_ARCH}.rpm" \;
echo "Created Linux installers in $RELEASE"
