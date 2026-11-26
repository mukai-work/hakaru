// ../results の評価結果を public/data に写す（画面は静的JSONを読むだけで、LLMは呼ばない）
import { cpSync, mkdirSync, rmSync, existsSync } from 'node:fs'
const src = new URL('../../results/', import.meta.url)
const dst = new URL('../public/data/', import.meta.url)
if (!existsSync(new URL('index.json', src))) {
  console.error('results/index.json がありません。先に hakaru index を実行してください')
  process.exit(1)
}
rmSync(dst, { recursive: true, force: true })
mkdirSync(dst, { recursive: true })
cpSync(src, dst, { recursive: true, filter: (p) => !p.endsWith('.log') })
console.log('評価結果を public/data に写しました')
