const { spawn } = require('child_process')
const crypto = require('crypto')
const fs = require('fs')
const os = require('os')
const path = require('path')

const pluginId = 'vnite-game-cache'
const managedLauncherPresetId = 'vgc-managed-entry'
let apiRef = null
let autoAdoptInterval = null
let autoAdoptStartupTimeout = null
let autoAdoptEventTimeout = null
let autoAdoptRunning = false
let autoArchiveInterval = null
let autoArchiveStartupTimeout = null
let autoCleanupInterval = null
let autoCleanupRunning = false
let controlCommandInterval = null
let savePathCommandRunning = false
const eventSubscriptions = []

function normalizeLaunchMode(value) {
  return value === 'TranslationPreferred' || value === 'MTool' ? value : 'Direct'
}

function getMaintenanceModePath() {
  return path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache', 'config', 'maintenance-mode.json')
}

function isMaintenanceModeEnabled() {
  try {
    const maintenancePath = getMaintenanceModePath()
    if (!fs.existsSync(maintenancePath)) return false
    const state = JSON.parse(fs.readFileSync(maintenancePath, 'utf8'))
    return Boolean(state?.enabled)
  } catch {
    return true
  }
}

async function activate(api) {
  apiRef = api
  await log('activate', { plugin_id: pluginId })
  await api.PluginDB.setValue('lastActivationUtc', new Date().toISOString())
  await syncSharedSettings(api)
  await registerToolboxShortcuts(api)
  await registerManagedLauncherPreset(api)
  if (isMaintenanceModeEnabled()) {
    await log('maintenance_mode_active', { reason: 'auto launch-route migration skipped' })
  } else {
    migrateLaunchRoutes({ apply: true })
      .then(async (result) => {
        await api.PluginDB.setValue('lastLaunchRouteAutoApply', result)
        await log('launch_route_auto_apply', { migrated_count: result.migrated_count, candidate_count: result.candidate_count })
      })
      .catch(async (error) => {
        await log('launch_route_auto_apply_failed', { message: error.message })
      })
  }

  api.ipc.handle('vgc:test-launch', async (_event, payload) => {
    const result = await launchWrapper(payload || {})
    await api.PluginDB.setValue('lastLaunchResult', result)
    return result
  })

  api.ipc.handle('vgc:get-last-result', async () => {
    return await api.PluginDB.getValue('lastLaunchResult', null)
  })

  api.ipc.handle('vgc:library-dry-run', async (_event, payload) => {
    const result = await runAgentJson('library-dry-run', await buildLibraryArgs(payload || {}))
    await api.PluginDB.setValue('lastLibraryDryRun', result)
    return result
  })

  api.ipc.handle('vgc:library-import', async (_event, payload) => {
    const args = await buildLibraryArgs(payload || {})
    const result = await runAgentJson('library-import', args)
    const vniteResult = await tryApplyVniteEntries(result)
    const combined = { ...result, vnite_result: vniteResult }
    await api.PluginDB.setValue('lastLibraryImport', combined)
    return combined
  })

  api.ipc.handle('vgc:archive-preview', async (_event, payload) => {
    const archivePath = String(payload?.archive_path || '').trim()
    if (!archivePath) throw new Error('Archive path is required')
    return await runAgentJson('archive-preview', ['--archive-path', archivePath])
  })

  api.ipc.handle('vgc:archive-import', async (_event, payload) => {
    return await runArchiveImport(payload)
  })

  api.ipc.handle('vgc:launch-route-dry-run', async () => {
    const result = await migrateLaunchRoutes({ apply: false })
    await api.PluginDB.setValue('lastLaunchRouteDryRun', result)
    return result
  })

  api.ipc.handle('vgc:launch-route-apply', async () => {
    const result = await migrateLaunchRoutes({ apply: true })
    await api.PluginDB.setValue('lastLaunchRouteApply', result)
    return result
  })

  api.ipc.handle('vgc:launch-route-restore', async () => {
    const result = await restoreLaunchRoutes()
    await api.PluginDB.setValue('lastLaunchRouteRestore', result)
    return result
  })

  api.ipc.handle('vgc:adopt-existing-dry-run', async (_event, payload) => {
    const result = await adoptExistingVniteGames({ apply: false, payload: payload || {} })
    await api.PluginDB.setValue('lastAdoptExistingDryRun', result)
    writeAdoptDiagnostics(result, 'ipc_dry_run')
    return result
  })

  api.ipc.handle('vgc:adopt-existing-apply', async (_event, payload) => {
    const result = await adoptExistingVniteGames({ apply: true, payload: payload || {} })
    await api.PluginDB.setValue('lastAdoptExistingApply', result)
    writeAdoptDiagnostics(result, 'ipc_apply')
    return result
  })

  api.ipc.handle('vgc:takeover-residue-scan', async (_event, payload) => {
    const result = await scanTakeoverResidue(payload || {})
    await api.PluginDB.setValue('lastTakeoverResidueScan', result)
    writeTakeoverResidueDiagnostics(result, 'ipc_scan')
    return result
  })

  api.ipc.handle('vgc:takeover-consistency-scan', async (_event, payload) => {
    const result = await runTakeoverConsistency({ apply: false, payload: payload || {} })
    await api.PluginDB.setValue('lastTakeoverConsistencyScan', result)
    writeTakeoverConsistencyDiagnostics(result, 'ipc_scan')
    return result
  })

  api.ipc.handle('vgc:takeover-consistency-repair', async (_event, payload) => {
    const result = await runTakeoverConsistency({ apply: true, payload: payload || {} })
    await api.PluginDB.setValue('lastTakeoverConsistencyRepair', result)
    writeTakeoverConsistencyDiagnostics(result, 'ipc_repair')
    return result
  })

  api.ipc.handle('vgc:clear-takeover-properties', async (_event, payload) => {
    const result = await clearTakeoverProperties({ apply: true, payload: payload || {} })
    await api.PluginDB.setValue('lastClearTakeoverProperties', result)
    writeTakeoverResidueDiagnostics(result.scan || result, 'ipc_clear')
    return result
  })

  api.ipc.handle('vgc:renderer-diagnostics', async () => {
    const result = await getRendererDiagnostics()
    await api.PluginDB.setValue('lastRendererDiagnostics', result)
    return result
  })

  api.ipc.handle('vgc:vnite-save-capability', async () => {
    const result = await probeVniteSaveCapability()
    await api.PluginDB.setValue('lastVniteSaveCapability', result)
    return result
  })

  api.ipc.handle('vgc:mtool-validate', async (_event, payload) => {
    const mtoolPath = payload?.mtool_path || (await api.PluginDB.getValue('mtoolExecutablePath', ''))
    const result = await runAgentJson('mtool-validate', ['--mtool-path', mtoolPath || ''])
    await api.PluginDB.setValue('lastMToolValidation', result)
    return result
  })

  for (const [channel, command] of Object.entries({
    'vgc:cache-lru-suggest': 'cache-lru-suggest',
    'vgc:cache-lru-apply': 'cache-lru-apply',
    'vgc:cache-quick-refresh': 'cache-quick-refresh',
    'vgc:cache-clean-rebuild': 'cache-clean-rebuild',
    'vgc:cache-clear': 'cache-clear',
    'vgc:quarantine-restore': 'quarantine-restore',
    'vgc:quarantine-clean-expired': 'quarantine-clean-expired',
    'vgc:save-profile-add': 'save-profile-add',
    'vgc:save-profile-list': 'save-profile-list',
    'vgc:save-profile-validate': 'save-profile-validate',
    'vgc:save-backup': 'save-backup',
    'vgc:save-export': 'save-export',
    'vgc:save-import': 'save-import',
    'vgc:save-restore': 'save-restore'
  })) {
    api.ipc.handle(channel, async (_event, payload) => {
      const result = await runAgentJson(command, await buildPhase6Args(command, payload || {}))
      await api.PluginDB.setValue(`last:${channel}`, result)
      return result
    })
  }

  const autoAdoptExisting = await api.PluginDB.getValue('autoAdoptExistingOnActivate', false)
  const hasAdoptRoots = (await getExistingAdoptRoots({})).length > 0
  if (autoAdoptExisting && hasAdoptRoots && !isMaintenanceModeEnabled()) {
    scheduleAutoAdoptExisting()
    registerAutoAdoptEventAccelerators(api)
  } else {
    await log('auto_adopt_existing_disabled', { auto_adopt_existing: autoAdoptExisting, has_adopt_roots: hasAdoptRoots, maintenance_mode: isMaintenanceModeEnabled() })
  }
  scheduleAutoCacheCleanup()
  scheduleControlCommands()
  scheduleAutoArchiveDiscovery()

  const autoRun = await api.PluginDB.getValue('autoRunValidationOnActivate', false)
  if (autoRun) {
    await api.PluginDB.setValue('autoRunValidationOnActivate', false)
    await log('auto_run_disabled_on_activate', { reason: 'daily-use-safety' })
  }

  probeVniteSaveCapability()
    .then(async (result) => {
      await api.PluginDB.setValue('lastVniteSaveCapability', result)
      await log('vnite_save_capability_probe', result)
      writeVniteSavePathDiagnostics(result)
    })
    .catch(async (error) => {
      await log('vnite_save_capability_probe_failed', { error: error.message })
    })
}

async function deactivate(api) {
  if (autoArchiveInterval) {
    clearInterval(autoArchiveInterval)
    autoArchiveInterval = null
  }
  if (autoArchiveStartupTimeout) {
    clearTimeout(autoArchiveStartupTimeout)
    autoArchiveStartupTimeout = null
  }
  if (autoAdoptInterval) {
    clearInterval(autoAdoptInterval)
    autoAdoptInterval = null
  }
  if (autoAdoptStartupTimeout) {
    clearTimeout(autoAdoptStartupTimeout)
    autoAdoptStartupTimeout = null
  }
  if (autoAdoptEventTimeout) {
    clearTimeout(autoAdoptEventTimeout)
    autoAdoptEventTimeout = null
  }
  unregisterAutoAdoptEventAccelerators(api)
  if (autoCleanupInterval) {
    clearInterval(autoCleanupInterval)
    autoCleanupInterval = null
  }
  if (controlCommandInterval) {
    clearInterval(controlCommandInterval)
    controlCommandInterval = null
  }
  if (api && api.ipc && typeof api.ipc.removeHandler === 'function') {
    api.ipc.removeHandler('vgc:test-launch')
    api.ipc.removeHandler('vgc:get-last-result')
    api.ipc.removeHandler('vgc:library-dry-run')
    api.ipc.removeHandler('vgc:library-import')
    api.ipc.removeHandler('vgc:archive-preview')
    api.ipc.removeHandler('vgc:archive-import')
    api.ipc.removeHandler('vgc:launch-route-dry-run')
    api.ipc.removeHandler('vgc:launch-route-apply')
    api.ipc.removeHandler('vgc:launch-route-restore')
    api.ipc.removeHandler('vgc:adopt-existing-dry-run')
    api.ipc.removeHandler('vgc:adopt-existing-apply')
    api.ipc.removeHandler('vgc:takeover-residue-scan')
    api.ipc.removeHandler('vgc:takeover-consistency-scan')
    api.ipc.removeHandler('vgc:takeover-consistency-repair')
    api.ipc.removeHandler('vgc:clear-takeover-properties')
    api.ipc.removeHandler('vgc:renderer-diagnostics')
    api.ipc.removeHandler('vgc:vnite-save-capability')
    api.ipc.removeHandler('vgc:mtool-validate')
    for (const channel of [
      'vgc:cache-lru-suggest',
      'vgc:cache-lru-apply',
      'vgc:cache-quick-refresh',
      'vgc:cache-clean-rebuild',
      'vgc:cache-clear',
      'vgc:quarantine-restore',
      'vgc:quarantine-clean-expired',
      'vgc:save-profile-add',
      'vgc:save-profile-list',
      'vgc:save-profile-validate',
      'vgc:save-backup',
      'vgc:save-export',
      'vgc:save-import',
      'vgc:save-restore'
    ]) {
      api.ipc.removeHandler(channel)
    }
  }
  await log('deactivate', { plugin_id: pluginId })
  apiRef = null
}

async function buildLibraryArgs(payload) {
  await syncSharedSettings(apiRef)
  const libraryRoot = payload.library_root || (await apiRef.PluginDB.getValue('libraryRoot', ''))
  const cacheRoot = payload.cache_root || (await apiRef.PluginDB.getValue('defaultCacheRoot', ''))
  const launchMode = normalizeLaunchMode(payload.launch_mode || (await apiRef.PluginDB.getValue('defaultLaunchMode', 'Direct')))
  const mtoolPath = payload.mtool_path || (await apiRef.PluginDB.getValue('mtoolExecutablePath', ''))
  const scanDepth = payload.scan_depth || (await apiRef.PluginDB.getValue('libraryScanDepth', 3)) || 3
  if (!libraryRoot) throw new Error('NAS Library Root is not configured')
  if (!cacheRoot) throw new Error('Default Cache Root is not configured')

  const args = ['--library-root', libraryRoot, '--cache-root', cacheRoot, '--launch-mode', launchMode, '--scan-depth', String(scanDepth)]
  const dbPath = payload.db_path || process.env.VGC_DB_PATH
  if (dbPath) args.push('--db-path', dbPath)
  if (mtoolPath) args.push('--mtool-path', mtoolPath)
  return args
}

async function runAgentJson(command, args) {
  await syncSharedSettings(apiRef)
  const agentPath = (await apiRef.PluginDB.getValue('agentStubPath', '')) || process.env.VGC_AGENT_PATH
  if (!agentPath) throw new Error('Agent Path is not configured')
  const child = spawn(resolveExecutable(agentPath), buildWrapperArgs(agentPath, [command, ...args]), {
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  })
  const stdout = []
  const stderr = []
  child.stdout.on('data', (data) => stdout.push(data.toString('utf8')))
  child.stderr.on('data', (data) => stderr.push(data.toString('utf8')))
  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject)
    child.on('exit', (code) => resolve(code))
  })
  const output = stdout.join('').trim().split(/\r?\n/).filter(Boolean).at(-1)
  return {
    exit_code: exitCode,
    stdout: stdout.join(''),
    stderr: stderr.join(''),
    result: output ? JSON.parse(output) : null,
    timestamp_utc: new Date().toISOString()
  }
}

async function syncSharedSettings(api) {
  if (!api || !api.PluginDB) return
  const root = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache')
  const configRoot = path.join(root, 'config')
  const settingsPath = path.join(configRoot, 'settings.json')
  const numberValue = async (key, fallback) => {
    const value = Number(await api.PluginDB.getValue(key, fallback))
    return Number.isFinite(value) ? value : fallback
  }
  const boolValue = async (key, fallback) => {
    const value = await api.PluginDB.getValue(key, fallback)
    return typeof value === 'boolean' ? value : fallback
  }
  const stringValue = async (key, fallback = '') => {
    const value = await api.PluginDB.getValue(key, fallback)
    return typeof value === 'string' ? value : fallback
  }
  const minimumFreeSpaceMb = await numberValue('minimumFreeSpaceMb', 10240)
  const cacheMaxTotalMb = await numberValue('cacheMaxTotalMb', 0)
  const settings = {
    schema_version: 1,
    database_path: process.env.VGC_DB_PATH || '',
    default_cache_root: await stringValue('defaultCacheRoot'),
    default_save_backup_root: await stringValue('defaultSaveBackupRoot'),
    minimum_free_space_bytes: mbToBytes(minimumFreeSpaceMb),
    cache_max_total_bytes: mbToBytes(cacheMaxTotalMb),
    quarantine_retention_days: await numberValue('quarantineRetentionDays', 30),
    protect_config_files: await boolValue('protectConfigFiles', false),
    log_root: path.join(root, 'logs'),
    commands_root: path.join(root, 'commands'),
    default_launch_mode: normalizeLaunchMode(await stringValue('defaultLaunchMode', 'Direct')),
    mtool_executable_path: await stringValue('mtoolExecutablePath'),
    updated_utc: new Date().toISOString()
  }
  fs.mkdirSync(configRoot, { recursive: true })
  const tempPath = `${settingsPath}.${process.pid}.tmp`
  fs.writeFileSync(tempPath, JSON.stringify(settings, null, 2), 'utf8')
  fs.renameSync(tempPath, settingsPath)
  await api.PluginDB.setValue('lastSharedSettingsPath', settingsPath)
}

async function buildPhase6Args(command, payload) {
  const args = []
  const dbPath = payload.db_path || process.env.VGC_DB_PATH
  if (dbPath) args.push('--db-path', dbPath)
  const push = (name, value) => {
    if (value !== undefined && value !== null && value !== '') args.push(name, String(value))
  }
  const flag = (name, enabled) => {
    if (enabled) args.push(name)
  }
  push('--game-id', payload.game_id)
  push('--profile-id', payload.profile_id || payload.save_profile_id)
  push('--backup-id', payload.backup_id)
  push('--quarantine-id', payload.quarantine_id)
  push('--display-name', payload.display_name)
  push('--source-type', payload.source_type)
  push('--path-template', payload.path_template)
  push('--backup-root', payload.backup_root)
  push('--output', payload.output)
  push('--input', payload.input)
  push('--reason', payload.reason)
  push('--max-items', payload.max_items)
  push('--quarantine-retention-days', payload.quarantine_retention_days)
  const configuredCacheMaxMb = await apiRef.PluginDB.getValue('cacheMaxTotalMb', 0)
  const configuredMinimumFreeMb = await apiRef.PluginDB.getValue('minimumFreeSpaceMb', 10240)
  const maxTotalBytes = payload.max_total_cache_bytes ?? mbToBytes(payload.max_total_cache_mb ?? configuredCacheMaxMb)
  const minimumFreeBytes = payload.minimum_free_space_bytes ?? mbToBytes(payload.minimum_free_space_mb ?? configuredMinimumFreeMb)
  push('--max-total-cache-bytes', maxTotalBytes)
  push('--minimum-free-space-bytes', minimumFreeBytes)
  flag('--primary', payload.primary || payload.is_primary)
  flag('--backup-saves', payload.backup_saves)
  flag('--keep-user-data', payload.keep_user_data)
  flag('--force', payload.force)
  return args
}

