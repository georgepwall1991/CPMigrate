#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

mkdir -p ../../assets/video

npx remotion render src/index.ts Hero ../../assets/video/cpmigrate-hero.mp4
npx remotion still src/index.ts Hero ../../assets/video/cpmigrate-hero-poster.png --frame=280
npx remotion still src/index.ts SocialCard ../../site/assets/social-card.png

ls -lh ../../assets/video/ ../../site/assets/social-card.png
