#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$script_dir/../TutsVideoPlayer.Web/Media/demo-fixture.mp4"
mkdir -p "$(dirname "$out")"

ffmpeg -y -hide_banner -loglevel error \
  -f lavfi -i "testsrc2=size=320x180:rate=24" \
  -f lavfi -i "sine=frequency=440:sample_rate=44100" \
  -t 10 \
  -c:v libx264 -preset veryfast -crf 30 -pix_fmt yuv420p \
  -c:a aac -b:a 64k \
  -movflags +faststart \
  "$out"

echo "Generated $out"