async function registerToolboxShortcuts(api) {
  const enabled = await api.PluginDB.getValue('registerToolboxShortcuts', true)
  if (!enabled || !api.ConfigDB || typeof api.ConfigDB.getConfigLocalValue !== 'function') {
    return
  }

  const explorerPath = path.join(process.env.SystemRoot || 'C:\\Windows', 'explorer.exe')
  const controlPanelPath = await resolveControlPanelPath(api)
  const cacheRoot = await api.PluginDB.getValue('defaultCacheRoot', '')
  const logRoot = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache', 'logs')
  const projectRoot = path.resolve(__dirname, '..', '..', '..')
  const shortcuts = {
    'vgc-control-panel': controlPanelPath ? createExecutableTool('VGC 控制台', controlPanelPath) : null,
    'vgc-adopt-panel': controlPanelPath ? createExecutableTool('VGC 批量接管', controlPanelPath, '--adopt') : null,
    'vgc-cleanup-panel': controlPanelPath ? createExecutableTool('VGC 清理建议', controlPanelPath, '--cleanup') : null,
    'vgc-cache-root': cacheRoot ? createExplorerTool('VGC 缓存目录', explorerPath, cacheRoot) : null,
    'vgc-log-root': createExplorerTool('VGC 日志目录', explorerPath, logRoot),
    'vgc-project-root': fs.existsSync(projectRoot) ? createExplorerTool('VGC 项目目录', explorerPath, projectRoot) : null
  }

  const existingTools = (await api.ConfigDB.getConfigLocalValue('toolbox.tools')) || {}
  const nextTools = { ...existingTools }
  for (const [toolId, tool] of Object.entries(shortcuts)) {
    if (!tool) continue
    const existing = nextTools[toolId]
    nextTools[toolId] = existing?.path || existing?.args || existing?.workingDirectory
      ? existing
      : tool
  }
  await api.ConfigDB.setConfigLocalValue('toolbox.tools', nextTools)
  await api.ConfigDB.setConfigValue('appearances.sidebar.showToolbox', true)
  await api.PluginDB.setValue('lastToolboxShortcutRegistration', {
    registered_ids: Object.keys(shortcuts).filter((key) => shortcuts[key]),
    timestamp_utc: new Date().toISOString()
  })
}

async function registerManagedLauncherPreset(api) {
  const configDb = api.ConfigDB
  if (!configDb || typeof configDb.getConfigLocalValue !== 'function' || typeof configDb.setConfigLocalValue !== 'function') return

  try {
    const presets = await configDb.getConfigLocalValue('game.launcher.presets')
    if (!Array.isArray(presets)) return
    if (presets.some((preset) => preset?.id === managedLauncherPresetId)) {
      await log('managed_launcher_preset_present', { preset_id: managedLauncherPresetId })
      return
    }

    await configDb.setConfigLocalValue('game.launcher.presets', [...presets, {
      id: managedLauncherPresetId,
      name: 'VGC 接管入口（仅已接管游戏）',
      mode: 'file',
      path: '${vnite.gamePath}',
      monitorMode: 'process',
      monitorPath: 'VniteGameCache.Wrapper'
    }])
    const savedPresets = await configDb.getConfigLocalValue('game.launcher.presets')
    if (!Array.isArray(savedPresets) || !savedPresets.some((preset) => preset?.id === managedLauncherPresetId)) {
      throw new Error('Vnite launcher preset readback failed')
    }
    await log('managed_launcher_preset_registered', { preset_id: managedLauncherPresetId })
  } catch (error) {
    await log('managed_launcher_preset_registration_failed', { message: error.message })
  }
}

function createExplorerTool(name, explorerPath, targetPath) {
  return {
    name,
    path: explorerPath,
    args: quoteToolboxArg(targetPath),
    workingDirectory: path.dirname(explorerPath)
  }
}

function createExecutableTool(name, executablePath, args = '') {
  return {
    name,
    path: executablePath,
    args,
    workingDirectory: path.dirname(executablePath)
  }
}

async function resolveControlPanelPath(api) {
  const configured = await api.PluginDB.getValue('controlPanelPath', '')
  if (configured && fs.existsSync(configured)) {
    return configured
  }

  const agentPath = await api.PluginDB.getValue('agentStubPath', '')
  if (agentPath) {
    const sibling = path.join(path.dirname(agentPath), 'VniteGameCache.ControlPanel.exe')
    if (fs.existsSync(sibling)) {
      return sibling
    }
  }

  const distCandidate = path.resolve(__dirname, '..', '..', 'bin', 'VniteGameCache.ControlPanel.exe')
  if (fs.existsSync(distCandidate)) {
    return distCandidate
  }

  return ''
}

function quoteToolboxArg(value) {
  return `"${String(value).replace(/"/g, '\\"')}"`
}

function scheduleAutoAdoptExisting() {
  if (autoAdoptInterval || autoAdoptStartupTimeout) {
    return
  }

  autoAdoptStartupTimeout = setTimeout(() => {
    autoAdoptStartupTimeout = null
    runAutoAdoptExisting('startup_reconcile_15s')
  }, 15_000)
  autoAdoptInterval = setInterval(() => {
    runAutoAdoptExisting('periodic_reconcile_15m')
  }, 15 * 60_000)
}

function scheduleAutoArchiveDiscovery() {
  if (autoArchiveInterval || autoArchiveStartupTimeout) return
  autoArchiveStartupTimeout = setTimeout(() => {
    autoArchiveStartupTimeout = null
    runAutoArchiveDiscovery().catch((error) => log('archive_discovery_failed', { error: error.message }))
  }, 30_000)
  autoArchiveInterval = setInterval(() => {
    runAutoArchiveDiscovery().catch((error) => log('archive_discovery_failed', { error: error.message }))
  }, 15 * 60_000)
}

function archiveDiscoveryCandidates(root, checkpoint, limit = 20) {
  const entries = fs.readdirSync(root, { withFileTypes: true })
  const candidates = []
  for (const entry of entries) {
    if (!entry.isFile() || !/\.(zip|7z|rar)$/i.test(entry.name) || /\.part\d+\.rar$/i.test(entry.name)) continue
    const archivePath = path.join(root, entry.name)
    const stat = fs.statSync(archivePath)
    const signature = `${stat.size}:${stat.mtimeMs}`
    if (checkpoint[archivePath] === signature) continue
    candidates.push({ archivePath, signature })
  }
  return candidates.sort((a, b) => a.archivePath.localeCompare(b.archivePath)).slice(0, limit)
}

function isAutomaticArchiveExecutable(candidate) {
  const name = path.basename(String(candidate || '').replace(/\\/g, '/'))
  return /\.exe$/i.test(name) && !/(?:^|[._ -])(setup|install|uninstall|update|config|launcher|卸载|安装|配置)(?:[._ -]|$)/i.test(name)
}

async function runAutoArchiveDiscovery() {
  if (autoAdoptRunning || !apiRef || isMaintenanceModeEnabled() ||
      !(await apiRef.PluginDB.getValue('autoDiscoverArchives', false))) return
  const root = String(await apiRef.PluginDB.getValue('libraryRoot', '') || '').trim()
  if (!root || !fs.existsSync(root)) return
  const cacheRoot = String(await apiRef.PluginDB.getValue('defaultCacheRoot', '') || '').trim()
  if (!cacheRoot) return

  autoAdoptRunning = true
  try {
    const checkpoint = await apiRef.PluginDB.getValue('archiveDiscoveryCheckpoint', {}) || {}
    const found = archiveDiscoveryCandidates(root, checkpoint)
    const results = []
    for (const candidate of found) {
      const existingId = await apiRef.PluginDB.getValue(`vniteGameId:${candidate.archivePath}`, '')
      if (existingId) {
        if (checkpoint[candidate.archivePath] && checkpoint[candidate.archivePath] !== candidate.signature) {
          results.push({ archive_path: candidate.archivePath, status: 'source_changed_needs_review' })
        }
        checkpoint[candidate.archivePath] = candidate.signature
        continue
      }
      const preview = await runAgentJson('archive-preview', ['--archive-path', candidate.archivePath])
      const candidates = preview.result?.executable_candidates || []
      if (preview.exit_code !== 0 || !preview.result?.supported || candidates.length !== 1 ||
          !isAutomaticArchiveExecutable(candidates[0])) {
        results.push({ archive_path: candidate.archivePath, status: 'needs_confirmation', executable_candidates: candidates })
        checkpoint[candidate.archivePath] = candidate.signature
        continue
      }
      const imported = await runArchiveImport({
        archive_path: candidate.archivePath,
        executable_relative_path: candidates[0],
        cache_root: cacheRoot,
        display_name: path.parse(candidate.archivePath).name
      })
      const applied = imported.exit_code === 0 && imported.vnite_result?.applied_count === 1
      results.push({ archive_path: candidate.archivePath, status: applied ? 'imported' : 'failed', error: imported.result?.error || imported.vnite_result?.reason || '' })
      if (applied) checkpoint[candidate.archivePath] = candidate.signature
    }
    await apiRef.PluginDB.setValue('archiveDiscoveryCheckpoint', checkpoint)
    if (results.length) {
      const previous = await apiRef.PluginDB.getValue('lastArchiveDiscovery', null)
      const retained = new Map((previous?.root === root ? previous.results || [] : [])
        .map((item) => [item.archive_path, item]))
      for (const item of results) retained.set(item.archive_path, item)
      const report = { root, results: [...retained.values()].slice(-200), timestamp_utc: new Date().toISOString() }
      await apiRef.PluginDB.setValue('lastArchiveDiscovery', report)
      fs.mkdirSync(getCommandsRoot(), { recursive: true })
      fs.writeFileSync(path.join(getCommandsRoot(), 'archive-discovery-result.json'), JSON.stringify(report, null, 2), 'utf8')
      await log('archive_discovery_finished', { root, results })
    }
  } finally {
    autoAdoptRunning = false
  }
}

function registerAutoAdoptEventAccelerators(api) {
  const eventBus = api?.eventBus
  if (!eventBus || typeof eventBus.on !== 'function') {
    log('auto_adopt_event_accelerator_unavailable', { reason: 'eventBus.on is unavailable' })
    return
  }

  const onGameAdded = (...args) => {
    const meta = args[1] || args[0] || {}
    if (meta?.source === pluginId || meta?.dataSource === pluginId) {
      return
    }
    scheduleAutoAdoptFromEvent('event_game_added')
  }

  const onGameLaunched = (...args) => {
    const meta = args[1] || args[0] || {}
    if (meta?.source === pluginId || meta?.dataSource === pluginId) {
      return
    }
    scheduleAutoAdoptFromEvent('event_game_launched')
  }

  eventBus.on('game:added', onGameAdded)
  eventBus.on('game:launched', onGameLaunched)
  eventSubscriptions.push(['game:added', onGameAdded])
  eventSubscriptions.push(['game:launched', onGameLaunched])
  log('auto_adopt_event_accelerator_registered', { events: ['game:added', 'game:launched'] })
}

function unregisterAutoAdoptEventAccelerators(api) {
  const eventBus = api?.eventBus
  while (eventSubscriptions.length > 0) {
    const [eventName, handler] = eventSubscriptions.pop()
    try {
      if (eventBus && typeof eventBus.off === 'function') {
        eventBus.off(eventName, handler)
      } else if (eventBus && typeof eventBus.removeListener === 'function') {
        eventBus.removeListener(eventName, handler)
      }
    } catch (error) {
      log('auto_adopt_event_accelerator_unsubscribe_failed', { event_name: eventName, error: error.message })
    }
  }
}

function scheduleAutoAdoptFromEvent(reason) {
  if (autoAdoptEventTimeout || autoAdoptRunning || !apiRef) {
    return
  }

  autoAdoptEventTimeout = setTimeout(() => {
    autoAdoptEventTimeout = null
    runAutoAdoptExisting(reason)
  }, 5_000)
}

async function runAutoAdoptExisting(reason) {
  if (autoAdoptRunning || !apiRef) {
    return
  }

  autoAdoptRunning = true
  try {
    if (isMaintenanceModeEnabled()) {
      await log('auto_adopt_existing_skipped', { reason, maintenance_mode: true })
      return
    }

    const enabled = await apiRef.PluginDB.getValue('autoAdoptExistingOnActivate', false)
    if (!enabled) {
      await log('auto_adopt_existing_skipped', { reason, auto_adopt_existing: false })
      return
    }

    const roots = await getExistingAdoptRoots({})
    if (roots.length === 0) {
      await log('auto_adopt_existing_skipped', { reason, auto_adopt_existing: true, root_count: 0 })
      return
    }

    const configuredBatchSize = Number(await apiRef.PluginDB.getValue('autoAdoptBatchSize', 25))
    const batchSize = Number.isFinite(configuredBatchSize)
      ? Math.min(100, Math.max(1, Math.trunc(configuredBatchSize)))
      : 25
    const result = await adoptExistingVniteGames({
      apply: true,
      payload: { max_count: Math.max(1, batchSize), roots }
    })
    await apiRef.PluginDB.setValue('lastAdoptExistingApply', { ...result, reason })
    writeAdoptDiagnostics(result, reason)
    await log('auto_adopt_existing_success', {
      reason,
      adopted_count: result.adopted_count,
      candidate_count: result.candidate_count,
      remaining_count: result.remaining_count,
      skipped_count: result.skipped?.length || 0
    })
  } catch (error) {
    writeAdoptDiagnostics({
      status: 'error',
      error: error.message,
      adopted_count: 0,
      candidate_count: 0,
      remaining_count: 0,
      candidates: [],
      skipped: []
    }, `${reason}_failed`)
    await log('auto_adopt_existing_failed', { reason, error: error.message })
  } finally {
    autoAdoptRunning = false
  }
}

function scheduleAutoCacheCleanup() {
  if (autoCleanupInterval) {
    return
  }

  setTimeout(() => {
    runAutoCacheCleanup('startup_delayed_120s')
  }, 120_000)
  autoCleanupInterval = setInterval(() => {
    runAutoCacheCleanup('periodic')
  }, 15 * 60_000)
}

async function runAutoCacheCleanup(reason) {
  if (autoCleanupRunning || !apiRef) {
    return
  }

  const enabled = await apiRef.PluginDB.getValue('autoCleanupOnThreshold', false)
  const cacheMaxTotalMb = Number(await apiRef.PluginDB.getValue('cacheMaxTotalMb', 0))
  const minimumFreeSpaceMb = Number(await apiRef.PluginDB.getValue('minimumFreeSpaceMb', 10240))
  if (!enabled ||
      ((!Number.isFinite(cacheMaxTotalMb) || cacheMaxTotalMb <= 0) &&
       (!Number.isFinite(minimumFreeSpaceMb) || minimumFreeSpaceMb <= 0))) {
    return
  }

  autoCleanupRunning = true
  try {
    const maxItems = Math.max(1, Number(await apiRef.PluginDB.getValue('autoCleanupMaxItemsPerRun', 2)) || 2)
    const apply = await runAgentJson('cache-lru-apply', await buildPhase6Args('cache-lru-apply', {
      max_items: maxItems,
      reason
    }))
    const result = apply.result?.result || {}
    const summary = {
      reason,
      enabled,
      cache_max_total_mb: cacheMaxTotalMb,
      minimum_free_space_mb: minimumFreeSpaceMb,
      status: result.status || apply.exit_code,
      candidate_count: Array.isArray(result.candidates) ? result.candidates.length : 0,
      cleared_count: Array.isArray(result.deleted) ? result.deleted.length : 0,
      failed_count: Array.isArray(result.skipped_or_failed) ? result.skipped_or_failed.length : 0,
      permanently_deleted_mb: Math.round((Number(result.permanently_deleted_bytes) || 0) / 1024 / 1024),
      actual_free_space_increase_mb: Math.round((Number(result.actual_free_space_increase_bytes) || 0) / 1024 / 1024),
      protected_user_data_mb: Math.round((Number(result.protected_user_data_bytes) || 0) / 1024 / 1024),
      deleted: result.deleted || [],
      skipped_or_failed: result.skipped_or_failed || [],
      timestamp_utc: new Date().toISOString()
    }
    await apiRef.PluginDB.setValue('lastAutoCacheCleanup', summary)
    await log('auto_cache_cleanup_finished', summary)
  } catch (error) {
    const summary = { reason, error: error.message, timestamp_utc: new Date().toISOString() }
    await apiRef.PluginDB.setValue('lastAutoCacheCleanup', summary)
    await log('auto_cache_cleanup_failed', summary)
  } finally {
    autoCleanupRunning = false
  }
}

function scheduleControlCommands() {
  if (controlCommandInterval) {
    return
  }

  controlCommandInterval = setInterval(() => {
    runControlCommandRequest().catch(async (error) => {
      await log('control_command_failed', { error: error.message })
    })
    runSavePathCommandRequest().catch(async (error) => {
      await log('save_path_command_failed', { error: error.message })
    })
  }, 10_000)
}

