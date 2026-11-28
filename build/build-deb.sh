#!/usr/bin/env bash
# Wrap a self-contained Linux release publish in a Debian binary package.
set -euo pipefail
if (( $# != 4 )); then
    echo "Usage: $0 PUBLISH_DIR UPSTREAM_VERSION amd64|arm64 OUTPUT_DIR" >&2
    exit 2
fi

publish=$(realpath "$1")
version=${2/-/'~'} # Debian prereleases sort before the corresponding final release.
arch=$3
case "$arch" in amd64|arm64) ;; *) echo "Unsupported Debian architecture: $arch" >&2; exit 2 ;; esac
dpkg --validate-version "$version"
mkdir -p "$4"
output=$(realpath "$4")
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir "$stage/source"
cp -r "$(dirname "$0")/debian" "$stage/source/"
cd "$stage/source"
ln -s "$publish" upstream
cat >debian/changelog <<CHANGELOG
sockseek ($version) unstable; urgency=medium

  * Package the upstream $2 release.

 -- $(sed -n 's/^Maintainer: //p' debian/control)  $(date -Ru)
CHANGELOG
dpkg-buildpackage --build=binary --no-sign --host-arch="$arch"
install -m644 "../sockseek_${version}_${arch}.deb" "$output/"
