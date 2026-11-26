#!/usr/bin/env bash
# ハカルの画面を mukai.works（../portfolio）の public/hakaru/ に組み込む。
# 公開そのものは portfolio 側で `npx vercel deploy --prod --yes`。
set -euo pipefail
cd "$(dirname "$0")/../web"
PORTFOLIO="${PORTFOLIO_DIR:-../../portfolio}"
# Git Bash は /hakaru/ を "C:/Program Files/Git/hakaru/" に書き換えるので、変換を止める
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' HAKARU_BASE=/hakaru/ npm run build
grep -q 'src="/hakaru/assets/' dist/index.html || { echo "部品のパスが /hakaru/ になっていません" >&2; exit 1; }
rm -rf "$PORTFOLIO/public/hakaru"
mkdir -p "$PORTFOLIO/public/hakaru"
cp -r dist/. "$PORTFOLIO/public/hakaru/"
echo "組み込み完了: $PORTFOLIO/public/hakaru"