async function runControlCommandRequest() {
  if (autoAdoptRunning || !apiRef) {
    return
  }

  const archiveRequestPath = path.join(getCommandsRoot(), 'archive-import-request.json')
  if (fs.existsSync(archiveRequestPath)) {
    const resultPath = path.join(getCommandsRoot(), 'archive-import-result.json')
    let request
    try {
      request = JSON.parse(fs.readFileSync(archiveRequestPath, 'utf8'))
      fs.rmSync(archiveRequestPath, { force: true })
      autoAdoptRunning = true
      const result = await runArchiveImport(request)
      writeCommandResult(resultPath, { ...result, request_id: request.request_id, status: 'complete' })
      await log('archive_import_finished', { exit_code: result.exit_code, applied_count: result.vnite_result?.applied_count || 0 })
    } catch (error) {
      writeCommandResult(resultPath, { request_id: request?.request_id, status: 'error', success: false, error: error.message })
      await log('archive_import_failed', { error: error.message })
    } finally {
      autoAdoptRunning = false
    }
    return
  }

  const consistencyRequestPath = path.join(getCommandsRoot(), 'takeover-consistency-request.json')
  if (fs.existsSync(consistencyRequestPath)) {
    let request = {}
    try {
      request = JSON.parse(fs.readFileSync(consistencyRequestPath, 'utf8'))
    } catch (error) {
      fs.rmSync(consistencyRequestPath, { force: true })
      await log('control_panel_takeover_consistency_request_invalid', { path: consistencyRequestPath, error: error.message })
      return
    }

    fs.rmSync(consistencyRequestPath, { force: true })
    autoAdoptRunning = true
    try {
      const apply = request.apply === true && request.dry_run !== true
      const result = await runTakeoverConsistency({ apply, payload: request })
      await apiRef.PluginDB.setValue(apply ? 'lastTakeoverConsistencyRepair' : 'lastTakeoverConsistencyScan', { ...result, reason: 'control_panel_request' })
      writeTakeoverConsistencyDiagnostics(result, 'control_panel_request')
      await log('control_panel_takeover_consistency_success', {
        apply,
        status: result.status,
        scanned_count: result.stats?.scanned || 0,
        adopted_count: result.stats?.adopted_success || 0,
        failed_count: result.stats?.real_failures || 0
      })
    } catch (error) {
      const failure = {
        status: 'error',
        error: error.message,
        timestamp_utc: new Date().toISOString(),
        stats: createTakeoverStats(),
        rows: []
      }
      writeTakeoverConsistencyDiagnostics(failure, 'control_panel_request_failed')
      await log('control_panel_takeover_consistency_failed', { error: error.message })
    } finally {
      autoAdoptRunning = false
    }
    return
  }

  const clearRequestPath = path.join(getCommandsRoot(), 'clear-takeover-request.json')
  if (fs.existsSync(clearRequestPath)) {
    const clearResultPath = path.join(getCommandsRoot(), 'clear-takeover-result.json')
    let request = {}
    try {
      request = JSON.parse(fs.readFileSync(clearRequestPath, 'utf8'))
    } catch (error) {
      fs.rmSync(clearRequestPath, { force: true })
      await log('control_panel_clear_takeover_request_invalid', { path: clearRequestPath, error: error.message })
      return
    }

    fs.rmSync(clearRequestPath, { force: true })
    autoAdoptRunning = true
    try {
      const apply = request.apply !== false && request.dry_run !== true
      const result = apply
        ? await clearTakeoverProperties({ apply: true, payload: request })
        : { scan: await scanTakeoverResidue(request) }
      await apiRef.PluginDB.setValue('lastClearTakeoverProperties', { ...result, reason: 'control_panel_request' })
      writeTakeoverResidueDiagnostics(result.scan || result, 'control_panel_request')
      writeCommandResult(clearResultPath, {
        request_id: request.request_id,
        requested_utc: request.requested_utc,
        success: (result.failed_count || 0) === 0,
        cleared_count: result.cleared_count || 0,
        failed_count: result.failed_count || 0,
        residue_count: result.scan?.residue_count || result.residue_count || 0,
        error: result.failed?.[0]?.error || null
      })
      await log('control_panel_clear_takeover_success', {
        apply,
        cleared_count: result.cleared_count || 0,
        residue_count: result.scan?.residue_count || result.residue_count || 0,
        readopted_count: result.readopt?.adopted_count || 0
      })
    } catch (error) {
      const failure = {
        status: 'error',
        error: error.message,
        timestamp_utc: new Date().toISOString(),
        residues: []
      }
      writeTakeoverResidueDiagnostics(failure, 'control_panel_request_failed')
      writeCommandResult(clearResultPath, { request_id: request.request_id, requested_utc: request.requested_utc, success: false, error: error.message })
      await log('control_panel_clear_takeover_failed', { error: error.message })
    } finally {
      autoAdoptRunning = false
    }
    return
  }

  const requestPath = path.join(getCommandsRoot(), 'adopt-existing-request.json')
  if (!fs.existsSync(requestPath)) {
    return
  }

  let request = {}
  try {
    request = JSON.parse(fs.readFileSync(requestPath, 'utf8'))
  } catch (error) {
    fs.rmSync(requestPath, { force: true })
    await log('control_panel_adopt_existing_request_invalid', { path: requestPath, error: error.message })
    return
  }

  fs.rmSync(requestPath, { force: true })
  autoAdoptRunning = true
  try {
    const maxCount = Number(request.max_count || request.maxCount || 50)
    const result = await adoptExistingVniteGames({
      apply: true,
      payload: { ...request, max_count: Math.max(1, Number.isFinite(maxCount) ? maxCount : 50) }
    })
    await apiRef.PluginDB.setValue('lastAdoptExistingApply', { ...result, reason: 'control_panel_request' })
    writeAdoptDiagnostics(result, 'control_panel_request')
    await log('control_panel_adopt_existing_success', {
      adopted_count: result.adopted_count,
      candidate_count: result.candidate_count,
      remaining_count: result.remaining_count,
      skipped_count: result.skipped?.length || 0
    })
  } catch (error) {
    writeAdoptDiagnostics({
      status: 'error',
      error: error.message,
      adopted_count: 0,
      candidate_count: 0,
      remaining_count: 0,
      candidates: [],
      skipped: []
    }, 'control_panel_request_failed')
    await log('control_panel_adopt_existing_failed', { error: error.message })
  } finally {
    autoAdoptRunning = false
  }
}

async function runSavePathCommandRequest() {
  if (savePathCommandRunning || !apiRef) {
    return
  }

  const requestPath = path.join(getCommandsRoot(), 'set-save-path-request.json')
  if (!fs.existsSync(requestPath)) {
    return
  }

  let request = {}
  try {
    request = JSON.parse(fs.readFileSync(requestPath, 'utf8'))
  } catch (error) {
    fs.rmSync(requestPath, { force: true })
    await log('save_path_command_request_invalid', { path: requestPath, error: error.message })
    return
  }

  fs.rmSync(requestPath, { force: true })
  savePathCommandRunning = true
  try {
    const result = await appendVniteSavePath(request)
    await apiRef.PluginDB.setValue('lastSavePathAppend', result)
    const capability = await probeVniteSaveCapability()
    await apiRef.PluginDB.setValue('lastVniteSaveCapability', capability)
    writeVniteSavePathDiagnostics(capability)
    await log('control_panel_save_path_append_success', result)
  } catch (error) {
    await apiRef.PluginDB.setValue('lastSavePathAppend', {
      success: false,
      error: error.message,
      timestamp_utc: new Date().toISOString()
    })
    await log('control_panel_save_path_append_failed', { error: error.message })
  } finally {
    savePathCommandRunning = false
  }
}

function writeAdoptDiagnostics(result, reason) {
  const diagnosticsDir = path.join(getLocalAppDataRoot(), 'diagnostics')
  fs.mkdirSync(diagnosticsDir, { recursive: true })
  const adoptedCount = result.adopted_count || 0
  const candidateCount = result.candidate_count || 0
  const remainingCount = result.remaining_count || 0
  const skipped = result.skipped || []
  const status = result.status || deriveAdoptDiagnosticStatus(result, adoptedCount, candidateCount, skipped)
  const document = {
    status,
    reason,
    message: result.reason || result.message || '',
    error: result.error || '',
    timestamp_utc: new Date().toISOString(),
    adopted_count: adoptedCount,
    candidate_count: candidateCount,
    remaining_count: remainingCount,
    roots: result.roots || [],
    candidates: result.candidates || [],
    skipped
  }
  const latestPath = path.join(diagnosticsDir, 'adopt-existing-latest.json')
  const actionablePath = path.join(diagnosticsDir, 'adopt-existing-actionable.json')
  fs.writeFileSync(latestPath, JSON.stringify(document, null, 2), 'utf8')
  if (status === 'needs_action' || status === 'error' || status === 'unavailable') {
    fs.writeFileSync(actionablePath, JSON.stringify(document, null, 2), 'utf8')
  } else {
    fs.rmSync(actionablePath, { force: true })
  }
}

function writeTakeoverResidueDiagnostics(result, reason) {
  const diagnosticsDir = path.join(getLocalAppDataRoot(), 'diagnostics')
  fs.mkdirSync(diagnosticsDir, { recursive: true })
  const document = {
    status: result.status || ((result.residue_count || 0) > 0 ? 'needs_action' : 'ok'),
    reason,
    message: result.message || '',
    error: result.error || '',
    timestamp_utc: new Date().toISOString(),
    residue_count: result.residue_count || 0,
    scanned_count: result.scanned_count || 0,
    normal_managed_count: result.normal_managed_count || 0,
    residues: result.residues || [],
    normal_managed: result.normal_managed || [],
    skipped: result.skipped || []
  }
  const latestPath = path.join(diagnosticsDir, 'takeover-residue-latest.json')
  const actionablePath = path.join(diagnosticsDir, 'takeover-residue-actionable.json')
  fs.writeFileSync(latestPath, JSON.stringify(document, null, 2), 'utf8')
  if (document.status === 'needs_action' || document.status === 'error') {
    fs.writeFileSync(actionablePath, JSON.stringify(document, null, 2), 'utf8')
  } else {
    fs.rmSync(actionablePath, { force: true })
  }
}

function writeTakeoverConsistencyDiagnostics(result, reason) {
  const diagnosticsDir = path.join(getLocalAppDataRoot(), 'diagnostics')
  fs.mkdirSync(diagnosticsDir, { recursive: true })
  const document = {
    status: result.status || 'ok',
    reason,
    message: result.message || '',
    error: result.error || '',
    timestamp_utc: new Date().toISOString(),
    environment: result.environment || {},
    stats: result.stats || createTakeoverStats(),
    rows: result.rows || [],
    adopt: result.adopt || null,
    residue: result.residue || null
  }
  const latestPath = path.join(diagnosticsDir, 'takeover-consistency-latest.json')
  const actionablePath = path.join(diagnosticsDir, 'takeover-consistency-actionable.json')
  fs.writeFileSync(latestPath, JSON.stringify(document, null, 2), 'utf8')
  if (document.status === 'needs_action' || document.status === 'error') {
    fs.writeFileSync(actionablePath, JSON.stringify(document, null, 2), 'utf8')
  } else {
    fs.rmSync(actionablePath, { force: true })
  }
}

function deriveAdoptDiagnosticStatus(result, adoptedCount, candidateCount, skipped) {
  if (result.error) return 'error'
  if (result.reason === 'GameDB is unavailable') return 'unavailable'
  if (result.reason === 'No adopt root is configured') return 'needs_action'
  if (candidateCount > adoptedCount || skipped.length > 0) return 'needs_action'
  return 'ok'
}

function writeVniteSavePathDiagnostics(result) {
  const diagnosticsDir = path.join(getLocalAppDataRoot(), 'diagnostics')
  fs.mkdirSync(diagnosticsDir, { recursive: true })
  fs.writeFileSync(path.join(diagnosticsDir, 'vnite-save-paths-latest.json'), JSON.stringify(result, null, 2), 'utf8')
}

function getLocalAppDataRoot() {
  return path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache')
}

function getCommandsRoot() {
  try {
    const settingsPath = path.join(getLocalAppDataRoot(), 'config', 'settings.json')
    if (fs.existsSync(settingsPath)) {
      const settings = JSON.parse(fs.readFileSync(settingsPath, 'utf8'))
      if (typeof settings.commands_root === 'string' && settings.commands_root.trim()) {
        return settings.commands_root
      }
    }
  } catch {
  }

  return path.join(getLocalAppDataRoot(), 'commands')
}

function writeCommandResult(resultPath, result) {
  const tempPath = `${resultPath}.${process.pid}.${crypto.randomUUID()}.tmp`
  try {
    fs.writeFileSync(tempPath, JSON.stringify(result, null, 2), 'utf8')
    fs.renameSync(tempPath, resultPath)
  } finally {
    fs.rmSync(tempPath, { force: true })
  }
}

function mbToBytes(value) {
  const number = Number(value)
  if (!Number.isFinite(number) || number <= 0) return 0
  return Math.floor(number * 1024 * 1024)
}

async function runArchiveImport(payload) {
  const archivePath = String(payload?.archive_path || '').trim()
  const executablePath = String(payload?.executable_relative_path || '').trim()
  const cacheRoot = String(payload?.cache_root || (await apiRef.PluginDB.getValue('defaultCacheRoot', ''))).trim()
  if (!archivePath || !executablePath || !cacheRoot) throw new Error('Archive, executable and cache root are required')
  const guard = validateAdoptRoot(archivePath, await getExistingAdoptRoots({}))
  if (!guard.allowed) throw new Error(`Archive is outside configured game roots: ${guard.reason}`)
  const args = ['--archive-path', archivePath, '--executable-relative-path', executablePath, '--cache-root', cacheRoot]
  if (payload?.display_name) args.push('--display-name', String(payload.display_name))
  const result = await runAgentJson('archive-import', args)
  if (result.exit_code !== 0) return result
  return { ...result, vnite_result: await tryApplyVniteEntries(result) }
}

async function tryApplyVniteEntries(importResult) {
  const gameDb = apiRef?.GameDB
  const imported = importResult?.result?.imported_games || []
  if (!gameDb) {
    return { mode: 'pending_manifest', reason: 'GameDB is not available in this plugin context', applied_count: 0 }
  }

  const adapter = createVniteGameLibraryAdapter(gameDb)
  if (!adapter.available) {
    return {
      mode: 'pending_manifest',
      reason: adapter.reason,
      available_methods: adapter.availableMethods,
      applied_count: 0
    }
  }

  let applied = 0
  for (const game of imported) {
    await adapter.upsertGame(game)
    applied++
  }
  return { mode: 'real_game_db_set_game', applied_count: applied }
}

function createVniteGameLibraryAdapter(gameDb) {
  const required = ['getAllGames', 'setGame', 'setGameLocal']
  const availableMethods = Object.getOwnPropertyNames(gameDb).filter((key) => typeof gameDb[key] === 'function')
  const missing = required.filter((name) => typeof gameDb[name] !== 'function')
  if (missing.length > 0) {
    return {
      available: false,
      reason: `Required real GameDB methods are missing: ${missing.join(', ')}`,
      availableMethods,
      upsertGame: async () => {
        throw new Error('GameDB adapter is unavailable')
      }
    }
  }

  return {
    available: true,
    reason: '',
    availableMethods,
    upsertGame: async (game) => {
      const wrapperPath = await getWrapperLauncherPath()
      await validateWrapperLauncherPath(wrapperPath)
      const roots = await getExistingAdoptRoots({})
      const guard = validateAdoptRoot(game.archive_path || game.source_root, roots)
      if (!guard.allowed) {
        throw new Error(`Refusing to import Vnite entry outside configured roots: ${guard.reason}`)
      }
      const allGames = await gameDb.getAllGames()
      const existingGame = allGames[game.game_id] || null
      if (game.archive_path && existingGame && !isManagedGame(existingGame)) {
        throw new Error(`Archive Game ID already belongs to an unmanaged Vnite entry: ${game.game_id}`)
      }
      let gameDoc = existingGame
      if (!existingGame) {
        gameDoc = createVniteGameDoc(game)
        await gameDb.setGame(game.game_id, gameDoc)
      }
      const existingLocal = await safeGetGameLocal(gameDb, game.game_id)
      await setGameLocalWithRollback(
        gameDb,
        game.game_id,
        existingLocal || createSourceGameLocalDoc(game),
        await createWrapperManagedGameLocalDoc(game, wrapperPath),
        'import'
      )
      emitGameAdded(game.game_id, gameDoc)
      await apiRef.PluginDB.setValue(`vniteGameId:${game.archive_path || game.source_root}`, game.game_id)
      await apiRef.PluginDB.setValue(`internalGameId:${game.game_id}`, {
        source_root: game.source_root,
        archive_path: game.archive_path || '',
        cache_root: game.cache_root,
        executable_path: game.executable_path,
        launch_mode: game.launch_mode,
        updated_utc: new Date().toISOString()
      })
    }
  }
}

async function getRendererDiagnostics() {
  const gameDb = apiRef?.GameDB
  const configDb = apiRef?.ConfigDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function') {
    return { available: false, reason: 'GameDB is unavailable' }
  }

  const allGames = await gameDb.getAllGames()
  const games = []
  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    if (!isManagedGame(gameDoc)) continue
    const local = await safeGetGameLocal(gameDb, gameId)
    games.push({
      game_id: gameId,
      name: gameDoc?.metadata?.name || '',
      original_name: gameDoc?.metadata?.originalName || '',
      sort_name: gameDoc?.metadata?.sortName || '',
      add_date: gameDoc?.record?.addDate || '',
      nsfw: gameDoc?.apperance?.nsfw ?? false,
      hide_from_recent_games: gameDoc?.record?.hideFromRecentGames ?? false,
      platforms: gameDoc?.metadata?.platforms || [],
      genres: gameDoc?.metadata?.genres || [],
      game_path: local?.path?.gamePath || '',
      launcher_mode: local?.launcher?.mode || '',
      root_path: local?.utils?.rootPath || '',
      route_status: classifyLaunchRoute(local, local?.launcher?.fileConfig?.path || '', gameId)
    })
  }

  return {
    available: true,
    managed_count: games.length,
    games,
    config: await getVisibilityConfig(configDb),
    timestamp_utc: new Date().toISOString()
  }
}

async function probeVniteSaveCapability() {
  const gameDb = apiRef?.GameDB
  const ipc = apiRef?.ipc
  const result = {
    available: false,
    can_read_save_paths: false,
    can_call_native_backup_ipc: false,
    native_backup_safe_to_use: false,
    reason: '',
    ipc_methods: ipc ? Object.getOwnPropertyNames(Object.getPrototypeOf(ipc)).filter((key) => key !== 'constructor') : [],
    game_db_methods: gameDb ? Object.getOwnPropertyNames(gameDb).filter((key) => typeof gameDb[key] === 'function') : [],
    sampled_games: [],
    timestamp_utc: new Date().toISOString()
  }

  if (!gameDb || typeof gameDb.getAllGames !== 'function') {
    result.reason = 'GameDB is unavailable'
    return result
  }

  result.available = true
  result.can_call_native_backup_ipc = typeof ipc?.invoke === 'function'
  result.native_backup_safe_to_use = result.can_call_native_backup_ipc
  if (!result.can_call_native_backup_ipc) {
    result.reason = 'Plugin API exposes main-process ipc handle/send, but not renderer-style invoke; game:backup-save cannot be safely called from the plugin.'
  }

  const allGames = await gameDb.getAllGames()
  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    const savePaths = await readGameLocalValue(gameDb, gameId, 'path.savePaths')
    const saveList = await readGameValue(gameDb, gameId, 'save.saveList')
    result.sampled_games.push({
      game_id: gameId,
      name: gameDoc?.metadata?.name || gameId,
      managed_by_vgc: isManagedGame(gameDoc),
      save_paths_readable: Array.isArray(savePaths),
      save_paths_count: Array.isArray(savePaths) ? savePaths.filter(Boolean).length : 0,
      save_paths: Array.isArray(savePaths) ? savePaths.filter(Boolean) : [],
      save_list_readable: saveList && typeof saveList === 'object',
      save_count: saveList && typeof saveList === 'object' ? Object.keys(saveList).length : 0
    })
  }

  result.can_read_save_paths = result.sampled_games.some((game) => game.save_paths_readable)
  return result
}

