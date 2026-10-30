const assert = require('assert')
const fs = require('fs')
const os = require('os')
const path = require('path')
const { __test } = require('../src/VniteGameCache.Plugin/index.js')

const residue = { game_id: 'broken', shortcut_path: 'broken.lnk' }
const healthy = { game_id: 'healthy', shortcut_path: 'healthy.lnk' }

assert.deepStrictEqual(
  __test.selectClearTakeoverTargets({ residues: [residue], normal_managed: [healthy] }),
  [residue, healthy]
)
assert.deepStrictEqual(__test.selectClearTakeoverTargets({ residues: [], normal_managed: [healthy] }), [healthy])

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'vgc-command-result-'))
try {
  const resultPath = path.join(root, 'clear-takeover-result.json')
  __test.writeCommandResult(resultPath, { status: 'pending', request_id: 'first' })
  __test.writeCommandResult(resultPath, { status: 'complete', request_id: 'first', success: true })
  assert.deepStrictEqual(JSON.parse(fs.readFileSync(resultPath, 'utf8')),
    { status: 'complete', request_id: 'first', success: true })
  assert.deepStrictEqual(fs.readdirSync(root), ['clear-takeover-result.json'])
} finally {
  fs.rmSync(root, { recursive: true, force: true })
}
