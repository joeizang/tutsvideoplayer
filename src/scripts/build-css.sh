#!/usr/bin/env bash
set -euo pipefail

web_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/../TutsVideoPlayer.Web" && pwd)"
cd "$web_dir"
npx --no-install tailwindcss -i Views/Shared/app.tailwind.css -o wwwroot/css/app.css --minify "$@"
echo "Generated wwwroot/css/app.css"