async function readGameLocalValue(gameDb, gameId, key) {
  if (typeof gameDb.getGameLocalValue === 'function') {
    return await gameDb.getGameLocalValue(gameId, key)
  }
  const local = await safeGetGameLocal(gameDb, gameId)
  return getValueByPath(local, key)
}

async function appendVniteSavePath(request) {
  if (request?.command && request.command !== 'append-vnite-save-path') {
    throw new Error(`Unsupported save path command: ${request.command}`)
  }

  const gameId = String(request?.game_id || request?.gameId || '').trim()
  const savePath = normalizeUserPath(request?.save_path || request?.savePath || '')
  if (!gameId) throw new Error('Missing game_id')
  if (!savePath) throw new Error('Missing save_path')

  const gameDb = apiRef?.GameDB
  if (!gameDb) throw new Error('GameDB is unavailable')

  const local = await safeGetGameLocal(gameDb, gameId)
  if (!local) throw new Error(`Game local config not found: ${gameId}`)

  const existingRaw = await readGameLocalValue(gameDb, gameId, 'path.savePaths')
  const existing = Array.isArray(existingRaw) ? existingRaw.filter(Boolean).map((item) => String(item)) : []
  const exists = existing.some((item) => pathEquals(item, savePath))
  const next = exists ? existing : [...existing, savePath]

  await apiRef.PluginDB.setValue(`savePathBackup:${gameId}`, {
    game_id: gameId,
    original_save_paths: existing,
    requested_save_path: savePath,
    timestamp_utc: new Date().toISOString()
  })

  if (!exists) {
    await writeGameLocalValue(gameDb, gameId, 'path.savePaths', next, local)
  }

  return {
    success: true,
    game_id: gameId,
    appended: !exists,
    save_path: savePath,
    save_paths: next,
    timestamp_utc: new Date().toISOString()
  }
}

async function writeGameLocalValue(gameDb, gameId, key, value, existingLocal) {
  if (typeof gameDb.setGameLocalValue === 'function') {
    await gameDb.setGameLocalValue(gameId, key, value)
    return
  }
  if (typeof gameDb.setGameLocal !== 'function') {
    throw new Error('GameDB cannot write game local values')
  }
  const nextLocal = structuredCloneSafe(existingLocal || {})
  setValueByPath(nextLocal, key, value)
  await gameDb.setGameLocal(gameId, nextLocal)
}

async function readGameValue(gameDb, gameId, key) {
  if (typeof gameDb.getGameValue === 'function') {
    return await gameDb.getGameValue(gameId, key)
  }
  if (typeof gameDb.getGame !== 'function') return undefined
  const doc = await gameDb.getGame(gameId)
  return getValueByPath(doc, key)
}

function getValueByPath(value, key) {
  return String(key || '')
    .split('.')
    .filter(Boolean)
    .reduce((current, part) => current?.[part], value)
}

function setValueByPath(target, key, value) {
  const parts = String(key || '').split('.').filter(Boolean)
  if (parts.length === 0) return
  let current = target
  for (const part of parts.slice(0, -1)) {
    if (!current[part] || typeof current[part] !== 'object') {
      current[part] = {}
    }
    current = current[part]
  }
  current[parts[parts.length - 1]] = value
}

function structuredCloneSafe(value) {
  if (typeof structuredClone === 'function') return structuredClone(value)
  return JSON.parse(JSON.stringify(value || {}))
}

function normalizeUserPath(value) {
  if (!value) return ''
  try {
    return path.resolve(String(value)).replace(/[\\/]+$/, '')
  } catch {
    return String(value).trim().replace(/[\\/]+$/, '')
  }
}

async function getVisibilityConfig(configDb) {
  if (!configDb || typeof configDb.getConfigValue !== 'function') {
    return { available: false }
  }
  return {
    available: true,
    selected_group: await configDb.getConfigValue('game.gameList.selectedGroup'),
    show_all_games_in_group: await configDb.getConfigValue('game.gameList.showAllGamesInGroup'),
    local_game_filter_mode: await configDb.getConfigValue('appearances.localGameFilterMode'),
    nsfw_filter_mode: await configDb.getConfigValue('appearances.nsfwFilterMode'),
    custom_visibility_filter_enabled: await configDb.getConfigValue('appearances.customVisibilityFilter.enabled'),
    excluded_path_prefixes: await configDb.getConfigValue('appearances.customVisibilityFilter.excludedPathPrefixes'),
    excluded_game_names: await configDb.getConfigValue('appearances.customVisibilityFilter.excludedGameNames')
  }
}

function emitGameAdded(gameId, gameDoc) {
  if (!apiRef?.eventBus || typeof apiRef.eventBus.emit !== 'function') return
  apiRef.eventBus.emit(
    'game:added',
    {
      gameId,
      name: gameDoc?.metadata?.name || gameId,
      dataSource: pluginId,
      metadata: gameDoc?.metadata || {}
    },
    { source: pluginId }
  )
}

function createVniteGameDoc(game) {
  const title = game.display_name || path.basename(game.source_root)
  return {
    _id: game.game_id,
    metadata: {
      name: title,
      originalName: '',
      sortName: title,
      releaseDate: '',
      description: '',
      developers: [],
      publishers: [],
      platforms: [],
      genres: [],
      tags: [],
      relatedSites: [],
      steamId: '',
      vndbId: '',
      igdbId: '',
      ymgalId: '',
      extra: [
        { key: 'managed_by_vnite_game_cache', value: ['true'] },
        { key: 'vgc_internal_game_id', value: [game.game_id] }
      ]
    },
    record: {
      addDate: new Date().toISOString(),
      lastRunDate: '',
      score: -1,
      playTime: 0,
      playStatus: 'unplayed',
      hideFromRecentGames: false,
      timers: [],
      dailyPlayTimes: [],
      storageSize: -1
    },
    save: {
      saveList: {},
      maxBackups: 7,
      autoRestoreSave: false
    },
    memory: {
      memoryList: {}
    },
    apperance: {
      logo: {
        position: { x: 1.5, y: 35 },
        size: 100,
        visible: true
      },
      nsfw: false
    }
  }
}

function createSourceGameLocalDoc(game) {
  const gameRoot = game.archive_path ? game.cache_root : game.source_root
  return {
    _id: game.game_id,
    path: {
      gamePath: game.executable_path,
      savePaths: [],
      screenshotPath: ''
    },
    launcher: {
      mode: 'file',
      fileConfig: {
        path: game.executable_path,
        args: [],
        monitorMode: 'file',
        monitorPath: game.executable_path
      },
      urlConfig: {
        url: '',
        browserPath: '',
        monitorMode: 'process',
        monitorPath: ''
      },
      scriptConfig: {
        workingDirectory: gameRoot,
        command: [],
        monitorMode: 'file',
        monitorPath: game.executable_path
      },
      useMagpie: false
    },
    utils: {
      markPath: gameRoot,
      rootPath: gameRoot
    }
  }
}

async function createWrapperManagedGameLocalDoc(game, wrapperPath, existingLocal = null) {
  const wrapperDirectory = path.dirname(wrapperPath)
  const monitorProcessName = getWrapperProcessName(wrapperPath)
  const shortcutPath = await ensureWrapperShortcut(game, wrapperPath)
  const nextLocal = structuredCloneSafe(existingLocal || createSourceGameLocalDoc(game))
  nextLocal._id = game.game_id
  nextLocal.path = {
    ...(nextLocal.path || {}),
    gamePath: shortcutPath,
    savePaths: Array.isArray(nextLocal.path?.savePaths) ? nextLocal.path.savePaths : [],
    screenshotPath: nextLocal.path?.screenshotPath || ''
  }
  nextLocal.launcher = {
    ...(nextLocal.launcher || {}),
    mode: 'file',
    fileConfig: {
      ...(nextLocal.launcher?.fileConfig || {}),
      path: shortcutPath,
      args: [],
      monitorMode: 'process',
      monitorPath: monitorProcessName
    },
    urlConfig: {
      url: '',
      browserPath: '',
      monitorMode: 'process',
      monitorPath: '',
      ...(nextLocal.launcher?.urlConfig || {})
    },
    scriptConfig: {
      ...(nextLocal.launcher?.scriptConfig || {}),
      workingDirectory: wrapperDirectory,
      command: [],
      monitorMode: 'process',
      monitorPath: monitorProcessName
    },
    useMagpie: Boolean(nextLocal.launcher?.useMagpie)
  }
  nextLocal.utils = {
    ...(nextLocal.utils || {}),
    markPath: game.archive_path ? game.cache_root : game.source_root,
    rootPath: game.archive_path ? game.cache_root : game.source_root
  }
  return nextLocal
}

async function migrateLaunchRoutes({ apply }) {
  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function' || typeof gameDb.setGameLocal !== 'function') {
    return { mode: apply ? 'apply' : 'dry_run', migrated_count: 0, candidates: [], reason: 'GameDB is unavailable' }
  }

  let wrapperPath = ''
  try {
    wrapperPath = await getWrapperLauncherPath()
  } catch (error) {
    await log('takeover_residue_wrapper_path_unavailable', { error: error?.message || String(error) })
  }
  await validateWrapperLauncherPath(wrapperPath)
  const roots = await getExistingAdoptRoots({})
  const allGames = await gameDb.getAllGames()
  const candidates = []
  const skipped = []
  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    const local = await safeGetGameLocal(gameDb, gameId)
    const status = classifyLaunchRoute(local, wrapperPath, gameId)
    if (!isManagedGame(gameDoc) && status !== 'WrapperManaged') continue

    const internal = await apiRef.PluginDB.getValue(`internalGameId:${gameId}`, null)
    const backup = await apiRef.PluginDB.getValue(launchBackupKey(gameId), null)
    const backupLocal = backup?.local_doc || null
    const executablePath =
      internal?.executable_path ||
      backup?.original_executable_path ||
      getLocalExecutablePath(backupLocal)
    if (!executablePath) continue

    const game = {
      game_id: gameId,
      display_name: gameDoc?.metadata?.name || gameId,
      source_root:
        internal?.source_root ||
        getLocalSourceRoot(backupLocal, executablePath),
      archive_path: internal?.archive_path || '',
      cache_root: internal?.cache_root || '',
      executable_path: executablePath,
      launch_mode: internal?.launch_mode || 'Direct'
    }
    const guard = validateAdoptRoot(game.source_root, roots)
    if (!guard.allowed) {
      skipped.push({
        game_id: gameId,
        source_root: game.source_root,
        reason: guard.reason
      })
      continue
    }
    const expectedMonitorPath = getWrapperProcessName(wrapperPath)
    const currentMonitorPath = getLauncherMonitorPath(local)
    const monitorNeedsMigration = status === 'WrapperManaged' &&
      expectedMonitorPath &&
      !pathEquals(currentMonitorPath, expectedMonitorPath)
    if (status === 'WrapperManaged' && !monitorNeedsMigration) continue
    const migrationStatus = monitorNeedsMigration ? 'WrapperManagedLegacyMonitor' : status
    candidates.push({
      game_id: gameId,
      status: migrationStatus,
      current_path: local?.launcher?.fileConfig?.path || local?.path?.gamePath || '',
      target_path: wrapperPath,
      current_monitor_path: currentMonitorPath,
      target_monitor_path: expectedMonitorPath
    })
    if (apply) {
      await setGameLocalWithRollback(
        gameDb,
        gameId,
        local || createSourceGameLocalDoc(game),
        await createWrapperManagedGameLocalDoc(game, wrapperPath, local),
        'migration'
      )
    }
  }

  return {
    mode: apply ? 'apply' : 'dry_run',
    migrated_count: apply ? candidates.length : 0,
    candidate_count: candidates.length,
    candidates,
    skipped,
    timestamp_utc: new Date().toISOString()
  }
}

async function adoptExistingVniteGames({ apply, payload }) {
  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function' || typeof gameDb.setGameLocal !== 'function') {
    return { mode: apply ? 'apply' : 'dry_run', adopted_count: 0, candidates: [], reason: 'GameDB is unavailable' }
  }

  const wrapperPath = await getWrapperLauncherPath()
  await validateWrapperLauncherPath(wrapperPath)
  const cacheRoot = payload.cache_root || (await apiRef.PluginDB.getValue('defaultCacheRoot', ''))
  if (!cacheRoot) throw new Error('Default Cache Root is not configured')
  const roots = await getExistingAdoptRoots(payload)
  if (roots.length === 0) {
    return { mode: apply ? 'apply' : 'dry_run', adopted_count: 0, candidates: [], reason: 'No adopt root is configured' }
  }

  const allGames = await gameDb.getAllGames()
  const candidates = []
  const skipped = []
  const maxCount = Number(payload.max_count || payload.maxCount || 0)
  const selectedIds = new Set((payload.selected_game_ids || payload.game_ids || [])
    .map((value) => String(value || '').toLowerCase())
    .filter(Boolean))
  const sqlite = await readSqliteGameIdentityMap()
  let applied = 0
  const failed = []
  const stats = createTakeoverStats()
  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    if (selectedIds.size > 0 && !selectedIds.has(String(gameId).toLowerCase())) continue
    stats.scanned++
    const local = await safeGetGameLocal(gameDb, gameId)
    const internal = await apiRef.PluginDB.getValue(`internalGameId:${gameId}`, null)
    if (internal?.archive_path) continue
    const sqliteRecord = sqlite.byGameId.get(gameId.toLowerCase()) || sqlite.byVniteGameId.get(gameId.toLowerCase()) || null
    const takeoverEvidence = findVgcTakeoverEvidence(local, wrapperPath, gameId)
    if (takeoverEvidence.has_takeover) {
      const shortcut = await inspectVgcShortcut(takeoverEvidence.shortcut_path, wrapperPath, gameId, sqlite)
      if (!sqliteRecord || !internal || !shortcut.valid) {
        stats.takeover_residue++
        skipped.push({
          game_id: gameId,
          name: gameDoc?.metadata?.name || gameId,
          source_root: getLocalDirectoryRoot(local),
          reason: 'takeover_residue',
          candidates: takeoverEvidence.evidence.map((item) => ({ name: `${item.field}: ${item.value}` }))
        })
        continue
      }
    }

    const resolved = resolveAdoptLaunchSource(local, roots, gameDoc?.metadata?.name || gameId, sqliteRecord)
    if (!resolved.executable_path) {
      incrementTakeoverSkipStat(stats, resolved.reason)
      if (resolved.source_root || resolved.reason !== 'missing_source_root') {
        skipped.push({
          game_id: gameId,
          name: gameDoc?.metadata?.name || gameId,
          source_root: resolved.source_root || '',
          reason: resolved.reason,
          candidates: resolved.candidates || []
        })
      }
      continue
    }

    const executablePath = resolved.executable_path
    const sourceRoot = resolved.source_root
    const game = {
      game_id: gameId,
      display_name: gameDoc?.metadata?.name || path.basename(sourceRoot),
      source_root: sourceRoot,
      cache_root: internal?.cache_root || buildCacheRoot(cacheRoot, path.basename(sourceRoot), gameId),
      executable_path: executablePath,
      launch_mode: 'Direct'
    }
    const status = classifyLaunchRoute(local, wrapperPath, gameId)
    const wrapperComplete = isWrapperManagedLocalDocComplete(local, wrapperPath, gameId, game)
    const internalCurrent = isInternalGameSnapshotCurrent(internal, game)
    if (status === 'WrapperManaged' && wrapperComplete && internalCurrent) continue

    candidates.push({
      game_id: gameId,
      name: gameDoc?.metadata?.name || gameId,
      source_root: game.source_root,
      executable_path: game.executable_path,
      cache_root: game.cache_root,
      status: wrapperComplete && !internalCurrent ? `${status}:SourceChanged` : wrapperComplete ? status : `${status}:NeedsRewrite`,
      current_path: executablePath,
      original_launcher_mode: local?.launcher?.mode || '',
      resolution: resolved.resolution
    })

    if (apply && (!Number.isFinite(maxCount) || maxCount <= 0 || applied < maxCount)) {
      try {
        const upsertArgs = [
          '--game-id', game.game_id,
          '--source-root', game.source_root,
          '--cache-root', game.cache_root,
          '--executable-path', game.executable_path,
          '--display-name', gameDoc?.metadata?.name || path.basename(sourceRoot)
        ]
        const originalScriptJson = getOriginalScriptJson(local)
        if (originalScriptJson) {
          upsertArgs.push('--original-script-json', originalScriptJson)
        }
        const upsert = requireAgentSuccess(await runAgentJson('game-upsert', upsertArgs), 'game-upsert').result || {}
        const persistedGame = {
          ...game,
          internal_record_id: upsert.game_id || game.game_id,
          cache_root: upsert.cache_root || game.cache_root,
          source_root: upsert.source_root || game.source_root,
          executable_path: upsert.executable_path || game.executable_path
        }
        await setGameLocalWithRollback(
          gameDb,
          gameId,
          local || createSourceGameLocalDoc(game),
          await createWrapperManagedGameLocalDoc(game, wrapperPath, local),
          'adopt_existing'
        )
        await apiRef.PluginDB.setValue(`internalGameId:${gameId}`, {
          internal_record_id: persistedGame.internal_record_id,
          source_root: persistedGame.source_root,
          cache_root: persistedGame.cache_root,
          executable_path: persistedGame.executable_path,
          launch_mode: game.launch_mode,
          adopted_existing_vnite_game: true,
          updated_utc: new Date().toISOString()
        })
        await apiRef.PluginDB.setValue(`vniteGameId:${game.source_root}`, gameId)
        applied++
        stats.adopted_success++
      } catch (error) {
        const failure = {
          game_id: gameId,
          name: gameDoc?.metadata?.name || gameId,
          source_root: game.source_root,
          executable_path: game.executable_path,
          reason: classifyAdoptApplyFailure(error),
          error: error.message
        }
        failed.push(failure)
        stats.real_failures++
        if (failure.reason === 'shortcut_creation_failed') stats.shortcut_creation_failed++
        await log('adopt_existing_game_failed', failure)
      }
    }
  }

  return {
    mode: apply ? 'apply' : 'dry_run',
    adopted_count: apply ? applied : 0,
    candidate_count: candidates.length,
    remaining_count: apply ? Math.max(0, candidates.length - applied) : candidates.length,
    roots,
    candidates,
    skipped,
    failed,
    stats,
    timestamp_utc: new Date().toISOString()
  }
}

