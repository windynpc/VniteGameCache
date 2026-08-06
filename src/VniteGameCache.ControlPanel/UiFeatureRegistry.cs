namespace VniteGameCache.ControlPanel;

public enum UiFeatureDangerLevel
{
    Normal,
    Destructive,
    Dangerous
}

public sealed record UiFeatureDefinition(
    string Id,
    string Name,
    string Page,
    string PrimaryEntry,
    string BackupEntry,
    bool SupportsSingleSelection,
    bool SupportsMultiSelection,
    string RequiredState,
    UiFeatureDangerLevel DangerLevel,
    bool MustExist);

public static class UiFeatureRegistry
{
    public static IReadOnlyList<UiFeatureDefinition> Features { get; } =
    [
        Feature("launch", "启动", "游戏管理", "启动", "右键菜单", true, false, "已接管", UiFeatureDangerLevel.Normal),
        Feature("diagnose", "诊断", "游戏管理", "诊断", "右键菜单 / 修复中心", true, true, "任意", UiFeatureDangerLevel.Normal),
        Feature("auto_repair", "自动修复", "游戏管理", "自动修复", "修复中心", true, true, "任意", UiFeatureDangerLevel.Normal),
        Feature("manual_repair", "手动修复", "游戏管理", "手动修复", "右键菜单", true, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("prepare_cache", "立即缓存", "游戏管理", "立即缓存", "右键菜单", true, true, "已接管", UiFeatureDangerLevel.Normal),
        Feature("verify_cache", "验证缓存", "清理 / 删除", "验证缓存", "右键菜单", true, false, "已缓存", UiFeatureDangerLevel.Normal),
        Feature("refresh_cache", "刷新缓存", "清理 / 删除", "刷新缓存", "右键菜单", true, false, "已接管", UiFeatureDangerLevel.Normal),
        Feature("clear_cache", "清除缓存", "清理 / 删除", "清除缓存", "缓存任务 / 右键菜单", true, true, "非运行中", UiFeatureDangerLevel.Destructive),
        Feature("delete_plugin_record", "删除插件记录", "清理 / 删除", "删除插件记录", "修复中心 / 右键菜单", true, true, "非运行中", UiFeatureDangerLevel.Destructive),
        Feature("clear_takeover_properties", "一键清除接管属性", "清理 / 删除", "一键清除接管属性", "修复中心 / 右键菜单", true, true, "接管残留/非运行中", UiFeatureDangerLevel.Destructive),
        Feature("clear_takeover_and_readopt", "清除后重新接管", "清理 / 删除", "清除后重新接管", "修复中心 / 右键菜单", true, true, "接管残留/非运行中", UiFeatureDangerLevel.Destructive),
        Feature("takeover_consistency_check", "检查接管一致性", "修复中心", "检查接管一致性", "批量重新接管", true, true, "任意", UiFeatureDangerLevel.Normal),
        Feature("safe_takeover_repair", "一键安全修复", "修复中心", "一键安全修复", "批量重新接管", true, true, "任意", UiFeatureDangerLevel.Normal),
        Feature("batch_readopt", "批量重新接管", "游戏管理", "批量重新接管", "修复中心", true, true, "任意", UiFeatureDangerLevel.Normal),
        Feature("unmanage", "解除接管", "清理 / 删除", "解除接管", "右键菜单", true, true, "已接管", UiFeatureDangerLevel.Destructive),
        Feature("readopt", "重新接管", "游戏管理", "自动修复", "待接管向导", true, true, "未接管/已解除", UiFeatureDangerLevel.Normal),
        Feature("suspend", "暂停接管", "更多操作", "暂停接管", "右键菜单", true, true, "已接管", UiFeatureDangerLevel.Destructive),
        Feature("ignore", "永久忽略", "清理 / 删除", "永久忽略", "右键菜单", true, true, "非运行中", UiFeatureDangerLevel.Destructive),
        Feature("unignore", "取消忽略", "清理 / 删除", "取消忽略", "右键菜单", true, false, "已忽略", UiFeatureDangerLevel.Normal),
        Feature("reset_current", "完全重置", "清理 / 删除", "完全重置当前游戏", "右键菜单", true, true, "非运行中", UiFeatureDangerLevel.Dangerous),
        Feature("delete_from_vnite", "从 Vnite 删除", "清理 / 删除", "从 Vnite 删除（高级危险操作）", "预览/跳转", true, false, "任意", UiFeatureDangerLevel.Dangerous),
        Feature("change_source_root", "修改根目录", "游戏管理", "双击游戏根目录", "手动修复", true, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("change_cache_root", "修改缓存目录", "游戏管理", "详情面板缓存目录", "清理 / 删除", true, false, "已接管", UiFeatureDangerLevel.Normal),
        Feature("change_executable", "修改启动程序", "游戏管理", "双击启动程序", "手动修复", true, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("change_launch_mode", "修改启动模式", "更多操作", "修改启动模式", "右键菜单", true, false, "已接管", UiFeatureDangerLevel.Normal),
        Feature("rebuild_shortcut", "重建快捷方式", "更多操作", "重建快捷方式", "自动修复", true, true, "已接管", UiFeatureDangerLevel.Normal),
        Feature("open_source", "打开源目录", "更多操作", "打开源目录", "右键菜单 / 双击源目录", true, false, "有源目录", UiFeatureDangerLevel.Normal),
        Feature("open_cache", "打开缓存目录", "更多操作", "打开缓存目录", "右键菜单 / 双击缓存目录", true, false, "有缓存目录", UiFeatureDangerLevel.Normal),
        Feature("open_logs", "打开日志", "操作记录", "打开日志目录", "设置与诊断", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("launch_session", "查看最近 Launch Session", "操作记录", "查看最近启动详情", "右键菜单", true, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("scan_obsolete", "扫描过期记录", "修复中心", "扫描过期记录", "清理 / 删除", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("scan_duplicates", "扫描重复游戏", "修复中心", "扫描重复游戏", "清理 / 删除", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("operation", "查看 Operation", "操作记录", "刷新记录", "操作记录", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("snapshot", "查看 Snapshot", "设置与诊断", "备份状态", "撤销上次操作", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("rollback", "Rollback", "设置与诊断", "撤销上次操作", "恢复状态", false, false, "任意", UiFeatureDangerLevel.Destructive),
        Feature("backup", "备份", "设置与诊断", "备份状态", "设置与诊断", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("restore", "恢复", "设置与诊断", "恢复状态", "设置与诊断", false, false, "任意", UiFeatureDangerLevel.Destructive),
        Feature("repair_install", "修复安装", "设置与诊断", "发布校验", "自检报告", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("self_check", "自检", "设置与诊断", "自检报告", "发布校验", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("adopt_diagnostics", "接管诊断", "修复中心", "打开接管诊断", "跳过问题中心", false, false, "任意", UiFeatureDangerLevel.Normal),
        Feature("maintenance", "维护模式", "设置与诊断", "维护模式", "设置与诊断", false, false, "任意", UiFeatureDangerLevel.Destructive)
    ];

    private static UiFeatureDefinition Feature(
        string id,
        string name,
        string page,
        string primaryEntry,
        string backupEntry,
        bool supportsSingleSelection,
        bool supportsMultiSelection,
        string requiredState,
        UiFeatureDangerLevel dangerLevel) =>
        new(id, name, page, primaryEntry, backupEntry, supportsSingleSelection, supportsMultiSelection, requiredState, dangerLevel, MustExist: true);
}
