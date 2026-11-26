<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import type { IndexEntry, ItemResult, RunResult, Verdict } from './types'
import { VERDICT_LABEL, VERDICT_RANK } from './types'

const CONTACT = 'info@mukai.works'
const BASE = import.meta.env.BASE_URL

const index = ref<IndexEntry[]>([])
const runs = ref<Record<string, RunResult>>({})
const error = ref('')

const baseId = ref('')
const targetId = ref('')
const filter = ref<'all' | 'bad' | 'review' | 'changed'>('changed')

onMounted(async () => {
  try {
    const res = await fetch(`${BASE}data/index.json`)
    if (!res.ok) throw new Error(`index.json を読めません（${res.status}）`)
    index.value = await res.json()
    const loaded = await Promise.all(
      index.value.map(async (e) => {
        const r = await fetch(`${BASE}data/${e.file}`)
        return [e.runId, (await r.json()) as RunResult] as const
      }),
    )
    runs.value = Object.fromEntries(loaded)
    // 既定の比較: 文書なし → 文書あり（同じモデル）があればそれ、なければ先頭と末尾
    const noDocs = index.value.find((e) => !e.withContext)
    const withDocs = index.value.find((e) => e.withContext && e.model === noDocs?.model)
    baseId.value = (noDocs ?? index.value[0])?.runId ?? ''
    targetId.value = (withDocs ?? index.value[index.value.length - 1])?.runId ?? ''
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

const base = computed(() => runs.value[baseId.value])
const target = computed(() => runs.value[targetId.value])

const pct = (v: number) => `${(v * 100).toFixed(1)}%`
const usd = (v: number) => (v === 0 ? '0円（ローカル）' : `$${v.toFixed(5)}`)
const sec = (v: number) => `${v.toFixed(1)}秒`
const runName = (e: { label: string; model: string; withContext: boolean }) =>
  `${shortModel(e.model)}・社内文書${e.withContext ? 'あり' : 'なし'}`
function shortModel(m: string) {
  return m.replace(' (reasoning:none)', '').replace('qwen3.6-35b-a3b', 'Qwen3.6 35B（ローカル）').replace('claude-haiku', 'Claude Haiku（クラウド）')
}

/** 一覧で一番よい値に印を付ける */
const best = computed(() => {
  const s = index.value.map((e) => e.summary)
  if (!s.length) return null
  return {
    accuracy: Math.max(...s.map((x) => x.accuracy)),
    hallucinationRate: Math.min(...s.map((x) => x.hallucinationRate)),
    cost: Math.min(...s.map((x) => x.costPerQuestionUsd)),
    latency: Math.min(...s.map((x) => x.avgLatencySeconds)),
  }
})

interface Row {
  id: string
  question: string
  expected: string
  answerable: boolean
  before?: ItemResult
  after: ItemResult
  change: 'worse' | 'better' | 'same'
}

const rows = computed<Row[]>(() => {
  if (!target.value) return []
  const map = new Map(base.value?.items.map((i) => [i.id, i]) ?? [])
  return target.value.items.map((a) => {
    const b = map.get(a.id)
    let change: Row['change'] = 'same'
    if (b && VERDICT_RANK[a.verdict] < VERDICT_RANK[b.verdict]) change = 'worse'
    else if (b && VERDICT_RANK[a.verdict] > VERDICT_RANK[b.verdict]) change = 'better'
    return { id: a.id, question: a.question, expected: a.expected, answerable: a.answerable, before: b, after: a, change }
  })
})

const changeCount = computed(() => ({
  worse: rows.value.filter((r) => r.change === 'worse').length,
  better: rows.value.filter((r) => r.change === 'better').length,
}))

const shown = computed(() =>
  rows.value.filter((r) => {
    if (filter.value === 'bad') return r.after.verdict !== 'Correct'
    if (filter.value === 'review') return r.after.needsReview || r.before?.needsReview || !!r.after.humanNote || !!r.before?.humanNote
    if (filter.value === 'changed') return r.change !== 'same'
    return true
  }),
)

// 比べる組が同じになったら、もう一方をずらす
watch(baseId, (v) => {
  if (v === targetId.value) targetId.value = index.value.find((e) => e.runId !== v)?.runId ?? ''
})

const humanFixed = (id: string) => runs.value[id]?.items.filter((i) => i.humanNote).length ?? 0

const verdictClass = (v: Verdict) => `v v-${v.toLowerCase()}`

/** 判定内訳の積み上げ棒（左から よい → わるい の順） */
const STACK: { key: Verdict; field: 'correct' | 'partial' | 'abstained' | 'wrong' | 'hallucination' }[] = [
  { key: 'Correct', field: 'correct' },
  { key: 'Partial', field: 'partial' },
  { key: 'Abstained', field: 'abstained' },
  { key: 'Wrong', field: 'wrong' },
  { key: 'Hallucination', field: 'hallucination' },
]
</script>

<template>
  <header class="top">
    <div class="wrap top-inner">
      <div class="brand">
        <span class="logo" aria-hidden="true">ハ</span>
        <span><strong>ハカル</strong><small>AI機能の精度チェック台</small></span>
      </div>
      <nav class="studio-nav" aria-label="作った人">
        <a class="studio" href="https://mukai.works/products">ほかの製品</a>
        <a class="studio" href="https://mukai.works/engineer">開発者のプロフィール</a>
      </nav>
    </div>
  </header>

  <main class="wrap">
    <section class="hero">
      <h1>AI機能を入れる前に、<br />正答率とコストを数字で出します。</h1>

      <div class="about">
        <p class="about-lead">
          ハカルは、<strong>AIの答えがどれくらい当たっているかを、テストのように点数で測る道具</strong>です。
        </p>
        <p>
          自社のサービスにAIのチャット（問い合わせ対応・社内ヘルプなど）を入れるとき、いちばん怖いのは、AIがもっともらしい嘘を言うことです。何問か試して「なんとなく大丈夫そう」で出してしまう代わりに、その「なんとなく」を数字に置き換えます。モデルを安いものに替えるときや、指示文を書き換えるときも、同じ問題で解かせて点数が下がっていないかを確かめられます。
        </p>
        <ol class="test-steps" aria-label="ハカルがやっていること">
          <li>
            <span class="num">1</span>
            <strong>問題用紙を作る</strong>
            <span>「チームプランは月いくら？」→ 正解「980円」のような、質問と正解の組を用意する</span>
          </li>
          <li>
            <span class="num">2</span>
            <strong>AIに解かせる</strong>
            <span>同じ問題を、条件を変えたAIに解かせる（社内の資料を渡す／渡さない、ローカルのAI／Claude など）</span>
          </li>
          <li>
            <span class="num">3</span>
            <strong>採点する</strong>
            <span>正解のキーワードが入っているかと、採点役のAIの判定の2つで採点し、最後は人が見直す</span>
          </li>
          <li>
            <span class="num">4</span>
            <strong>成績表を出す</strong>
            <span>正答率・嘘をついた割合・1問あたりの費用を並べ、悪くなった問題を一覧にする</span>
          </li>
        </ol>
      </div>

      <h2 class="sample-head">この画面の見本について</h2>
      <p class="lead">
        社内ヘルプのAIを想定し、同じ30問を3つの構成で答えさせて採点しました。下の数字はすべてこの画面のために実際に動かした結果です（各構成1回・2026-11-26）。AI審判の採点に誤りがあり人が検算して直した問題は、直した理由を1問ずつ残しています。
      </p>
      <p class="note">
        題材は架空の会社「ソラノート株式会社」の社内文書8本です。30問のうち4問は、文書に答えが無い質問です（「分かりません」と答えるのが正解）。
      </p>
    </section>

    <p v-if="error" class="error">結果を読み込めませんでした: {{ error }}</p>

    <section v-if="index.length" class="block">
      <h2>30問の判定の内訳</h2>
      <div class="stacks">
        <div v-for="e in index" :key="e.runId" class="stack-row">
          <div class="stack-label">{{ runName(e) }}</div>
          <div class="stack" role="img" :aria-label="`${runName(e)}: ${STACK.map((x) => `${VERDICT_LABEL[x.key]} ${e.summary[x.field]}問`).join('、')}`">
            <span
              v-for="x in STACK.filter((x) => e.summary[x.field] > 0)"
              :key="x.key"
              :class="`seg seg-${x.key.toLowerCase()}`"
              :style="{ flexGrow: e.summary[x.field] }"
              :title="`${VERDICT_LABEL[x.key]} ${e.summary[x.field]}問`"
            >{{ e.summary[x.field] }}</span>
          </div>
        </div>
      </div>
      <ul class="legend">
        <li v-for="x in STACK" :key="x.key"><span :class="`dot seg-${x.key.toLowerCase()}`"></span>{{ VERDICT_LABEL[x.key] }}</li>
      </ul>
    </section>

    <section v-if="index.length && best" class="block">
      <h2>3つの構成の比較</h2>
      <div class="table-scroll">
        <table class="summary">
          <thead>
            <tr>
              <th scope="col">構成</th>
              <th scope="col">正答率</th>
              <th scope="col">でっちあげ率</th>
              <th scope="col">1問あたりの費用</th>
              <th scope="col">平均の応答時間</th>
              <th scope="col">人の確認が必要</th>
              <th scope="col">人が修正</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in index" :key="e.runId">
              <th scope="row">{{ runName(e) }}</th>
              <td :class="{ best: e.summary.accuracy === best.accuracy }">{{ pct(e.summary.accuracy) }}</td>
              <td :class="{ best: e.summary.hallucinationRate === best.hallucinationRate }">{{ pct(e.summary.hallucinationRate) }}</td>
              <td :class="{ best: e.summary.costPerQuestionUsd === best.cost }">{{ usd(e.summary.costPerQuestionUsd) }}</td>
              <td :class="{ best: e.summary.avgLatencySeconds === best.latency }">{{ sec(e.summary.avgLatencySeconds) }}</td>
              <td>{{ e.summary.needsReview }}問</td>
              <td>{{ humanFixed(e.runId) }}問</td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="caption">
        太字はその列で一番よい値。費用はローカルLLMは電気代を除き0円。Claudeは <code>claude -p</code>（Claude Code経由）が報告した額で、質問と社内文書のほかに Claude Code 側の文脈も送っていて（内訳は未調査）、入力は1問あたり平均約17万トークン（キャッシュの読み書きを含む合計）でした。同じ内容を Qwen に送ったときは約1,200トークンなので、APIを直接呼ぶ場合の費用とは別物です。応答時間も <code>claude -p</code> の起動を含みます。応答時間は回答にかかった時間のみ（採点の時間は含まない）。
        「人の確認が必要」は、キーワード照合とAI審判の判定が食い違った問題の数です。「人が修正」は、AI審判の判定を人が読んで直した問題の数で、直した理由は1問ずつの欄に残しています。審判のままだと文書ありの Qwen が100%、Claude Haiku が96.7%で、順位が逆でした。
      </p>
    </section>

    <section v-if="base && target" class="block">
      <h2>1問ずつ見比べる</h2>
      <div class="pickers">
        <label>
          比べる前
          <select v-model="baseId">
            <option v-for="e in index" :key="e.runId" :value="e.runId">{{ runName(e) }}</option>
          </select>
        </label>
        <span class="arrow" aria-hidden="true">→</span>
        <label>
          比べた後
          <select v-model="targetId">
            <option v-for="e in index.filter((x) => x.runId !== baseId)" :key="e.runId" :value="e.runId">{{ runName(e) }}</option>
          </select>
        </label>
      </div>
      <p class="delta">
        正答率 {{ pct(base.summary.accuracy) }} → <strong>{{ pct(target.summary.accuracy) }}</strong>　／
        よくなった問題 <strong class="up">{{ changeCount.better }}</strong>　悪くなった問題
        <strong :class="changeCount.worse ? 'down' : ''">{{ changeCount.worse }}</strong>
      </p>

      <div class="filters" role="group" aria-label="表示する問題">
        <button :aria-pressed="filter === 'changed'" @click="filter = 'changed'">判定が変わった問題</button>
        <button :aria-pressed="filter === 'bad'" @click="filter = 'bad'">正しくない問題</button>
        <button :aria-pressed="filter === 'review'" @click="filter = 'review'">要確認・人が修正</button>
        <button :aria-pressed="filter === 'all'" @click="filter = 'all'">全30問</button>
      </div>

      <p v-if="!shown.length" class="empty">該当する問題はありません。</p>
      <article v-for="r in shown" :key="r.id" class="item" :class="r.change">
        <header class="item-head">
          <span class="qid">{{ r.id }}</span>
          <h3>{{ r.question }}</h3>
          <span v-if="!r.answerable" class="tag">文書に答えが無い質問</span>
          <span v-if="r.change === 'worse'" class="tag down">悪化</span>
          <span v-if="r.change === 'better'" class="tag up">改善</span>
        </header>
        <p class="expected"><span>期待する答え</span>{{ r.expected }}</p>
        <div class="answers">
          <div v-if="r.before" class="answer">
            <div class="answer-head">
              <span class="who">{{ runName(base) }}</span>
              <span :class="verdictClass(r.before.verdict)">{{ VERDICT_LABEL[r.before.verdict] }}</span>
            </div>
            <p>{{ r.before.answer }}</p>
            <p class="reason">審判: {{ r.before.judgeReason }}<template v-if="r.before.needsReview">（要確認）</template></p>
            <p v-if="r.before.humanNote" class="human">人が修正（AI審判の判定は「{{ VERDICT_LABEL[r.before.judgeVerdict!] }}」）: {{ r.before.humanNote }}</p>
          </div>
          <div class="answer">
            <div class="answer-head">
              <span class="who">{{ runName(target) }}</span>
              <span :class="verdictClass(r.after.verdict)">{{ VERDICT_LABEL[r.after.verdict] }}</span>
            </div>
            <p>{{ r.after.answer }}</p>
            <p class="reason">審判: {{ r.after.judgeReason }}<template v-if="r.after.needsReview">（要確認）</template></p>
            <p v-if="r.after.humanNote" class="human">人が修正（AI審判の判定は「{{ VERDICT_LABEL[r.after.judgeVerdict!] }}」）: {{ r.after.humanNote }}</p>
          </div>
        </div>
      </article>
    </section>

    <section class="block two">
      <div>
        <h2>測ったこと</h2>
        <ul>
          <li>同じ質問に対する正答率と、文書に無いことを言い切る「でっちあげ」の率</li>
          <li>1問あたりの費用と応答時間</li>
          <li>構成を変えたときに、どの問題がよくなり、どれが悪くなったか</li>
        </ul>
      </div>
      <div>
        <h2>測っていないこと</h2>
        <ul>
          <li>30問は小さな見本です。本番の質問の分布を代表しているとは限りません</li>
          <li>採点はAI（Qwen3.6・温度0）が行い、キーワード照合で照らし合わせています。審判の判定は完全ではないため、食い違いは「要確認」として残しています</li>
          <li>審判と回答者が同じQwenの構成は、身びいきが出る可能性があります</li>
        </ul>
      </div>
    </section>

    <section class="block cta">
      <h2>御社のAI機能で測りませんか</h2>
      <p>
        評価用の質問集づくりから、モデルやプロンプトを替えたときの比較、改修のたびに悪化を見つける仕組みまでをお渡しします。データを社外に出さない構成（ローカルLLM）でも実施できます。
      </p>
      <ul class="facts">
        <li>まずは対象を1機能に絞った、固定価格の小さな検証から</li>
        <li>夜間・土日に進め、進み具合は毎週文書でご報告します</li>
      </ul>
      <div class="cta-row">
        <a class="button" :href="`mailto:${CONTACT}?subject=${encodeURIComponent('ハカルについて')}`">{{ CONTACT }} に相談する</a>
        <a class="sub-link" href="https://mukai.works/engineer">作った人の経歴を見る</a>
      </div>
    </section>
  </main>

  <footer class="foot">
    <div class="wrap">
      ハカル — <a href="https://mukai.works">ムカイクリエイトスタジオ（ムカイ）</a> ／ <a href="https://github.com/mukai-work/hakaru">コード（GitHub）</a> ／ 評価エンジンは C#（.NET 10）、この画面は Vue 3 で作っています。画面はあらかじめ計算した結果を表示するだけで、AIは呼び出しません。
    </div>
  </footer>
</template>
