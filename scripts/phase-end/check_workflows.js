// Static checks of the saved Workflow scripts in .claude/workflows/*.js. The Workflow tool runs a script's body
// inside an async function, after its `export const meta = {...}` literal, so a plain `node --check` refuses a
// top-level `return`. This wraps the body the same way, then checks it parses, and checks the rules the tool
// enforces at run time, so a broken script is caught in CI instead of at the end of a phase:
//   - meta is a pure literal with a name and a description;
//   - every phase('<title>') call has a meta.phases entry with the same title, and every entry is started
//     (by phase() or by an agent's { phase: '<title>' } option);
//   - no Date.now(), Math.random() or argless new Date() (they throw: they would break resume).
// Usage: node scripts/phase-end/check_workflows.js [file ...]   (default: every .claude/workflows/*.js)
// Exit 0 when every file passes; 1 otherwise, with one line per problem.
'use strict'
const fs = require('fs')
const path = require('path')
const vm = require('vm')

const root = path.resolve(__dirname, '..', '..')
const dir = path.join(root, '.claude', 'workflows')
const files = process.argv.length > 2
  ? process.argv.slice(2)
  : fs.readdirSync(dir).filter(f => f.endsWith('.js')).sort().map(f => path.join(dir, f))

const BACKSLASH = String.fromCharCode(92)
const NEWLINE = String.fromCharCode(10)

// The span of the object literal that follows `export const meta =`, found by matching braces outside
// string literals and comments, so a nested object or a "}" inside a string cannot end it early.
function metaSpan(src) {
  const decl = /^export const meta\s*=\s*/m.exec(src)
  if (!decl) return null
  let i = decl.index + decl[0].length
  if (src[i] !== '{') return null
  const start = i
  let depth = 0
  for (; i < src.length; i++) {
    const c = src[i]
    if (c === "'" || c === '"' || c === '`') {
      for (i++; i < src.length && src[i] !== c; i++) {
        if (src[i] === BACKSLASH) i++
      }
      continue
    }
    if (c === '/' && src[i + 1] === '/') {
      while (i < src.length && src[i] !== NEWLINE) i++
      continue
    }
    if (c === '/' && src[i + 1] === '*') {
      i = src.indexOf('*/', i + 2)
      if (i < 0) return null
      i++
      continue
    }
    if (c === '{') depth++
    if (c === '}') {
      depth--
      if (depth === 0) return { declStart: decl.index, start, end: i + 1 }
    }
  }
  return null
}

// Why the meta literal is not pure, or null. Evaluating it is not enough: a fresh vm context still has the JS
// builtins, so `Math.max(1, 2)`, `String(3)` and `${1 + 1}` would evaluate. So the source is scanned: outside
// string literals only braces, brackets, colons, commas, numbers, true/false/null and keys may appear, and no
// template literal may interpolate.
function metaImpurity(text) {
  let code = ''
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if (c === "'" || c === '"' || c === '`') {
      let j = i + 1
      for (; j < text.length && text[j] !== c; j++) {
        if (text[j] === BACKSLASH) { j++; continue }
        if (c === '`' && text[j] === '$' && text[j + 1] === '{') return 'a template literal interpolates ${...}'
      }
      code += ' "" '
      i = j
      continue
    }
    if (c === '/' && text[i + 1] === '/') { while (i < text.length && text[i] !== NEWLINE) i++; continue }
    if (c === '/' && text[i + 1] === '*') { i = text.indexOf('*/', i + 2); if (i < 0) return 'unterminated comment'; i++; continue }
    code += c
  }
  const bad = code.match(/[^\s{}\[\]:,0-9.\-A-Za-z_$"]/)
  if (bad) return `"${bad[0]}" outside a string literal (only literals are allowed)`
  for (const m of code.matchAll(/[A-Za-z_$][A-Za-z0-9_$]*/g)) {
    const word = m[0]
    const after = code.slice(m.index + word.length).match(/^\s*(.)/)
    const isKey = after && after[1] === ':'
    if (!isKey && !['true', 'false', 'null'].includes(word)) return `the identifier ${word} is not a literal`
  }
  return null
}

function problemsOf(file) {
  const src = fs.readFileSync(file, 'utf8')
  const out = []
  const span = metaSpan(src)
  if (!span) return ['no `export const meta = {...}` object literal']
  const impure = metaImpurity(src.slice(span.start, span.end))
  if (impure) return [`meta is not a pure literal: ${impure}`]
  let meta
  try {
    // A pure literal: evaluated with no globals in scope, so a variable or a call in it fails here.
    meta = vm.runInNewContext('(' + src.slice(span.start, span.end) + ')', Object.create(null), { timeout: 1000 })
  } catch (e) {
    return [`meta does not evaluate as a literal: ${e.message}`]
  }
  if (!meta || typeof meta.name !== 'string' || !meta.name) out.push('meta.name is missing')
  if (!meta || typeof meta.description !== 'string' || !meta.description) out.push('meta.description is missing')
  const body = src.slice(0, span.declStart) + src.slice(span.end)
  try {
    // The tool's shape: the body inside an async function, with its hooks as parameters.
    new vm.Script('(async function (agent, parallel, pipeline, phase, log, args, budget, workflow) {' + NEWLINE +
                  body + NEWLINE + '})')
  } catch (e) {
    out.push(`the body does not parse: ${e.message}`)
  }
  const titles = (meta && Array.isArray(meta.phases) ? meta.phases : []).map(p => p && p.title)
  // A phase is started by phase('<title>') or by an agent's { phase: '<title>' } option.
  const called = [...body.matchAll(/\bphase\(\s*(['"`])([^'"`]+)\1\s*\)/g)].map(x => x[2])
  const optioned = [...body.matchAll(/\bphase:\s*(['"`])([^'"`]+)\1/g)].map(x => x[2])
  for (const t of called) if (!titles.includes(t)) out.push(`phase('${t}') has no meta.phases entry`)
  for (const t of titles) if (!called.includes(t) && !optioned.includes(t)) out.push(`meta.phases entry '${t}' is never started`)
  if (/\bDate\.now\s*\(|\bMath\.random\s*\(|\bnew Date\s*\(\s*\)/.test(body)) {
    out.push('uses Date.now(), Math.random() or new Date(); pass a timestamp in through args instead')
  }
  return out
}

if (!files.length) {
  console.error('check_workflows: no workflow files found')
  process.exit(1)
}
let bad = 0
for (const f of files) {
  const rel = path.relative(root, f)
  const problems = problemsOf(f)
  if (problems.length) {
    bad++
    for (const p of problems) console.log(`FAIL ${rel}: ${p}`)
  } else {
    console.log(`ok   ${rel}`)
  }
}
console.log(`check_workflows: ${files.length - bad} of ${files.length} workflow file(s) pass`)
process.exit(bad ? 1 : 0)
