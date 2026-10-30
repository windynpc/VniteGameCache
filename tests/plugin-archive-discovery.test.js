const assert = require('assert')
const fs = require('fs')
const os = require('os')
const path = require('path')
const { __test } = require('../src/VniteGameCache.Plugin/index.js')

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'vgc-archive-discovery-'))
try {
  fs.writeFileSync(path.join(root, '游戏.zip'), 'zip')
  fs.writeFileSync(path.join(root, 'second.7z'), '7z')
  fs.writeFileSync(path.join(root, 'sample.part1.rar'), 'rar')
  fs.mkdirSync(path.join(root, 'category'))
  fs.writeFileSync(path.join(root, 'category', 'nested.zip'), 'zip')

  const found = __test.archiveDiscoveryCandidates(root, {}, 20)
  assert.deepStrictEqual(found.map((item) => path.basename(item.archivePath)).sort(), ['second.7z', '游戏.zip'])
  const checkpoint = Object.fromEntries(found.map((item) => [item.archivePath, item.signature]))
  assert.deepStrictEqual(__test.archiveDiscoveryCandidates(root, checkpoint), [])
  assert.strictEqual(__test.isAutomaticArchiveExecutable('Game.exe'), true)
  assert.strictEqual(__test.isAutomaticArchiveExecutable('folder/Setup.exe'), false)
  assert.strictEqual(__test.isAutomaticArchiveExecutable('folder/MTool_Config.exe'), false)
} finally {
  fs.rmSync(root, { recursive: true, force: true })
}
