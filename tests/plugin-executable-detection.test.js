const assert = require('assert')
const fs = require('fs')
const os = require('os')
const path = require('path')

const { __test } = require('../src/VniteGameCache.Plugin/index.js')

function normalizedRoot(value) {
  return path.resolve(value).replace(/[\\/]+$/, '').toLowerCase()
}

function withTempGame(files, test) {
  const tempParent = fs.mkdtempSync(path.join(os.tmpdir(), 'vgc-plugin-detect-'))
  const root = path.join(tempParent, 'qqqq')
  fs.mkdirSync(root)
  try {
    for (const file of files) {
      const target = path.join(root, file)
      fs.mkdirSync(path.dirname(target), { recursive: true })
      fs.writeFileSync(target, '')
    }
    test(root)
  } finally {
    fs.rmSync(tempParent, { recursive: true, force: true })
  }
}

withTempGame(['jiboku-claude-3-opus.exe'], (root) => {
  const result = __test.detectExecutableInDirectory(root, 'Unknown Vnite Title')
  assert.strictEqual(result.executable_path, path.join(root, 'jiboku-claude-3-opus.exe'))
  assert.strictEqual(result.reason, undefined)
  assert.strictEqual(result.candidates.length, 1)
})


withTempGame([path.join('悪女の栄冠', 'akujo.exe')], (root) => {
  const result = __test.detectExecutableInDirectory(root, '悪女の栄冠')
  assert.strictEqual(result.executable_path, path.join(root, '悪女の栄冠', 'akujo.exe'))
  assert.strictEqual(result.reason, undefined)
  assert.strictEqual(result.candidates[0].name, path.join('悪女の栄冠', 'akujo.exe'))
})

withTempGame(['hbunny.exe', 'hbunny_GalTransl-14B-V3.exe'], (root) => {
  const result = __test.detectExecutableInDirectory(root, 'もっと！孕ませ！炎のおっぱい異世界')
  assert.strictEqual(result.executable_path, path.join(root, 'hbunny_GalTransl-14B-V3.exe'))
  assert.strictEqual(result.reason, undefined)
})

withTempGame(['The Clinic of Depravity.exe', 'ファイル破損チェックツール.exe'], (root) => {
  const result = __test.detectExecutableInDirectory(root, '淫落カウンセリング')
  assert.strictEqual(result.executable_path, path.join(root, 'The Clinic of Depravity.exe'))
  assert.strictEqual(result.reason, undefined)
  assert.ok(!result.candidates.some((candidate) => candidate.name.includes('破損チェック')))
})

withTempGame(['魔女の護送人.exe', path.join('Tool', 'nw.exe'), path.join('Tool', 'chromedriver.exe')], (root) => {
  const result = __test.detectExecutableInDirectory(root, '魔女的护送人')
  assert.strictEqual(result.executable_path, path.join(root, '魔女の護送人.exe'))
  assert.strictEqual(result.reason, undefined)
  assert.ok(!result.candidates.some((candidate) => candidate.name.startsWith('Tool')))
})

withTempGame(['MAMABOKU.exe', '汉化启动.exe', path.join('Locale.Emulator.2.4.1.0', 'LEProc.exe')], (root) => {
  const result = __test.detectExecutableInDirectory(root, 'MAMABOKU')
  assert.strictEqual(result.executable_path, path.join(root, '汉化启动.exe'))
  assert.strictEqual(result.reason, undefined)
})

withTempGame(['chosen.exe', 'start.exe', 'play.exe'], (root) => {
  const manual = { source_root: root, executable_path: path.join(root, 'chosen.exe') }
  const local = { utils: { rootPath: root, markPath: root }, path: { gamePath: '' }, launcher: { mode: 'file', fileConfig: { path: '' } } }
  const resolved = __test.resolveAdoptLaunchSource(local, [normalizedRoot(root)], 'Ambiguous Manual Game', manual)
  assert.strictEqual(resolved.source_root, root)
  assert.strictEqual(resolved.executable_path, path.join(root, 'chosen.exe'))
  assert.strictEqual(resolved.resolution, 'ManualSqliteExecutableSelection')
})
withTempGame(['start.exe', 'play.exe'], (root) => {
  const result = __test.detectExecutableInDirectory(root, 'Unrelated Title')
  assert.strictEqual(result.executable_path, undefined)
  assert.ok(['low_confidence_executable_candidate', 'ambiguous_executable_candidates'].includes(result.reason))
  assert.strictEqual(result.candidates.length, 2)
})

withTempGame(['setup.exe'], (root) => {
  const result = __test.detectExecutableInDirectory(root, 'Setup Only')
  assert.strictEqual(result.executable_path, undefined)
  assert.strictEqual(result.reason, 'no_safe_executable_candidate')
  assert.deepStrictEqual(result.candidates, [])
})