function createTakeoverStats() {
  return {
    scanned: 0,
    adopted_success: 0,
    shortcut_creation_failed: 0,
    executable_confirmation_required: 0,
    root_missing: 0,
    takeover_residue: 0,
    ignored: 0,
    suspended: 0,
    real_failures: 0,
    launch_routes_checked: 0,
    launch_routes_resolved: 0,
    launch_routes_unresolved: 0,
    legacy_arguments_detected: 0,
    malformed_arguments: 0,
    migrated_shortcuts: 0,
    migration_failures: 0
  }
}

function incrementTakeoverSkipStat(stats, reason) {
  if (reason === 'missing_source_root' || reason === 'outside_adopt_roots') stats.root_missing++
  else if (reason === 'ambiguous_executable_candidates' || reason === 'low_confidence_executable_candidate') stats.executable_confirmation_required++
  else if (reason === 'ignored') stats.ignored++
  else if (reason === 'suspended') stats.suspended++
}

function classifyAdoptApplyFailure(error) {
  const message = error?.message || ''
  if (message.includes('Failed to create wrapper shortcut')) return 'shortcut_creation_failed'
  if (message.includes('rollback')) return 'rollback_failed'
  if (message.includes('game-upsert')) return 'sqlite_upsert_failed'
  return 'apply_failed'
}

async function restoreLaunchRoutes() {
  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function' || typeof gameDb.setGameLocal !== 'function') {
    return { restored_count: 0, restored: [], reason: 'GameDB is unavailable' }
  }

  const allGames = await gameDb.getAllGames()
  const restored = []
  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    if (!isManagedGame(gameDoc)) continue
    const backup = await apiRef.PluginDB.getValue(launchBackupKey(gameId), null)
    if (!backup?.local_doc) continue
    await gameDb.setGameLocal(gameId, backup.local_doc)
    restored.push({ game_id: gameId, restored_from: backup.reason || 'backup' })
  }

  return { restored_count: restored.length, restored, timestamp_utc: new Date().toISOString() }
}

async function scanTakeoverResidue(payload = {}) {
  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function') {
    return { status: 'unavailable', reason: 'GameDB is unavailable', scanned_count: 0, residue_count: 0, residues: [] }
  }

  const wrapperPath = await getWrapperLauncherPath()
  const sqlite = await readSqliteGameIdentityMap()
  const allGames = await gameDb.getAllGames()
  const selectedIds = new Set((payload.selected_game_ids || payload.game_ids || [])
    .map((value) => String(value || '').toLowerCase())
    .filter(Boolean))
  const residues = []
  const normalManaged = []
  const skipped = []
  let scanned = 0

  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    if (selectedIds.size > 0 && !selectedIds.has(String(gameId).toLowerCase())) continue
    scanned++
    const local = await safeGetGameLocal(gameDb, gameId)
    const internal = await apiRef.PluginDB.getValue(`internalGameId:${gameId}`, null)
    const sqliteRecord = sqlite.byGameId.get(gameId.toLowerCase()) || sqlite.byVniteGameId.get(gameId.toLowerCase()) || null
    const evidence = findVgcTakeoverEvidence(local, wrapperPath, gameId)
    const shortcut = await inspectVgcShortcut(evidence.shortcut_path, wrapperPath, gameId, sqlite)
    const localDocComplete = Boolean(sqliteRecord) &&
      isWrapperManagedLocalDocComplete(local, wrapperPath, gameId, sqliteRecord)
    const normal = evidence.has_takeover &&
      Boolean(sqliteRecord) &&
      Boolean(internal) &&
      shortcut.valid &&
      localDocComplete

    if (normal) {
      normalManaged.push({
        game_id: gameId,
        name: gameDoc?.metadata?.name || gameId,
        shortcut_path: evidence.shortcut_path,
        sqlite_game_id: sqliteRecord.game_id
      })
      continue
    }

    if (!evidence.has_takeover) {
      skipped.push({
        game_id: gameId,
        name: gameDoc?.metadata?.name || gameId,
        status: 'unmanaged_or_clean'
      })
      continue
    }

    residues.push({
      game_id: gameId,
      name: gameDoc?.metadata?.name || gameId,
      source_root: getLocalDirectoryRoot(local),
      root_path: local?.utils?.rootPath || '',
      mark_path: local?.utils?.markPath || '',
      game_path: local?.path?.gamePath || '',
      launcher_mode: local?.launcher?.mode || '',
      launcher_path: local?.launcher?.fileConfig?.path || '',
      monitor_path: getLauncherMonitorPath(local),
      shortcut_path: evidence.shortcut_path,
      sqlite_exists: Boolean(sqliteRecord),
      plugin_db_exists: Boolean(internal),
      local_doc_complete: localDocComplete,
      shortcut_exists: shortcut.exists,
      shortcut_valid: shortcut.valid,
      shortcut_status: shortcut.status,
      shortcut_parsed_game_id: shortcut.parsed_game_id,
      shortcut_resolved_game_id: shortcut.resolved_game_id,
      evidence: evidence.evidence,
      classification: Boolean(sqliteRecord) || Boolean(internal) ? 'HighProbabilityTakeoverResidue' : 'ConfirmedTakeoverResidue'
    })
  }

  return {
    status: residues.length > 0 ? 'needs_action' : 'ok',
    scanned_count: scanned,
    residue_count: residues.length,
    normal_managed_count: normalManaged.length,
    sqlite_available: sqlite.available,
    sqlite_error: sqlite.error,
    residues,
    normal_managed: normalManaged,
    skipped,
    timestamp_utc: new Date().toISOString()
  }
}

async function runTakeoverConsistency({ apply, payload }) {
  const environment = await buildTakeoverEnvironmentSnapshot()
  const shortcutMigration = await migrateShortcutArgumentContract({ apply, payload })
  const residue = await scanTakeoverResidue(payload)
  const adopt = await adoptExistingVniteGames({ apply, payload })
  const stats = mergeTakeoverStats(adopt.stats, residue, shortcutMigration)
  const rows = []

  for (const item of residue.residues || []) {
    const status = !item.shortcut_valid
      ? (item.shortcut_status || 'ShortcutBroken')
      : !item.sqlite_exists || !item.plugin_db_exists
        ? 'MappingMissing'
        : !item.local_doc_complete
          ? 'LaunchFieldsIncomplete'
          : 'TakeoverResidue'
    const issue = !item.shortcut_valid
      ? `Wrapper 快捷方式不可用：${item.shortcut_status || 'ShortcutBroken'}`
      : !item.sqlite_exists || !item.plugin_db_exists
        ? 'Vnite 启动字段残留但 SQLite/PluginDB 映射不完整'
        : !item.local_doc_complete
          ? 'Vnite 启动字段不完整，需要回写游戏路径与 Wrapper 启动字段'
          : 'Vnite 启动字段存在接管残留'
    rows.push({
      game_id: item.game_id,
      name: item.name,
      status,
      issue,
      source_root: item.source_root,
      shortcut_path: item.shortcut_path
    })
  }

  for (const item of adopt.skipped || []) {
    rows.push({
      game_id: item.game_id,
      name: item.name,
      status: mapAdoptSkipToConsistencyStatus(item.reason),
      issue: item.reason,
      source_root: item.source_root,
      candidates: item.candidates || []
    })
  }

  for (const item of adopt.failed || []) {
    rows.push({
      game_id: item.game_id,
      name: item.name,
      status: item.reason === 'shortcut_creation_failed' ? 'ShortcutBroken' : 'EnvironmentMismatch',
      issue: item.error,
      source_root: item.source_root,
      executable_path: item.executable_path
    })
  }

  for (const item of adopt.candidates || []) {
    if (apply && !(adopt.failed || []).some((failed) => failed.game_id === item.game_id)) continue
    rows.push({
      game_id: item.game_id,
      name: item.name,
      status: 'RebindRequired',
      issue: '需要写入或重写 Wrapper 启动链路',
      source_root: item.source_root,
      executable_path: item.executable_path
    })
  }

  if (!environment.ok) {
    rows.unshift({
      game_id: '',
      name: 'VGC 环境',
      status: 'EnvironmentMismatch',
      issue: environment.issues.join('; '),
      source_root: '',
      executable_path: environment.wrapper_path || ''
    })
  }

  const status = !environment.ok || rows.some((row) => row.status !== 'HealthyTakeover')
    ? 'needs_action'
    : 'ok'
  return {
    mode: apply ? 'repair' : 'scan',
    status,
    environment,
    stats,
    rows,
    shortcut_migration: shortcutMigration,
    adopt,
    residue,
    timestamp_utc: new Date().toISOString()
  }
}

async function migrateShortcutArgumentContract({ apply, payload }) {
  const result = {
    mode: apply ? 'apply' : 'dry_run',
    scanned: 0,
    canonical: 0,
    legacy_single_quoted: 0,
    legacy_double_quoted: 0,
    malformed: 0,
    rebuilt: 0,
    verified: 0,
    failed: 0,
    rolled_back: 0,
    candidates: [],
    failed_items: [],
    timestamp_utc: new Date().toISOString()
  }
  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.getAllGames !== 'function') {
    result.reason = 'GameDB is unavailable'
    return result
  }
  let wrapperPath = ''
  try {
    wrapperPath = await getWrapperLauncherPath()
    await validateWrapperLauncherPath(wrapperPath)
  } catch (error) {
    result.reason = error.message
    return result
  }
  const sqlite = await readSqliteGameIdentityMap()
  const allGames = await gameDb.getAllGames()
  const selectedIds = new Set((payload.selected_game_ids || payload.game_ids || [])
    .map((value) => String(value || '').toLowerCase())
    .filter(Boolean))

  for (const [gameId, gameDoc] of Object.entries(allGames || {})) {
    if (selectedIds.size > 0 && !selectedIds.has(String(gameId).toLowerCase())) continue
    const local = await safeGetGameLocal(gameDb, gameId)
    const evidence = findVgcTakeoverEvidence(local, wrapperPath, gameId)
    if (!evidence.has_takeover || !evidence.shortcut_path) continue

    result.scanned++
    const shortcut = await inspectVgcShortcut(evidence.shortcut_path, wrapperPath, gameId, sqlite)
    if (shortcut.valid) {
      result.canonical++
      result.verified++
      continue
    }
    if (shortcut.status === 'ShortcutArgumentsLegacyQuoted') {
      if (/--game-id\s+'[^']+'|--game-id='[^']+'/i.test(shortcut.arguments)) result.legacy_single_quoted++
      else result.legacy_double_quoted++
    } else if (shortcut.status === 'ShortcutArgumentsMalformed' || shortcut.status === 'ShortcutArgumentsMissing') {
      result.malformed++
    } else {
      continue
    }

    result.candidates.push({
      game_id: gameId,
      name: gameDoc?.metadata?.name || gameId,
      shortcut_path: evidence.shortcut_path,
      status: shortcut.status,
      arguments: shortcut.arguments
    })
    if (!apply) continue

    try {
      const record = sqlite.byGameId.get(String(gameId).toLowerCase()) ||
        sqlite.byVniteGameId.get(String(gameId).toLowerCase()) ||
        (shortcut.parsed_game_id ? resolveShortcutGameRecord(shortcut.parsed_game_id, sqlite) : null)
      if (!record) throw new Error('ShortcutGameIdUnresolved')
      const game = {
        game_id: record.game_id || gameId,
        display_name: gameDoc?.metadata?.name || record.display_name || gameId,
        source_root: record.source_root || getLocalDirectoryRoot(local),
        cache_root: record.cache_root || '',
        executable_path: record.executable_path || '',
        launch_mode: record.launch_mode || 'Direct'
      }
      const nextLocal = await createWrapperManagedGameLocalDoc(game, wrapperPath, local)
      await setGameLocalWithRollback(gameDb, gameId, local || createSourceGameLocalDoc(game), nextLocal, 'shortcut_argument_contract_migration')
      const verified = await inspectVgcShortcut(nextLocal.launcher?.fileConfig?.path || nextLocal.path?.gamePath, wrapperPath, game.game_id, sqlite)
      if (!verified.valid) throw new Error(`readback_failed:${verified.status}`)
      result.rebuilt++
      result.verified++
    } catch (error) {
      result.failed++
      result.failed_items.push({
        game_id: gameId,
        shortcut_path: evidence.shortcut_path,
        error: error.message
      })
    }
  }
  return result
}

async function buildTakeoverEnvironmentSnapshot() {
  const issues = []
  let wrapperPath = ''
  try {
    wrapperPath = await getWrapperLauncherPath()
  } catch (error) {
    issues.push(`Wrapper path unavailable: ${error.message}`)
  }

  const settingsPath = path.join(getLocalAppDataRoot(), 'config', 'settings.json')
  const databasePath = path.join(getLocalAppDataRoot(), 'data', 'vnite-game-cache.db')
  const wrapperDirectory = wrapperPath ? path.dirname(wrapperPath) : ''
  const wrapperExists = Boolean(wrapperPath && fs.existsSync(wrapperPath))
  const settingsExists = fs.existsSync(settingsPath)
  const databaseExists = fs.existsSync(databasePath)
  if (!wrapperExists) issues.push('Wrapper 不存在或未配置')
  if (!settingsExists) issues.push('settings.json 不存在')
  if (!databaseExists) issues.push('SQLite 数据库不存在')

  return {
    ok: issues.length === 0,
    issues,
    wrapper_path: wrapperPath,
    wrapper_directory: wrapperDirectory,
    wrapper_exists: wrapperExists,
    wrapper_hash_sha256: wrapperExists ? sha256File(wrapperPath) : '',
    wrapper_version: readPackageVersion(),
    settings_path: settingsPath,
    settings_exists: settingsExists,
    database_path: databasePath,
    database_exists: databaseExists,
    plugin_db_path: 'runtime:Vnite PluginDB',
    plugin_db_available: Boolean(apiRef?.PluginDB),
    checked_utc: new Date().toISOString()
  }
}

function sha256File(filePath) {
  try {
    return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex')
  } catch {
    return ''
  }
}

function readPackageVersion() {
  try {
    const manifestPath = path.join(__dirname, 'package.json')
    if (!fs.existsSync(manifestPath)) return ''
    return JSON.parse(fs.readFileSync(manifestPath, 'utf8')).version || ''
  } catch {
    return ''
  }
}

function mergeTakeoverStats(adoptStats, residue, shortcutMigration) {
  const stats = { ...createTakeoverStats(), ...(adoptStats || {}) }
  stats.takeover_residue = Math.max(stats.takeover_residue || 0, residue.residue_count || 0)
  stats.shortcut_creation_failed = Math.max(stats.shortcut_creation_failed || 0, (adoptStats?.shortcut_creation_failed || 0))
  stats.launch_routes_checked += shortcutMigration?.scanned || 0
  stats.launch_routes_resolved += shortcutMigration?.verified || 0
  stats.launch_routes_unresolved += (shortcutMigration?.malformed || 0) + (shortcutMigration?.failed || 0)
  stats.legacy_arguments_detected += (shortcutMigration?.legacy_single_quoted || 0) + (shortcutMigration?.legacy_double_quoted || 0)
  stats.malformed_arguments += shortcutMigration?.malformed || 0
  stats.migrated_shortcuts += shortcutMigration?.rebuilt || 0
  stats.migration_failures += shortcutMigration?.failed || 0
  return stats
}

function mapAdoptSkipToConsistencyStatus(reason) {
  if (reason === 'takeover_residue') return 'TakeoverResidue'
  if (reason === 'missing_source_root' || reason === 'outside_adopt_roots') return 'MappingMissing'
  if (reason === 'ambiguous_executable_candidates' || reason === 'low_confidence_executable_candidate') return 'ExecutableConfirmationRequired'
  return 'RebindRequired'
}

async function clearTakeoverProperties({ apply, payload }) {
  const scan = await scanTakeoverResidue(payload)
  if (!apply) {
    return { mode: 'dry_run', scan, timestamp_utc: new Date().toISOString() }
  }

  const gameDb = apiRef?.GameDB
  if (!gameDb || typeof gameDb.setGameLocal !== 'function') {
    return { mode: 'apply', cleared_count: 0, failed: [], scan, reason: 'GameDB is unavailable' }
  }

  const selected = selectClearTakeoverTargets(scan)
  const cleared = []
  const failed = []
  for (const item of selected) {
    try {
      const local = await safeGetGameLocal(gameDb, item.game_id)
      const internal = await apiRef.PluginDB.getValue(`internalGameId:${item.game_id}`, null)
      const launchBackup = await apiRef.PluginDB.getValue(launchBackupKey(item.game_id), null)
      await apiRef.PluginDB.setValue(`takeoverClearBackup:${item.game_id}`, {
        game_id: item.game_id,
        local_doc: local,
        internal_mapping: internal,
        launch_backup: launchBackup,
        cleared_utc: new Date().toISOString()
      })
      const nextLocal = createClearedTakeoverLocalDoc(local, item)
      await setGameLocalWithRollback(gameDb, item.game_id, local, nextLocal, 'clear_takeover_properties')
      const shortcutResult = deleteVgcShortcutForGame(item.game_id, item.shortcut_path)
      await clearPluginDbTakeoverKeys(item.game_id, internal, local)
      let deleteResult = null
      if (payload.delete_plugin_record) {
        deleteResult = await runAgentJson('game-delete', ['--game-id', item.game_id])
        if (deleteResult.exit_code !== 0 || deleteResult.result?.success !== true) {
          throw new Error(`VGC record delete failed: ${deleteResult.stderr || deleteResult.stdout || deleteResult.exit_code}`)
        }
      }
      cleared.push({
        game_id: item.game_id,
        name: item.name,
        source_root: nextLocal?.utils?.rootPath || item.source_root || '',
        shortcut_result: shortcutResult,
        sqlite_delete: deleteResult?.result || null
      })
    } catch (error) {
      failed.push({ game_id: item.game_id, name: item.name, error: error.message })
    }
  }

  let readopt = null
  if (payload.readopt && cleared.length > 0) {
    readopt = await adoptExistingVniteGames({
      apply: true,
      payload: {
        max_count: cleared.length,
        selected_game_ids: cleared.map((item) => item.game_id)
      }
    })
    writeAdoptDiagnostics(readopt, 'clear_takeover_readopt')
  }

  const afterScan = await scanTakeoverResidue(payload)
  return {
    mode: 'apply',
    cleared_count: cleared.length,
    failed_count: failed.length,
    cleared,
    failed,
    scan: afterScan,
    readopt,
    timestamp_utc: new Date().toISOString()
  }
}

