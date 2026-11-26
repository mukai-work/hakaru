// 評価エンジン（C#）が書き出す結果JSONの形。Hakaru.Core/Models.cs と揃える。
export type Verdict = 'Correct' | 'Partial' | 'Wrong' | 'Hallucination' | 'Abstained'

export interface RunSummary {
  total: number
  correct: number
  partial: number
  wrong: number
  hallucination: number
  abstained: number
  needsReview: number
  accuracy: number
  hallucinationRate: number
  totalCostUsd: number
  costPerQuestionUsd: number
  avgLatencySeconds: number
}

export interface IndexEntry {
  file: string
  runId: string
  label: string
  model: string
  withContext: boolean
  judgeModel: string
  startedAt: string
  dataset: string
  summary: RunSummary
}

export interface ItemResult {
  id: string
  question: string
  expected: string
  answerable: boolean
  answer: string
  verdict: Verdict
  judgeReason: string
  keywordHit: boolean | null
  needsReview: boolean
  inputTokens: number
  outputTokens: number
  latencySeconds: number
  costUsd: number
  /** 人が直したときだけ入る、AI審判の元の判定 */
  judgeVerdict?: Verdict | null
  humanNote?: string | null
}

export interface RunResult extends Omit<IndexEntry, 'file'> {
  items: ItemResult[]
}

export const VERDICT_LABEL: Record<Verdict, string> = {
  Correct: '正しい',
  Partial: '一部正しい',
  Wrong: '間違い',
  Hallucination: 'でっちあげ',
  Abstained: '答えを控えた',
}

/** 悪化・改善の判定に使う順位（エンジンの Regression.Rank と同じ）。 */
export const VERDICT_RANK: Record<Verdict, number> = {
  Correct: 3,
  Partial: 2,
  Abstained: 1,
  Wrong: 0,
  Hallucination: 0,
}
