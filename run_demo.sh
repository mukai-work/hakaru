#!/usr/bin/env bash
# デモ用の3構成を順に回す（審判はすべて Qwen・思考なしで固定）。
# 1構成ごとに GPU ロック（HAKARU_GPU_LOCK）を取り、他の GPU 処理と VRAM を取り合わない。
set -e
cd "$(dirname "$0")"
dotnet build engine/Hakaru.Cli -c Release -v q >/dev/null
CLI="dotnet engine/Hakaru.Cli/bin/Release/net10.0/Hakaru.Cli.dll"
LOCK="py -3.11 tools/with_gpu_lock.py"
COMMON="--dataset datasets/sorano-faq.jsonl --docs datasets/sorano-docs --judge qwen3.6-35b-a3b --reasoning none --out results"
$LOCK $CLI run $COMMON --model qwen3.6-35b-a3b --context off --label qwen-no-docs
$LOCK $CLI run $COMMON --model qwen3.6-35b-a3b --context on  --label qwen-with-docs
$LOCK $CLI run $COMMON --model claude:haiku    --context on  --label claude-haiku-with-docs
$CLI index --dir results