function selectClearTakeoverTargets(scan) {
  return [...(scan.residues || []), ...(scan.normal_managed || [])]
}

async function getWrapperLauncherPath() {
  const wrapperPath = (await apiRef.PluginDB.getValue('wrapperLauncherPath', '')) || process.env.VGC_WRAPPER_PATH
  if (!wrapperPath) throw new Error('Wrapper Launcher path is not configured')
  return wrapperPath
}

async function readSqliteGameIdentityMap() {
  const result = {
    available: false,
    error: '',
    byGameId: new Map(),
    byVniteGameId: new Map()
  }
  try {
    const list = await runAgentJson('game-list', [])
    if (list.exit_code !== 0 || list.result?.success === false) {
      result.error = list.stderr || list.result?.error || String(list.exit_code)
      return result
    }

    for (const game of list.result?.games || []) {
      if (game?.game_id) result.byGameId.set(String(game.game_id).toLowerCase(), game)
      if (game?.vnite_game_id) result.byVniteGameId.set(String(game.vnite_game_id).toLowerCase(), game)
    }
    result.available = true
    return result
  } catch (error) {
    result.error = error.message
    return result
  }
}

function findVgcTakeoverEvidence(local, wrapperPath, gameId) {
  const fields = [
    ['path.gamePath', local?.path?.gamePath],
    ['launcher.fileConfig.path', local?.launcher?.fileConfig?.path],
    ['launcher.fileConfig.monitorPath', local?.launcher?.fileConfig?.monitorPath],
    ['launcher.scriptConfig.workingDirectory', local?.launcher?.scriptConfig?.workingDirectory],
    ['launcher.scriptConfig.monitorPath', local?.launcher?.scriptConfig?.monitorPath],
    ['launcher.urlConfig.monitorPath', local?.launcher?.urlConfig?.monitorPath],
    ['launcher.scriptConfig.command', Array.isArray(local?.launcher?.scriptConfig?.command) ? local.launcher.scriptConfig.command.join(' ') : '']
  ]
  const evidence = []
  let shortcutPath = ''
  for (const [field, value] of fields) {
    if (!value) continue
    if (valueLooksVgcTakeover(value, wrapperPath, gameId)) {
      evidence.push({ field, value: String(value) })
    }
    if (!shortcutPath && String(value).toLowerCase().endsWith('.lnk')) {
      shortcutPath = String(value)
    }
  }

  return {
    has_takeover: evidence.length > 0,
    shortcut_path: shortcutPath,
    evidence
  }
}

function valueLooksVgcTakeover(value, wrapperPath, gameId) {
  const text = String(value || '')
  const lowered = text.toLowerCase()
  const launcherRoot = normalizePath(path.join(getLocalAppDataRoot(), 'launchers'))
  const normalizedText = normalizePath(text)
  if (wrapperPath && pathEquals(text, wrapperPath)) return true
  if (lowered.includes('vnitegamecache.wrapper')) return true
  if (lowered.includes('--game-id')) return true
  if (lowered.includes('vnite缓存启动')) return true
  if (gameId && lowered.includes(String(gameId).toLowerCase()) && lowered.endsWith('.lnk')) return true
  if (launcherRoot && normalizedText.startsWith(`${launcherRoot}${path.sep}`)) return true
  return false
}

function buildLaunchArguments(gameId) {
  return `--game-id=${normalizeLaunchGameId(gameId)}`
}

function parseLaunchArguments(argumentsText) {
  const tokens = tokenizeLaunchArguments(argumentsText)
  const startIndex = tokens.findIndex((token) => {
    const key = normalizeLaunchArgumentKey(token).toLowerCase()
    return key === '--game-id' || key.startsWith('--game-id=')
  })
  const launchTokens = startIndex >= 0 ? tokens.slice(startIndex) : tokens
  let gameId = ''
  let legacyQuoted = false
  let rawGameId = ''
  for (let index = 0; index < launchTokens.length; index++) {
    const token = launchTokens[index]
    const key = normalizeLaunchArgumentKey(token)
    let value = ''
    if (key.toLowerCase().startsWith('--game-id=')) {
      value = key.slice('--game-id='.length)
    } else if (key.toLowerCase() === '--game-id') {
      if (index + 1 >= launchTokens.length) {
        const error = new Error('Missing value for --game-id')
        error.code = 'MissingGameIdArgument'
        throw error
      }
      index++
      value = launchTokens[index]
    } else {
      const error = new Error(`Unexpected launch argument: ${token}`)
      error.code = 'MalformedGameIdArgument'
      throw error
    }
    if (gameId) {
      const error = new Error('Duplicate --game-id argument')
      error.code = 'DuplicateGameIdArgument'
      throw error
    }
    rawGameId = value
    legacyQuoted = isOuterQuoted(value)
    gameId = normalizeLaunchGameId(value)
  }
  if (!gameId) {
    const error = new Error('Missing --game-id argument')
    error.code = 'MissingGameIdArgument'
    throw error
  }
  return {
    game_id: gameId,
    raw_game_id: rawGameId,
    legacy_quoted_game_id_normalized: legacyQuoted && rawGameId !== gameId
  }
}

function normalizeLaunchArgumentKey(value) {
  const text = String(value || '').trim()
  return isOuterQuoted(text) ? text.slice(1, -1).trim() : text
}

function normalizeLaunchGameId(value) {
  const unquoted = unquoteLaunchValue(value)
  const compact = unquoted.replace(/^\{|\}$/g, '').replace(/-/g, '').toLowerCase()
  if (!/^[0-9a-f]{32}$/.test(compact)) {
    const error = new Error(`Malformed game ID: ${value || '<empty>'}`)
    error.code = value ? 'MalformedGameIdArgument' : 'MissingGameIdArgument'
    throw error
  }
  return `${compact.slice(0, 8)}-${compact.slice(8, 12)}-${compact.slice(12, 16)}-${compact.slice(16, 20)}-${compact.slice(20)}`
}

function unquoteLaunchValue(value) {
  const trimmed = String(value || '').trim()
  if (!trimmed) return ''
  if (isOuterQuoted(trimmed)) return trimmed.slice(1, -1).trim()
  if (trimmed.startsWith("'") || trimmed.endsWith("'") || trimmed.startsWith('"') || trimmed.endsWith('"')) {
    const error = new Error(`Unmatched quote in game ID: ${value}`)
    error.code = 'MalformedGameIdArgument'
    throw error
  }
  return trimmed
}

function isOuterQuoted(value) {
  const text = String(value || '').trim()
  return text.length >= 2 &&
    ((text.startsWith("'") && text.endsWith("'")) ||
      (text.startsWith('"') && text.endsWith('"')))
}

function tokenizeLaunchArguments(argumentsText) {
  const tokens = []
  let current = ''
  let inDoubleQuotes = false
  for (const char of String(argumentsText || '')) {
    if (char === '"') {
      current += char
      inDoubleQuotes = !inDoubleQuotes
      continue
    }
    if (/\s/.test(char) && !inDoubleQuotes) {
      if (current) {
        tokens.push(current)
        current = ''
      }
      continue
    }
    current += char
  }
  if (inDoubleQuotes) {
    const error = new Error('Unmatched quote in launch arguments')
    error.code = 'MalformedGameIdArgument'
    throw error
  }
  if (current) tokens.push(current)
  return tokens
}

function resolveShortcutGameRecord(parsedGameId, sqlite) {
  if (!parsedGameId || !sqlite?.available) return null
  return sqlite.byGameId.get(parsedGameId.toLowerCase()) ||
    sqlite.byVniteGameId.get(parsedGameId.toLowerCase()) ||
    null
}

