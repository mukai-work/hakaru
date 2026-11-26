# ハカル — AI機能の精度チェック台

**デモ: https://mukai.works/hakaru**（ログイン不要・架空の社内ヘルプAIを30問×3構成で実測した結果）

AI機能を入れる前に、**正答率・でっちあげ（ハルシネーション）率・1問あたりの費用・応答時間**を数字で出すための小さな道具です。
同じ評価セットを、モデル・プロンプト・文書の有無を変えた複数の構成で回し、1問ずつの差分と「悪くなった問題」を一覧にします。

- 評価エンジン: C#（.NET 10）。接続先は `ILlmClient` で差し替え（OpenAI互換API＝LM Studio / Ollama / OpenAI など、`claude -p`）
- 画面: Vue 3 + Vite の静的サイト。あらかじめ計算した結果JSONを表示するだけで、画面からAIは呼びません
- データを社外に出さない構成: ローカルLLM（LM Studio）だけで回答・採点まで完結できます

## 仕組み

```
datasets/*.jsonl ──┐
datasets/<docs>/ ──┼─▶ hakaru run ──▶ results/<runId>.json ──▶ hakaru index ──▶ web（表示のみ）
   回答役 ILlmClient ┘        │
   審判役 ILlmClient ─────────┘  キーワード照合 × AI審判 → 食い違いは「要確認」
```

1問ごとに次の5つのどれかで判定します。

| 判定 | 意味 |
|---|---|
| Correct | 正しい（答えの無い質問に「分からない」と答えた場合も含む） |
| Partial | 方向は合っているが、大事な条件が抜けている |
| Wrong | 文書と食い違う |
| Hallucination | 文書に無いことを、あるかのように言い切った |
| Abstained | 答えがあるのに「分からない」と答えた |

採点は **キーワード照合（安い・ぶれない・言い換えに弱い）** と **AI審判（温度0・審判モデル固定）** の二重です。両者が食い違った問題は `needsReview` として残し、人が確かめます。

## 使い方

```bash
# 1構成を回す（LM Studio の Qwen を回答役と審判役に）
dotnet run --project engine/Hakaru.Cli -c Release -- run \
  --dataset datasets/sorano-faq.jsonl --docs datasets/sorano-docs \
  --model qwen3.6-35b-a3b --judge qwen3.6-35b-a3b --reasoning none \
  --context on --label qwen-with-docs --out results

# Claude（claude -p）を回答役に
... run --model claude:haiku --judge qwen3.6-35b-a3b --reasoning none --label claude-haiku-with-docs ...

# 2回の結果を比べる（悪化があれば終了コード1 → CIで改修のたびに回せる）
dotnet run --project engine/Hakaru.Cli -- compare --before results/A.json --after results/B.json

# 画面用の一覧を作り、画面を起動
dotnet run --project engine/Hakaru.Cli -- index --dir results
cd web && npm install && npm run dev
```

評価セットは1行1問の JSONL です。

```json
{"id":"q07","question":"年払いを契約して1週間で解約したら返金されますか？","expected":"契約から14日以内なので全額返金される","keywords":["14日","全額"],"answerable":true}
```

`keywords` はすべて必要で、`"a|b"` と書くとどちらか一方でよい扱いです。`answerable:false` は「文書に答えが無い質問」です。

## 同梱のデモ素材

`datasets/sorano-docs/` は架空の会社「ソラノート株式会社」の社内文書8本、`datasets/sorano-faq.jsonl` はその30問（うち4問は答えが無い質問）です。実在の会社・顧客の情報は含みません。

## 分かっている限界

- 30問は小さな見本です。本番の質問の分布を代表するとは限りません
- AI審判の判定は完全ではありません。審判と回答者が同じモデルだと身びいきが出る可能性があります
- `claude -p` は検証用です（呼び出し回数の制限があるため、製品の中核にはしていません）

## テスト

```bash
cd engine && dotnet test
```

---
ムカイクリエイトスタジオ — https://mukai.works ／ ご相談: info@mukai.works