withTempGame(['Game_CN.exe'], (root) => {
  const local = {
    path: { gamePath: path.join(os.tmpdir(), 'vgc-launchers', 'vnite-rename.lnk') },
    launcher: {
      mode: 'file',
      fileConfig: {
        path: path.join(os.tmpdir(), 'vgc-launchers', 'vnite-rename.lnk'),
        monitorPath: 'VniteGameCache.Wrapper'
      }
    },
    utils: {
      rootPath: root,
      markPath: root
    }
  }
  const resolved = __test.resolveAdoptLaunchSource(local, [normalizedRoot(root)], 'Renamed Game')
  assert.strictEqual(resolved.source_root, root)
  assert.strictEqual(resolved.executable_path, path.join(root, 'Game_CN.exe'))
  assert.strictEqual(resolved.resolution, 'StableRootDirectoryExecutableDetected')

  const game = {
    game_id: 'vnite-rename',
    source_root: root,
    executable_path: resolved.executable_path
  }
  assert.strictEqual(
    __test.isWrapperManagedLocalDocComplete(local, path.join(os.tmpdir(), 'VniteGameCache.Wrapper.exe'), 'vnite-rename', game),
    true
  )
  assert.strictEqual(
    __test.isWrapperManagedLocalDocComplete(
      { ...local, path: { ...local.path, gamePath: '' } },
      path.join(os.tmpdir(), 'VniteGameCache.Wrapper.exe'),
      'vnite-rename',
      game
    ),
    false
  )
  assert.strictEqual(
    __test.isInternalGameSnapshotCurrent({
      source_root: path.join(path.dirname(root), 'old-name'),
      cache_root: path.join(os.tmpdir(), 'cache'),
      executable_path: path.join(path.dirname(root), 'old-name', 'Game_CN.exe')
    }, game),
    false
  )
})

assert.strictEqual(
  __test.sanitizeShortcutFileName('CON...', 'game-id-1', 80),
  'VGC-game-id-1'
)
assert.ok(!__test.sanitizeShortcutFileName('坏<名>: 游戏. ', 'game-id-2', 80).includes('<'))
assert.strictEqual(__test.mapAdoptSkipToConsistencyStatus('ambiguous_executable_candidates'), 'ExecutableConfirmationRequired')

const canonicalGameId = '57137a85-c50a-40d4-951e-d0fa9978be83'
assert.strictEqual(__test.buildLaunchArguments(canonicalGameId.toUpperCase()), `--game-id=${canonicalGameId}`)
assert.strictEqual(__test.parseLaunchArguments(`--game-id=${canonicalGameId}`).game_id, canonicalGameId)
assert.strictEqual(__test.parseLaunchArguments(`--game-id "${canonicalGameId}"`).game_id, canonicalGameId)
assert.strictEqual(__test.parseLaunchArguments(`--game-id '${canonicalGameId}'`).game_id, canonicalGameId)
assert.strictEqual(__test.parseLaunchArguments(`"--game-id" "${canonicalGameId}"`).game_id, canonicalGameId)
assert.strictEqual(__test.parseLaunchArguments(`"E:\\VGC\\dist\\current\\bin\\VniteGameCache.Wrapper.dll" --game-id=${canonicalGameId}`).game_id, canonicalGameId)
const expectedLocal = {
  path: { gamePath: 'C:\\VGC\\launchers\\Game [GameId-12345678] 12345678-1234-1234-1234-123456789abc - Vnite缓存启动.lnk' },
  launcher: {
    mode: 'file',
    fileConfig: { path: 'C:\\VGC\\launchers\\Game [GameId-12345678] 12345678-1234-1234-1234-123456789abc - Vnite缓存启动.lnk', monitorPath: 'VniteGameCache.Wrapper' },
    scriptConfig: { workingDirectory: 'E:\\VGC\\dist\\current\\bin' }
  },
  utils: { rootPath: 'G:\\Game\\Demo', markPath: 'G:\\Game\\Demo' }
}
assert.strictEqual(__test.isGameLocalWriteVerified(structuredClone(expectedLocal), expectedLocal), true)
assert.strictEqual(__test.isGameLocalWriteVerified({ ...expectedLocal, path: { gamePath: '' } }, expectedLocal), false)
assert.throws(() => __test.parseLaunchArguments('--game-id not-a-guid'), /Malformed game ID/)
assert.throws(() => __test.parseLaunchArguments(`--game-id=${canonicalGameId} --vnite-id ${canonicalGameId}`), /Unexpected launch argument/)