async function inspectVgcShortcut(shortcutPath, wrapperPath, gameId, sqliteMap = null) {
  const result = {
    exists: false,
    valid: false,
    status: 'ShortcutMissing',
    target_path: '',
    arguments: '',
    working_directory: '',
    parsed_game_id: '',
    resolved_game_id: ''
  }
  if (!shortcutPath || !shortcutPath.toLowerCase().endsWith('.lnk')) return result
  try {
    if (!fs.existsSync(shortcutPath)) return result
    result.exists = true
    result.status = 'ShortcutUnreadable'
    const script = [
      '$shell = New-Object -ComObject WScript.Shell',
      `$shortcut = $shell.CreateShortcut(${quotePowerShellArg(shortcutPath)})`,
      '[pscustomobject]@{ TargetPath = $shortcut.TargetPath; Arguments = $shortcut.Arguments; WorkingDirectory = $shortcut.WorkingDirectory } | ConvertTo-Json -Compress'
    ].join('; ')
    const read = await runHiddenProcess('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script])
    if (read.exit_code !== 0 || !read.stdout.trim()) return result
    const parsed = JSON.parse(read.stdout)
    result.target_path = parsed?.TargetPath || ''
    result.arguments = parsed?.Arguments || ''
    result.working_directory = parsed?.WorkingDirectory || ''
    if (!wrapperPath) return result
    const targetMatches = wrapperPath.toLowerCase().endsWith('.dll')
      ? path.basename(result.target_path).toLowerCase() === 'dotnet.exe' && result.arguments.toLowerCase().includes(wrapperPath.toLowerCase())
      : pathEquals(result.target_path, wrapperPath)
    if (!targetMatches) {
      result.status = 'ShortcutTargetMismatch'
      return result
    }
    const workingDirectoryMatches = !result.working_directory || pathEquals(result.working_directory, path.dirname(wrapperPath))
    if (!workingDirectoryMatches) {
      result.status = 'ShortcutWorkingDirectoryMismatch'
      return result
    }
    let parsedArgs
    try {
      parsedArgs = parseLaunchArguments(result.arguments)
    } catch (error) {
      result.status = error?.code === 'MissingGameIdArgument' ? 'ShortcutArgumentsMissing' : 'ShortcutArgumentsMalformed'
      return result
    }
    result.parsed_game_id = parsedArgs.game_id
    if (parsedArgs.legacy_quoted_game_id_normalized) {
      result.status = 'ShortcutArgumentsLegacyQuoted'
      return result
    }
    const sqlite = sqliteMap || await readSqliteGameIdentityMap()
    const resolved = resolveShortcutGameRecord(parsedArgs.game_id, sqlite)
    if (!resolved) {
      result.status = 'ShortcutGameIdUnresolved'
      return result
    }
    result.resolved_game_id = resolved.game_id || ''
    if (!pathEquals(result.resolved_game_id, gameId) && !pathEquals(resolved.vnite_game_id, gameId)) {
      result.status = 'ShortcutGameIdInvalid'
      return result
    }
    result.valid = true
    result.status = 'HealthyTakeover'
    return result
  } catch {
    result.status = 'LaunchResolutionFailed'
    return result
  }
}

function createClearedTakeoverLocalDoc(local, residue) {
  const nextLocal = structuredCloneSafe(local || {})
  nextLocal.path = {
    ...(nextLocal.path || {}),
    savePaths: Array.isArray(nextLocal.path?.savePaths) ? nextLocal.path.savePaths : [],
    screenshotPath: nextLocal.path?.screenshotPath || ''
  }
  if (valueLooksVgcTakeover(nextLocal.path.gamePath, '', residue.game_id)) {
    nextLocal.path.gamePath = ''
  }

  const rootPath = nextLocal.utils?.rootPath || residue.root_path || residue.source_root || ''
  nextLocal.utils = {
    ...(nextLocal.utils || {}),
    rootPath
  }
  if (!nextLocal.utils.markPath || valueLooksVgcTakeover(nextLocal.utils.markPath, '', residue.game_id)) {
    nextLocal.utils.markPath = rootPath
  }

  if (nextLocal.launcher && findVgcTakeoverEvidence(nextLocal, '', residue.game_id).has_takeover) {
    nextLocal.launcher = structuredCloneSafe(nextLocal.launcher)
    nextLocal.launcher.mode = ''
    nextLocal.launcher.fileConfig = {
      ...(nextLocal.launcher.fileConfig || {}),
      path: '',
      args: [],
      monitorMode: '',
      monitorPath: ''
    }
    nextLocal.launcher.scriptConfig = {
      ...(nextLocal.launcher.scriptConfig || {}),
      workingDirectory: valueLooksVgcTakeover(nextLocal.launcher.scriptConfig?.workingDirectory, '', residue.game_id)
        ? ''
        : (nextLocal.launcher.scriptConfig?.workingDirectory || ''),
      command: [],
      monitorMode: '',
      monitorPath: ''
    }
    nextLocal.launcher.urlConfig = {
      ...(nextLocal.launcher.urlConfig || {}),
      monitorMode: '',
      monitorPath: ''
    }
    nextLocal.launcher.useMagpie = Boolean(nextLocal.launcher.useMagpie)
  }
  return nextLocal
}

async function clearPluginDbTakeoverKeys(gameId, internal, local) {
  await apiRef.PluginDB.setValue(`internalGameId:${gameId}`, null)
  await apiRef.PluginDB.setValue(launchBackupKey(gameId), null)
  const roots = [
    internal?.source_root,
    internal?.archive_path,
    local?.utils?.rootPath,
    local?.utils?.markPath
  ].filter(Boolean)
  for (const root of roots) {
    await apiRef.PluginDB.setValue(`vniteGameId:${root}`, null)
  }
}

function deleteVgcShortcutForGame(gameId, shortcutPath) {
  const candidates = []
  if (shortcutPath) candidates.push(shortcutPath)
  const launcherRoot = path.join(getLocalAppDataRoot(), 'launchers')
  try {
    if (fs.existsSync(launcherRoot)) {
      for (const file of fs.readdirSync(launcherRoot)) {
        if (file.toLowerCase().endsWith('.lnk') && file.toLowerCase().includes(String(gameId).toLowerCase())) {
          candidates.push(path.join(launcherRoot, file))
        }
      }
    }
  } catch {
  }

  const deleted = []
  const skipped = []
  for (const candidate of [...new Set(candidates)]) {
    try {
      if (!fs.existsSync(candidate)) continue
      if (!isPathUnderRoot(candidate, launcherRoot)) {
        skipped.push({ path: candidate, reason: 'outside_vgc_launcher_root' })
        continue
      }
      if (!path.basename(candidate).toLowerCase().includes(String(gameId).toLowerCase())) {
        skipped.push({ path: candidate, reason: 'ownership_not_confirmed' })
        continue
      }
      fs.rmSync(candidate, { force: true })
      deleted.push(candidate)
    } catch (error) {
      skipped.push({ path: candidate, reason: error.message })
    }
  }
  return { deleted, skipped }
}

async function validateWrapperLauncherPath(wrapperPath) {
  if (!wrapperPath || !fs.existsSync(wrapperPath)) {
    throw new Error(`Wrapper Launcher path does not exist: ${wrapperPath || '<empty>'}`)
  }
  const extension = path.extname(wrapperPath).toLowerCase()
  if (extension !== '.exe' && extension !== '.dll') {
    throw new Error(`Wrapper Launcher must be an .exe or .dll: ${wrapperPath}`)
  }
}

async function setGameLocalWithRollback(gameDb, gameId, originalLocal, nextLocal, reason) {
  await backupOriginalLaunchFields(gameId, originalLocal, reason)
  try {
    await writeFullGameLocal(gameDb, gameId, nextLocal)
    const readback = await safeGetGameLocal(gameDb, gameId)
    if (typeof gameDb.getGameLocal === 'function' && !isGameLocalWriteVerified(readback, nextLocal)) {
      throw new Error('Vnite launch field write verification failed')
    }
    await log('launch_route_write_verified', {
      game_id: gameId,
      reason,
      mode: nextLocal?.launcher?.mode || '',
      game_path: nextLocal?.path?.gamePath || '',
      launcher_path: nextLocal?.launcher?.fileConfig?.path || '',
      root_path: nextLocal?.utils?.rootPath || ''
    })
  } catch (error) {
    await rollbackGameLocalAfterFailure(gameDb, gameId, originalLocal, reason, error)
    throw error
  }
}

async function writeFullGameLocal(gameDb, gameId, nextLocal) {
  if (typeof gameDb.setGameLocal !== 'function') {
    throw new Error('GameDB cannot write game local document')
  }
  await gameDb.setGameLocal(gameId, nextLocal)
  if (typeof gameDb.setGameLocalValue !== 'function') return

  const values = [
    ['path.gamePath', nextLocal?.path?.gamePath || ''],
    ['path.savePaths', Array.isArray(nextLocal?.path?.savePaths) ? nextLocal.path.savePaths : []],
    ['path.screenshotPath', nextLocal?.path?.screenshotPath || ''],
    ['launcher.mode', nextLocal?.launcher?.mode || ''],
    ['launcher.fileConfig', nextLocal?.launcher?.fileConfig || {}],
    ['launcher.urlConfig', nextLocal?.launcher?.urlConfig || {}],
    ['launcher.scriptConfig', nextLocal?.launcher?.scriptConfig || {}],
    ['launcher.useMagpie', Boolean(nextLocal?.launcher?.useMagpie)],
    ['utils.markPath', nextLocal?.utils?.markPath || ''],
    ['utils.rootPath', nextLocal?.utils?.rootPath || '']
  ]

  for (const [key, value] of values) {
    await gameDb.setGameLocalValue(gameId, key, value)
  }
}

function isGameLocalWriteVerified(actual, expected) {
  if (!actual || !expected) return false
  return actual.launcher?.mode === expected.launcher?.mode &&
    pathEquals(actual.path?.gamePath, expected.path?.gamePath) &&
    pathEquals(actual.launcher?.fileConfig?.path, expected.launcher?.fileConfig?.path) &&
    pathEquals(actual.launcher?.fileConfig?.monitorPath, expected.launcher?.fileConfig?.monitorPath) &&
    pathEquals(actual.launcher?.scriptConfig?.workingDirectory, expected.launcher?.scriptConfig?.workingDirectory) &&
    pathEquals(actual.utils?.rootPath, expected.utils?.rootPath) &&
    pathEquals(actual.utils?.markPath, expected.utils?.markPath)
}

async function rollbackGameLocalAfterFailure(gameDb, gameId, originalLocal, reason, originalError) {
  try {
    if (originalLocal) {
      await gameDb.setGameLocal(gameId, originalLocal)
    }
    await log('launch_route_write_rollback', {
      game_id: gameId,
      reason,
      error: originalError.message
    })
  } catch (rollbackError) {
    await log('launch_route_write_rollback_failed', {
      game_id: gameId,
      reason,
      error: originalError.message,
      rollback_error: rollbackError.message
    })
  }
}

function requireAgentSuccess(result, command) {
  if (!result || result.exit_code !== 0 || result.result?.success === false) {
    throw new Error(`${command} failed before writing Vnite launch fields: ${result?.stderr || result?.result?.error || result?.exit_code}`)
  }
  return result
}

function buildWrapperCommand(wrapperPath, gameId) {
  const launchArguments = buildLaunchArguments(gameId)
  if (wrapperPath.toLowerCase().endsWith('.dll')) {
    return `dotnet ${quoteCmdArg(wrapperPath)} ${launchArguments}`
  }
  return `${quoteCmdArg(wrapperPath)} ${launchArguments}`
}

async function ensureWrapperShortcut(gameOrId, wrapperPath) {
  const gameId = typeof gameOrId === 'string' ? gameOrId : gameOrId.game_id
  const displayName = typeof gameOrId === 'string' ? '' : (gameOrId.display_name || gameOrId.name || '')
  const sourceRoot = typeof gameOrId === 'string' ? '' : (gameOrId.source_root || '')
  const shortcutRoot =
    (await apiRef.PluginDB.getValue('launcherShortcutRoot', '')) ||
    path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache', 'launchers')
  fs.mkdirSync(shortcutRoot, { recursive: true })
  const candidatePaths = [
    path.join(shortcutRoot, buildShortcutFileName(displayName, gameId)),
    path.join(shortcutRoot, `${sanitizeShortcutFileName(gameId, gameId, 110)}.lnk`)
  ]
  const wrapperDirectory = path.dirname(wrapperPath)
  const targetPath = wrapperPath.toLowerCase().endsWith('.dll')
    ? ((await apiRef.PluginDB.getValue('dotnetHostPath', '')) || 'dotnet.exe')
    : wrapperPath
  const launchArguments = buildLaunchArguments(gameId)
  const argumentsText = wrapperPath.toLowerCase().endsWith('.dll')
    ? `${quoteShortcutArg(wrapperPath)} ${launchArguments}`
    : launchArguments
  const errors = []
  for (const shortcutPath of candidatePaths) {
    const script = [
      '$shell = New-Object -ComObject WScript.Shell',
      `$shortcut = $shell.CreateShortcut(${quotePowerShellArg(shortcutPath)})`,
      `$shortcut.TargetPath = ${quotePowerShellArg(targetPath)}`,
      `$shortcut.Arguments = ${quotePowerShellArg(argumentsText)}`,
      `$shortcut.WorkingDirectory = ${quotePowerShellArg(wrapperDirectory)}`,
      '$shortcut.WindowStyle = 7',
      `$shortcut.Description = ${quotePowerShellArg(`Vnite Game Cache launcher; game_id=${gameId}; source_root=${sourceRoot}`)}`,
      '$shortcut.Save()'
    ].join('; ')
    const result = await runHiddenProcess('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script])
    if (result.exit_code === 0) {
      return shortcutPath
    }
    errors.push(result.stderr || String(result.exit_code))
  }
  throw new Error(`Failed to create wrapper shortcut: ${errors.join(' | ')}`)
}

function buildShortcutFileName(displayName, gameId) {
  const shortId = String(gameId).replace(/[^a-zA-Z0-9]/g, '').slice(0, 8) || 'unknown'
  const name = sanitizeShortcutFileName(displayName || gameId, gameId, 70)
  return `${name} [GameId-${shortId}] ${gameId} - Vnite缓存启动.lnk`
}

function sanitizeFileName(value) {
  return sanitizeShortcutFileName(value, '', 120)
}

function sanitizeShortcutFileName(value, gameId = '', maxLength = 120) {
  const reserved = new Set(['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'COM2', 'COM3', 'COM4', 'COM5', 'COM6', 'COM7', 'COM8', 'COM9', 'LPT1', 'LPT2', 'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9'])
  let sanitized = String(value || gameId || 'Vnite 游戏')
    .replace(/[<>:"/\\|?*\x00-\x1f]/g, '_')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/[ .]+$/g, '')
  if (!sanitized || reserved.has(sanitized.toUpperCase())) {
    sanitized = `VGC-${String(gameId || 'game').replace(/[^a-zA-Z0-9-]/g, '').slice(0, 36) || 'game'}`
  }
  if (sanitized.length > maxLength) {
    sanitized = sanitized.slice(0, maxLength).replace(/[ .]+$/g, '')
  }
  return sanitized || 'Vnite 游戏'
}

function quotePowerShellArg(value) {
  return `'${String(value).replace(/'/g, "''")}'`
}

function quoteShortcutArg(value) {
  return `"${String(value).replace(/"/g, '\\"')}"`
}

function runHiddenProcess(fileName, args) {
  const child = spawn(fileName, args, {
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  })
  const stdout = []
  const stderr = []
  child.stdout.on('data', (data) => stdout.push(data.toString('utf8')))
  child.stderr.on('data', (data) => stderr.push(data.toString('utf8')))
  return new Promise((resolve, reject) => {
    child.on('error', reject)
    child.on('exit', (code) => resolve({ exit_code: code, stdout: stdout.join(''), stderr: stderr.join('') }))
  })
}

function quoteCmdArg(value) {
  return `"${String(value).replace(/"/g, '\\"')}"`
}

async function safeGetGameLocal(gameDb, gameId) {
  if (typeof gameDb.getGameLocal !== 'function') return null
  try {
    return await gameDb.getGameLocal(gameId)
  } catch {
    return null
  }
}

function isManagedGame(gameDoc) {
  const extra = gameDoc?.metadata?.extra
  return Array.isArray(extra) && extra.some((item) =>
    item?.key === 'managed_by_vnite_game_cache' &&
    Array.isArray(item.value) &&
    item.value.includes('true'))
}

async function getExistingAdoptRoots(payload) {
  const configured = payload.roots || payload.root || (await apiRef.PluginDB.getValue('existingVniteAdoptRoots', ''))
  const libraryRoot = await apiRef.PluginDB.getValue('libraryRoot', '')
  const values = Array.isArray(configured) ? configured : splitPathList(configured)
  if (libraryRoot) values.push(libraryRoot)
  const expanded = await expandAdoptRootAliases(values)
  return [...new Set(expanded.map((value) => normalizePath(value)).filter(Boolean))]
}

function splitPathList(value) {
  return String(value || '')
    .split(/[;\r\n]+/)
    .map((item) => item.trim())
    .filter(Boolean)
}

async function expandAdoptRootAliases(values) {
  const rawRoots = values.map((value) => String(value || '').trim()).filter(Boolean)
  const driveQualifiedRoots = rawRoots.filter((value) => /^[a-zA-Z]:[\\/]/.test(value))
  const mappedRoots = await getMappedDriveRoots()
  const drivePrefixes = [...new Set([
    ...driveQualifiedRoots.map((value) => value.slice(0, 2).toUpperCase()),
    ...mappedRoots.map((mapping) => mapping.root.slice(0, 2).toUpperCase())
  ].filter(Boolean))]
  const expanded = []

  for (const root of rawRoots) {
    expanded.push(root)

    if (/^[\\/](?![\\/])/.test(root)) {
      for (const drivePrefix of drivePrefixes) {
        expanded.push(`${drivePrefix}${root}`)
      }
    }

    for (const mapping of mappedRoots) {
      if (isSameOrChildTextPath(root, mapping.displayRoot)) {
        expanded.push(replacePathPrefix(root, mapping.displayRoot, mapping.root))
      }
      if (isSameOrChildTextPath(root, mapping.root)) {
        expanded.push(replacePathPrefix(root, mapping.root, mapping.displayRoot))
      }
    }
  }

  return expanded
}

let mappedDriveRootsCache = { expiresAt: 0, roots: [] }

async function getMappedDriveRoots() {
  const now = Date.now()
  if (mappedDriveRootsCache.expiresAt > now) return mappedDriveRootsCache.roots

  const script = [
    '$ErrorActionPreference = "SilentlyContinue"',
    '$items = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.DisplayRoot } | ForEach-Object { [pscustomobject]@{ Root = $_.Root; DisplayRoot = $_.DisplayRoot } }',
    '$items | ConvertTo-Json -Compress'
  ].join('; ')
  try {
    const result = await runHiddenProcess('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script])
    if (result.exit_code !== 0 || !result.stdout.trim()) {
      mappedDriveRootsCache = { expiresAt: now + 60_000, roots: [] }
      return []
    }
    const parsed = JSON.parse(result.stdout)
    const items = Array.isArray(parsed) ? parsed : [parsed]
    const roots = items
      .map((item) => ({
        root: normalizeDriveRoot(item?.Root || ''),
        displayRoot: normalizeDisplayRoot(item?.DisplayRoot || '')
      }))
      .filter((item) => item.root && item.displayRoot)
    mappedDriveRootsCache = { expiresAt: now + 60_000, roots }
    return roots
  } catch (error) {
    await log('mapped_drive_alias_probe_failed', { error: error.message })
    mappedDriveRootsCache = { expiresAt: now + 60_000, roots: [] }
    return []
  }
}

function normalizeDriveRoot(value) {
  const text = String(value || '').trim()
  if (!/^[a-zA-Z]:[\\/]?$/.test(text)) return ''
  return `${text.slice(0, 2)}\\`
}

function normalizeDisplayRoot(value) {
  return String(value || '').trim().replace(/[\\/]+$/, '')
}

function isSameOrChildTextPath(value, root) {
  const normalizedValue = normalizeTextPath(value)
  const normalizedRoot = normalizeTextPath(root)
  return normalizedValue === normalizedRoot || normalizedValue.startsWith(`${normalizedRoot}\\`)
}

function replacePathPrefix(value, fromRoot, toRoot) {
  const normalizedValue = normalizeTextPath(value)
  const normalizedFromRoot = normalizeTextPath(fromRoot)
  const suffix = normalizedValue === normalizedFromRoot ? '' : normalizedValue.slice(normalizedFromRoot.length).replace(/^\\+/, '')
  return suffix ? path.join(toRoot, suffix) : toRoot
}

function normalizeTextPath(value) {
  return String(value || '').replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase()
}

function resolveManualAdoptLaunchSource(manualRecord, roots) {
  const sourceRoot = manualRecord?.source_root || ''
  const executablePath = manualRecord?.executable_path || ''
  if (!sourceRoot || !executablePath) return null
  try {
    if (!fs.existsSync(sourceRoot) || !fs.statSync(sourceRoot).isDirectory()) return null
    if (!fs.existsSync(executablePath) || !fs.statSync(executablePath).isFile()) return null
  } catch {
    return null
  }
  if (!isPathUnderAnyRoot(sourceRoot, roots) && !isPathUnderAnyRoot(executablePath, roots)) return null
  if (!isPathUnderRoot(executablePath, sourceRoot)) return null
  return {
    source_root: sourceRoot,
    executable_path: executablePath,
    resolution: 'ManualSqliteExecutableSelection'
  }
}

function getLocalExecutablePath(local) {
  if (!local?.launcher) return ''
  if (local.launcher.mode === 'file') {
    return local.launcher.fileConfig?.path || local.path?.gamePath || ''
  }
  if (local.launcher.mode === 'script') {
    return local.path?.gamePath || findExistingExecutableInCommands(local.launcher.scriptConfig?.command || [])
  }
  return local.path?.gamePath || ''
}

function getLocalSourceRoot(local, executablePath) {
  return local?.utils?.rootPath || local?.utils?.markPath || path.dirname(executablePath)
}

function resolveAdoptLaunchSource(local, roots, gameName = '', manualRecord = null) {
  const manual = resolveManualAdoptLaunchSource(manualRecord, roots)
  if (manual) return manual

  const sourceRoot = getLocalDirectoryRoot(local)
  const executablePath = getLocalExecutablePath(local)

  if (sourceRoot && isPathUnderAnyRoot(sourceRoot, roots)) {
    if (executablePath && !executablePath.toLowerCase().endsWith('.lnk') && isPathUnderRoot(executablePath, sourceRoot)) {
      return {
        source_root: sourceRoot,
        executable_path: executablePath,
        resolution: 'StableRootExistingExecutablePath'
      }
    }

    const detected = detectExecutableInDirectory(sourceRoot, gameName)
    if (!detected.executable_path) {
      return {
        source_root: sourceRoot,
        reason: detected.reason,
        candidates: detected.candidates
      }
    }

    return {
      source_root: sourceRoot,
      executable_path: detected.executable_path,
      resolution: 'StableRootDirectoryExecutableDetected',
      candidates: detected.candidates
    }
  }

  if (!sourceRoot) {
    if (executablePath && !executablePath.toLowerCase().endsWith('.lnk')) {
      const executableSourceRoot = getLocalSourceRoot(local, executablePath)
      if (isPathUnderAnyRoot(executableSourceRoot, roots) || isPathUnderAnyRoot(executablePath, roots)) {
        return {
          source_root: executableSourceRoot,
          executable_path: executablePath,
          resolution: 'ExecutablePathFallback'
        }
      }
    }
    return { reason: 'missing_source_root' }
  }

  return { source_root: sourceRoot, reason: 'outside_adopt_roots' }
}

function isPathUnderRoot(value, root) {
  const normalizedValue = normalizePath(value)
  const normalizedRoot = normalizePath(root)
  return normalizedValue === normalizedRoot || normalizedValue.startsWith(`${normalizedRoot}${path.sep}`)
}

function isWrapperManagedLocalDocComplete(local, wrapperPath, gameId, game) {
  if (!local?.launcher) return false
  const monitorProcessName = getWrapperProcessName(wrapperPath)
  const gamePath = local.path?.gamePath || ''
  const launcherPath = local.launcher.fileConfig?.path || ''
  return local.launcher.mode === 'file' &&
    isWrapperShortcutPathForGame(gamePath, gameId) &&
    isWrapperShortcutPathForGame(launcherPath, gameId) &&
    pathEquals(gamePath, launcherPath) &&
    pathEquals(local.launcher.fileConfig?.monitorPath, monitorProcessName) &&
    pathEquals(local.utils?.rootPath, game.source_root) &&
    pathEquals(local.utils?.markPath, game.source_root)
}

function isInternalGameSnapshotCurrent(internal, game) {
  if (!internal) return false
  return pathEquals(internal.source_root, game.source_root) &&
    pathEquals(internal.executable_path, game.executable_path) &&
    Boolean(internal.cache_root)
}

function getLocalDirectoryRoot(local) {
  for (const value of [local?.utils?.rootPath, local?.utils?.markPath, local?.path?.gamePath]) {
    if (!value) continue
    try {
      if (fs.existsSync(value) && fs.statSync(value).isDirectory()) {
        return value
      }
    } catch {
      continue
    }
  }
  return ''
}

function detectExecutableInDirectory(sourceRoot, gameName = '') {
  const rootEntries = readDirectoryEntries(sourceRoot)
  if (!rootEntries) {
    return { reason: 'read_failed:unable_to_read_directory', candidates: [] }
  }

  const candidates = enumerateExecutableCandidates(sourceRoot, 1)
    .map((candidate) => {
      const entries = candidate.depth === 0 ? rootEntries : readDirectoryEntries(candidate.directory) || []
      const rootProfile = buildExecutableRootProfile(candidate.directory, entries, gameName)
      const score = scoreExecutableCandidate(candidate.fileName, rootProfile) - candidate.depth * 5
      return {
        name: candidate.relativePath,
        path: candidate.path,
        score,
        engine: rootProfile.engine
      }
    })
    .filter((candidate) => candidate.score > 0)
    .sort((left, right) => right.score - left.score || left.name.localeCompare(right.name))

  if (candidates.length === 0) {
    return { reason: 'no_safe_executable_candidate', candidates: [] }
  }

  const best = candidates[0]
  const second = candidates[1]
  if (candidates.length === 1) {
    return {
      executable_path: best.path,
      candidates
    }
  }
  if (best.score < 60) {
    return { reason: 'low_confidence_executable_candidate', candidates }
  }
  if (second && best.score < 90 && best.score - second.score < 20) {
    return { reason: 'ambiguous_executable_candidates', candidates }
  }

  return {
    executable_path: best.path,
    candidates
  }
}

function readDirectoryEntries(directoryPath) {
  try {
    return fs.readdirSync(directoryPath, { withFileTypes: true })
  } catch {
    return null
  }
}

function enumerateExecutableCandidates(sourceRoot, maxDepth = 1) {
  const candidates = []
  const pending = [{ directory: sourceRoot, depth: 0 }]
  while (pending.length > 0) {
    const current = pending.shift()
    const entries = readDirectoryEntries(current.directory)
    if (!entries) continue
    for (const entry of entries) {
      const fullPath = path.join(current.directory, entry.name)
      if (entry.isFile() && entry.name.toLowerCase().endsWith('.exe')) {
        candidates.push({
          fileName: entry.name,
          path: fullPath,
          directory: current.directory,
          relativePath: path.relative(sourceRoot, fullPath) || entry.name,
          depth: current.depth
        })
      }
    }
    if (current.depth >= maxDepth) continue
    for (const entry of entries) {
      if (!entry.isDirectory() || shouldSkipExecutableScanDirectory(entry.name)) continue
      pending.push({ directory: path.join(current.directory, entry.name), depth: current.depth + 1 })
    }
  }
  return candidates
}

function shouldSkipExecutableScanDirectory(name) {
  return new Set([
    'save', 'saves', 'savedata', 'userdata', 'screenshots', 'screenshot',
    'mod', 'mods', 'plugin', 'plugins', 'redist', 'redistributable', 'runtime',
    'www', 'node_modules', 'locales', 'swiftshader', 'renpy', 'lib',
    'mtooldata', 'mtoolscripts', 'mcenter', 'quarantine', 'staging',
    'tool', 'tools', 'directx', 'locale.emulator.2.4.1.0', 'locale emulator',
    'backup', 'backups', 'bak', '備份', '备份', '漢化備份', '汉化备份'
  ]).has(String(name || '').toLowerCase())
}
function buildExecutableRootProfile(sourceRoot, entries, gameName = '') {
  const names = new Set(entries.map((entry) => entry.name.toLowerCase()))
  const directories = new Set(entries.filter((entry) => entry.isDirectory()).map((entry) => entry.name.toLowerCase()))
  const files = new Set(entries.filter((entry) => entry.isFile()).map((entry) => entry.name.toLowerCase()))
  const folderName = path.basename(sourceRoot)
  return {
    folderName,
    normalizedFolderName: normalizeCandidateText(folderName),
    normalizedGameName: normalizeCandidateText(gameName),
    engine: detectEngineProfile(names, directories, files)
  }
}

function detectEngineProfile(names, directories, files) {
  if (directories.has('www') && (files.has('package.json') || files.has('nw.dll'))) return 'NWjs'
  if (directories.has('www') || [...files].some((name) => name.endsWith('.rvproj2') || name.endsWith('.rpgproject'))) return 'RPGMaker'
  if ([...directories].some((name) => name.endsWith('_data')) && (files.has('unityplayer.dll') || files.has('gameassembly.dll'))) return 'Unity'
  if (directories.has('renpy') || directories.has('lib') && [...files].some((name) => name.endsWith('.py') || name.endsWith('.rpy'))) return 'RenPy'
  if (files.has('krkr.eXe'.toLowerCase()) || files.has('xp3filter.tpm') || [...files].some((name) => name.endsWith('.xp3'))) return 'KirikiroidKiriki'
  if ((directories.has('data') && directories.has('image')) || files.has('config.tjs')) return 'Kirikiri'
  if (files.has('arc.nsa') || files.has('nscript.dat') || files.has('0.txt')) return 'NScripter'
  if (files.has('rio.ini') || files.has('malie.ini')) return 'Malie'
  if ([...files].some((name) => name.endsWith('.pac') || name.endsWith('.ypf'))) return 'GalgameArchive'
  return 'Unknown'
}

function scoreExecutableCandidate(fileName, rootProfile) {
  const name = fileName.toLowerCase()
  const baseName = path.basename(name, '.exe')
  const normalizedBaseName = normalizeCandidateText(baseName)
  if (isAuxiliaryExecutableName(name)) return -100
  if (isTranslationExecutableName(name)) return 130
  if (name === 'game_cn.exe') return 130
  if (rootProfile.normalizedGameName && normalizedBaseName === rootProfile.normalizedGameName) return 115
  if (baseName === rootProfile.folderName.toLowerCase()) return 110
  if (normalizedBaseName && normalizedBaseName === rootProfile.normalizedFolderName) return 110
  if (name === 'game.exe') return 105
  if (rootProfile.engine === 'Unity' && name === 'unitycrashhandler64.exe') return -100
  if (rootProfile.engine === 'NWjs' && (name === 'nw.exe' || name === 'nwjs.exe')) return 85
  if (rootProfile.engine === 'RenPy' && !baseName.includes('crash') && !baseName.includes('update')) return 75
  if ((rootProfile.engine === 'Kirikiri' || rootProfile.engine === 'KirikiroidKiriki') && (baseName === 'krkr' || baseName === 'kirikiri')) return 70
  if (rootProfile.normalizedGameName && normalizedBaseName.length >= 4 && normalizedTextSimilarity(normalizedBaseName, rootProfile.normalizedGameName) >= 0.82) return 92
  if (normalizedBaseName && rootProfile.normalizedFolderName.includes(normalizedBaseName) && normalizedBaseName.length >= 4) return 82
  if (normalizedBaseName && normalizedBaseName.length >= 4 && normalizedTextSimilarity(normalizedBaseName, rootProfile.normalizedFolderName) >= 0.72) return 78
  if (baseName.includes('game')) return 65
  if (baseName.includes('launcher') || baseName.includes('start') || baseName.includes('play')) return 50
  return name.endsWith('.exe') ? 35 : 10
}

function isTranslationExecutableName(name) {
  const value = String(name || '').toLowerCase()
  return /(^|[_\-\s.])(cn|chs|sc|zh|zh-cn|zh_cn|zhcn|ai|gpt|claude|deepseek|galtransl|sugoi|sakura)([_\-\s.]|$|\d)/i.test(value) ||
    /中文|简中|簡中|汉化|漢化|简体|簡体|官方中文|机翻|翻译|翻譯/.test(value)
}

function isAuxiliaryExecutableName(name) {
  const value = String(name || '').toLowerCase()
  return /^(unins|uninst|uninstall|setup|install|config|setting|settings|unitycrashhandler|crashreport|notification_helper|helper|tool|update|patch|register|reg|repair|redist|vcredist|dxsetup|language|locale|manual|leproc|legui|leinstaller|mtool|chromedriver|nwjc|payload|krkrextract|xp3_upk|syscfg|sdacti)/i.test(value) ||
    /(config|setting|settings|crash|helper|updater|update|patcher|patch|check|install|inst|uninstall|readme|manual|launcher_config|破損チェック|ファイル破損チェック|エンジン設定|セーブファイル設定)\d*\.exe$/i.test(value) ||
    /ファイル破損チェック|破損チェック|エンジン設定|セーブファイル設定|directx|redist|redistributable|locale[._ -]?emulator/.test(value)
}

function normalizeCandidateText(value) {
  return String(value || '')
    .normalize('NFKC')
    .toLowerCase()
    .replace(/\[[^\]]*\]|\([^)]*\)|（[^）]*）|【[^】]*】/g, '')
    .replace(/ver(?:sion)?\.?\s*\d+(?:\.\d+)*/gi, '')
    .replace(/v\d+(?:\.\d+)*/gi, '')
    .replace(/製品版|体验版|体験版|trial|demo|chs|cht|cn|jp|en|汉化|漢化|中文|简体|繁体|官方|补丁|patch/gi, '')
    .replace(/[^a-z0-9\u3040-\u30ff\u3400-\u9fff]+/g, '')
}

function normalizedTextSimilarity(left, right) {
  if (!left || !right) return 0
  if (left === right) return 1
  const shorter = left.length <= right.length ? left : right
  const longer = left.length > right.length ? left : right
  let matched = 0
  for (const char of new Set(shorter.split(''))) {
    if (longer.includes(char)) matched++
  }
  return matched / Math.max(1, new Set(shorter.split('')).size)
}

function findExistingExecutableInCommands(commands) {
  if (!Array.isArray(commands)) return ''
  for (const command of commands) {
    const matches = String(command).match(/"([^"]+\.exe)"|([^\s"]+\.exe)/gi) || []
    for (const match of matches) {
      const executable = match.replace(/^"|"$/g, '')
      if (fs.existsSync(executable)) {
        return executable
      }
    }
  }
  return ''
}

function getOriginalScriptJson(local) {
  if (local?.launcher?.mode !== 'script') return ''
  const command = local.launcher.scriptConfig?.command || []
  if (!Array.isArray(command) || command.length === 0) return ''
  return JSON.stringify(command)
}

function isPathUnderAnyRoot(value, roots) {
  const normalized = normalizePath(value)
  return roots.some((root) => normalized === root || normalized.startsWith(`${root}${path.sep}`))
}

function validateAdoptRoot(sourceRoot, roots) {
  if (!sourceRoot) {
    return { allowed: false, reason: 'missing_source_root' }
  }
  if (!Array.isArray(roots) || roots.length === 0) {
    return { allowed: false, reason: 'no_configured_adopt_roots' }
  }
  if (!isPathUnderAnyRoot(sourceRoot, roots)) {
    return { allowed: false, reason: 'outside_configured_adopt_roots' }
  }
  return { allowed: true, reason: '' }
}

function normalizePath(value) {
  if (!value) return ''
  try {
    return path.resolve(String(value)).replace(/[\\/]+$/, '').toLowerCase()
  } catch {
    return String(value).replace(/[\\/]+$/, '').toLowerCase()
  }
}

function buildCacheRoot(defaultCacheRoot, folderName, gameId) {
  const suffix = gameId.length > 8 ? gameId.slice(-8) : gameId
  return path.join(defaultCacheRoot, `${sanitizePathSegment(folderName || 'game')}-${suffix}`)
}

function sanitizePathSegment(value) {
  let sanitized = String(value).replace(/[<>:"/\\|?*\x00-\x1F]/g, '_').trim().replace(/\.+$/g, '')
  sanitized = sanitized.replace(/\s+/g, ' ')
  if (!sanitized) sanitized = 'game'
  const reserved = new Set(['con', 'prn', 'aux', 'nul', 'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9', 'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9'])
  if (reserved.has(sanitized.toLowerCase())) sanitized = 'game'
  return sanitized.length > 48 ? sanitized.slice(0, 48).trim() : sanitized
}

function classifyLaunchRoute(local, wrapperPath, gameId) {
  if (!local?.launcher) return 'Broken'
  const filePath = local.launcher.fileConfig?.path || ''
  const command = local.launcher.scriptConfig?.command || []
  const wrapperCommand = buildWrapperCommand(wrapperPath, gameId)
  if (
    local.launcher.mode === 'file' &&
    isWrapperShortcutPathForGame(filePath, gameId)
  ) {
    return 'WrapperManaged'
  }
  if (
    local.launcher.mode === 'script' &&
    Array.isArray(command) &&
    command.includes(wrapperCommand)
  ) {
    return 'WrapperManaged'
  }
  if (filePath && !pathEquals(filePath, wrapperPath)) return 'DirectSource'
  return 'Broken'
}

function isWrapperShortcutPathForGame(shortcutPath, gameId) {
  const value = String(shortcutPath || '').toLowerCase()
  const expected = String(gameId || '').toLowerCase()
  if (!value.endsWith('.lnk') || !expected) return false
  return value.endsWith(`${expected}.lnk`) || value.includes(expected)
}

function getGameProcessName(game) {
  const executablePath = game?.executable_path || ''
  if (!executablePath) return ''
  return path.basename(executablePath, path.extname(executablePath))
}

function getWrapperProcessName(wrapperPath) {
  const fileName = path.basename(wrapperPath, path.extname(wrapperPath))
  return fileName || 'VniteGameCache.Wrapper'
}

function getLauncherMonitorPath(local) {
  if (!local?.launcher) return ''
  if (local.launcher.mode === 'script') {
    return local.launcher.scriptConfig?.monitorPath || ''
  }
  if (local.launcher.mode === 'file') {
    return local.launcher.fileConfig?.monitorPath || ''
  }
  return local.launcher.scriptConfig?.monitorPath || local.launcher.fileConfig?.monitorPath || ''
}

async function backupOriginalLaunchFields(gameId, localDoc, reason) {
  const key = launchBackupKey(gameId)
  const existing = await apiRef.PluginDB.getValue(key, null)
  if (existing?.local_doc) return
  await apiRef.PluginDB.setValue(key, {
    game_id: gameId,
    reason,
    launch_entry_version: 1,
    local_doc: localDoc,
    original_executable_path: localDoc?.launcher?.fileConfig?.path || localDoc?.path?.gamePath || '',
    original_arguments: localDoc?.launcher?.fileConfig?.args || [],
    original_working_directory: localDoc?.launcher?.scriptConfig?.workingDirectory || '',
    backed_up_utc: new Date().toISOString()
  })
}

function launchBackupKey(gameId) {
  return `launchBackup:${gameId}`
}

function pathEquals(left, right) {
  return String(left || '').toLowerCase() === String(right || '').toLowerCase()
}

async function launchWrapper(payload) {
  if (!apiRef) {
    throw new Error('Plugin is not activated')
  }

  const wrapperPath =
    payload.wrapper_launcher_path ||
    (await apiRef.PluginDB.getValue('wrapperLauncherPath', '')) ||
    process.env.VGC_WRAPPER_PATH
  const agentPath =
    payload.agent_stub_path ||
    (await apiRef.PluginDB.getValue('agentStubPath', '')) ||
    process.env.VGC_AGENT_PATH
  const executablePath =
    payload.executable_path ||
    (await apiRef.PluginDB.getValue('testExecutablePath', ''))
  const workingDirectory =
    payload.working_directory ||
    (await apiRef.PluginDB.getValue('testWorkingDirectory', ''))
  const rawArguments =
    payload.test_arguments_raw ||
    (await apiRef.PluginDB.getValue('testArgumentsRaw', ''))

  if (!wrapperPath) throw new Error('Wrapper Launcher path is not configured')
  if (!agentPath) throw new Error('Agent Stub path is not configured')
  if (!executablePath) throw new Error('Test executable path is not configured')
  if (!workingDirectory) throw new Error('Test working directory is not configured')

  const request = {
    protocol_version: 1,
    request_id: payload.request_id || crypto.randomUUID(),
    game_id: payload.game_id || 'phase2.5-real-vnite-validation',
    timestamp_utc: new Date().toISOString(),
    executable_path: executablePath,
    arguments: Array.isArray(payload.arguments) ? payload.arguments : parseWindowsArgs(rawArguments),
    working_directory: workingDirectory,
    environment: payload.environment || {},
    additional_process_names: Array.isArray(payload.additional_process_names)
      ? payload.additional_process_names
      : []
  }

  const requestPath = path.join(os.tmpdir(), `vgc-launch-${request.request_id}.json`)
  fs.writeFileSync(requestPath, JSON.stringify(request), 'utf8')
  await log('wrapper_starting', { request_id: request.request_id, wrapper_path: wrapperPath, agent_path: agentPath })

  const args = ['--request-file', requestPath, '--agent-path', agentPath]
  const child = spawn(resolveExecutable(wrapperPath), buildWrapperArgs(wrapperPath, args), {
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  })

  const stdout = []
  const stderr = []
  child.stdout.on('data', (data) => stdout.push(data.toString('utf8')))
  child.stderr.on('data', (data) => stderr.push(data.toString('utf8')))

  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject)
    child.on('exit', (code) => resolve(code))
  })

  const output = stdout.join('').trim().split(/\r?\n/).filter(Boolean).at(-1)
  const result = {
    exit_code: exitCode,
    stdout: stdout.join(''),
    stderr: stderr.join(''),
    result: output ? JSON.parse(output) : null,
    request: request,
    timestamp_utc: new Date().toISOString()
  }
  await log('wrapper_finished', { request_id: request.request_id, exit_code: exitCode, success: result.result?.success })
  return result
}

