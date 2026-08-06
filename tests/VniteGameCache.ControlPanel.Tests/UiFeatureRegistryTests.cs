using VniteGameCache.ControlPanel;

namespace VniteGameCache.ControlPanel.Tests;

public sealed class UiFeatureRegistryTests
{
    [Fact]
    public void RegistryContainsRequiredPhase72Entrypoints()
    {
        var requiredIds = new[]
        {
            "launch",
            "diagnose",
            "auto_repair",
            "manual_repair",
            "prepare_cache",
            "verify_cache",
            "refresh_cache",
            "clear_cache",
            "delete_plugin_record",
            "clear_takeover_properties",
            "clear_takeover_and_readopt",
            "takeover_consistency_check",
            "safe_takeover_repair",
            "batch_readopt",
            "unmanage",
            "readopt",
            "suspend",
            "ignore",
            "unignore",
            "reset_current",
            "delete_from_vnite",
            "change_source_root",
            "change_cache_root",
            "change_executable",
            "change_launch_mode",
            "rebuild_shortcut",
            "open_logs",
            "launch_session",
            "scan_obsolete",
            "scan_duplicates",
            "operation",
            "snapshot",
            "rollback",
            "backup",
            "restore",
            "self_check",
            "adopt_diagnostics",
            "maintenance"
        };

        var actualIds = UiFeatureRegistry.Features.Where(feature => feature.MustExist).Select(feature => feature.Id).ToHashSet();

        foreach (var requiredId in requiredIds)
        {
            Assert.Contains(requiredId, actualIds);
        }
    }

    [Fact]
    public void CleanupDeleteAndIgnoreAreSeparateFeatures()
    {
        var delete = UiFeatureRegistry.Features.Single(feature => feature.Id == "delete_plugin_record");
        var ignore = UiFeatureRegistry.Features.Single(feature => feature.Id == "ignore");

        Assert.NotEqual(delete.Id, ignore.Id);
        Assert.Equal("删除插件记录", delete.Name);
        Assert.Equal("永久忽略", ignore.Name);
        Assert.DoesNotContain("忽略", delete.PrimaryEntry);
        Assert.NotEqual(delete.PrimaryEntry, ignore.PrimaryEntry);
    }

    [Fact]
    public void ProgramExposesFixedCleanupDeleteButtonAndMenuEntries()
    {
        var source = File.ReadAllText(GetControlPanelProgramPath());

        foreach (var label in new[]
                 {
                     "清理 / 删除 ▼",
                     "清除缓存",
                     "删除插件记录",
                     "一键清除接管属性",
                     "清除后重新接管",
                     "检查接管一致性",
                     "一键安全修复",
                     "批量重新接管",
                     "仅删除插件记录",
                     "删除插件记录并清除接管属性",
                     "解除接管",
                     "永久忽略",
                     "取消忽略",
                     "完全重置当前游戏",
                     "从 Vnite 删除（高级危险操作）"
                 })
        {
            Assert.Contains(label, source);
        }
    }

    [Fact]
    public void ProgramExposesBatchCleanupDeleteEntries()
    {
        var source = File.ReadAllText(GetControlPanelProgramPath());

        foreach (var label in new[]
                 {
                     "批量清除缓存",
                     "批量扫描接管残留",
                     "批量清除接管属性",
                     "批量清除后重新接管",
                     "批量删除插件记录并清除接管属性",
                     "批量删除插件记录",
                     "批量解除接管",
                     "批量永久忽略",
                     "批量完全重置"
                 })
        {
            Assert.Contains(label, source);
        }
    }

    private static string GetControlPanelProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "VniteGameCache.ControlPanel", "Program.cs");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate src/VniteGameCache.ControlPanel/Program.cs from test output.");
    }
}