function resolveExecutable(wrapperPath) {
  return wrapperPath.toLowerCase().endsWith('.dll') ? 'dotnet' : wrapperPath
}

function buildWrapperArgs(wrapperPath, args) {
  return wrapperPath.toLowerCase().endsWith('.dll') ? [wrapperPath, ...args] : args
}

function parseWindowsArgs(input) {
  if (!input || !input.trim()) return []
  const args = []
  let current = ''
  let inQuotes = false
  for (let index = 0; index < input.length; index++) {
    const char = input[index]
    if (char === '"') {
      inQuotes = !inQuotes
      continue
    }
    if (/\s/.test(char) && !inQuotes) {
      if (current.length > 0) {
        args.push(current)
        current = ''
      }
      continue
    }
    current += char
  }
  if (current.length > 0) args.push(current)
  return args
}

const maximumLogTotalBytes = 500 * 1024 * 1024
const maximumLogFileBytes = 10 * 1024 * 1024

function trimLogs(roots, maximumBytes = maximumLogTotalBytes) {
  const files = new Map()
  function scan(root) {
    try {
      const rootStat = fs.lstatSync(root)
      if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) return
      for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
        const filePath = path.join(root, entry.name)
        if (entry.isSymbolicLink()) continue
        if (entry.isDirectory()) {
          scan(filePath)
        } else if (entry.isFile() && (
          entry.name === 'vnite-game-cache.jsonl' ||
          /^\d{8}(?:\.plugin)?(?:\.[a-f0-9]+)?\.jsonl$/i.test(entry.name) ||
          /\.robocopy\.log(?:\.progress\.json)?$/i.test(entry.name)
        )) {
          try {
            const stat = fs.statSync(filePath)
            files.set(path.resolve(filePath), { filePath, size: stat.size, modified: stat.mtimeMs })
          } catch { /* A concurrent logger may have rotated this file. */ }
        }
      }
    } catch { /* Missing or inaccessible directories are retried on the next write. */ }
  }
  for (const root of new Set(roots)) scan(root)
  let total = [...files.values()].reduce((sum, file) => sum + file.size, 0)
  for (const file of [...files.values()].sort((a, b) => a.modified - b.modified || a.filePath.localeCompare(b.filePath))) {
    if (total <= maximumBytes) break
    try {
      fs.unlinkSync(file.filePath)
      total -= file.size
    } catch { /* Locked files are retried on the next write. */ }
  }
}

function appendLog(filePath, line, roots, maximumBytes = maximumLogTotalBytes, fileLimit = maximumLogFileBytes) {
  try {
    fs.mkdirSync(path.dirname(filePath), { recursive: true })
    if (fs.existsSync(filePath) && fs.statSync(filePath).size + Buffer.byteLength(line, 'utf8') > fileLimit) {
      fs.renameSync(filePath, filePath.replace(/\.jsonl$/, `.${crypto.randomUUID().replace(/-/g, '')}.jsonl`))
    }
    fs.appendFileSync(filePath, line, 'utf8')
  } catch { /* Logging must not interrupt plugin actions. */ }
  trimLogs(roots, maximumBytes)
}

async function log(event, data) {
  const root = path.join(process.env.LOCALAPPDATA || os.tmpdir(), 'VniteGameCache')
  const logDir = path.join(root, 'plugin')
  const entry = {
    timestamp_utc: new Date().toISOString(),
    component: 'vnite-plugin',
    event,
    ...data
  }
  const day = new Date().toISOString().slice(0, 10).replace(/-/g, '')
  appendLog(path.join(logDir, `${day}.plugin.jsonl`), JSON.stringify(entry) + os.EOL,
    [logDir, path.join(root, 'logs')])
}

module.exports = {
  activate,
  deactivate,
  __test: {
    writeCommandResult,
    trimLogs,
    appendLog,
    maximumLogTotalBytes,
    detectExecutableInDirectory,
    scoreExecutableCandidate,
    resolveAdoptLaunchSource,
    findVgcTakeoverEvidence,
    valueLooksVgcTakeover,
    createClearedTakeoverLocalDoc,
    selectClearTakeoverTargets,
    isWrapperManagedLocalDocComplete,
    isInternalGameSnapshotCurrent,
    isGameLocalWriteVerified,
    sanitizeShortcutFileName,
    mapAdoptSkipToConsistencyStatus,
    buildLaunchArguments,
    parseLaunchArguments,
    normalizeLaunchGameId,
    archiveDiscoveryCandidates,
    isAutomaticArchiveExecutable
  }
}


