using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using VniteGameCache.Caching;
using VniteGameCache.Contracts;
using VniteGameCache.Persistence;

namespace VniteGameCache.ControlPanel;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}

internal sealed class MainForm : Form
{
    private static readonly string[] MToolBatchNames = ["与工具一同启动.bat", "StartWithTool.bat"];
    private static readonly Regex MToolInjectCommand = new(
        "(?im)^\\s*\"(?<inject>[^\"\\r\\n]*loaders[\\\\/]+inject\\.exe)\"\\s+\"(?<game>[^\"\\r\\n]+\\.exe)\"\\s+\"(?<loader>[^\"\\r\\n]+\\.dll)\"\\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string[] _startupArgs;
    private readonly Label _pluginStatus = new();
    private readonly Label _settingsStatus = new();
    private readonly Label _databaseStatus = new();
    private readonly Label _cacheStatus = new();
    private readonly Label _recentStatus = new();
    private readonly Label _adoptStatus = new();
    private readonly Label _copyProgressStatus = new();
    private readonly DataGridView _gamesGrid = new();
    private readonly TextBox _detailsText = new();
    private readonly TextBox _searchBox = new();
    private readonly ComboBox _viewFilter = new();
    private readonly ComboBox _managedFilter = new();
    private readonly ComboBox _cacheFilter = new();
    private readonly ComboBox _launchFilter = new();
    private readonly Label _selectionStatus = new() { AutoSize = true, Padding = new Padding(0, 10, 8, 0) };
    private readonly FlowLayoutPanel _batchBar = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Visible = false };
    private readonly Button _databaseCard = new();
    private readonly Button _cachedCard = new();
    private readonly Button _pendingCard = new();
    private readonly Button _taskCard = new();
    private readonly Button _failedCard = new();
    private readonly Button _skippedCard = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _onlyIssues = new() { Text = "只看异常 / 未接管", AutoSize = true };
    private readonly ContextMenuStrip _gameMenu = new();
    private readonly ContextMenuStrip _cleanupDeleteMenu = new();
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _searchDebounceTimer = new() { Interval = 250 };
    private readonly string _logRoot = VgcSettingsStore.ResolveLogRoot();
    private readonly string _pluginLogRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "plugin");
    private readonly string _diagnosticsRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "diagnostics");
    private readonly string _commandsRoot = VgcSettingsStore.ResolveCommandsRoot();
    private readonly string _maintenanceModePath = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "config", "maintenance-mode.json");
    private readonly string _projectRoot = ResolveProjectRoot();
    private readonly string _uiConfigPath = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "config", "control-panel-ui.json");
    private readonly string _stateBackupRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "backups", "state");
    private List<GameRow> _allRows = [];
    private string? _lastUndoBackupPath;

    public MainForm(string[] args)
    {
        _startupArgs = args;
        Text = "VGC 控制台";
        Width = 1280;
        Height = 760;
        MinimumSize = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Vnite 游戏缓存状态",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true
        };

        _gamesGrid.Dock = DockStyle.Fill;
        _gamesGrid.ReadOnly = true;
        _gamesGrid.AllowUserToAddRows = false;
        _gamesGrid.AllowUserToDeleteRows = false;
        _gamesGrid.AutoGenerateColumns = false;
        _gamesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gamesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gamesGrid.MultiSelect = true;
        _gamesGrid.RowHeadersVisible = false;
        _gamesGrid.CellDoubleClick += (_, e) => HandleGridCellDoubleClick(e);
        _gamesGrid.SelectionChanged += (_, _) =>
        {
            UpdateDetailsPanel();
            UpdateBatchBar();
        };
        _gamesGrid.KeyDown += HandleGridKeyDown;
        _gamesGrid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || _gamesGrid.Rows[e.RowIndex].DataBoundItem is not GameRow row) return;
            e.ToolTipText = BuildCompactRowTooltip(row);
        };
        ConfigureGameGridColumns();
        ConfigureGameContextMenu();

        root.Controls.Add(title);
        root.Controls.Add(CreateStatusPanel());
        root.Controls.Add(CreateTabs());
        Controls.Add(root);
        _progressTimer.Tick += (_, _) => _copyProgressStatus.Text = ReadLatestCopyProgress();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyGameFilters();
        };
        FormClosed += (_, _) => _progressTimer.Stop();

        Shown += (_, _) =>
        {
            if (_startupArgs.Any(arg => arg.Equals("--adopt", StringComparison.OrdinalIgnoreCase)))
            {
                _onlyIssues.Checked = true;
            }

            LoadStatus();
            _progressTimer.Start();

            if (_startupArgs.Any(arg => arg.Equals("--cleanup", StringComparison.OrdinalIgnoreCase)))
            {
                ShowCleanupSuggestions();
            }
        };
    }

    private Control CreateStatusPanel()
    {
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 8)
        };
        statusPanel.Controls.Add(CreateStatusCards());
        statusPanel.Controls.Add(CreateStatusRow("插件状态", _pluginStatus));
        statusPanel.Controls.Add(CreateStatusRow("配置状态", _settingsStatus));
        statusPanel.Controls.Add(CreateStatusRow("数据库状态", _databaseStatus));
        statusPanel.Controls.Add(CreateStatusRow("缓存状态", _cacheStatus));
        statusPanel.Controls.Add(CreateStatusRow("最近活动", _recentStatus));
        statusPanel.Controls.Add(CreateStatusRow("接管诊断", _adoptStatus));
        statusPanel.Controls.Add(CreateStatusRow("复制进度", _copyProgressStatus));
        return statusPanel;
    }

    private Control CreateTabs()
    {
        _tabs.TabPages.Add(CreateGameManagementPage());
        _tabs.TabPages.Add(CreateTaskPage());
        _tabs.TabPages.Add(CreateRepairPage());
        _tabs.TabPages.Add(CreateOperationPage());
        _tabs.TabPages.Add(CreateSettingsPage());
        return _tabs;
    }

    private Control CreateStatusCards()
    {
        var cards = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6)
        };
        ConfigureStatusCard(_databaseCard, "数据库\n读取中", () => ApplyQuickView("全部游戏"));
        ConfigureStatusCard(_cachedCard, "已缓存\n读取中", () => ApplyQuickView("已缓存"));
        ConfigureStatusCard(_pendingCard, "待处理\n读取中", () => ApplyQuickView("需要处理"));
        ConfigureStatusCard(_taskCard, "后台任务\n读取中", () => _tabs.SelectedIndex = 1);
        ConfigureStatusCard(_failedCard, "启动失败\n读取中", () => ApplyQuickView("启动失败"));
        ConfigureStatusCard(_skippedCard, "跳过\n读取中", OpenSkippedProblemCenter);
        cards.Controls.AddRange([_databaseCard, _cachedCard, _pendingCard, _taskCard, _failedCard, _skippedCard]);
        return cards;
    }

    private static void ConfigureStatusCard(Button button, string text, Action action)
    {
        button.Text = text;
        button.Width = 132;
        button.Height = 54;
        button.TextAlign = ContentAlignment.MiddleCenter;
        button.Margin = new Padding(0, 0, 8, 4);
        button.Click += (_, _) => action();
        button.TabStop = true;
    }

    private TabPage CreateGameManagementPage()
    {
        var page = new TabPage("游戏管理");
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 860
        };

        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Controls.Add(CreateGameFilters(), 0, 0);
        left.Controls.Add(_gamesGrid, 0, 1);
        left.Controls.Add(CreateGameActionArea(), 0, 2);

        _detailsText.Dock = DockStyle.Fill;
        _detailsText.Multiline = true;
        _detailsText.ReadOnly = true;
        _detailsText.ScrollBars = ScrollBars.Vertical;
        _detailsText.Font = new Font("Consolas", 9);
        _detailsText.Text = "选择一个游戏查看详情。";

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(_detailsText);
        page.Controls.Add(split);
        return page;
    }

    private Control CreateGameFilters()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6)
        };
        _searchBox.Width = 260;
        _searchBox.PlaceholderText = "搜索游戏名、路径、启动程序";
        _searchBox.TextChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        };
        _onlyIssues.CheckedChanged += (_, _) => ApplyGameFilters();
        ConfigureFilter(_viewFilter, ["全部游戏", "需要处理", "接管残留", "未接管", "待接管", "未缓存", "已缓存", "启动失败", "目录失效", "重复记录", "已忽略", "已暂停", "最近游玩"]);
        ConfigureFilter(_managedFilter, ["全部管理状态", "已接管", "未接管"]);
        ConfigureFilter(_cacheFilter, ["全部缓存状态", "未缓存", "已缓存", "复制中", "验证中", "运行中", "失败"]);
        ConfigureFilter(_launchFilter, ["全部启动模式", "跟随默认", "直接启动", "翻译优先", "MTool"]);
        panel.Controls.Add(new Label { Text = "搜索", AutoSize = true, Padding = new Padding(0, 7, 4, 0) });
        panel.Controls.Add(_searchBox);
        panel.Controls.Add(_viewFilter);
        panel.Controls.Add(_onlyIssues);
        panel.Controls.Add(_managedFilter);
        panel.Controls.Add(_cacheFilter);
        panel.Controls.Add(_launchFilter);
        return panel;
    }

    private Control CreateGameActionArea()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, AutoSize = true };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CreateGameActionBar(), 0, 0);
        _batchBar.Controls.Add(_selectionStatus);
        _batchBar.Controls.Add(CreateButton("批量接管", (_, _) => BatchAdoptSelected()));
        _batchBar.Controls.Add(CreateButton("批量缓存", (_, _) => BatchCacheSelected()));
        _batchBar.Controls.Add(CreateButton("批量诊断", (_, _) => BatchDiagnoseSelected()));
        _batchBar.Controls.Add(CreateButton("批量重新接管", (_, _) => BatchReAdoptSelected()));
        _batchBar.Controls.Add(CreateButton("批量忽略", (_, _) => BatchSetManagementIntent(ManagementIntent.Ignored)));
        _batchBar.Controls.Add(CreateButton("批量暂停", (_, _) => BatchSetManagementIntent(ManagementIntent.Suspended)));
        _batchBar.Controls.Add(CreateButton("批量清除缓存", (_, _) => BatchClearCacheSelected()));
        _batchBar.Controls.Add(CreateButton("批量清除接管属性", (_, _) => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false)));
        _batchBar.Controls.Add(CreateButton("取消选择", (_, _) => _gamesGrid.ClearSelection()));
        layout.Controls.Add(_batchBar, 0, 1);
        return layout;
    }

    private static void ConfigureFilter(ComboBox comboBox, IEnumerable<string> items)
    {
        comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBox.Width = 130;
        foreach (var item in items) comboBox.Items.Add(item);
        comboBox.SelectedIndex = 0;
        comboBox.SelectedIndexChanged += (_, _) =>
        {
            if (comboBox.FindForm() is MainForm form) form.ApplyGameFilters();
        };
    }

    private Control CreateGameActionBar()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };
        buttons.Controls.Add(CreateButton("启动", (_, _) => LaunchSelectedGame()));
        buttons.Controls.Add(CreateButton("诊断", (_, _) => DiagnoseSelectedGame()));
        buttons.Controls.Add(CreateButton("自动修复", (_, _) => RequestAdoptRepair()));
        buttons.Controls.Add(CreateButton("手动修复", (_, _) => ManualRepairSelectedGame()));
        buttons.Controls.Add(CreateButton("启动程序", (_, _) => OpenExecutableManagerSelectedGame()));
        buttons.Controls.Add(CreateButton("立即缓存", (_, _) => PrepareSelectedGameCache()));
        buttons.Controls.Add(CreateButton("清理 / 删除 ▼", (sender, _) =>
        {
            if (sender is Control control) ShowCleanupDeleteMenu(control);
        }));
        var more = CreateButton("更多操作", (sender, _) =>
        {
            if (sender is Control control) _gameMenu.Show(control, new Point(0, control.Height));
        });
        buttons.Controls.Add(more);
        return buttons;
    }

    private TabPage CreateTaskPage()
    {
        var page = new TabPage("缓存任务");
        var text = CreateReadonlyPageText();
        text.Text = BuildTaskPageText();
        var buttons = CreatePageButtonBar(
            ("刷新任务", () => text.Text = BuildTaskPageText()),
            ("清理建议", ShowCleanupSuggestions),
            ("清除选中缓存", ClearSelectedCache));
        page.Controls.Add(CreatePageLayout(text, buttons));
        return page;
    }

    private TabPage CreateRepairPage()
    {
        var page = new TabPage("修复中心");
        var text = CreateReadonlyPageText();
        text.Text = BuildRepairPageText();
        var buttons = CreatePageButtonBar(
            ("检查接管一致性", () => RequestTakeoverConsistency(apply: false)),
            ("一键安全修复", () => RequestTakeoverConsistency(apply: true)),
            ("批量重新接管", () => BatchReAdoptSelected()),
            ("打开一致性诊断", OpenTakeoverConsistencyDiagnosticsReport),
            ("打开接管诊断", OpenAdoptDiagnosticsReport),
            ("打开接管残留诊断", OpenTakeoverResidueDiagnosticsReport),
            ("一键修复接管", RequestAdoptRepair),
            ("一键清除接管属性", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false)),
            ("清除后重新接管", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false)),
            ("扫描过期记录", ScanObsoleteRecords),
            ("扫描重复游戏", ScanDuplicateGames),
            ("仅删除插件记录", DeleteSelectedRecord));
        page.Controls.Add(CreatePageLayout(text, buttons));
        return page;
    }

    private TabPage CreateOperationPage()
    {
        var page = new TabPage("操作记录");
        var text = CreateReadonlyPageText();
        text.Text = BuildOperationPageText();
        var buttons = CreatePageButtonBar(
            ("刷新记录", () => text.Text = BuildOperationPageText()),
            ("查看最近启动详情", OpenLatestLaunchDetail),
            ("打开日志目录", () => OpenFolder(_logRoot)));
        page.Controls.Add(CreatePageLayout(text, buttons));
        return page;
    }

    private TabPage CreateSettingsPage()
    {
        var page = new TabPage("设置与诊断");
        var text = CreateReadonlyPageText();
        text.Text = BuildSettingsPageText();
        var buttons = CreatePageButtonBar(
            ("刷新", () => { LoadStatus(); text.Text = BuildSettingsPageText(); }),
            ("维护模式", ToggleMaintenanceMode),
            ("备份状态", BackupVgcState),
            ("恢复状态", RestoreVgcState),
            ("撤销上次操作", UndoLastOperation),
            ("发布校验", RunReleaseValidation),
            ("自检报告", OpenHealthCheckReport),
            ("打开项目目录", () => OpenFolder(_projectRoot)),
            ("打开日志目录", () => OpenFolder(_logRoot)),
            ("打开 MTool/MCenter", OpenMToolCenter),
            ("生成 MTool Bridge 探针", CreateMToolBridgeProbe),
            ("打开 MTool 链接目录", OpenMToolLinksRoot));
        page.Controls.Add(CreatePageLayout(text, buttons));
        return page;
    }

    private static TextBox CreateReadonlyPageText() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9)
    };

    private static Control CreatePageLayout(Control content, Control buttons)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(content, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        return layout;
    }

    private static Control CreatePageButtonBar(params (string Text, Action Action)[] actions)
    {
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        foreach (var action in actions)
        {
            buttons.Controls.Add(CreateButton(action.Text, (_, _) => action.Action()));
        }

        return buttons;
    }

    private static Control CreateStatusRow(string title, Label value)
    {
        value.AutoSize = true;
        value.Dock = DockStyle.Fill;
        value.Text = "读取中...";

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 3, 0, 3)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold) }, 0, 0);
        row.Controls.Add(value, 1, 0);
        return row;
    }

    private static Button CreateButton(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 4),
            Margin = new Padding(6)
        };
        button.Click += onClick;
        return button;
    }

    private void ConfigureGameGridColumns()
    {
        _gamesGrid.Columns.Clear();
        AddColumn(nameof(GameRow.StatusIcon), "状态", 54);
        AddColumn(nameof(GameRow.Name), "游戏名称", 230);
        AddColumn(nameof(GameRow.ManagedStatus), "管理状态", 90);
        AddColumn(nameof(GameRow.AdoptStatus), "接管状态", 110);
        AddColumn(nameof(GameRow.CacheStatus), "缓存状态", 90);
        AddColumn(nameof(GameRow.LaunchMode), "启动模式", 100);
        AddColumn(nameof(GameRow.Issue), "问题", 260);
        AddColumn(nameof(GameRow.LastPlayed), "最近游玩", 125);
        AddColumn(nameof(GameRow.CacheMb), "缓存大小(MB)", 110);
        AddColumn(nameof(GameRow.GameId), "Game ID", 170, false);
        AddColumn(nameof(GameRow.VniteId), "Vnite ID", 150, false);
        AddColumn(nameof(GameRow.LaunchStatus), "启动状态", 110, false);
        AddColumn(nameof(GameRow.SourceRoot), "游戏根目录", 240, false);
        AddColumn(nameof(GameRow.CacheRoot), "缓存目录", 240, false);
        AddColumn(nameof(GameRow.ExecutablePath), "启动程序", 220, false);
        AddColumn(nameof(GameRow.ShortcutPath), "启动快捷方式", 220, false);
        AddColumn(nameof(GameRow.CandidatePrograms), "候选启动程序", 220, false);
    }

    private void AddColumn(string propertyName, string headerText, int width, bool visible = true)
    {
        _gamesGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            Name = propertyName,
            Width = width,
            Visible = visible,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
    }

    private void ConfigureGameContextMenu()
    {
        _gamesGrid.ContextMenuStrip = _gameMenu;
        _gameMenu.Opening += (_, _) => RebuildGameContextMenu();
    }

    private void AddMenuItem(string text, Action action)
    {
        _gameMenu.Items.Add(text, null, (_, _) => action());
    }

    private void ShowCleanupDeleteMenu(Control anchor)
    {
        _cleanupDeleteMenu.Items.Clear();
        var selected = GetSelectedRows();
        if (selected.Count > 1)
        {
            AddCleanupMenuItem($"批量清除缓存（{selected.Count} 个）", BatchClearCacheSelected, selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量扫描接管残留（Dry Run，{selected.Count} 个）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: true), selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量清除接管属性（{selected.Count} 个）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false), selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量清除后重新接管（{selected.Count} 个）", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false), selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量删除插件记录并清除接管属性（推荐，{selected.Count} 个）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: true, dryRun: false), selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量仅删除插件记录（{selected.Count} 个）", BatchDeleteSelectedRecords, selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量解除接管（{selected.Count} 个）", () => BatchSetManagementIntent(ManagementIntent.Unmanaged), selected.Any(row => row.ManagedStatus == "已接管" && !IsRowRunning(row)));
            AddCleanupMenuItem($"批量永久忽略（{selected.Count} 个）", () => BatchSetManagementIntent(ManagementIntent.Ignored), selected.Any(row => !IsRowRunning(row)));
            AddCleanupMenuItem($"批量完全重置（{selected.Count} 个）", BatchDeleteSelectedRecords, selected.Any(row => !IsRowRunning(row)));
            _cleanupDeleteMenu.Show(anchor, new Point(0, anchor.Height));
            return;
        }

        var row = selected.FirstOrDefault() ?? GetSelectedRow();
        if (row is null)
        {
            AddCleanupMenuItem("请先选择一个游戏", () => { }, enabled: false);
            _cleanupDeleteMenu.Show(anchor, new Point(0, anchor.Height));
            return;
        }

        var isRunning = IsRowRunning(row);
        if (isRunning)
        {
            AddCleanupMenuItem("游戏正在运行，停止后才能执行危险操作", () => { }, enabled: false);
            _cleanupDeleteMenu.Items.Add(new ToolStripSeparator());
        }

        AddCleanupMenuItem("清除缓存", ClearSelectedCache, !isRunning && CanClearCache(row));
        AddCleanupMenuItem("一键清除接管属性", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false), !isRunning);
        AddCleanupMenuItem("清除后重新接管", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false), !isRunning);
        AddCleanupMenuItem("删除插件记录并清除接管属性（推荐）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: true, dryRun: false), !isRunning);
        AddCleanupMenuItem("仅删除插件记录", DeleteSelectedRecord, !isRunning && !string.IsNullOrWhiteSpace(row.GameId));
        AddCleanupMenuItem("解除接管", () => SetSelectedManagementIntent(ManagementIntent.Unmanaged), !isRunning && row.ManagedStatus == "已接管" && row.ManagementIntent is not ManagementIntent.Ignored);
        if (row.ManagementIntent == ManagementIntent.Ignored)
        {
            AddCleanupMenuItem("取消忽略", () => SetSelectedManagementIntent(ManagementIntent.Managed), !isRunning);
        }
        else
        {
            AddCleanupMenuItem("永久忽略", () => SetSelectedManagementIntent(ManagementIntent.Ignored), !isRunning);
        }

        AddCleanupMenuItem("完全重置当前游戏", ResetSelectedGame, !isRunning && !string.IsNullOrWhiteSpace(row.GameId));
        _cleanupDeleteMenu.Items.Add(new ToolStripSeparator());
        AddCleanupMenuItem("从 Vnite 删除（高级危险操作）", ShowDeleteFromVnitePreview, !isRunning);
        _cleanupDeleteMenu.Show(anchor, new Point(0, anchor.Height));
    }

    private void AddCleanupMenuItem(string text, Action action, bool enabled)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => action();
        _cleanupDeleteMenu.Items.Add(item);
    }

    private static bool IsRowRunning(GameRow row) =>
        row.CacheStatus == "运行中" ||
        row.LaunchStatus.Contains("running", StringComparison.OrdinalIgnoreCase) ||
        row.LaunchStatus.Contains("运行", StringComparison.OrdinalIgnoreCase);

    private static bool CanClearCache(GameRow row) =>
        !string.IsNullOrWhiteSpace(row.CacheRoot) || row.CacheStatus is "已缓存" or "失败" or "需刷新" or "不完整";

    private void RebuildGameContextMenu()
    {
        _gameMenu.Items.Clear();
        var selected = GetSelectedRows();
        if (selected.Count > 1)
        {
            AddMenuItem("批量接管", BatchAdoptSelected);
            AddMenuItem("批量缓存", BatchCacheSelected);
            AddMenuItem("批量诊断", BatchDiagnoseSelected);
            AddMenuItem("批量忽略", () => BatchSetManagementIntent(ManagementIntent.Ignored));
            AddMenuItem("批量暂停", () => BatchSetManagementIntent(ManagementIntent.Suspended));
            AddMenuItem("批量清除缓存", BatchClearCacheSelected);
            AddMenuItem("批量扫描接管残留（Dry Run）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: true));
            AddMenuItem("批量清除接管属性", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false));
            AddMenuItem("批量清除后重新接管", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false));
            AddMenuItem("批量删除插件记录并清除接管属性（推荐）", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: true, dryRun: false));
            AddMenuItem("批量仅删除插件记录", BatchDeleteSelectedRecords);
            AddMenuItem("批量解除接管", () => BatchSetManagementIntent(ManagementIntent.Unmanaged));
            return;
        }

        var row = selected.FirstOrDefault() ?? GetSelectedRow();
        if (row is null)
        {
            AddMenuItem("刷新", LoadStatus);
            return;
        }

        if (row.ManagedStatus != "已接管")
        {
            AddMenuItem("接管", RequestAdoptRepair);
            AddMenuItem("打开待接管向导", OpenOnboardingWizard);
            AddMenuItem("启动程序管理", OpenExecutableManagerSelectedGame);
            AddMenuItem("选择启动程序", ManualRepairSelectedGame);
            AddMenuItem("选择游戏根目录", ManualRepairSelectedGame);
            AddMenuItem("清除接管属性", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false));
            AddMenuItem("清除后重新检测", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false));
            AddMenuItem("永久忽略", () => BatchSetManagementIntent(ManagementIntent.Ignored));
            if (!string.IsNullOrWhiteSpace(row.SourceRoot)) AddMenuItem("打开源目录", OpenSelectedSourceRoot);
            AddMenuItem("查看诊断", DiagnoseSelectedGame);
            AddMenuItem("删除插件记录", DeleteSelectedRecord);
            return;
        }

        if (row.ManagementIntent == ManagementIntent.Ignored)
        {
            AddMenuItem("取消忽略", () => SetSelectedManagementIntent(ManagementIntent.Managed));
            AddMenuItem("打开源目录", OpenSelectedSourceRoot);
            AddMenuItem("查看历史", OpenLatestLaunchDetail);
            AddMenuItem("删除插件记录", DeleteSelectedRecord);
            AddMenuItem("完全重置", DeleteSelectedRecord);
            return;
        }

        if (row.ManagementIntent == ManagementIntent.Suspended)
        {
            AddMenuItem("恢复管理", () => SetSelectedManagementIntent(ManagementIntent.Managed));
            AddMenuItem("查看诊断", DiagnoseSelectedGame);
            AddMenuItem("打开源目录", OpenSelectedSourceRoot);
            AddMenuItem("永久忽略", () => SetSelectedManagementIntent(ManagementIntent.Ignored));
            AddMenuItem("删除插件记录", DeleteSelectedRecord);
            AddMenuItem("完全重置", DeleteSelectedRecord);
            return;
        }

        AddMenuItem("启动", LaunchSelectedGame);
        if (row.CacheStatus is "" or "未缓存" or "不完整" or "需刷新" or "失败") AddMenuItem("立即缓存", PrepareSelectedGameCache);
        if (row.CacheStatus is "已缓存" or "失败" or "需刷新")
        {
            AddMenuItem("验证缓存", DiagnoseSelectedGame);
            AddMenuItem("刷新缓存", PrepareSelectedGameCache);
            AddMenuItem("清除缓存", ClearSelectedCache);
            AddMenuItem("打开缓存目录", OpenSelectedCacheRoot);
        }
        AddMenuItem("诊断", DiagnoseSelectedGame);
        AddMenuItem("自动修复", RequestAdoptRepair);
        AddMenuItem("手动修复", ManualRepairSelectedGame);
        AddMenuItem("清除接管属性", () => RequestClearTakeoverProperties(readopt: false, deletePluginRecord: false, dryRun: false));
        AddMenuItem("清除后重新检测", () => RequestClearTakeoverProperties(readopt: true, deletePluginRecord: false, dryRun: false));
        AddMenuItem("修改启动模式", ChangeSelectedLaunchMode);
        AddMenuItem("打开 MTool/MCenter", OpenMToolCenter);
        AddMenuItem("打开选中游戏 MTool 模板", OpenSelectedMToolTemplate);
        AddMenuItem("修改启动程序", ManualRepairSelectedGame);
        AddMenuItem("解除接管", () => SetSelectedManagementIntent(ManagementIntent.Unmanaged));
        AddMenuItem("暂停接管", () => SetSelectedManagementIntent(ManagementIntent.Suspended));
        AddMenuItem("同步存档", ImportSaveForSelectedGame);
        AddMenuItem("查看最近启动", OpenLatestLaunchDetail);
        AddMenuItem("删除插件记录", DeleteSelectedRecord);
    }

    private void LoadStatus()
    {
        try
        {
            var settingsHealth = VgcSettingsStore.LoadWithDiagnostics();
            SetSettingsStatus(settingsHealth);
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            var migrations = new MigrationRunner(factory);
            migrations.Initialize();

            var games = new GameRepository(factory).List();
            var installedVersion = ReadInstalledPluginVersion();
            var latestActivity = ReadLatestActivity();
            var copyProgress = ReadLatestCopyProgress();
            var diagnostics = ReadAdoptDiagnostics();
            var residueDiagnostics = ReadTakeoverResidueDiagnostics();
            var launchStates = ReadLaunchStates();
            var totalCacheBytes = games.Sum(game => game.CacheSizeBytes);
            var readyCount = games.Count(game => game.CacheStatus == CacheStatus.Ready);
            var runningCount = games.Count(game => game.CacheStatus == CacheStatus.Running);
            var failedCount = games.Count(game => game.CacheStatus == CacheStatus.Failed);
            var rows = BuildRows(games, diagnostics, residueDiagnostics, launchStates);
            if (_onlyIssues.Checked)
            {
                rows = rows.Where(row => !string.IsNullOrWhiteSpace(row.Issue) || row.ManagedStatus != "已接管").ToList();
            }

            _pluginStatus.Text = installedVersion is null
                ? "未发现已安装插件"
                : $"已安装 {installedVersion}；维护模式 {(IsMaintenanceModeEnabled() ? "已开启" : "关闭")}；如果 Vnite 已启动，插件会在启动时自动激活";
            _databaseStatus.Text = $"{databaseOptions.DatabasePath}；游戏 {games.Count} 个；Schema {Schema.CurrentVersion}";
            _cacheStatus.Text = $"Ready {readyCount}，Running {runningCount}，Failed {failedCount}，缓存占用 {ToMb(totalCacheBytes):N0} MB";
            _recentStatus.Text = latestActivity;
            _adoptStatus.Text = diagnostics is null
                ? "暂无接管诊断；点击“一键修复接管”或重启 Vnite 后生成"
                : $"{TranslateAdoptStatus(diagnostics.Status)}；候选 {diagnostics.CandidateCount}，已接管 {diagnostics.AdoptedCount}，剩余 {diagnostics.RemainingCount}，跳过 {diagnostics.Skipped.Count}，根目录 {diagnostics.Roots.Count} 个，更新时间 {diagnostics.TimestampUtc}";
            _copyProgressStatus.Text = copyProgress;
            _databaseCard.Text = $"数据库\n{games.Count:N0} 个游戏";
            _cachedCard.Text = $"已缓存\n{readyCount:N0} 个 / {ToMb(totalCacheBytes):N0} MB";
            _pendingCard.Text = $"待处理\n{rows.Count(row => row.ManagedStatus != "已接管" || !string.IsNullOrWhiteSpace(row.Issue)):N0} 个";
            _taskCard.Text = $"后台任务\n{runningCount:N0} 个运行";
            _failedCard.Text = $"启动失败\n{rows.Count(row => row.LaunchStatus.Contains("失败", StringComparison.OrdinalIgnoreCase) || row.CacheStatus == "失败"):N0} 个";
            _skippedCard.Text = $"跳过/残留\n{(diagnostics?.Skipped.Count ?? 0) + (residueDiagnostics?.Residues.Count ?? 0):N0} 个";
            _allRows = rows;
            ApplyGameFilters();
        }
        catch (Exception error)
        {
            _pluginStatus.Text = "读取失败";
            if (string.IsNullOrWhiteSpace(_settingsStatus.Text) || _settingsStatus.Text == "读取中...")
            {
                _settingsStatus.Text = "读取失败";
                _settingsStatus.ForeColor = Color.Firebrick;
            }
            _databaseStatus.Text = error.Message;
            _cacheStatus.Text = "未知";
            _recentStatus.Text = "请先确认插件已安装，并至少启动过一次 Vnite";
            _adoptStatus.Text = "未知";
            _copyProgressStatus.Text = "未知";
            _allRows = [];
            _gamesGrid.DataSource = Array.Empty<GameRow>();
            UpdateDetailsPanel();
        }
    }

    private void ApplyGameFilters()
    {
        var rows = _allRows.AsEnumerable();
        var query = _searchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = rows.Where(row =>
                Contains(row.Name, query) ||
                Contains(row.Issue, query) ||
                Contains(row.SourceRoot, query) ||
                Contains(row.CacheRoot, query) ||
                Contains(row.ExecutablePath, query) ||
                Contains(row.CandidatePrograms, query));
        }

        if (_onlyIssues.Checked)
        {
            rows = rows.Where(row => !string.IsNullOrWhiteSpace(row.Issue) || row.ManagedStatus != "已接管");
        }

        rows = ApplyComboFilter(rows, _managedFilter, "全部管理状态", row => row.ManagedStatus);
        rows = ApplyComboFilter(rows, _cacheFilter, "全部缓存状态", row => row.CacheStatus);
        rows = ApplyComboFilter(rows, _launchFilter, "全部启动模式", row => row.LaunchMode);
        rows = ApplyViewFilter(rows, _viewFilter.SelectedItem?.ToString() ?? "全部游戏");
        _gamesGrid.DataSource = rows.ToList();
        UpdateDetailsPanel();
        UpdateBatchBar();
    }

    private static IEnumerable<GameRow> ApplyViewFilter(IEnumerable<GameRow> rows, string view) => view switch
    {
        "需要处理" => rows.Where(row => row.ManagedStatus != "已接管" || !string.IsNullOrWhiteSpace(row.Issue) || row.LaunchStatus.Contains("失败", StringComparison.OrdinalIgnoreCase)),
        "接管残留" => rows.Where(row => row.AdoptStatus.Contains("接管残留", StringComparison.OrdinalIgnoreCase) || row.Issue.Contains("接管残留", StringComparison.OrdinalIgnoreCase)),
        "未接管" => rows.Where(row => row.ManagedStatus != "已接管"),
        "待接管" => rows.Where(row => row.AdoptStatus.Contains("待", StringComparison.OrdinalIgnoreCase) || row.AdoptStatus.Contains("需", StringComparison.OrdinalIgnoreCase)),
        "未缓存" => rows.Where(row => row.CacheStatus is "" or "未缓存"),
        "已缓存" => rows.Where(row => row.CacheStatus == "已缓存"),
        "启动失败" => rows.Where(row => row.LaunchStatus.Contains("失败", StringComparison.OrdinalIgnoreCase) || row.CacheStatus == "失败"),
        "目录失效" => rows.Where(row => row.Issue.Contains("目录", StringComparison.CurrentCultureIgnoreCase) || row.Issue.Contains("不存在", StringComparison.CurrentCultureIgnoreCase)),
        "重复记录" => rows.GroupBy(row => Normalize(row.SourceRoot), StringComparer.OrdinalIgnoreCase).Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1).SelectMany(group => group),
        "已忽略" => rows.Where(row => row.ManagementIntent == ManagementIntent.Ignored),
        "已暂停" => rows.Where(row => row.ManagementIntent == ManagementIntent.Suspended),
        "最近游玩" => rows.Where(row => !string.IsNullOrWhiteSpace(row.LastPlayed)).OrderByDescending(row => row.LastPlayed),
        _ => rows
    };

    private void ApplyQuickView(string view)
    {
        if (_viewFilter.Items.Contains(view))
        {
            _viewFilter.SelectedItem = view;
        }
        else
        {
            ApplyGameFilters();
        }
        _tabs.SelectedIndex = 0;
    }

    private static IEnumerable<GameRow> ApplyComboFilter(
        IEnumerable<GameRow> rows,
        ComboBox comboBox,
        string allText,
        Func<GameRow, string> selector)
    {
        var selected = comboBox.SelectedItem?.ToString();
        return string.IsNullOrWhiteSpace(selected) || selected == allText
            ? rows
            : rows.Where(row => string.Equals(selector(row), selected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Contains(string value, string query) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void SetSettingsStatus(VgcSettingsLoadResult health)
    {
        switch (health.Status)
        {
            case VgcSettingsHealthStatus.Healthy:
                _settingsStatus.ForeColor = SystemColors.ControlText;
                _settingsStatus.Text = $"正常；{health.SettingsPath}；更新时间 {health.Settings.UpdatedUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                break;
            case VgcSettingsHealthStatus.Missing:
                _settingsStatus.ForeColor = Color.DarkOrange;
                _settingsStatus.Text = $"未生成共享配置；启动一次 Vnite 插件后自动创建：{health.SettingsPath}";
                break;
            default:
                _settingsStatus.ForeColor = Color.Firebrick;
                _settingsStatus.Text = $"异常（{health.Status}）；已阻止静默回退：{health.SettingsPath}";
                break;
        }
    }

    private static List<GameRow> BuildRows(
        IReadOnlyCollection<GameRecord> games,
        AdoptDiagnostics? diagnostics,
        TakeoverResidueDiagnostics? residueDiagnostics,
        IReadOnlyDictionary<string, string> launchStates)
    {
        var sourceRoots = new HashSet<string>(games.Select(game => Normalize(game.SourceRoot)), StringComparer.OrdinalIgnoreCase);
        var knownGameIds = new HashSet<string>(games.Select(game => game.GameId), StringComparer.OrdinalIgnoreCase);
        var knownVniteIds = new HashSet<string>(
            games.Select(game => game.VniteGameId ?? "").Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        var skippedByGameId = diagnostics?.Skipped
            .Where(item => !string.IsNullOrWhiteSpace(item.GameId))
            .GroupBy(item => item.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, SkippedAdoptItem>(StringComparer.OrdinalIgnoreCase);
        var skippedBySourceRoot = diagnostics?.Skipped
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceRoot))
            .GroupBy(item => Normalize(item.SourceRoot), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, SkippedAdoptItem>(StringComparer.OrdinalIgnoreCase);

        var rows = games
            .Select(game =>
            {
                var managedIssue = DescribeManagedIssue(game);
                var diagnostic = skippedByGameId.GetValueOrDefault(game.GameId)
                    ?? (!string.IsNullOrWhiteSpace(game.VniteGameId) ? skippedByGameId.GetValueOrDefault(game.VniteGameId) : null)
                    ?? skippedBySourceRoot.GetValueOrDefault(Normalize(game.SourceRoot));
                var diagnosticIssue = diagnostic is null ? "" : TranslateSkipReason(diagnostic.Reason, diagnostic.Candidates.Count);
                var issue = string.IsNullOrWhiteSpace(diagnosticIssue) ? managedIssue : diagnosticIssue;
                var hasLaunchSource = HasUsableLaunchSource(game);
                var launcherShortcutPath = FindLauncherShortcutPath(game.GameId);
                var hasVerifiedVgcLauncher = !string.IsNullOrWhiteSpace(launcherShortcutPath) && File.Exists(launcherShortcutPath);
                if (hasLaunchSource && diagnostic is null && !hasVerifiedVgcLauncher)
                {
                    issue = AppendIssue(issue, "缺少 VGC 启动快捷方式或 Vnite 启动字段未验证");
                }

                var managedStatus = hasLaunchSource && diagnostic is null && hasVerifiedVgcLauncher ? "已接管" : "未接管";
                var adoptStatus = managedStatus == "已接管"
                    ? (string.IsNullOrWhiteSpace(issue) ? "正常" : "需处理")
                    : "待接管";
                var candidatePrograms = diagnostic is null
                    ? FindCandidatePrograms(game.SourceRoot, game.DisplayName)
                    : string.Join("; ", diagnostic.Candidates.Take(5));

                return new GameRow(
                    ResolveStatusIcon(game, launchStates.TryGetValue(game.GameId, out var launchStateForIcon) ? launchStateForIcon : ""),
                    game.DisplayName,
                    game.GameId,
                    game.VniteGameId ?? "",
                    game.ManagementIntent,
                    managedStatus,
                    adoptStatus,
                    TranslateCacheStatus(game.CacheStatus),
                    launchStates.TryGetValue(game.GameId, out var launchState) ? launchState : "",
                    issue,
                    TranslateLaunchModeOverride(game.LaunchModeOverride),
                    ToMb(game.CacheSizeBytes),
                    game.LastPlayedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
                    game.SourceRoot,
                    game.CacheRoot,
                    game.ExecutablePath,
                    launcherShortcutPath,
                    candidatePrograms);
            })
            .ToList();

        if (diagnostics is not null)
        {
            rows.AddRange(diagnostics.Skipped
                .Where(item => !string.IsNullOrWhiteSpace(item.SourceRoot))
                .Where(item => !sourceRoots.Contains(Normalize(item.SourceRoot)))
                .Select(item => new GameRow(
                    "⚠",
                    item.Name,
                    item.GameId,
                    item.GameId,
                    ManagementIntent.Unmanaged,
                    "未接管",
                    "待接管",
                    "",
                    "",
                    TranslateSkipReason(item.Reason, item.Candidates.Count),
                    "",
                    0,
                    "",
                    item.SourceRoot,
                    "",
                    "",
                    "",
                    string.Join("; ", item.Candidates.Take(5)))));
        }

        if (residueDiagnostics is not null)
        {
            rows.AddRange(residueDiagnostics.Residues
                .Where(item => !knownGameIds.Contains(item.GameId) && !knownVniteIds.Contains(item.GameId))
                .Select(item => new GameRow(
                    "⚠",
                    item.Name,
                    item.GameId,
                    item.GameId,
                    ManagementIntent.Unmanaged,
                    "未接管",
                    "接管残留",
                    "",
                    "",
                    "接管残留：Vnite 启动属性仍指向 VGC，但 SQLite/PluginDB/快捷方式不完整",
                    "",
                    0,
                    "",
                    item.SourceRoot,
                    "",
                    item.LauncherPath,
                    item.ShortcutPath,
                    string.Join("; ", item.Evidence.Take(5)))));
        }

        return rows
            .OrderByDescending(row => row.ManagedStatus != "已接管")
            .ThenByDescending(row => !string.IsNullOrWhiteSpace(row.Issue))
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ResolveStatusIcon(GameRecord game, string launchState)
    {
        if (game.ManagementIntent == ManagementIntent.Ignored) return "⊘";
        if (game.ManagementIntent == ManagementIntent.Suspended) return "⏸";
        if (game.CacheStatus == CacheStatus.Running) return "▶";
        if (game.CacheStatus == CacheStatus.Ready) return "✓";
        if (game.CacheStatus == CacheStatus.Failed || launchState.Contains("failed", StringComparison.OrdinalIgnoreCase)) return "!";
        if (!string.IsNullOrWhiteSpace(DescribeManagedIssue(game))) return "⚠";
        return "·";
    }

    private static string DescribeManagedIssue(GameRecord game)
    {
        if (game.CacheStatus == CacheStatus.Failed) return "缓存失败，查看最近日志";
        if (string.IsNullOrWhiteSpace(game.SourceRoot)) return "缺少游戏本体目录";
        if (!Directory.Exists(game.SourceRoot)) return "游戏本体目录不存在或不可访问";
        if (string.IsNullOrWhiteSpace(game.ExecutablePath)) return "缺少真实启动程序";
        if (!File.Exists(game.ExecutablePath)) return "真实启动程序不存在或不可访问";
        if (string.IsNullOrWhiteSpace(game.CacheRoot)) return "缺少缓存目录";
        return "";
    }

    private static string AppendIssue(string existing, string issue)
    {
        if (string.IsNullOrWhiteSpace(issue)) return existing;
        if (string.IsNullOrWhiteSpace(existing)) return issue;
        return existing.Contains(issue, StringComparison.OrdinalIgnoreCase) ? existing : $"{existing}；{issue}";
    }

    private static bool HasUsableLaunchSource(GameRecord game) =>
        !string.IsNullOrWhiteSpace(game.SourceRoot) &&
        Directory.Exists(game.SourceRoot) &&
        !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
        File.Exists(game.ExecutablePath);

    private static string TranslateCacheStatus(CacheStatus status) => status switch
    {
        CacheStatus.NotCached => "未缓存",
        CacheStatus.Checking => "检查中",
        CacheStatus.Copying => "复制中",
        CacheStatus.Verifying => "验证中",
        CacheStatus.Ready => "已缓存",
        CacheStatus.Incomplete => "不完整",
        CacheStatus.Stale => "需刷新",
        CacheStatus.Running => "运行中",
        CacheStatus.PendingSaveSync => "等待存档处理",
        CacheStatus.Conflict => "冲突",
        CacheStatus.Offline => "离线",
        CacheStatus.Failed => "失败",
        _ => status.ToString()
    };

    private static string TranslateLaunchModeOverride(GameLaunchModeOverride mode) => mode switch
    {
        GameLaunchModeOverride.Inherit => "跟随默认",
        GameLaunchModeOverride.Direct => "直接启动",
        GameLaunchModeOverride.TranslationPreferred => "翻译优先",
        GameLaunchModeOverride.MTool => "MTool",
        GameLaunchModeOverride.CustomScript => "自定义脚本",
        _ => mode.ToString()
    };

    private static string TranslateLaunchCandidateRole(LaunchCandidateRole role) => role switch
    {
        LaunchCandidateRole.MainGame => "主程序",
        LaunchCandidateRole.TranslationGame => "翻译 EXE",
        LaunchCandidateRole.MToolTemplate => "MTool BAT",
        LaunchCandidateRole.MToolTarget => "MTool 目标",
        LaunchCandidateRole.CustomLaunchScript => "自定义脚本",
        LaunchCandidateRole.ConfigurationTool => "配置工具",
        LaunchCandidateRole.Updater => "更新器",
        LaunchCandidateRole.Uninstaller => "卸载程序",
        LaunchCandidateRole.CrashReporter => "崩溃工具",
        LaunchCandidateRole.Runtime => "运行库",
        LaunchCandidateRole.Launcher => "快捷方式",
        _ => role.ToString()
    };

    private static string TranslateLaunchCandidateTestStatus(LaunchCandidateTestStatus status) => status switch
    {
        LaunchCandidateTestStatus.Untested => "未验证",
        LaunchCandidateTestStatus.ReadOnlyValid => "只读有效",
        LaunchCandidateTestStatus.ReadOnlyInvalid => "只读无效",
        LaunchCandidateTestStatus.TestSucceeded => "测试成功",
        LaunchCandidateTestStatus.TestFailed => "测试失败",
        LaunchCandidateTestStatus.NeedsRevalidation => "需重验",
        LaunchCandidateTestStatus.HardBlocked => "已阻止",
        _ => status.ToString()
    };

    private static string TranslateSkipReason(string reason, int candidateCount) => reason switch
    {
        "missing_source_root" => "Vnite 没有记录游戏本体目录",
        "outside_adopt_roots" => "不在接管根目录内",
        "outside_configured_adopt_roots" => "不在接管根目录内",
        "no_safe_executable_candidate" => "目录内没有安全的启动程序候选",
        "low_confidence_executable_candidate" => $"启动程序置信度低，候选 {candidateCount} 个",
        "ambiguous_executable_candidates" => $"多个启动程序不明确，候选 {candidateCount} 个",
        "takeover_residue" => "接管残留：Vnite 仍有 VGC 启动/追踪字段，但 VGC 记录或映射不完整",
        _ when reason.StartsWith("read_failed:", StringComparison.OrdinalIgnoreCase) => "目录读取失败",
        _ => string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason
    };

    private static List<string> BuildCleanupSuggestions(IReadOnlyCollection<GameRecord> games)
    {
        var now = DateTimeOffset.Now;
        var readyGames = games
            .Where(game => game.CacheStatus == CacheStatus.Ready)
            .Where(game => game.CacheSizeBytes > 0 || Directory.Exists(game.CacheRoot))
            .ToList();
        var deletable = readyGames
            .Where(game => CleanupBlockReason(game) is null)
            .OrderBy(game => game.LastPlayedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(game => game.CacheSizeBytes)
            .ToList();
        var blocked = readyGames
            .Select(game => new { Game = game, Reason = CleanupBlockReason(game) })
            .Where(item => item.Reason is not null)
            .OrderBy(item => item.Game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var lines = new List<string>
        {
            $"VGC 缓存清理建议 - {now:yyyy-MM-dd HH:mm:ss}",
            "",
            $"当前 Ready 缓存：{readyGames.Count} 个，合计 {ToMb(readyGames.Sum(game => Math.Max(0, game.CacheSizeBytes))):N0} MB",
            $"建议可清理：{deletable.Count} 个，合计 {ToMb(deletable.Sum(game => Math.Max(0, game.CacheSizeBytes))):N0} MB",
            $"保护不清理：{blocked.Count} 个",
            "",
            "可清理候选（按最久未玩优先）："
        };

        if (deletable.Count == 0)
        {
            lines.Add("- 暂无安全可清理缓存。");
        }
        else
        {
            lines.AddRange(deletable.Select(game =>
                $"- {game.DisplayName} | {ToMb(game.CacheSizeBytes):N0} MB | 最后游玩 {game.LastPlayedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "未知"} | {game.CacheRoot}"));
        }

        lines.Add("");
        lines.Add("保护不清理：");
        if (blocked.Count == 0)
        {
            lines.Add("- 无");
        }
        else
        {
            lines.AddRange(blocked.Select(item =>
                $"- {item.Game.DisplayName} | {ToMb(item.Game.CacheSizeBytes):N0} MB | {item.Reason} | {item.Game.CacheRoot}"));
        }

        lines.Add("");
        lines.Add("说明：此报告只展示建议，不会删除文件。自动清理应继续使用同一套保护规则，并默认进入 Quarantine。");
        return lines;
    }

    private static string? CleanupBlockReason(GameRecord game)
    {
        if (game.IsPinned) return "已固定 pinned";
        if (game.CacheStatus is CacheStatus.Running or CacheStatus.Copying or CacheStatus.Checking or CacheStatus.Verifying ||
            RuntimeGameLock.IsHeld(game.GameId)) return "正在运行或缓存任务中";
        if (game.ContainsPossibleSaveData) return "检测到可能存档/用户数据";
        if (game.PendingManualSaveBackup) return "存在待手动备份存档标记";
        if (string.IsNullOrWhiteSpace(game.CacheRoot)) return "缺少缓存目录";
        if (!Directory.Exists(game.CacheRoot)) return "缓存目录不存在";
        return null;
    }

    private void OpenSelectedCacheRoot()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.CacheRoot))
        {
            MessageBox.Show(this, "请先选择一个游戏。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenFolder(row.CacheRoot);
    }

    private void ImportSaveForSelectedGame()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "请先选择一个已接管游戏。", "同步存档", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var savePaths = ReadVniteSavePaths(row.GameId);
        var requestedVnitePathWrite = false;
        var targetPath = savePaths.Count switch
        {
            0 => ChooseNewSaveTarget(row),
            1 => savePaths[0],
            _ => ChooseSaveTarget(savePaths)
        };
        if (string.IsNullOrWhiteSpace(targetPath)) return;

        if (savePaths.Count == 0)
        {
            RequestAppendVniteSavePath(row.GameId, targetPath);
            requestedVnitePathWrite = true;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要导入/同步到游戏存档目录的存档文件夹。导入前会自动备份当前目标存档。",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(row.SourceRoot) ? row.SourceRoot : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        var confirm = MessageBox.Show(
            this,
            $"将把下面文件夹的内容导入到游戏存档目录。\n\n来源：{dialog.SelectedPath}\n目标：{targetPath}\n\n导入前会自动备份目标目录。是否继续？",
            "同步存档",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            _lastUndoBackupPath = BackupDatabase(databaseOptions.DatabasePath, "manual-update-selected-record");
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var service = new SaveProfileService(factory);
            var profile = service.AddOrUpdate(new SaveProfileCreateRequest(
                row.GameId,
                $"{row.Name} - Vnite 存档路径",
                SaveProfileSourceType.Absolute,
                targetPath,
                null,
                true));
            var result = service.Import(profile.SaveProfileId, dialog.SelectedPath);
            var vnitePathMessage = requestedVnitePathWrite
                ? "\n已请求插件把目标目录追加写入 Vnite 存档路径；Vnite 正在运行时通常 10 秒内生效。"
                : "";
            MessageBox.Show(
                this,
                $"同步完成。\n导入文件数：{result.ImportedFileCount}\n自动备份：{result.PreImportBackup.BackupPath}{vnitePathMessage}",
                "同步存档",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"同步失败：{error.Message}", "同步存档", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string ChooseNewSaveTarget(GameRow row)
    {
        var initialDirectory = Directory.Exists(row.SourceRoot)
            ? row.SourceRoot
            : Directory.Exists(row.CacheRoot)
                ? row.CacheRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Vnite 还没有这个游戏的存档路径。请选择游戏实际读取/写入存档的目标目录，通常是游戏根目录下的 save、Save 或 saves 文件夹。",
            UseDescriptionForTitle = true,
            InitialDirectory = initialDirectory
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : "";
    }

    private string ChooseSaveTarget(IReadOnlyList<string> savePaths)
    {
        using var form = new Form
        {
            Text = "选择目标存档目录",
            Width = 720,
            Height = 260,
            StartPosition = FormStartPosition.CenterParent
        };
        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (var savePath in savePaths) list.Items.Add(savePath);
        list.SelectedIndex = 0;
        var ok = new Button { Text = "确定", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK };
        form.Controls.Add(list);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        return form.ShowDialog(this) == DialogResult.OK && list.SelectedItem is string selected ? selected : "";
    }

    private List<string> ReadVniteSavePaths(string gameId)
    {
        var path = Path.Combine(_diagnosticsRoot, "vnite-save-paths-latest.json");
        if (!File.Exists(path)) return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("sampled_games", out var games) || games.ValueKind != JsonValueKind.Array) return [];
            foreach (var game in games.EnumerateArray())
            {
                if (!string.Equals(GetString(game, "game_id"), gameId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!game.TryGetProperty("save_paths", out var paths) || paths.ValueKind != JsonValueKind.Array) return [];
                return paths.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private void RequestAppendVniteSavePath(string gameId, string savePath)
    {
        Directory.CreateDirectory(_commandsRoot);
        var requestPath = Path.Combine(_commandsRoot, "set-save-path-request.json");
        var payload = JsonSerializer.Serialize(new
        {
            command = "append-vnite-save-path",
            game_id = gameId,
            save_path = Path.GetFullPath(savePath),
            requested_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(requestPath, payload);
    }

    private void OpenSelectedSourceRoot()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.SourceRoot))
        {
            MessageBox.Show(this, "请先选择一个游戏。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenFolder(row.SourceRoot);
    }

    private GameRow? GetSelectedRow()
    {
        if (_gamesGrid.CurrentRow?.DataBoundItem is GameRow current) return current;
        return _gamesGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<GameRow>()
            .FirstOrDefault();
    }

    private List<GameRow> GetSelectedRows()
    {
        var selected = _gamesGrid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<GameRow>()
            .ToList();
        if (selected.Count == 0 && _gamesGrid.CurrentRow?.DataBoundItem is GameRow current)
        {
            selected.Add(current);
        }

        return selected
            .GroupBy(row => row.GameId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private void UpdateBatchBar()
    {
        var selected = GetSelectedRows();
        _batchBar.Visible = selected.Count > 1;
        _selectionStatus.Text = selected.Count > 1
            ? $"已选择 {selected.Count:N0} 个游戏"
            : "";
    }

    private void UpdateDetailsPanel()
    {
        if (GetSelectedRow() is not { } row)
        {
            _detailsText.Text = "选择一个游戏查看详情。";
            return;
        }

        var recommended = BuildRecommendedActions(row);
        _detailsText.Text =
            $"游戏名称：{row.Name}\r\n" +
            $"管理状态：{row.ManagedStatus}\r\n" +
            $"接管状态：{row.AdoptStatus}\r\n" +
            $"管理意图：{TranslateManagementIntent(row.ManagementIntent)}\r\n" +
            $"缓存状态：{row.CacheStatus}\r\n" +
            $"启动模式：{row.LaunchMode}\r\n" +
            $"启动状态：{row.LaunchStatus}\r\n" +
            $"问题：{(string.IsNullOrWhiteSpace(row.Issue) ? "无" : row.Issue)}\r\n" +
            $"最近游玩：{row.LastPlayed}\r\n" +
            $"缓存大小：{row.CacheMb:N0} MB\r\n" +
            "\r\n身份\r\n" +
            $"Game ID：{row.GameId}\r\n" +
            $"Vnite ID：{row.VniteId}\r\n" +
            "\r\n路径\r\n" +
            $"游戏根目录：{row.SourceRoot}\r\n" +
            $"缓存目录：{row.CacheRoot}\r\n" +
            $"启动程序：{row.ExecutablePath}\r\n" +
            $"启动快捷方式：{row.ShortcutPath}\r\n" +
            $"候选启动程序：{row.CandidatePrograms}\r\n" +
            "\r\nMTool\r\n" +
            BuildMToolDetail(row) +
            "\r\n\r\n建议操作\r\n" +
            string.Join("\r\n", recommended.Select(item => $"- {item}"));
    }

    private static string TranslateManagementIntent(ManagementIntent intent) => intent switch
    {
        ManagementIntent.Managed => "正常管理",
        ManagementIntent.Unmanaged => "未接管",
        ManagementIntent.Ignored => "已忽略",
        ManagementIntent.Suspended => "已暂停",
        _ => intent.ToString()
    };

    private static List<string> BuildRecommendedActions(GameRow row)
    {
        var actions = new List<string>();
        if (row.ManagedStatus != "已接管") actions.Add("点击“自动修复”接管；若候选不明确，右键“手动修复”。");
        if (string.IsNullOrWhiteSpace(row.ExecutablePath) && !string.IsNullOrWhiteSpace(row.CandidatePrograms)) actions.Add("双击“问题”或右键“手动修复”，人工选择启动程序。");
        if (!string.IsNullOrWhiteSpace(row.Issue)) actions.Add("先处理“问题”列，再启动。");
        if (row.CacheStatus is "" or "未缓存") actions.Add("点击“立即缓存”，确认缓存链路。");
        if (!string.IsNullOrWhiteSpace(row.CacheRoot)) actions.Add("右键可打开缓存目录或清除缓存。");
        if (FindMToolTemplatePath(row.CacheRoot, row.SourceRoot) is not null && row.LaunchMode != "MTool") actions.Add("检测到 MTool 模板；可右键“修改启动模式”设为 MTool 批处理优先。");
        if (actions.Count == 0) actions.Add("状态正常，可从 Vnite 或控制台启动。");
        return actions;
    }

    private static string BuildCompactRowTooltip(GameRow row) =>
        $"Game ID: {row.GameId}\nVnite ID: {row.VniteId}\nSource: {row.SourceRoot}\nCache: {row.CacheRoot}\nExe: {row.ExecutablePath}";

    private void HandleGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.A)
        {
            _gamesGrid.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.F)
        {
            _searchBox.Focus();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.F5)
        {
            LoadStatus();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.Delete)
        {
            ShowDeleteActionPicker();
            e.Handled = true;
        }
    }

    private void ShowDeleteActionPicker()
    {
        if (GetSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "请先选择一个游戏。", "删除/清理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var choice = MessageBox.Show(
            this,
            $"你按下了 Delete。\n\n当前游戏：{row.Name}\n\n" +
            "是：清除本地缓存（不删 VGC 记录）\n" +
            "否：临时删除 VGC 接管记录（可重新接管，不设置永久忽略，不删缓存、不删 Vnite 条目、不删源游戏）\n" +
            "取消：不操作\n\n" +
            "注意：永久忽略是单独操作，不等于删除记录。",
            "选择删除动作",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);
        if (choice == DialogResult.Yes) ClearSelectedCache();
        else if (choice == DialogResult.No) DeleteSelectedRecord();
    }

    private void HandleGridCellDoubleClick(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_gamesGrid.Rows[e.RowIndex].DataBoundItem is not GameRow row) return;
        var propertyName = _gamesGrid.Columns[e.ColumnIndex].DataPropertyName;
        switch (propertyName)
        {
            case nameof(GameRow.CacheStatus):
            case nameof(GameRow.CacheRoot):
                if (Directory.Exists(row.CacheRoot)) OpenFolder(row.CacheRoot);
                else PrepareCacheForRow(row);
                break;
            case nameof(GameRow.SourceRoot):
                if (Directory.Exists(row.SourceRoot)) OpenFolder(row.SourceRoot);
                else ChooseSourceRoot(row);
                break;
            case nameof(GameRow.ExecutablePath):
            case nameof(GameRow.CandidatePrograms):
                OpenExecutableManager(row);
                break;
            case nameof(GameRow.ShortcutPath):
                if (File.Exists(row.ShortcutPath)) OpenFileLocation(row.ShortcutPath);
                else RequestAdoptRepair();
                break;
            case nameof(GameRow.GameId):
            case nameof(GameRow.VniteId):
                Clipboard.SetText(propertyName == nameof(GameRow.VniteId) ? row.VniteId : row.GameId);
                MessageBox.Show(this, "已复制 ID。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case nameof(GameRow.LaunchStatus):
                OpenLatestLaunchDetail();
                break;
            default:
                ShowManualRepairGuide(row);
                break;
        }
    }

    private void ManualRepairSelectedGame()
    {
        if (GetSelectedRow() is { } row)
        {
            ShowManualRepairGuide(row);
            return;
        }

        MessageBox.Show(this, "请先选择一个游戏。", "手动修复", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DiagnoseSelectedGame()
    {
        if (GetSelectedRow() is { } row)
        {
            MessageBox.Show(this, _detailsText.Text, $"诊断 - {row.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenHealthCheckReport();
    }

    private void OpenOnboardingWizard()
    {
        var diagnostics = ReadAdoptDiagnostics();
        var rows = _allRows
            .Where(row => row.ManagedStatus != "已接管" || row.AdoptStatus == "待接管")
            .Take(500)
            .ToList();
        var lines = new List<string>
        {
            "待接管向导",
            "",
            "用途：集中处理自动扫描到但尚未写入 Wrapper 启动链路的游戏。",
            "安全边界：不改标题、封面、标签；只通过现有接管请求让 Vnite 插件写启动字段。",
            "",
            $"诊断状态：{diagnostics?.Status ?? "无诊断文件"}",
            $"候选：{diagnostics?.CandidateCount ?? 0}；已接管：{diagnostics?.AdoptedCount ?? 0}；剩余：{diagnostics?.RemainingCount ?? 0}",
            "",
            "当前列表预览："
        };
        lines.AddRange(rows.Select(row => $"- {row.Name} | {row.Issue} | {row.SourceRoot} | {row.CandidatePrograms}"));
        lines.Add("");
        lines.Add("是否现在写入接管请求？");
        var confirm = MessageBox.Show(this, string.Join(Environment.NewLine, lines.Take(80)), "待接管向导", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        WriteAdoptRequest(rows.Select(row => row.GameId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList(), Math.Max(rows.Count, 100));
        RecordOperation("onboarding_wizard_request", null, rows, "已从待接管向导写入请求", []);
        MessageBox.Show(this, "已写入接管请求。", "待接管向导", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenSkippedProblemCenter()
    {
        var diagnostics = ReadAdoptDiagnostics();
        Directory.CreateDirectory(_diagnosticsRoot);
        var path = Path.Combine(_diagnosticsRoot, $"skipped-problem-center-{DateTimeOffset.Now:yyyyMMddHHmmss}.txt");
        var lines = new List<string>
        {
            "跳过问题中心",
            "",
            "处理方式：",
            "- 目录无候选：双击“游戏根目录”确认目录，再双击“启动程序/候选启动程序”手动选择。",
            "- 候选多个：优先选择翻译 exe 或与工具一同启动 bat。",
            "- 根目录不存在：先在 Vnite 属性里修正根目录，再回控制台一键修复接管。",
            ""
        };
        if (diagnostics?.Skipped.Count > 0)
        {
            lines.AddRange(diagnostics.Skipped
                .GroupBy(item => TranslateSkipReason(item.Reason, item.Candidates.Count))
                .SelectMany(group => new[] { $"## {group.Key}" }.Concat(group.Take(200).Select(item => $"- {item.Name} | {item.SourceRoot} | {string.Join("; ", item.Candidates.Take(8))}"))));
        }
        else
        {
            lines.Add("无跳过诊断记录。");
        }

        File.WriteAllLines(path, lines);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void PrepareSelectedGameCache()
    {
        if (GetSelectedRow() is { } row)
        {
            PrepareCacheForRow(row);
            return;
        }

        MessageBox.Show(this, "请先选择一个游戏。", "立即缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void LaunchSelectedGame()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "请先选择一个已接管游戏。", "启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var wrapperPath = Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Wrapper.exe");
        if (!File.Exists(wrapperPath))
        {
            MessageBox.Show(this, $"找不到 Wrapper：{wrapperPath}", "启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string launchArguments;
        try
        {
            launchArguments = LaunchArgumentCodec.Build(row.GameId);
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            _ = WrapperGameResolver.Resolve(new GameRepository(factory), LaunchArgumentCodec.Parse(launchArguments).GameId);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"启动参数或数据库记录不可解析：\n\n{error.Message}", "启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"将通过 Wrapper 启动此游戏，并进入缓存链路。\n\n游戏：{row.Name}\nGame ID：{row.GameId}\n\n是否继续？",
            "启动游戏",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = wrapperPath,
            Arguments = launchArguments,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private void ShowManualRepairGuide(GameRow row)
    {
        var message =
            $"游戏：{row.Name}\n状态：{row.ManagedStatus} / {row.CacheStatus}\n问题：{row.Issue}\n\n" +
            "可直接双击这些列处理：\n" +
            "- 游戏根目录：目录存在时打开；不存在时重新选择目录。\n" +
            "- 启动程序 / 候选启动程序：选择真实 exe/bat。\n" +
            "- 缓存状态 / 缓存目录：目录存在时打开；未缓存时手动缓存。\n" +
            "- 启动快捷方式：打开快捷方式位置；缺失时请求重新接管。\n\n" +
            "是否现在为此游戏选择真实启动程序？";
        if (MessageBox.Show(this, message, "手动修复引导", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            ChooseExecutable(row);
        }
    }

    private void ChooseSourceRoot(GameRow row)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择这个游戏新的本体根目录。目录确认后不会修改标题、封面、标签。",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(row.SourceRoot)
                ? row.SourceRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        if (!HasExistingGameRecord(row))
        {
            var repairRow = row with { SourceRoot = dialog.SelectedPath };
            var detectedExecutable = DetectBestExecutable(dialog.SelectedPath, row.Name);
            if (string.IsNullOrWhiteSpace(detectedExecutable))
            {
                MessageBox.Show(this, "已选择新的游戏根目录，但未能自动确认启动程序。请继续选择真实 exe/bat。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ChooseExecutable(repairRow);
                return;
            }

            UpsertManualExecutableSelection(repairRow, dialog.SelectedPath, detectedExecutable);
            return;
        }

        UpdateManagedGame(row.GameId, game =>
        {
            var executablePath = File.Exists(game.ExecutablePath) && IsPathUnder(game.ExecutablePath, dialog.SelectedPath)
                ? game.ExecutablePath
                : DetectBestExecutable(dialog.SelectedPath, row.Name);
            return game with
            {
                SourceRoot = dialog.SelectedPath,
                WorkingDirectory = !string.IsNullOrWhiteSpace(executablePath) ? Path.GetDirectoryName(executablePath) ?? dialog.SelectedPath : dialog.SelectedPath,
                ExecutablePath = executablePath,
                ExecutableRelativePath = TryRelative(dialog.SelectedPath, executablePath),
                WorkingDirectoryRelativePath = TryRelative(dialog.SelectedPath, !string.IsNullOrWhiteSpace(executablePath) ? Path.GetDirectoryName(executablePath) ?? dialog.SelectedPath : dialog.SelectedPath),
                CacheStatus = CacheStatus.NotCached,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }, "已更新游戏根目录；下次启动会重新缓存。");
    }

    private void ChooseExecutable(GameRow row)
    {
        var initial = Directory.Exists(row.SourceRoot) ? row.SourceRoot : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        using var dialog = new OpenFileDialog
        {
            Title = "选择真实启动程序（exe 或 bat/cmd）",
            InitialDirectory = initial,
            Filter = "启动程序 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName)) return;
        var sourceRoot = Directory.Exists(row.SourceRoot) && IsPathUnder(dialog.FileName, row.SourceRoot)
            ? row.SourceRoot
            : Path.GetDirectoryName(dialog.FileName) ?? row.SourceRoot;
        UpsertManualExecutableSelection(row, sourceRoot, dialog.FileName);
    }

    private void OpenExecutableManagerSelectedGame()
    {
        if (GetSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "请先选择一个游戏。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenExecutableManager(row);
    }

    private void OpenExecutableManager(GameRow row)
    {
        if (string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "此 Vnite 条目缺少 Game ID，不能保存启动配置。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(row.SourceRoot) || !Directory.Exists(row.SourceRoot))
        {
            MessageBox.Show(this, "游戏根目录不存在。请先双击“游戏根目录”重新选择。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var repository = new GameRepository(factory);
            var existing = repository.GetById(row.GameId) ?? repository.GetByVniteGameId(row.VniteId) ?? repository.GetByVniteGameId(row.GameId);
            var settings = VgcSettingsStore.LoadRequired();
            var previewGame = existing ?? new GameRecord
            {
                GameId = row.GameId,
                VniteGameId = string.IsNullOrWhiteSpace(row.VniteId) ? row.GameId : row.VniteId,
                DisplayName = string.IsNullOrWhiteSpace(row.Name) ? Path.GetFileName(row.SourceRoot) : row.Name,
                SourceRoot = row.SourceRoot,
                CacheRoot = BuildCacheRoot(settings.DefaultCacheRoot, row.SourceRoot, row.GameId),
                ExecutablePath = string.IsNullOrWhiteSpace(row.ExecutablePath) ? row.SourceRoot : row.ExecutablePath,
                WorkingDirectory = row.SourceRoot,
                ManagementIntent = ManagementIntent.Unmanaged,
                CacheStatus = CacheStatus.NotCached
            };

            var scanner = new LaunchCandidateScanner();
            var candidates = scanner.Scan(previewGame).ToList();
            if (existing is not null)
            {
                new LaunchCandidateRepository(factory).ReplaceForGame(existing.GameId, candidates);
            }

            using var form = CreateExecutableManagerForm(row, previewGame, candidates, scanner, launchAfterSave: false);
            form.ShowDialog(this);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"打开启动程序管理失败：{error.Message}", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Form CreateExecutableManagerForm(GameRow row, GameRecord previewGame, List<GameLaunchCandidateRecord> candidates, LaunchCandidateScanner scanner, bool launchAfterSave)
    {
        var form = new Form
        {
            Text = $"启动程序管理 - {row.Name}",
            Width = 980,
            Height = 620,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = true
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9)
        };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false
        };
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.DefaultMark), "默认", 50);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.FileName), "文件", 220);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.Role), "类型", 110);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.Score), "评分", 70);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.Confidence), "置信度", 80);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.Status), "状态", 120);
        AddCandidateColumn(grid, nameof(LaunchCandidateDisplayRow.RelativePath), "相对路径", 360);
        var rows = BuildLaunchCandidateRows(candidates);
        grid.DataSource = rows;

        void UpdatePreview()
        {
            var candidate = SelectedCandidate(grid) ?? candidates.FirstOrDefault();
            preview.Text = FormatEffectiveLaunchPlan(scanner.BuildPreview(previewGame, candidate));
        }

        grid.SelectionChanged += (_, _) => UpdatePreview();
        grid.CellDoubleClick += (_, _) => SaveCandidateFromManager(row, previewGame, candidates, grid, startAfterSave: false);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        buttons.Controls.Add(CreateButton("只读验证", (_, _) => ValidateSelectedLaunchCandidate(previewGame, grid)));
        buttons.Controls.Add(CreateButton("设为默认", (_, _) => SaveCandidateFromManager(row, previewGame, candidates, grid, startAfterSave: false)));
        buttons.Controls.Add(CreateButton("保存并测试启动", (_, _) => SaveCandidateFromManager(row, previewGame, candidates, grid, startAfterSave: true)));
        buttons.Controls.Add(CreateButton("手动选择文件", (_, _) => { form.Close(); ChooseExecutable(row); }));
        buttons.Controls.Add(CreateButton("打开游戏根目录", (_, _) => OpenFolder(row.SourceRoot)));
        buttons.Controls.Add(CreateButton("关闭", (_, _) => form.Close()));
        layout.Controls.Add(preview, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        form.Controls.Add(layout);
        if (rows.Count > 0) grid.Rows[0].Selected = true;
        UpdatePreview();
        return form;
    }

    private static void AddCandidateColumn(DataGridView grid, string propertyName, string headerText, int width)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = propertyName,
            HeaderText = headerText,
            Name = propertyName,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
    }

    private static List<LaunchCandidateDisplayRow> BuildLaunchCandidateRows(IEnumerable<GameLaunchCandidateRecord> candidates) => candidates
        .Select(candidate => new LaunchCandidateDisplayRow(
            candidate.IsPrimary ? "✓" : "",
            Path.GetFileName(candidate.RelativePath),
            TranslateLaunchCandidateRole(candidate.CandidateRole),
            candidate.Score,
            $"{candidate.Confidence}%",
            TranslateLaunchCandidateTestStatus(candidate.LastTestStatus),
            candidate.RelativePath,
            candidate))
        .ToList();

    private static GameLaunchCandidateRecord? SelectedCandidate(DataGridView grid)
    {
        if (grid.CurrentRow?.DataBoundItem is LaunchCandidateDisplayRow current) return current.Candidate;
        return grid.SelectedRows.Cast<DataGridViewRow>().Select(item => item.DataBoundItem).OfType<LaunchCandidateDisplayRow>().FirstOrDefault()?.Candidate;
    }

    private void ValidateSelectedLaunchCandidate(GameRecord game, DataGridView grid)
    {
        var candidate = SelectedCandidate(grid);
        if (candidate is null)
        {
            MessageBox.Show(this, "没有可验证的候选。", "只读验证", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var path = LaunchCandidateScanner.ResolveAbsolutePath(game, candidate);
        var messages = new List<string>
        {
            $"候选：{candidate.RelativePath}",
            $"类型：{TranslateLaunchCandidateRole(candidate.CandidateRole)}",
            $"评分：{candidate.Score}，置信度：{candidate.Confidence}%"
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) messages.Add("结果：文件不存在或路径越界。");
        else if (candidate.LastTestStatus == LaunchCandidateTestStatus.HardBlocked) messages.Add($"结果：被安全规则阻止。{candidate.ExclusionReasonsJson}");
        else messages.Add($"结果：路径有效，位于游戏根目录内。{Environment.NewLine}绝对路径：{path}");
        MessageBox.Show(this, string.Join(Environment.NewLine, messages), "只读验证", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveCandidateFromManager(GameRow row, GameRecord previewGame, List<GameLaunchCandidateRecord> candidates, DataGridView grid, bool startAfterSave)
    {
        var candidate = SelectedCandidate(grid);
        if (candidate is null)
        {
            MessageBox.Show(this, "请先选择一个启动候选。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (candidate.LastTestStatus == LaunchCandidateTestStatus.HardBlocked)
        {
            MessageBox.Show(this, "此候选被安全规则阻止，不能设为默认。请手动选择确认过的启动程序。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var executablePath = LaunchCandidateScanner.ResolveAbsolutePath(previewGame, candidate);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            MessageBox.Show(this, "候选文件不存在或不在游戏根目录内。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var mode = LaunchCandidateScanner.ToLaunchMode(candidate, previewGame.LaunchModeOverride);
            var saved = SaveManualExecutableSelection(row, previewGame.SourceRoot, executablePath, mode);
            var marked = candidates
                .Select(item => item.CandidateId == candidate.CandidateId
                    ? item with { GameId = saved.GameId, IsPrimary = true, UserConfirmed = true, LastTestStatus = LaunchCandidateTestStatus.ReadOnlyValid, LastTestUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow }
                    : item with { GameId = saved.GameId, IsPrimary = false })
                .ToList();
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new LaunchCandidateRepository(factory).ReplaceForGame(saved.GameId, marked);
            WriteAdoptRequest([saved.VniteGameId ?? saved.GameId], 1);
            RecordOperation("executable_manager_select", saved.GameId, [row], $"已保存默认启动候选：{candidate.RelativePath}，模式：{TranslateLaunchModeOverride(mode)}", []);
            MessageBox.Show(this, "已保存启动候选，并写入接管请求。Vnite 插件处理后将通过 Wrapper 启动。", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadStatus();
            if (startAfterSave) LaunchGameById(saved.GameId);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"保存启动候选失败：{error.Message}", "启动程序管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string FormatEffectiveLaunchPlan(EffectiveLaunchPlanPreview plan)
    {
        var warnings = plan.Warnings.Count == 0 ? "无" : string.Join("；", plan.Warnings);
        return string.Join(Environment.NewLine + Environment.NewLine,
            "当前有效启动计划（只读预览）",
            $"Wrapper 参数：{plan.WrapperArguments}",
            $"候选相对路径：{plan.CandidateRelativePath}",
            $"候选类型：{TranslateLaunchCandidateRole(plan.CandidateRole)} / {plan.CandidateType}",
            $"启动模式：{TranslateLaunchModeOverride(plan.LaunchMode)}",
            $"工作目录相对路径：{plan.WorkingDirectoryRelativePath}",
            $"缓存根目录：{plan.CacheRoot}",
            $"警告：{warnings}");
    }

    private void LaunchGameById(string gameId)
    {
        var wrapperPath = Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Wrapper.exe");
        if (!File.Exists(wrapperPath))
        {
            MessageBox.Show(this, $"找不到 Wrapper：{wrapperPath}", "测试启动", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = wrapperPath,
            Arguments = LaunchArgumentCodec.Build(gameId),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private void PrepareCacheForRow(GameRow row)
    {
        if (string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "未接管游戏需要先接管，才能手动缓存。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var confirm = MessageBox.Show(this, $"现在为“{row.Name}”执行手动缓存？", "手动缓存", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;
        var agentPath = Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Agent.exe");
        if (!File.Exists(agentPath))
        {
            MessageBox.Show(this, $"找不到 Agent：{agentPath}", "手动缓存", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = agentPath,
            Arguments = $"prepare-cache --game-id {Quote(row.GameId)}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        Process.Start(startInfo);
        MessageBox.Show(this, "已启动缓存任务。复制进度会在顶部刷新。", "手动缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BatchAdoptSelected()
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            OpenOnboardingWizard();
            return;
        }

        var candidates = rows.Where(row => row.ManagedStatus != "已接管").ToList();
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "选中的游戏都已接管。", "批量接管", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ConfirmBatch("批量接管", candidates, "写入接管请求，由 Vnite 插件按现有安全规则处理。")) return;
        WriteAdoptRequest(candidates.Select(row => row.GameId).ToList(), Math.Max(candidates.Count, 100));
        RecordOperation("batch_adopt_request", null, candidates, "已写入接管请求", []);
        MessageBox.Show(this, "已写入批量接管请求。Vnite 运行时会处理，稍后刷新。", "批量接管", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BatchCacheSelected()
    {
        var rows = GetSelectedRows()
            .Where(row => row.ManagedStatus == "已接管" && !string.IsNullOrWhiteSpace(row.GameId))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "请选择已接管游戏。", "批量缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ConfirmBatch("批量缓存", rows, "逐个启动 Agent prepare-cache；不会启动游戏。")) return;
        var agentPath = Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Agent.exe");
        if (!File.Exists(agentPath))
        {
            MessageBox.Show(this, $"找不到 Agent：{agentPath}", "批量缓存", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var started = 0;
        foreach (var row in rows)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = agentPath,
                Arguments = $"prepare-cache --game-id {Quote(row.GameId)}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            started++;
        }

        RecordOperation("batch_prepare_cache", null, rows, $"已启动 {started} 个缓存任务", []);
        MessageBox.Show(this, $"已启动 {started} 个缓存任务。", "批量缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BatchDiagnoseSelected()
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            OpenHealthCheckReport();
            return;
        }

        Directory.CreateDirectory(_diagnosticsRoot);
        var path = Path.Combine(_diagnosticsRoot, $"batch-diagnostics-{DateTimeOffset.Now:yyyyMMddHHmmss}.txt");
        File.WriteAllLines(path, BuildBatchDiagnosticsLines(rows));
        RecordOperation("batch_diagnostics", null, rows, $"已生成诊断：{path}", []);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void BatchSetManagementIntent(ManagementIntent intent)
    {
        var selected = GetSelectedRows();
        var rows = selected
            .Where(row => !string.IsNullOrWhiteSpace(row.GameId) && row.ManagedStatus == "已接管" && !IsRowRunning(row))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "请选择非运行中的已接管游戏。", "批量管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var actionName = intent switch
        {
            ManagementIntent.Ignored => "永久忽略",
            ManagementIntent.Suspended => "暂停接管",
            ManagementIntent.Managed => "恢复管理",
            ManagementIntent.Unmanaged => "解除接管",
            _ => intent.ToString()
        };
        var skippedRunning = selected.Count(IsRowRunning);
        if (!ConfirmBatch(actionName, rows, $"只修改 VGC 管理意图，不删除游戏文件、不改 Vnite 标题封面标签。运行中项目会跳过：{skippedRunning}。")) return;

        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            _lastUndoBackupPath = BackupDatabase(databaseOptions.DatabasePath, $"batch-{intent.ToString().ToLowerInvariant()}");
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var repository = new GameRepository(factory);
            var changed = 0;
            foreach (var row in rows)
            {
                var game = repository.GetById(row.GameId);
                if (game is null) continue;
                repository.Update(game with { ManagementIntent = intent, UpdatedUtc = DateTimeOffset.UtcNow });
                changed++;
            }

            RecordOperation($"batch_set_intent_{intent}", null, rows, $"已修改 {changed} 条；备份 {_lastUndoBackupPath}", []);
            MessageBox.Show(this, $"已修改 {changed} 条。可在“设置与诊断”里撤销上次操作。", actionName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"批量修改失败：{error.Message}", actionName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void BatchClearCacheSelected()
    {
        var selected = GetSelectedRows();
        var rows = selected
            .Where(row => row.ManagedStatus == "已接管" && !string.IsNullOrWhiteSpace(row.GameId) && !IsRowRunning(row))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "请选择非运行中的已接管游戏。", "批量清除缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var skippedRunning = selected.Count(IsRowRunning);
        if (!ConfirmBatch("批量清除缓存", rows, $"按安全清除语义移动到隔离区；不删除源游戏。运行中和有风险项会跳过。运行中项目：{skippedRunning}。")) return;
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var service = new CacheManagementService(factory, CreateCacheService(factory), VgcSettingsStore.LoadRequired().ProtectConfigFiles);
        var cleared = 0;
        var warnings = new List<string>();
        foreach (var row in rows)
        {
            var game = repository.GetById(row.GameId);
            if (game is null) continue;
            var result = service.ClearCache(new CacheClearRequest(row.GameId, Force: false, Reason: "control_panel_batch_clear_cache"));
            if (result.Cleared)
            {
                cleared++;
            }
            else
            {
                warnings.Add($"{row.Name}: {TranslateClearStatus(result.Status)} {string.Join("; ", result.DetectedUserDataPaths.Take(5))}");
            }
        }

        RecordOperation("batch_clear_cache", null, rows, $"已处理 {rows.Count} 条，清理 {cleared} 条", warnings);
        MessageBox.Show(this, $"已处理 {rows.Count} 条，清理 {cleared} 条，跳过 {warnings.Count} 条。", "批量清除缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        LoadStatus();
    }

    private void BatchDeleteSelectedRecords()
    {
        var selected = GetSelectedRows();
        var rows = selected
            .Where(row => !string.IsNullOrWhiteSpace(row.GameId) && !IsRowRunning(row))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "请选择非运行中的 VGC 记录。", "批量删除插件记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var running = selected.Count(row => IsRowRunning(row));
        var totalCacheMb = rows.Sum(row => row.CacheMb);
        var preview =
            $"将临时删除 {rows.Count} 条 VGC 插件记录。\n\n" +
            $"选中总数：{selected.Count}\n" +
            $"将跳过运行中：{running}\n" +
            $"涉及缓存大小：{totalCacheMb:N0} MB\n\n" +
            "将删除：\n" +
            "- VGC SQLite 记录\n" +
            "- 可确认归属的 Wrapper 快捷方式\n\n" +
            "将保留：\n" +
            "- NAS/G 盘游戏源文件\n" +
            "- Vnite 游戏条目、标题、封面、标签\n" +
            "- 用户存档\n" +
            "- 本地缓存目录\n\n" +
            "不会设置永久忽略；如果游戏仍位于接管根目录，之后可以重新接管。\n\n是否继续？";
        if (MessageBox.Show(this, preview, "批量删除插件记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (MessageBox.Show(this, "请再次确认：只删除 VGC 插件记录，不删除游戏本体。", "批量删除插件记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var wasMaintenanceEnabled = IsMaintenanceModeEnabled();
        if (!wasMaintenanceEnabled)
        {
            SetMaintenanceMode(true, "control_panel_batch_delete_records");
        }

        try
        {
            _lastUndoBackupPath = BackupDatabase(databaseOptions.DatabasePath, "batch-delete-plugin-records");
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var repository = new GameRepository(factory);
            var deleted = 0;
            var warnings = new List<string>();
            foreach (var row in rows)
            {
                var game = repository.GetById(row.GameId);
                if (game is null)
                {
                    warnings.Add($"{row.Name}: 记录不存在");
                    continue;
                }

                try
                {
                    TryDeleteShortcutForGame(game.GameId, row.ShortcutPath);
                    repository.Delete(game.GameId);
                    deleted++;
                }
                catch (Exception error)
                {
                    warnings.Add($"{row.Name}: {error.Message}");
                }
            }

            RecordOperation("batch_delete_plugin_records", null, rows, $"已删除 {deleted} 条 VGC 记录；备份 {_lastUndoBackupPath}", warnings);
            MessageBox.Show(this, $"已删除 {deleted} 条 VGC 记录，跳过/失败 {warnings.Count} 条。可在“设置与诊断”里撤销上次操作。", "批量删除插件记录", MessageBoxButtons.OK, warnings.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"批量删除失败：{error.Message}", "批量删除插件记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!wasMaintenanceEnabled)
            {
                SetMaintenanceMode(false, "");
            }
        }
    }

    private void ResetSelectedGame()
    {
        if (MessageBox.Show(
                this,
                "完全重置当前游戏会先走现有“删除插件记录”安全路径：备份数据库、删除 VGC 记录和可确认归属的 Wrapper 快捷方式。\n\n不会删除源游戏、Vnite 标题封面标签、存档或缓存目录。\n\n是否继续？",
                "完全重置当前游戏",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        DeleteSelectedRecord();
    }

    private void ShowDeleteFromVnitePreview()
    {
        if (GetSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "请先选择一个游戏。", "从 Vnite 删除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        MessageBox.Show(
            this,
            $"高级危险操作：从 Vnite 删除游戏条目。\n\n游戏：{row.Name}\nVnite ID：{row.VniteId}\n\n当前插件没有可靠确认 Vnite 删除 API 的用户确认语义，因此不会直接删除 Vnite 条目。\n请在 Vnite 本体中删除游戏条目；如只想让 VGC 重新生成接管记录，请使用“删除插件记录”。",
            "从 Vnite 删除（预览）",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void SetSelectedManagementIntent(ManagementIntent intent)
    {
        if (GetSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "请先选择一个游戏。", "管理意图", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BatchSetManagementIntent(intent);
    }

    private void ChangeSelectedLaunchMode()
    {
        if (GetSelectedRow() is not { } row || row.ManagedStatus != "已接管")
        {
            MessageBox.Show(this, "请先选择一个已接管游戏。", "启动模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new Form
        {
            Text = "修改启动模式",
            Width = 360,
            Height = 180,
            StartPosition = FormStartPosition.CenterParent
        };
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(12) };
        combo.Items.AddRange(["跟随默认", "直接启动", "翻译优先 EXE", "MTool 批处理优先"]);
        combo.SelectedIndex = 0;
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom };
        dialog.Controls.Add(combo);
        dialog.Controls.Add(ok);
        dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var mode = combo.SelectedIndex switch
        {
            1 => GameLaunchModeOverride.Direct,
            2 => GameLaunchModeOverride.TranslationPreferred,
            3 => GameLaunchModeOverride.MTool,
            _ => GameLaunchModeOverride.Inherit
        };
        UpdateManagedGame(row.GameId, game => game with { LaunchModeOverride = mode, UpdatedUtc = DateTimeOffset.UtcNow }, "已更新启动模式。");
    }

    private bool ConfirmBatch(string title, IReadOnlyList<GameRow> rows, string operation)
    {
        var preview = string.Join(Environment.NewLine, rows.Take(12).Select(row => $"- {row.Name}"));
        if (rows.Count > 12) preview += $"{Environment.NewLine}- ...另 {rows.Count - 12} 个";
        return MessageBox.Show(
            this,
            $"{operation}\n\n数量：{rows.Count}\n\n{preview}\n\n是否继续？",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static IEnumerable<string> BuildBatchDiagnosticsLines(IReadOnlyList<GameRow> rows)
    {
        yield return $"VGC 批量诊断 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}";
        yield return "";
        foreach (var row in rows)
        {
            yield return $"[{row.StatusIcon}] {row.Name}";
            yield return $"  Game ID: {row.GameId}";
            yield return $"  Vnite ID: {row.VniteId}";
            yield return $"  状态: {row.ManagedStatus} / {row.AdoptStatus} / {row.CacheStatus} / {row.LaunchStatus}";
            yield return $"  问题: {(string.IsNullOrWhiteSpace(row.Issue) ? "无" : row.Issue)}";
            yield return $"  根目录: {row.SourceRoot}";
            yield return $"  缓存目录: {row.CacheRoot}";
            yield return $"  启动程序: {row.ExecutablePath}";
            yield return $"  候选: {row.CandidatePrograms}";
            yield return "";
        }
    }

    private void WriteAdoptRequest(IReadOnlyList<string> selectedGameIds, int maxCount)
    {
        Directory.CreateDirectory(_commandsRoot);
        var requestPath = Path.Combine(_commandsRoot, "adopt-existing-request.json");
        var payload = JsonSerializer.Serialize(new
        {
            command = "adopt-existing-apply",
            max_count = maxCount,
            selected_game_ids = selectedGameIds,
            requested_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(requestPath, payload);
        _adoptStatus.Text = "已写入接管请求；等待 Vnite 插件处理，稍后会自动刷新";
    }

    private void RequestTakeoverConsistency(bool apply)
    {
        var selected = GetSelectedRows();
        var selectedIds = selected
            .Select(row => string.IsNullOrWhiteSpace(row.VniteId) ? row.GameId : row.VniteId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var title = apply ? "一键安全修复" : "检查接管一致性";
        var preview = selectedIds.Count == 0
            ? $"{title} 将检查全部 Vnite/VGC 接管状态。"
            : $"{title} 将处理选中的 {selectedIds.Count} 个游戏。";
        preview += "\n\n不会删除 NAS/G 盘游戏源文件、Vnite 标题、封面、标签和用户存档。";
        if (apply)
        {
            preview += "\n\n会按安全规则修复 Wrapper 快捷方式、SQLite/PluginDB 映射和 Vnite 启动字段；多启动程序仍会进入人工确认。";
        }
        if (MessageBox.Show(this, preview, title, MessageBoxButtons.YesNo, apply ? MessageBoxIcon.Warning : MessageBoxIcon.Information) != DialogResult.Yes) return;

        Directory.CreateDirectory(_commandsRoot);
        var requestPath = Path.Combine(_commandsRoot, "takeover-consistency-request.json");
        var payload = JsonSerializer.Serialize(new
        {
            command = apply ? "takeover-consistency-repair" : "takeover-consistency-scan",
            apply,
            dry_run = !apply,
            selected_game_ids = selectedIds,
            requested_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(requestPath, payload);
        _adoptStatus.Text = apply
            ? "已写入一键安全修复请求；等待 Vnite 插件处理"
            : "已写入接管一致性检查请求；等待 Vnite 插件处理";
        RecordOperation(apply ? "takeover_consistency_repair_request" : "takeover_consistency_scan_request", null, selected, $"已写入请求：{requestPath}", []);
        MessageBox.Show(this, "已提交请求。Vnite 插件通常 10 秒内处理；稍后点击刷新查看结果。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BatchReAdoptSelected()
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            RequestTakeoverConsistency(apply: true);
            return;
        }

        if (!ConfirmBatch("批量重新接管", rows, "统一检查并重写 VGC Wrapper 启动链路；不会修改标题、封面、标签和源游戏文件。")) return;
        RequestTakeoverConsistency(apply: true);
    }

    private void RequestClearTakeoverProperties(bool readopt, bool deletePluginRecord, bool dryRun)
    {
        var selected = GetSelectedRows();
        if (selected.Count == 0 && GetSelectedRow() is { } row)
        {
            selected = new List<GameRow> { row };
        }

        var rows = selected
            .Where(row => !IsRowRunning(row))
            .ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "请选择非运行中的游戏。", "清除接管属性", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var gameIds = rows
            .Select(row => string.IsNullOrWhiteSpace(row.VniteId) ? row.GameId : row.VniteId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (gameIds.Count == 0)
        {
            MessageBox.Show(this, "选中项没有 Vnite Game ID，无法清除 Vnite 启动属性。", "清除接管属性", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var title = dryRun
            ? "扫描接管残留（Dry Run）"
            : readopt
                ? "清除后重新接管"
                : deletePluginRecord
                    ? "删除记录并清除接管属性"
                    : "一键清除接管属性";
        var preview =
            $"将处理 {gameIds.Count} 个游戏。\n\n" +
            "将清除：VGC Wrapper/.lnk、--game-id、启动器路径、追踪进程、PluginDB 接管映射。\n\n" +
            "将保留：Vnite 游戏条目、标题、封面、标签、分类、有效 rootPath、NAS/G 盘游戏文件和用户存档。\n\n" +
            (dryRun ? "本次只生成预览，不写入 Vnite。\n\n" : "") +
            (readopt ? "清除后会请求插件重新接管；多个启动程序不会自动选第一个，会进入待处理。\n\n" : "") +
            (deletePluginRecord ? "同时会删除对应 VGC SQLite 记录；执行前控制台会先备份数据库。\n\n" : "") +
            "是否继续？";
        if (MessageBox.Show(this, preview, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        if (deletePluginRecord)
        {
            try
            {
                _lastUndoBackupPath = BackupDatabase(VgcSettingsStore.ResolveDatabasePath(), "clear-takeover-delete-records");
            }
            catch (Exception error)
            {
                MessageBox.Show(this, $"数据库备份失败，已停止：{error.Message}", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        var wasMaintenanceEnabled = IsMaintenanceModeEnabled();
        if (!wasMaintenanceEnabled)
        {
            SetMaintenanceMode(true, dryRun ? "control_panel_takeover_residue_dry_run" : "control_panel_clear_takeover_properties");
        }

        Directory.CreateDirectory(_commandsRoot);
        var requestPath = Path.Combine(_commandsRoot, "clear-takeover-request.json");
        var payload = JsonSerializer.Serialize(new
        {
            command = "clear-takeover-properties",
            apply = !dryRun,
            dry_run = dryRun,
            readopt,
            delete_plugin_record = deletePluginRecord,
            selected_game_ids = gameIds,
            requested_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(requestPath, payload);
        _adoptStatus.Text = dryRun
            ? "已写入接管残留 Dry Run 请求；等待 Vnite 插件处理"
            : "已写入清除接管属性请求；等待 Vnite 插件处理";
        RecordOperation(dryRun ? "takeover_residue_dry_run_request" : "clear_takeover_properties_request", null, rows, $"已写入请求：{requestPath}", deletePluginRecord && _lastUndoBackupPath is not null ? [$"数据库备份：{_lastUndoBackupPath}"] : []);

        var refreshTimer = new System.Windows.Forms.Timer { Interval = 12_000 };
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            if (!wasMaintenanceEnabled && IsMaintenanceModeEnabled())
            {
                SetMaintenanceMode(false, "");
            }
            LoadStatus();
        };
        refreshTimer.Start();
        MessageBox.Show(this, $"已提交：{title}。\nVnite 插件通常 10 秒内处理；控制台会自动刷新一次。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RecordOperation(string operationType, string? gameId, IReadOnlyList<GameRow> rows, string changedSummary, IReadOnlyList<string> warnings)
    {
        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            using var connection = factory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO operation_records (
                  operation_id, operation_type, game_id, status, created_utc, updated_utc,
                  started_utc, completed_utc, requested_changes_json, steps_json,
                  changed_items_json, warnings_json, error, rollback_status)
                VALUES (
                  $operation_id, $operation_type, $game_id, $status, $created_utc, $updated_utc,
                  $started_utc, $completed_utc, $requested_changes_json, $steps_json,
                  $changed_items_json, $warnings_json, NULL, $rollback_status);
                """;
            var now = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("$operation_id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$operation_type", operationType);
            command.Parameters.AddWithValue("$game_id", string.IsNullOrWhiteSpace(gameId) ? DBNull.Value : gameId);
            command.Parameters.AddWithValue("$status", OperationStatus.Completed.ToString());
            command.Parameters.AddWithValue("$created_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$updated_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$started_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$completed_utc", now.ToString("O"));
            command.Parameters.AddWithValue("$requested_changes_json", JsonSerializer.Serialize(new { rows = rows.Select(row => new { row.GameId, row.Name }) }));
            command.Parameters.AddWithValue("$steps_json", JsonSerializer.Serialize(new[] { changedSummary }));
            command.Parameters.AddWithValue("$changed_items_json", JsonSerializer.Serialize(rows.Select(row => row.GameId)));
            command.Parameters.AddWithValue("$warnings_json", JsonSerializer.Serialize(warnings));
            command.Parameters.AddWithValue("$rollback_status", string.IsNullOrWhiteSpace(_lastUndoBackupPath) ? "" : $"database_backup:{_lastUndoBackupPath}");
            command.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    private void UpsertManualExecutableSelection(GameRow row, string sourceRoot, string executablePath)
    {
        try
        {
            var existing = HasExistingGameRecord(row);
            var record = SaveManualExecutableSelection(row, sourceRoot, executablePath, null);
            WriteAdoptRequest([record.VniteGameId ?? record.GameId], 1);
            MessageBox.Show(
                this,
                existing
                    ? "已更新真实启动程序；已写入接管修复请求。下次启动会重新缓存并从缓存目录启动。"
                    : "已保存启动程序并创建 VGC 接管记录；已写入接管请求。请等待 Vnite 插件处理后刷新。",
                "VGC 控制台",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"保存启动程序失败：{error.Message}", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private GameRecord SaveManualExecutableSelection(GameRow row, string sourceRoot, string executablePath, GameLaunchModeOverride? launchModeOverride)
    {
        if (string.IsNullOrWhiteSpace(row.GameId))
        {
            throw new PersistenceException("missing_game_id", "这个候选没有 Game ID，不能写入接管记录。请先在 Vnite 中确认游戏条目存在。");
        }

        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var existing = repository.GetById(row.GameId) ?? repository.GetByVniteGameId(row.VniteId) ?? repository.GetByVniteGameId(row.GameId);
        var now = DateTimeOffset.UtcNow;
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? sourceRoot;
        var gameId = existing?.GameId ?? row.GameId;
        var settings = VgcSettingsStore.LoadRequired();
        var cacheRoot = existing?.CacheRoot;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = BuildCacheRoot(settings.DefaultCacheRoot, sourceRoot, gameId);
        }

        var mode = launchModeOverride ?? existing?.LaunchModeOverride ?? GameLaunchModeOverride.Inherit;
        var record = existing is null
            ? new GameRecord
            {
                GameId = gameId,
                VniteGameId = string.IsNullOrWhiteSpace(row.VniteId) ? row.GameId : row.VniteId,
                DisplayName = string.IsNullOrWhiteSpace(row.Name) ? Path.GetFileName(sourceRoot) : row.Name,
                SourceRoot = sourceRoot,
                CacheRoot = cacheRoot,
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory,
                ExecutableRelativePath = TryRelative(sourceRoot, executablePath),
                WorkingDirectoryRelativePath = TryRelative(sourceRoot, workingDirectory),
                ManagementIntent = ManagementIntent.Managed,
                LaunchModeOverride = mode,
                CacheStatus = CacheStatus.NotCached,
                CreatedUtc = now,
                UpdatedUtc = now
            }
            : existing with
            {
                SourceRoot = sourceRoot,
                ExecutablePath = executablePath,
                WorkingDirectory = workingDirectory,
                ExecutableRelativePath = TryRelative(sourceRoot, executablePath),
                WorkingDirectoryRelativePath = TryRelative(sourceRoot, workingDirectory),
                ManagementIntent = ManagementIntent.Managed,
                LaunchModeOverride = mode,
                CacheStatus = CacheStatus.NotCached,
                UpdatedUtc = now
            };

        repository.Upsert(record);
        return record;
    }

    private bool HasExistingGameRecord(GameRow row)
    {
        if (string.IsNullOrWhiteSpace(row.GameId)) return false;
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        return repository.GetById(row.GameId) is not null ||
               repository.GetByVniteGameId(row.VniteId) is not null ||
               repository.GetByVniteGameId(row.GameId) is not null;
    }
    private void UpdateManagedGame(string gameId, Func<GameRecord, GameRecord> update, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            MessageBox.Show(this, "未接管游戏不能直接修改 VGC 数据库，请先一键修复接管。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var repository = new GameRepository(factory);
            var game = repository.GetById(gameId);
            if (game is null)
            {
                MessageBox.Show(this, "数据库里找不到这个游戏记录，请先一键修复接管。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            repository.Update(update(game));
            MessageBox.Show(this, successMessage, "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"更新失败：{error.Message}", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RequestAdoptRepair()
    {
        Directory.CreateDirectory(_commandsRoot);
        var requestPath = Path.Combine(_commandsRoot, "adopt-existing-request.json");
        var payload = JsonSerializer.Serialize(new
        {
            command = "adopt-existing-apply",
            max_count = 100,
            requested_utc = DateTimeOffset.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true });
        WriteAllTextAtomic(requestPath, payload);
        _adoptStatus.Text = "已写入接管请求；等待 Vnite 插件处理，稍后会自动刷新";
        var refreshTimer = new System.Windows.Forms.Timer { Interval = 12_000 };
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            refreshTimer.Dispose();
            LoadStatus();
        };
        refreshTimer.Start();
        MessageBox.Show(this, "已请求插件执行接管修复。Vnite 正在运行时通常 10 秒内处理；控制台会自动刷新一次。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ToggleMaintenanceMode()
    {
        var enabled = IsMaintenanceModeEnabled();
        if (!enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_maintenanceModePath)!);
            var payload = JsonSerializer.Serialize(new
            {
                enabled = true,
                reason = "control_panel_manual",
                updated_utc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true });
            WriteAllTextAtomic(_maintenanceModePath, payload);
            MessageBox.Show(this, "维护模式已开启。自动接管、自动迁移和自动批量写入会暂停；手动修复仍可使用。", "维护模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            var confirm = MessageBox.Show(
                this,
                "退出维护模式后，插件会恢复自动接管和自动迁移。请确认当前修复已经完成。\n\n是否退出维护模式？",
                "维护模式",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            File.Delete(_maintenanceModePath);
            MessageBox.Show(this, "维护模式已关闭。", "维护模式", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        LoadStatus();
    }

    private bool IsMaintenanceModeEnabled()
    {
        if (!File.Exists(_maintenanceModePath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_maintenanceModePath));
            return document.RootElement.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return true;
        }
    }

    private void SetMaintenanceMode(bool enabled, string reason)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_maintenanceModePath)!);
            WriteAllTextAtomic(_maintenanceModePath, JsonSerializer.Serialize(new
            {
                enabled = true,
                reason,
                updated_utc = DateTimeOffset.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            File.Delete(_maintenanceModePath);
        }
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, path, overwrite: true);
    }

    private void OpenAdoptDiagnosticsReport()
    {
        var diagnostics = ReadAdoptDiagnostics();
        if (diagnostics is null)
        {
            MessageBox.Show(this, "暂无接管诊断。请先点击“一键修复接管”，或重启一次 Vnite。", "接管诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "adopt-existing-readable.txt");
        var lines = new List<string>
        {
            $"VGC 接管诊断 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            $"状态：{TranslateAdoptStatus(diagnostics.Status)}",
            $"原因：{diagnostics.Reason}",
            $"信息：{diagnostics.Message}",
            $"错误：{diagnostics.Error}",
            $"更新时间：{diagnostics.TimestampUtc}",
            $"接管根目录：{diagnostics.Roots.Count} 个",
        };
        lines.AddRange(diagnostics.Roots.Select(root => $"- {root}"));
        lines.Add("");
        lines.Add($"可接管候选：{diagnostics.CandidateCount}，本次已接管：{diagnostics.AdoptedCount}，剩余：{diagnostics.RemainingCount}");
        if (diagnostics.Candidates.Count > 0)
        {
            lines.Add("");
            lines.Add("候选：");
            lines.AddRange(diagnostics.Candidates.Take(200).Select(item =>
                $"- {item.Name} | {item.GameId} | {item.Status} | {item.SourceRoot} | {item.ExecutablePath}"));
        }

        if (diagnostics.Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add("跳过/需人工确认：");
            lines.AddRange(diagnostics.Skipped.Take(300).Select(item =>
                $"- {item.Name} | {TranslateSkipReason(item.Reason, item.Candidates.Count)} | {item.SourceRoot} | 候选：{string.Join("; ", item.Candidates.Take(8))}"));
        }

        lines.Add("");
        lines.Add("处理建议：");
        lines.Add("- 如果显示“不在接管根目录内”，把游戏根目录或分类根目录加入“接管已有 Vnite 游戏根目录”。");
        lines.Add("- 如果显示“多个启动程序不明确”，需要先在 Vnite 原游戏属性里选对真实 exe，再点“一键修复接管”。");
        lines.Add("- 如果显示“Vnite 没有记录游戏本体目录”，该条目缺少可定位的根目录，插件不能安全接管。");
        File.WriteAllLines(reportPath, lines);
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void OpenTakeoverResidueDiagnosticsReport()
    {
        var diagnostics = ReadTakeoverResidueDiagnostics();
        if (diagnostics is null)
        {
            MessageBox.Show(this, "暂无接管残留诊断。请先执行“扫描接管残留（Dry Run）”或“一键清除接管属性”。", "接管残留诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "takeover-residue-readable.txt");
        var lines = new List<string>
        {
            $"VGC 接管残留诊断 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            $"状态：{TranslateAdoptStatus(diagnostics.Status)}",
            $"原因：{diagnostics.Reason}",
            $"错误：{diagnostics.Error}",
            $"更新时间：{diagnostics.TimestampUtc}",
            $"接管残留：{diagnostics.ResidueCount} 个",
            "",
            "语义：接管残留表示 Vnite 游戏属性仍有 VGC Wrapper/.lnk/--game-id/追踪字段，但 VGC SQLite、PluginDB 或快捷方式链路不完整。",
            ""
        };
        if (diagnostics.Residues.Count == 0)
        {
            lines.Add("- 未发现接管残留。");
        }
        else
        {
            foreach (var item in diagnostics.Residues.Take(500))
            {
                lines.Add($"- {item.Name} | {item.GameId}");
                lines.Add($"  SourceRoot: {item.SourceRoot}");
                lines.Add($"  Launcher: {item.LauncherPath}");
                lines.Add($"  Shortcut: {item.ShortcutPath}");
                lines.Add($"  Evidence: {string.Join("; ", item.Evidence.Take(8))}");
            }
        }

        lines.Add("");
        lines.Add("建议操作：");
        lines.Add("- 先对少量游戏执行“一键清除接管属性”。");
        lines.Add("- 清除后状态会回到待检测；再执行“清除后重新接管”或“一键修复接管”。");
        lines.Add("- 多启动程序游戏不会自动选第一个，需要在手动修复里确认。");
        File.WriteAllLines(reportPath, lines);
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void OpenTakeoverConsistencyDiagnosticsReport()
    {
        var latestPath = Path.Combine(_diagnosticsRoot, "takeover-consistency-latest.json");
        var actionablePath = Path.Combine(_diagnosticsRoot, "takeover-consistency-actionable.json");
        var sourcePath = File.Exists(actionablePath) ? actionablePath : latestPath;
        if (!File.Exists(sourcePath))
        {
            MessageBox.Show(this, "暂无接管一致性诊断。请先点击“检查接管一致性”。", "接管一致性诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "takeover-consistency-readable.txt");
        var lines = new List<string>
        {
            $"VGC 接管一致性诊断 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "说明：此报告来自插件统一 TakeoverConsistencyService。它不会删除源游戏、封面、标签或存档。",
            "",
            File.ReadAllText(sourcePath)
        };
        File.WriteAllLines(reportPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void OpenHealthCheckReport()
    {
        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "health-check.txt");
        File.WriteAllLines(reportPath, BuildHealthCheckReport());
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private List<string> BuildHealthCheckReport()
    {
        var lines = new List<string>
        {
            $"VGC 自检报告 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "结论：此报告只做配置与文件存在性检查，不会启动游戏、不会复制缓存、不会删除文件。",
            ""
        };
        var settingsHealth = VgcSettingsStore.LoadWithDiagnostics();
        lines.Add($"共享配置：{settingsHealth.Status}");
        lines.Add($"共享配置路径：{settingsHealth.SettingsPath}");
        if (!string.IsNullOrWhiteSpace(settingsHealth.ErrorMessage)) lines.Add($"共享配置错误：{settingsHealth.ErrorMessage}");
        var launchMode = VgcSettingsStore.ParseLaunchMode(settingsHealth.Settings.DefaultLaunchMode);
        lines.Add($"默认启动模式：{launchMode switch
        {
            VgcLaunchMode.TranslationPreferred => "翻译 EXE 优先",
            VgcLaunchMode.MTool => "MTool 批处理优先",
            _ => "直接启动"
        }}");
        if (launchMode == VgcLaunchMode.MTool)
        {
            var mtoolPath = settingsHealth.Settings.MToolExecutablePath;
            lines.Add($"MTool 路径：{mtoolPath ?? "未配置"} | {(!string.IsNullOrWhiteSpace(mtoolPath) && File.Exists(mtoolPath) ? "存在" : "不可用")}");
        }
        lines.Add($"数据库路径：{SafeResolve(VgcSettingsStore.ResolveDatabasePath)}");
        lines.Add($"日志目录：{_logRoot} | {(Directory.Exists(_logRoot) ? "存在" : "不存在")}");
        lines.Add($"命令目录：{_commandsRoot} | {(Directory.Exists(_commandsRoot) ? "存在" : "不存在")}");
        lines.Add($"诊断目录：{_diagnosticsRoot} | {(Directory.Exists(_diagnosticsRoot) ? "存在" : "不存在")}");
        lines.Add("");

        var binRoot = AppContext.BaseDirectory;
        lines.Add($"控制台目录：{binRoot}");
        AddFileCheck(lines, "Agent", Path.Combine(binRoot, "VniteGameCache.Agent.exe"));
        AddFileCheck(lines, "Wrapper", Path.Combine(binRoot, "VniteGameCache.Wrapper.exe"));
        AddFileCheck(lines, "ControlPanel", Path.Combine(binRoot, "VniteGameCache.ControlPanel.exe"));
        lines.Add("");

        var installedManifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "vnite",
            "app",
            "plugins",
            "vnite-game-cache",
            "package.json");
        if (!File.Exists(installedManifest))
        {
            installedManifest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "vnite",
                "app",
                "plugins",
                "vnite-game-cache",
                "manifest.json");
        }
        lines.Add($"已安装插件：{ReadInstalledPluginVersion() ?? "未发现"}");
        lines.Add($"已安装插件清单：{installedManifest} | {(File.Exists(installedManifest) ? "存在" : "不存在")}");
        lines.Add("");

        var latestAdopt = Path.Combine(_diagnosticsRoot, "adopt-existing-latest.json");
        var actionableAdopt = Path.Combine(_diagnosticsRoot, "adopt-existing-actionable.json");
        AddFileCheck(lines, "最近接管诊断", latestAdopt);
        AddFileCheck(lines, "待处理接管诊断", actionableAdopt);
        lines.Add("");
        lines.Add("最近活动：");
        lines.Add(ReadLatestActivity());
        lines.Add("");
        lines.Add("复制进度：");
        lines.Add(ReadLatestCopyProgress());
        return lines;
    }

    private void ShowCleanupSuggestions()
    {
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var games = new GameRepository(factory).List();
        var suggestions = BuildCleanupSuggestions(games);
        var reportPath = Path.Combine(_diagnosticsRoot, "cache-cleanup-suggestions.txt");
        Directory.CreateDirectory(_diagnosticsRoot);
        File.WriteAllLines(reportPath, suggestions);
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void ClearSelectedCache()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "请先选择一个已接管游戏。", "清除缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"将清除本地 SSD 缓存。\n\n游戏：{row.Name}\n缓存目录：{row.CacheRoot}\n\n不会删除 NAS/G 盘源游戏，不会删除 Vnite 标题、封面、标签，不会删除 VGC 游戏记录。\n默认会进入 Quarantine；如果 Quarantine 与缓存在同一磁盘，不会立即释放空间。\n\n是否继续？",
            "清除缓存",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var cacheService = CreateCacheService(factory);
            var cacheManagement = new CacheManagementService(factory, cacheService, VgcSettingsStore.LoadRequired().ProtectConfigFiles);
            var result = cacheManagement.ClearCache(new CacheClearRequest(row.GameId, Force: false, Reason: "control_panel_clear_cache"));
            if (result.BlockedByPossibleSaveData && !result.Cleared)
            {
                var riskConfirm = MessageBox.Show(
                    this,
                    $"检测到可能的存档/用户数据，已阻止清除。\n\n检测到：\n- {string.Join("\n- ", result.DetectedUserDataPaths.Take(12))}\n\n如果你确认这些内容已经备份，可以强制移动完整缓存到 Quarantine。\n是否强制清除？",
                    "清除缓存被阻止",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (riskConfirm != DialogResult.Yes) return;
                result = cacheManagement.ClearCache(new CacheClearRequest(row.GameId, Force: true, Reason: "control_panel_clear_cache_force"));
            }

            MessageBox.Show(
                this,
                $"清除缓存结果：{TranslateClearStatus(result.Status)}\nQuarantine：{result.QuarantineEntry?.QuarantinePath ?? "无"}",
                "清除缓存",
                MessageBoxButtons.OK,
                result.Cleared ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"清除缓存失败：{error.Message}", "清除缓存", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string TranslateClearStatus(string status) => status switch
    {
        "cleared_to_quarantine" => "已移动到 Quarantine",
        "blocked_possible_save_data" => "检测到存档/用户数据，已阻止",
        "cache_in_use" => "缓存正在使用中",
        "cache_missing" => "缓存目录不存在",
        "game_not_found" => "插件记录不存在",
        _ => status
    };

    private void ScanObsoleteRecords()
    {
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var saveProfiles = new SaveProfileRepository(factory);
        var games = repository.List();
        var candidates = games
            .Select(game => new { Game = game, HasSaveProfiles = saveProfiles.ListByGame(game.GameId).Count > 0 })
            .Where(item => CanDeleteObsoleteRecord(item.Game, item.HasSaveProfiles, out _))
            .Select(item =>
            {
                CanDeleteObsoleteRecord(item.Game, item.HasSaveProfiles, out var reason);
                return new CleanupCandidate(item.Game, "过期孤立记录", reason);
            })
            .OrderBy(item => item.Game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "obsolete-records-scan.txt");
        var lines = new List<string>
        {
            $"VGC 过期记录扫描 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "此报告只扫描，不自动删除。",
            "可自动删除条件：源目录不存在、缓存不存在或为空、无 Save Profile、无存档风险、未固定、未运行。",
            "",
            $"数据库总记录：{games.Count}",
            $"可安全删除的过期记录：{candidates.Count}",
            ""
        };
        lines.AddRange(candidates.Count == 0
            ? ["- 无"]
            : candidates.Select(item => $"- {item.Game.DisplayName} | {item.Game.GameId} | {item.Reason} | Source={item.Game.SourceRoot} | Cache={item.Game.CacheRoot}"));
        File.WriteAllLines(reportPath, lines);
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void ScanDuplicateGames()
    {
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var games = new GameRepository(factory).List();
        var groups = new List<(string Kind, string Key, List<GameRecord> Games)>();
        groups.AddRange(games
            .Where(game => !string.IsNullOrWhiteSpace(game.VniteGameId))
            .GroupBy(game => game.VniteGameId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => ("相同 Vnite ID", group.Key, group.ToList())));
        groups.AddRange(games
            .Where(game => !string.IsNullOrWhiteSpace(game.SourceRoot))
            .GroupBy(game => Normalize(game.SourceRoot), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => ("相同游戏根目录", group.Key, group.ToList())));

        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, "duplicate-games-scan.txt");
        var lines = new List<string>
        {
            $"VGC 重复游戏扫描 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "此报告只扫描，不自动合并或删除。不确定项请在“游戏管理”里选中后使用“删除插件记录”。",
            "",
            $"重复组：{groups.Count}",
            ""
        };
        foreach (var group in groups)
        {
            var keeper = ChooseDuplicateKeeper(group.Games);
            lines.Add($"[{group.Kind}] {group.Key}");
            lines.Add($"建议保留：{keeper.DisplayName} | {keeper.GameId}");
            lines.AddRange(group.Games.Select(game =>
                $"- {game.DisplayName} | {game.GameId} | Cache={TranslateCacheStatus(game.CacheStatus)} {ToMb(game.CacheSizeBytes):N0} MB | Source={game.SourceRoot}"));
            lines.Add("");
        }
        if (groups.Count == 0) lines.Add("- 未发现重复组。");
        File.WriteAllLines(reportPath, lines);
        Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
    }

    private void CleanupTestGames()
    {
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var games = new GameRepository(factory);
        var candidates = games.List()
            .Where(game => IsTestGame(game) && game.CacheStatus != CacheStatus.Running)
            .ToList();

        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "没有发现可清理的测试缓存。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"发现 {candidates.Count} 个测试/重复记录。会删除对应缓存目录并移除数据库记录。是否继续？",
            "清理测试缓存",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var removed = 0;
        var cacheService = CreateCacheService(factory);
        var cacheManagement = new CacheManagementService(factory, cacheService, VgcSettingsStore.LoadRequired().ProtectConfigFiles);
        foreach (var game in candidates)
        {
            var clear = cacheManagement.ClearCache(new CacheClearRequest(
                game.GameId,
                Force: true,
                Reason: "control_panel_test_cleanup"));
            if (clear.Cleared || clear.Status == "cache_missing" || clear.Status == "game_not_found")
            {
                games.Delete(game.GameId);
                removed++;
            }
        }

        MessageBox.Show(this, $"已清理 {removed} 个测试/重复记录。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
        LoadStatus();
    }

    private void DeleteSelectedRecord()
    {
        if (GetSelectedRow() is not { } row || string.IsNullOrWhiteSpace(row.GameId))
        {
            MessageBox.Show(this, "请先选择一条记录。", "删除插件记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var game = repository.GetById(row.GameId);
        if (game is null)
        {
            MessageBox.Show(
                this,
                "这条是未接管诊断项，不是 VGC SQLite 游戏记录，不能在这里删除。\n\n如果要删除 Vnite 游戏条目，请在 Vnite 本体里删除；如果要接管，先修正根目录后点“一键修复接管”。",
                "删除插件记录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var saveProfiles = new SaveProfileRepository(factory).ListByGame(game.GameId);
        var warnings = new List<string>();
        if (game.CacheStatus is CacheStatus.Running or CacheStatus.Checking or CacheStatus.Copying or CacheStatus.Verifying)
        {
            warnings.Add($"当前缓存状态为 {TranslateCacheStatus(game.CacheStatus)}");
        }
        if (RuntimeGameLock.IsHeld(game.GameId)) warnings.Add("存在运行锁");
        if (game.IsPinned) warnings.Add("此游戏已固定");
        if (saveProfiles.Count > 0) warnings.Add($"存在 {saveProfiles.Count} 个 Save Profile");
        if (game.ContainsPossibleSaveData) warnings.Add("检测到可能存档/用户数据");
        if (game.PendingManualSaveBackup) warnings.Add("存在待手动备份存档标记");
        if (game.CacheSizeBytes > 0 || Directory.Exists(game.CacheRoot)) warnings.Add("存在缓存记录或缓存目录");

        var warningText = warnings.Count == 0
            ? "未发现缓存/存档/运行风险。"
            : $"注意：\n- {string.Join("\n- ", warnings)}\n";
        var confirm = MessageBox.Show(
            this,
            $"将临时删除 VGC 对这一个游戏的 SQLite 接管记录，并尝试删除对应 Wrapper 快捷方式。\n\n" +
            $"游戏：{game.DisplayName}\nGame ID：{game.GameId}\nVnite ID：{game.VniteGameId ?? ""}\n\n" +
            $"{warningText}\n" +
            "这不会设置永久忽略；如果游戏仍在接管根目录，之后可以重新接管并生成新记录。\n" +
            "不会删除 NAS/G 盘游戏源文件。\n不会删除 Vnite 标题、封面、标签。\n不会删除缓存目录；缓存需要另用“缓存状态/缓存目录”相关功能处理。\n\n是否继续？",
            "删除插件记录",
            MessageBoxButtons.YesNo,
            warnings.Count == 0 ? MessageBoxIcon.Question : MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var typeConfirm = MessageBox.Show(
            this,
            "这是不可撤销的数据库记录删除操作，但会先备份数据库。\n\n请再次确认只删除这条 VGC 记录。",
            "删除插件记录",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (typeConfirm != DialogResult.Yes) return;

        var wasMaintenanceEnabled = IsMaintenanceModeEnabled();
        if (!wasMaintenanceEnabled)
        {
            SetMaintenanceMode(true, "control_panel_delete_selected_record");
        }

        try
        {
            var backupPath = BackupDatabase(databaseOptions.DatabasePath, "manual-delete-selected-record");
            _lastUndoBackupPath = backupPath;
            var shortcutResult = TryDeleteShortcutForGame(game.GameId, row.ShortcutPath);
            repository.Delete(game.GameId);
            WriteManualDeleteReport(game, backupPath, shortcutResult, warnings);
            MessageBox.Show(
                this,
                $"已删除 VGC 记录。\n数据库备份：{backupPath}\n快捷方式：{shortcutResult}",
                "删除插件记录",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"删除失败：{error.Message}", "删除插件记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (!wasMaintenanceEnabled)
            {
                SetMaintenanceMode(false, "");
            }
        }
    }

    private void CleanupObsoleteAndDuplicateRecords()
    {
        var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
        var factory = new SqliteConnectionFactory(databaseOptions);
        new MigrationRunner(factory).Initialize();
        var repository = new GameRepository(factory);
        var saveProfiles = new SaveProfileRepository(factory);
        var games = repository.List();
        var plan = BuildObsoleteCleanupPlan(games, saveProfiles);

        Directory.CreateDirectory(_diagnosticsRoot);
        var dryRunPath = Path.Combine(_diagnosticsRoot, "obsolete-duplicate-cleanup-dry-run.txt");
        File.WriteAllLines(dryRunPath, BuildObsoleteCleanupReport(plan, apply: false, backupPath: ""));
        Process.Start(new ProcessStartInfo { FileName = dryRunPath, UseShellExecute = true });

        if (plan.DeleteCandidates.Count == 0)
        {
            MessageBox.Show(this, "没有发现可自动删除的安全过期/重复记录。报告里会列出需要人工确认的项目。", "清理过期/重复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"预演发现 {plan.DeleteCandidates.Count} 条可安全删除的 VGC 旧记录。\n\n" +
            "不会删除 NAS/G 盘游戏源文件，不会删除 Vnite 标题、封面、标签，不会删除有缓存或存档配置的记录。\n\n" +
            $"预演报告已打开：{dryRunPath}\n\n是否继续删除这些 VGC 旧记录？",
            "清理过期/重复",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        var wasMaintenanceEnabled = IsMaintenanceModeEnabled();
        if (!wasMaintenanceEnabled)
        {
            SetMaintenanceMode(true, "control_panel_obsolete_duplicate_cleanup");
        }

        string backupPath;
        try
        {
            backupPath = BackupDatabase(databaseOptions.DatabasePath, "obsolete-duplicate-cleanup");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"数据库备份失败，已停止清理：{error.Message}", "清理过期/重复", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (!wasMaintenanceEnabled) SetMaintenanceMode(false, "");
            return;
        }

        var removed = new List<CleanupCandidate>();
        var failed = new List<string>();
        foreach (var candidate in plan.DeleteCandidates)
        {
            try
            {
                repository.Delete(candidate.Game.GameId);
                removed.Add(candidate);
            }
            catch (Exception error)
            {
                failed.Add($"{candidate.Game.DisplayName} | {candidate.Game.GameId} | {error.Message}");
            }
        }

        if (!wasMaintenanceEnabled)
        {
            SetMaintenanceMode(false, "");
        }

        var applyReportPath = Path.Combine(_diagnosticsRoot, "obsolete-duplicate-cleanup-apply.txt");
        File.WriteAllLines(applyReportPath, BuildObsoleteCleanupReport(
            plan with { DeleteCandidates = removed, ManualReviewItems = plan.ManualReviewItems.Concat(failed.Select(item => new ManualReviewItem("删除失败", item, ""))).ToList() },
            apply: true,
            backupPath));
        Process.Start(new ProcessStartInfo { FileName = applyReportPath, UseShellExecute = true });

        MessageBox.Show(
            this,
            $"清理完成：删除 {removed.Count} 条，失败 {failed.Count} 条。\n数据库备份：{backupPath}",
            "清理过期/重复",
            MessageBoxButtons.OK,
            failed.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        LoadStatus();
    }

    private static CacheService CreateCacheService(SqliteConnectionFactory factory)
    {
        var logRoot = Path.Combine(VgcSettingsStore.ResolveLogRoot(), "robocopy");
        return new CacheService(
            new CacheStore(factory),
            new CopyPlanner(),
            new RobocopyCommandBuilder(logRoot),
            new RobocopyRunner(),
            new CacheVerifier(),
            VgcSettingsStore.ResolveMinimumFreeSpaceBytes() ?? CachePathSafety.DefaultMinimumFreeSpaceBytes);
    }

    private static ObsoleteCleanupPlan BuildObsoleteCleanupPlan(IReadOnlyList<GameRecord> games, SaveProfileRepository saveProfiles)
    {
        var deleteById = new Dictionary<string, CleanupCandidate>(StringComparer.OrdinalIgnoreCase);
        var manual = new List<ManualReviewItem>();

        foreach (var game in games)
        {
            var hasSaveProfiles = saveProfiles.ListByGame(game.GameId).Count > 0;
            if (CanDeleteObsoleteRecord(game, hasSaveProfiles, out var reason))
            {
                deleteById.TryAdd(game.GameId, new CleanupCandidate(game, "过期孤立记录", reason));
            }
        }

        AddDuplicateGroups(
            "相同 Vnite ID",
            games.Where(game => !string.IsNullOrWhiteSpace(game.VniteGameId)).GroupBy(game => game.VniteGameId!, StringComparer.OrdinalIgnoreCase),
            deleteById,
            manual,
            saveProfiles);
        AddDuplicateGroups(
            "相同游戏根目录",
            games.Where(game => !string.IsNullOrWhiteSpace(game.SourceRoot)).GroupBy(game => Normalize(game.SourceRoot), StringComparer.OrdinalIgnoreCase),
            deleteById,
            manual,
            saveProfiles);

        return new ObsoleteCleanupPlan(
            deleteById.Values.OrderBy(item => item.Game.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            manual.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            games.Count);
    }

    private static void AddDuplicateGroups(
        string duplicateKind,
        IEnumerable<IGrouping<string, GameRecord>> groups,
        Dictionary<string, CleanupCandidate> deleteById,
        List<ManualReviewItem> manual,
        SaveProfileRepository saveProfiles)
    {
        foreach (var group in groups.Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1))
        {
            var items = group.ToList();
            var keeper = ChooseDuplicateKeeper(items);
            foreach (var game in items.Where(game => !string.Equals(game.GameId, keeper.GameId, StringComparison.OrdinalIgnoreCase)))
            {
                var hasSaveProfiles = saveProfiles.ListByGame(game.GameId).Count > 0;
                if (CanDeleteObsoleteRecord(game, hasSaveProfiles, out var reason))
                {
                    deleteById.TryAdd(game.GameId, new CleanupCandidate(game, duplicateKind, $"{reason}；保留：{keeper.DisplayName} / {keeper.GameId}"));
                }
                else
                {
                    manual.Add(new ManualReviewItem(
                        duplicateKind,
                        game.DisplayName,
                        $"{game.GameId} 与 {keeper.GameId} 可能重复，但不满足自动删除条件：{DescribeKeepReason(game, hasSaveProfiles)}"));
                }
            }
        }
    }

    private static GameRecord ChooseDuplicateKeeper(IReadOnlyList<GameRecord> games)
    {
        return games
            .OrderByDescending(game => Directory.Exists(game.SourceRoot))
            .ThenByDescending(game => Directory.Exists(game.CacheRoot))
            .ThenByDescending(game => game.LastPlayedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(game => game.CacheSizeBytes)
            .ThenByDescending(game => game.UpdatedUtc)
            .ThenBy(game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .First();
    }

    private static bool CanDeleteObsoleteRecord(GameRecord game, bool hasSaveProfiles, out string reason)
    {
        reason = DescribeKeepReason(game, hasSaveProfiles);
        if (!string.IsNullOrWhiteSpace(reason)) return false;

        var sourceMissing = string.IsNullOrWhiteSpace(game.SourceRoot) || !Directory.Exists(game.SourceRoot);
        var cacheAbsent = string.IsNullOrWhiteSpace(game.CacheRoot) || !Directory.Exists(game.CacheRoot) || IsEmptyDirectory(game.CacheRoot);
        if (!sourceMissing)
        {
            reason = "游戏根目录仍存在";
            return false;
        }
        if (!cacheAbsent)
        {
            reason = "缓存目录仍存在且非空";
            return false;
        }

        reason = "游戏根目录不存在，缓存不存在或为空，且无存档/运行/固定风险";
        return true;
    }

    private static string DescribeKeepReason(GameRecord game, bool hasSaveProfiles)
    {
        if (game.ManagementIntent != ManagementIntent.Managed) return $"管理意图为 {game.ManagementIntent}";
        if (game.IsPinned) return "已固定";
        if (RuntimeGameLock.IsHeld(game.GameId)) return "运行锁存在";
        if (game.CacheStatus is CacheStatus.Running or CacheStatus.Checking or CacheStatus.Copying or CacheStatus.Verifying) return $"缓存状态为 {game.CacheStatus}";
        if (hasSaveProfiles) return "存在 Save Profile";
        if (game.ContainsPossibleSaveData) return "检测到可能存档/用户数据";
        if (game.PendingManualSaveBackup) return "存在待手动备份标记";
        if (game.CacheSizeBytes > 0) return "数据库记录显示有缓存占用";
        return "";
    }

    private static bool IsEmptyDirectory(string directory)
    {
        try
        {
            return Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    private static List<string> BuildObsoleteCleanupReport(ObsoleteCleanupPlan plan, bool apply, string backupPath)
    {
        var lines = new List<string>
        {
            $"VGC 过期/重复记录清理{(apply ? "执行报告" : "预演报告")} - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "安全边界：",
            "- 只删除 VGC SQLite 中可证明无用的游戏记录。",
            "- 不删除 NAS/G 盘游戏源文件。",
            "- 不删除 Vnite 标题、封面、标签、评分和介绍。",
            "- 不删除有缓存、运行中、固定、Save Profile 或存档风险的记录。",
            "- 不确定的重复项只列入人工确认，不自动合并。",
            ""
        };
        if (apply)
        {
            lines.Add($"数据库备份：{backupPath}");
            lines.Add("");
        }

        lines.Add($"数据库总记录：{plan.TotalGames}");
        lines.Add($"可自动删除：{plan.DeleteCandidates.Count}");
        lines.Add($"需人工确认：{plan.ManualReviewItems.Count}");
        lines.Add("");

        lines.Add("将删除/已删除：");
        if (plan.DeleteCandidates.Count == 0)
        {
            lines.Add("- 无");
        }
        else
        {
            lines.AddRange(plan.DeleteCandidates.Select(item =>
                $"- {item.Game.DisplayName} | {item.Game.GameId} | {item.Kind} | {item.Reason} | Source={item.Game.SourceRoot} | Cache={item.Game.CacheRoot}"));
        }

        lines.Add("");
        lines.Add("需人工确认：");
        if (plan.ManualReviewItems.Count == 0)
        {
            lines.Add("- 无");
        }
        else
        {
            lines.AddRange(plan.ManualReviewItems.Take(500).Select(item =>
                $"- {item.DisplayName} | {item.Kind} | {item.Reason}"));
        }

        lines.Add("");
        lines.Add("下一步建议：");
        lines.Add("- 清理后点击“刷新”确认记录数下降。");
        lines.Add("- 如果 Vnite 里还有无路径游戏，先在 Vnite 属性修正“根目录”，再点“一键修复接管”。");
        lines.Add("- 人工确认项不要批量删除，先双击“游戏根目录”或“启动程序”列修复。");
        return lines;
    }

    private static string BackupDatabase(string databasePath, string reason)
    {
        if (!File.Exists(databasePath)) throw new FileNotFoundException("数据库不存在", databasePath);
        var backupRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "backups", "database");
        Directory.CreateDirectory(backupRoot);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMddHHmmss");
        var backupPath = Path.Combine(backupRoot, $"vnite-game-cache.{reason}.{timestamp}.db");
        File.Copy(databasePath, backupPath, overwrite: false);
        return backupPath;
    }

    private static string TryDeleteShortcutForGame(string gameId, string shortcutPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(shortcutPath)) candidates.Add(shortcutPath);
        var detected = FindLauncherShortcutPath(gameId);
        if (!string.IsNullOrWhiteSpace(detected)) candidates.Add(detected);

        var launcherRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "launchers");
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                if (!IsPathUnder(candidate, launcherRoot)) return $"跳过非 VGC 快捷方式：{candidate}";
                if (!Path.GetFileName(candidate).Contains(gameId, StringComparison.OrdinalIgnoreCase)) return $"跳过无法确认归属的快捷方式：{candidate}";
                File.Delete(candidate);
                return $"已删除 {candidate}";
            }
            catch (Exception error)
            {
                return $"删除失败：{candidate}；{error.Message}";
            }
        }

        return "未发现对应快捷方式";
    }

    private void WriteManualDeleteReport(GameRecord game, string backupPath, string shortcutResult, IReadOnlyList<string> warnings)
    {
        Directory.CreateDirectory(_diagnosticsRoot);
        var reportPath = Path.Combine(_diagnosticsRoot, $"manual-delete-{SanitizeReportFileName(game.DisplayName)}-{DateTimeOffset.Now:yyyyMMddHHmmss}.txt");
        var lines = new List<string>
        {
            $"VGC 手动删除记录 - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
            "",
            "删除范围：仅 VGC SQLite 游戏记录和可确认归属的 Wrapper 快捷方式。",
            "未删除：NAS/G 盘游戏源文件、Vnite 元数据、缓存目录、存档目录。",
            "",
            $"游戏：{game.DisplayName}",
            $"Game ID：{game.GameId}",
            $"Vnite ID：{game.VniteGameId ?? ""}",
            $"SourceRoot：{game.SourceRoot}",
            $"CacheRoot：{game.CacheRoot}",
            $"ExecutablePath：{game.ExecutablePath}",
            $"数据库备份：{backupPath}",
            $"快捷方式处理：{shortcutResult}",
            ""
        };
        if (warnings.Count > 0)
        {
            lines.Add("删除前风险提示：");
            lines.AddRange(warnings.Select(item => $"- {item}"));
            lines.Add("");
        }
        lines.Add("如需恢复：关闭 Vnite 后，用数据库备份替换当前 vnite-game-cache.db，或重新接管该游戏。");
        File.WriteAllLines(reportPath, lines);
    }

    private void BackupVgcState()
    {
        try
        {
            Directory.CreateDirectory(_stateBackupRoot);
            var path = Path.Combine(_stateBackupRoot, $"vgc-state-{DateTimeOffset.Now:yyyyMMddHHmmss}.zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            AddFileToArchive(archive, VgcSettingsStore.ResolveDatabasePath(), "data/vnite-game-cache.db");
            AddFileToArchive(archive, Path.Combine(VgcSettingsStore.LocalAppDataRoot, "config", "settings.json"), "config/settings.json");
            AddDirectoryToArchive(archive, Path.Combine(VgcSettingsStore.LocalAppDataRoot, "launchers"), "launchers");
            AddDirectoryToArchive(archive, _commandsRoot, "commands");
            _lastUndoBackupPath = BackupDatabase(VgcSettingsStore.ResolveDatabasePath(), "state-backup");
            MessageBox.Show(this, $"已备份 VGC 状态：{path}", "备份状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
            OpenFileLocation(path);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"备份失败：{error.Message}", "备份状态", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreVgcState()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 VGC 状态备份 zip",
            InitialDirectory = Directory.Exists(_stateBackupRoot) ? _stateBackupRoot : VgcSettingsStore.LocalAppDataRoot,
            Filter = "VGC 状态备份 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var confirm = MessageBox.Show(this, "恢复会覆盖 VGC 数据库和共享配置。不会删除 NAS/G 盘游戏源文件。\n\n建议先关闭 Vnite，是否继续？", "恢复状态", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            _lastUndoBackupPath = BackupDatabase(VgcSettingsStore.ResolveDatabasePath(), "before-state-restore");
            using var archive = ZipFile.OpenRead(dialog.FileName);
            ExtractArchiveFile(archive, "data/vnite-game-cache.db", VgcSettingsStore.ResolveDatabasePath());
            ExtractArchiveFile(archive, "config/settings.json", Path.Combine(VgcSettingsStore.LocalAppDataRoot, "config", "settings.json"));
            RecordOperation("restore_state_backup", null, [], $"已恢复 {dialog.FileName}", []);
            MessageBox.Show(this, $"已恢复状态。恢复前数据库备份：{_lastUndoBackupPath}", "恢复状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"恢复失败：{error.Message}", "恢复状态", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UndoLastOperation()
    {
        if (string.IsNullOrWhiteSpace(_lastUndoBackupPath) || !File.Exists(_lastUndoBackupPath))
        {
            MessageBox.Show(this, "当前会话没有可撤销的数据库备份。", "撤销", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(this, $"将用备份覆盖当前 VGC 数据库：\n{_lastUndoBackupPath}\n\n建议先关闭 Vnite。是否继续？", "撤销上次操作", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var currentPath = VgcSettingsStore.ResolveDatabasePath();
            BackupDatabase(currentPath, "before-undo");
            File.Copy(_lastUndoBackupPath, currentPath, overwrite: true);
            MessageBox.Show(this, "已撤销到上次操作前的数据库状态。", "撤销", MessageBoxButtons.OK, MessageBoxIcon.Information);
            LoadStatus();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"撤销失败：{error.Message}", "撤销", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RunReleaseValidation()
    {
        try
        {
            var validationRoot = Path.Combine(_projectRoot, "dist", "current", "validation");
            Directory.CreateDirectory(validationRoot);
            var reportPath = Path.Combine(validationRoot, $"phase-7.3-release-validation-{DateTimeOffset.Now:yyyyMMddHHmmss}.md");
            var jsonPath = Path.ChangeExtension(reportPath, ".json");
            var databaseOptions = new DatabaseOptions(VgcSettingsStore.ResolveDatabasePath());
            var factory = new SqliteConnectionFactory(databaseOptions);
            new MigrationRunner(factory).Initialize();
            var games = new GameRepository(factory).List();
            var simulated = Enumerable.Range(0, 1000)
                .Select(index => new GameRow(index % 7 == 0 ? "⚠" : "✓", $"模拟游戏 {index:0000}", $"sim-{index}", $"sim-{index}", ManagementIntent.Managed, "已接管", index % 7 == 0 ? "需处理" : "正常", index % 3 == 0 ? "已缓存" : "未缓存", "", index % 7 == 0 ? "模拟问题" : "", "跟随默认", index, "", @"G:\Sim", $@"E:\VniteGameCache\Games\sim-{index}", "Game.exe", "", "Game.exe"))
                .ToList();
            var watch = Stopwatch.StartNew();
            var filtered = simulated
                .Where(row => row.Name.Contains("99", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(row.Issue))
                .ToList();
            watch.Stop();
            var checks = new Dictionary<string, object?>
            {
                ["database_path"] = databaseOptions.DatabasePath,
                ["database_exists"] = File.Exists(databaseOptions.DatabasePath),
                ["game_count"] = games.Count,
                ["agent_exists"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Agent.exe")),
                ["wrapper_exists"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "VniteGameCache.Wrapper.exe")),
                ["control_panel_exists"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "VniteGameCache.ControlPanel.exe")),
                ["settings_status"] = VgcSettingsStore.LoadWithDiagnostics().Status.ToString(),
                ["simulated_rows"] = simulated.Count,
                ["simulated_filter_result"] = filtered.Count,
                ["simulated_filter_ms"] = watch.ElapsedMilliseconds
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.WriteAllLines(reportPath,
            [
                "# Phase 7.3 Release Validation",
                "",
                $"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}",
                $"- 数据库：{databaseOptions.DatabasePath}",
                $"- 真实记录数：{games.Count}",
                $"- 1000 行模拟筛选：{watch.ElapsedMilliseconds} ms，结果 {filtered.Count} 行",
                $"- Agent：{checks["agent_exists"]}",
                $"- Wrapper：{checks["wrapper_exists"]}",
                $"- ControlPanel：{checks["control_panel_exists"]}",
                "",
                "说明：该校验不会启动真实游戏，不会复制缓存，不会删除文件。"
            ]);
            Process.Start(new ProcessStartInfo { FileName = reportPath, UseShellExecute = true });
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"发布校验失败：{error.Message}", "发布校验", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        if (File.Exists(filePath))
        {
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Fastest);
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string directoryPath, string entryRoot)
    {
        if (!Directory.Exists(directoryPath)) return;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryRoot}/{relative}", CompressionLevel.Fastest);
        }
    }

    private static void ExtractArchiveFile(ZipArchive archive, string entryName, string destinationPath)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private string BuildTaskPageText()
    {
        var lines = new List<string>
        {
            "缓存任务",
            "",
            "当前复制进度：",
            ReadLatestCopyProgress(),
            "",
            "说明：",
            "- “立即缓存”只对选中的已接管游戏执行 Agent prepare-cache。",
            "- “清除选中缓存”会走安全清除路径，默认移动到 Quarantine。",
            "- Quarantine 与缓存位于同一磁盘时，不会立即释放空间。",
            "",
            "最近活动：",
            ReadLatestActivity()
        };
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildRepairPageText()
    {
        var diagnostics = ReadAdoptDiagnostics();
        var lines = new List<string>
        {
            "修复中心",
            "",
            "推荐顺序：",
            "1. 先在“游戏管理”搜索并选中问题游戏。",
            "2. 双击“问题”或右键“手动修复”，选择正确游戏根目录或启动程序。",
            "3. 点击“自动修复”让 Vnite 启动字段重新接管到 Wrapper 快捷方式。",
            "4. 点击“立即缓存”验证缓存链路。",
            "",
            "接管诊断：",
            diagnostics is null
                ? "暂无接管诊断。"
                : $"{TranslateAdoptStatus(diagnostics.Status)}；候选 {diagnostics.CandidateCount}，已接管 {diagnostics.AdoptedCount}，剩余 {diagnostics.RemainingCount}，跳过 {diagnostics.Skipped.Count}。",
            "",
            "删除语义：",
            "- “清除缓存”：只处理本地缓存。",
            "- “删除插件记录”：只删除 VGC SQLite 记录和可确认的 VGC 快捷方式。",
            "- “扫描过期记录/扫描重复游戏”：只生成报告，不自动删除。"
        };
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildOperationPageText()
    {
        var latestLog = LatestFile(_logRoot, "*.jsonl");
        var lines = new List<string>
        {
            "操作记录",
            "",
            $"日志目录：{_logRoot}",
            $"最近日志：{latestLog ?? "无"}",
            ""
        };
        if (latestLog is null)
        {
            lines.Add("暂无日志。");
        }
        else
        {
            lines.AddRange(TailLines(latestLog, 60).Select(FormatLaunchLogLine));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildSettingsPageText()
    {
        var settingsHealth = VgcSettingsStore.LoadWithDiagnostics();
        var lines = new List<string>
        {
            "设置与诊断",
            "",
            $"共享配置状态：{settingsHealth.Status}",
            $"共享配置路径：{settingsHealth.SettingsPath}",
            $"数据库路径：{SafeResolve(VgcSettingsStore.ResolveDatabasePath)}",
            $"日志目录：{_logRoot}",
            $"命令目录：{_commandsRoot}",
            $"诊断目录：{_diagnosticsRoot}",
            $"项目目录：{_projectRoot}",
            $"维护模式：{(IsMaintenanceModeEnabled() ? "已开启" : "关闭")}",
            "",
            "说明：",
            "- 控制台、Agent、Wrapper 优先读取同一份共享 settings.json。",
            "- 自检报告不会启动游戏、不会复制缓存、不会删除文件。"
        };
        if (!string.IsNullOrWhiteSpace(settingsHealth.ErrorMessage))
        {
            lines.Add("");
            lines.Add($"配置错误：{settingsHealth.ErrorMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string SanitizeReportFileName(string value)
    {
        var sanitized = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "game" : sanitized[..Math.Min(48, sanitized.Length)];
    }

    private void CreateMToolBridgeProbe()
    {
        var outputDirectory = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "mtool-bridge");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "vgc-mtool-bridge-probe.js");
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "integrations", "mtool", "VGC-MTool-Bridge", "probe.js");
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, path, true);
        }
        else
        {
            var content = string.Join(Environment.NewLine, new[]
            {
                "// @AutoLoad",
                "// VGC MTool Bridge Probe v1.0.0",
                "(async () => {",
                "  const result = { bridgeProbeVersion: '1.0.0', protocolVersion: 1, getGameCWD: typeof getGameCWD === 'function', gameCwd: null, error: null };",
                "  try { if (result.getGameCWD) result.gameCwd = await getGameCWD(); } catch (error) { result.error = String(error); }",
                "  try { if (typeof console !== 'undefined' && typeof console.log === 'function') console.log('[VGC MTool Bridge Probe]', result); } catch (_) {}",
                "})();",
                ""
            });
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        MessageBox.Show(this, $"已生成 MTool Bridge 探针脚本：{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}请在 MTool/MCenter 中按官方脚本方式导入。Bridge 不可用时，VGC 仍保持薄适配启动。", "MTool Bridge 探针", MessageBoxButtons.OK, MessageBoxIcon.Information);
        OpenFileLocation(path);
    }

    private void OpenMToolCenter()
    {
        var settings = VgcSettingsStore.LoadWithDiagnostics().Settings;
        var executablePath = settings.MToolExecutablePath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            MessageBox.Show(this, $"MTool.exe 未配置或不存在：{executablePath ?? "<空>"}", "MTool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }

    private void OpenMToolLinksRoot()
    {
        OpenFolder(Path.Combine(VgcSettingsStore.LocalAppDataRoot, "mtool-links"));
    }

    private void OpenSelectedMToolTemplate()
    {
        if (GetSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "请先选择一个游戏。", "MTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var templatePath = FindMToolTemplatePath(row.CacheRoot, row.SourceRoot);
        if (templatePath is null)
        {
            MessageBox.Show(this, "未在缓存目录或游戏根目录发现已识别的 MTool 启动模板。", "MTool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        OpenFileLocation(templatePath);
    }

    private static string BuildMToolDetail(GameRow row)
    {
        var settings = VgcSettingsStore.LoadWithDiagnostics().Settings;
        var defaultMode = VgcSettingsStore.ParseLaunchMode(settings.DefaultLaunchMode);
        var effectiveMode = row.LaunchMode == "跟随默认" ? defaultMode.ToString() : row.LaunchMode;
        var templatePath = FindMToolTemplatePath(row.CacheRoot, row.SourceRoot);
        var template = templatePath is null ? null : TryReadMToolTemplate(templatePath);
        var executablePath = settings.MToolExecutablePath ?? "";
        var mtoolRoot = string.IsNullOrWhiteSpace(executablePath) ? "" : Path.GetDirectoryName(executablePath) ?? "";
        var injectPath = string.IsNullOrWhiteSpace(mtoolRoot) ? "" : Path.Combine(mtoolRoot, "loaders", "inject.exe");
        var nwPath = string.IsNullOrWhiteSpace(mtoolRoot) ? "" : Path.Combine(mtoolRoot, "nw.exe");
        var loaderPath = template is null || string.IsNullOrWhiteSpace(mtoolRoot) ? "" : Path.Combine(mtoolRoot, template.LoaderRelativePath);
        var lines = new List<string>
        {
            $"有效启动模式：{effectiveMode}",
            $"MTool.exe：{(File.Exists(executablePath) ? executablePath : "未配置或不存在")}",
            $"启动模板：{(templatePath is null ? "未发现" : templatePath)}"
        };

        if (template is not null)
        {
            lines.Add($"模板目标程序：{template.GameExecutableName}");
            lines.Add($"当前组件：inject={(File.Exists(injectPath) ? "存在" : "缺失")}; loader={(File.Exists(loaderPath) ? "存在" : "缺失")}; nw={(File.Exists(nwPath) ? "存在" : "缺失")}");
            lines.Add("说明：VGC 只把 BAT 当模板，启动时改用当前 MTool 组件和 SSD 缓存副本，不执行旧绝对路径。");
        }
        else
        {
            lines.Add("说明：未检测到 MTool 模板时，MTool 模式会退回翻译优先/直接启动。最好只对确实有“与工具一同启动.bat”的游戏启用 MTool。");
        }

        return string.Join("\r\n", lines);
    }

    private static string? FindMToolTemplatePath(params string[] roots)
    {
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var name in MToolBatchNames)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate) && TryReadMToolTemplate(candidate) is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static MToolTemplateProbe? TryReadMToolTemplate(string batchPath)
    {
        try
        {
            var text = File.ReadAllText(batchPath);
            var preferredName = MToolBatchNames.Contains(Path.GetFileName(batchPath), StringComparer.OrdinalIgnoreCase);
            var recognized = text.Contains("automatically generated by the MTool", StringComparison.OrdinalIgnoreCase)
                             || (preferredName && text.Contains("loaders\\inject.exe", StringComparison.OrdinalIgnoreCase));
            if (!recognized) return null;
            var match = MToolInjectCommand.Match(text);
            if (!match.Success) return new MToolTemplateProbe("", "");
            return new MToolTemplateProbe(Path.GetFileName(match.Groups["game"].Value), ExtractMToolLoaderRelativePath(match.Groups["loader"].Value));
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractMToolLoaderRelativePath(string loaderPath)
    {
        var normalized = loaderPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segment = $"{Path.DirectorySeparatorChar}loaders{Path.DirectorySeparatorChar}";
        var index = normalized.LastIndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return normalized[(index + 1)..];
        return normalized.StartsWith($"loaders{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? normalized : "";
    }

    private void OpenLatestLaunchDetail()
    {
        var latestLog = LatestFile(_logRoot, "*.jsonl");
        if (latestLog is null)
        {
            MessageBox.Show(this, "暂无启动日志。", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var detailPath = Path.Combine(_diagnosticsRoot, "latest-launch-detail.txt");
        Directory.CreateDirectory(_diagnosticsRoot);
        var lines = TailLines(latestLog, 80)
            .Select(FormatLaunchLogLine)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        File.WriteAllLines(detailPath, lines.Count == 0 ? [$"最近日志无可解析事件：{latestLog}"] : lines);
        Process.Start(new ProcessStartInfo { FileName = detailPath, UseShellExecute = true });
    }

    private static void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (!Directory.Exists(folder))
        {
            MessageBox.Show($"目录不存在或当前不可访问：{folder}", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = Quote(folder),
            UseShellExecute = true
        });
    }

    private static void OpenFileLocation(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            MessageBox.Show($"文件不存在或当前不可访问：{filePath}", "VGC 控制台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,{Quote(filePath)}",
            UseShellExecute = true
        });
    }

    private string ReadLatestActivity()
    {
        if (!Directory.Exists(_logRoot))
        {
            return "暂无日志";
        }

        var latestLog = Directory.EnumerateFiles(_logRoot, "*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latestLog is null)
        {
            return "暂无日志";
        }

        var lastLine = File.ReadLines(latestLog).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (lastLine is null)
        {
            return $"日志存在，但暂无事件：{latestLog}";
        }

        try
        {
            using var document = JsonDocument.Parse(lastLine);
            var root = document.RootElement;
            var component = GetString(root, "component") ?? "plugin";
            var eventName = GetString(root, "event") ?? "unknown";
            var result = GetString(root, "result") ?? "";
            var time = GetString(root, "timestamp_utc") ?? "";
            return $"{component} / {eventName} / {result} / {time}";
        }
        catch (JsonException)
        {
            return $"最近日志：{latestLog}";
        }
    }

    private string ReadLatestCopyProgress()
    {
        var latestProgress = LatestFile(_logRoot, "*.progress.json");
        if (latestProgress is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(latestProgress));
                var root = document.RootElement;
                var phase = GetString(root, "phase") ?? "Copying";
                var copiedBytes = GetLong(root, "copied_bytes");
                var totalBytes = GetLong(root, "total_bytes");
                var copiedFiles = GetInt(root, "copied_files");
                var totalFiles = GetInt(root, "total_files");
                var speed = GetDouble(root, "bytes_per_second");
                var currentFile = GetString(root, "current_file");
                var percent = totalBytes <= 0 ? 0 : Math.Clamp((int)Math.Round(copiedBytes * 100d / totalBytes), 0, 100);
                var remainingFiles = Math.Max(0, totalFiles - copiedFiles);
                var remainingSeconds = speed > 0 && totalBytes > copiedBytes ? (totalBytes - copiedBytes) / speed : 0;
                var remainingText = remainingSeconds > 0 ? $"，预计 {FormatDuration(TimeSpan.FromSeconds(remainingSeconds))}" : string.Empty;
                var fileText = string.IsNullOrWhiteSpace(currentFile) ? string.Empty : $"，当前 {Path.GetFileName(currentFile)}";
                if (phase.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return $"复制完成：{totalFiles:N0} 个文件，{ToMb(totalBytes):N0} MB";
                }

                return $"{TranslateCopyPhase(phase)} {percent}%（估算），{speed / 1024 / 1024:N1} MB/s，剩余约 {remainingFiles:N0} 个文件{remainingText}{fileText}";
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
            }
        }

        var latestRobocopyLog = LatestFile(_logRoot, "*.robocopy.log");
        if (latestRobocopyLog is null) return "暂无复制任务";

        var progress = TailLines(latestRobocopyLog, 80)
            .Select(ExtractPercent)
            .Where(value => value is not null)
            .LastOrDefault();
        var updated = File.GetLastWriteTime(latestRobocopyLog).ToString("yyyy-MM-dd HH:mm:ss");
        return progress is null
            ? $"最近复制日志：{Path.GetFileName(latestRobocopyLog)}，更新时间 {updated}"
            : $"{Path.GetFileName(latestRobocopyLog)}：{progress}% ，更新时间 {updated}";
    }

    private IReadOnlyDictionary<string, string> ReadLaunchStates()
    {
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_logRoot)) return states;

        foreach (var log in Directory.EnumerateFiles(_logRoot, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(3).Reverse())
        {
            foreach (var line in TailLines(log, 250))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var gameId = GetString(root, "game_id") ?? GetString(root, "gameId");
                    if (string.IsNullOrWhiteSpace(gameId)) continue;
                    var eventName = GetString(root, "event") ?? "";
                    states[gameId] = TranslateLaunchEvent(eventName);
                }
                catch (JsonException)
                {
                }
            }
        }

        return states;
    }

    private static string TranslateLaunchEvent(string eventName) => eventName switch
    {
        "received_request" => "收到启动请求",
        "agent_started" => "Agent 已启动",
        "agent_ready" => "缓存已就绪",
        "game_started" => "游戏已启动",
        "wrapper_detached" => "已交给游戏进程",
        "duplicate_launch_ignored" => "已在运行，忽略重复启动",
        "launch_failed" => "启动失败",
        "agent_rejected" => "Agent 拒绝",
        "cache_executable_missing" => "缓存启动程序缺失",
        _ => eventName
    };

    private static string FormatLaunchLogLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var time = GetString(root, "timestamp_utc") ?? "";
            var gameId = GetString(root, "game_id") ?? "";
            var component = GetString(root, "component") ?? "";
            var eventName = GetString(root, "event") ?? "";
            var result = GetString(root, "result") ?? "";
            var executable = GetString(root, "executable_path") ?? "";
            var error = GetString(root, "error") ?? "";
            return $"{time} | {gameId} | {component} | {TranslateLaunchEvent(eventName)} | {result} | {executable} | {error}";
        }
        catch (JsonException)
        {
            return line;
        }
    }

    private AdoptDiagnostics? ReadAdoptDiagnostics()
    {
        var actionablePath = Path.Combine(_diagnosticsRoot, "adopt-existing-actionable.json");
        var latestPath = Path.Combine(_diagnosticsRoot, "adopt-existing-latest.json");
        var path = File.Exists(actionablePath) ? actionablePath : latestPath;
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new AdoptDiagnostics(
                GetString(root, "status") ?? "",
                GetString(root, "reason") ?? "",
                GetString(root, "message") ?? "",
                GetString(root, "error") ?? "",
                GetString(root, "timestamp_utc") ?? "",
                GetInt(root, "adopted_count"),
                GetInt(root, "candidate_count"),
                GetInt(root, "remaining_count"),
                ReadStringArray(root, "roots"),
                ReadCandidates(root),
                ReadSkipped(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TakeoverResidueDiagnostics? ReadTakeoverResidueDiagnostics()
    {
        var actionablePath = Path.Combine(_diagnosticsRoot, "takeover-residue-actionable.json");
        var latestPath = Path.Combine(_diagnosticsRoot, "takeover-residue-latest.json");
        var path = File.Exists(actionablePath) ? actionablePath : latestPath;
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new TakeoverResidueDiagnostics(
                GetString(root, "status") ?? "",
                GetString(root, "reason") ?? "",
                GetString(root, "error") ?? "",
                GetString(root, "timestamp_utc") ?? "",
                GetInt(root, "residue_count"),
                ReadResidues(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<TakeoverResidueItem> ReadResidues(JsonElement root)
    {
        var result = new List<TakeoverResidueItem>();
        if (!root.TryGetProperty("residues", out var residues) || residues.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in residues.EnumerateArray())
        {
            result.Add(new TakeoverResidueItem(
                GetString(item, "game_id") ?? "",
                GetString(item, "name") ?? "",
                GetString(item, "source_root") ?? GetString(item, "root_path") ?? "",
                GetString(item, "launcher_path") ?? "",
                GetString(item, "shortcut_path") ?? "",
                ReadEvidence(item)));
        }
        return result;
    }

    private static List<string> ReadEvidence(JsonElement item)
    {
        var result = new List<string>();
        if (!item.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in evidence.EnumerateArray())
        {
            var field = GetString(entry, "field") ?? "";
            var value = GetString(entry, "value") ?? "";
            if (!string.IsNullOrWhiteSpace(field) || !string.IsNullOrWhiteSpace(value))
            {
                result.Add($"{field}={value}");
            }
        }
        return result;
    }

    private static List<SkippedAdoptItem> ReadSkipped(JsonElement root)
    {
        var result = new List<SkippedAdoptItem>();
        if (!root.TryGetProperty("skipped", out var skipped) || skipped.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in skipped.EnumerateArray())
        {
            var candidates = new List<string>();
            if (item.TryGetProperty("candidates", out var candidateArray) && candidateArray.ValueKind == JsonValueKind.Array)
            {
                candidates.AddRange(candidateArray.EnumerateArray()
                    .Select(candidate => GetString(candidate, "name") ?? "")
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            result.Add(new SkippedAdoptItem(
                GetString(item, "game_id") ?? "",
                GetString(item, "name") ?? "",
                GetString(item, "source_root") ?? "",
                GetString(item, "reason") ?? "",
                candidates));
        }

        return result;
    }

    private static List<AdoptCandidateItem> ReadCandidates(JsonElement root)
    {
        var result = new List<AdoptCandidateItem>();
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in candidates.EnumerateArray())
        {
            result.Add(new AdoptCandidateItem(
                GetString(item, "game_id") ?? "",
                GetString(item, "name") ?? "",
                GetString(item, "source_root") ?? "",
                GetString(item, "executable_path") ?? "",
                GetString(item, "status") ?? ""));
        }

        return result;
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static string? ReadInstalledPluginVersion()
    {
        var manifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "vnite",
            "app",
            "plugins",
            "vnite-game-cache",
            "manifest.json");
        if (!File.Exists(manifest))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = document.RootElement;
        var name = GetString(root, "name") ?? "Vnite 游戏缓存";
        var version = GetString(root, "version") ?? "unknown";
        return $"{name} {version}";
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : 0;
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : 0;
    }

    private static double GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)) return 0;
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value) ? value : 0;
    }

    private static string TranslateAdoptStatus(string status) => status switch
    {
        "ok" => "正常",
        "needs_action" => "需要处理",
        "unavailable" => "Vnite 接口不可用",
        "error" => "失败",
        _ => string.IsNullOrWhiteSpace(status) ? "未知" : status
    };

    private static string SafeResolve(Func<string> action)
    {
        try
        {
            return action();
        }
        catch (Exception error)
        {
            return $"读取失败：{error.Message}";
        }
    }

    private static void AddFileCheck(List<string> lines, string name, string path)
    {
        lines.Add($"{name}：{path} | {(File.Exists(path) ? "存在" : "不存在")}");
    }

    private static IEnumerable<string> TailLines(string path, int maxLines)
    {
        var queue = new Queue<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            queue.Enqueue(line);
            while (queue.Count > maxLines) queue.Dequeue();
        }

        return queue;
    }

    private static string? LatestFile(string root, string pattern)
    {
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    private static string TranslateCopyPhase(string phase) => phase switch
    {
        "Copying" => "正在复制",
        "Verifying" => "正在验证",
        "Failed" => "复制失败",
        "Cancelled" => "复制已取消",
        _ => phase
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒";
        return $"{Math.Max(1, duration.Seconds)} 秒";
    }

    private static int? ExtractPercent(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(?<!\d)(\d{1,3})%");
        if (!match.Success) return null;
        var value = int.Parse(match.Groups[1].Value);
        return value is >= 0 and <= 100 ? value : null;
    }

    private static bool IsTestGame(GameRecord game)
    {
        var text = $"{game.GameId} {game.DisplayName} {game.SourceRoot} {game.CacheRoot}";
        return text.Contains("TestGame", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("VGC Renderer Test", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("中文 空格 Game", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindLauncherShortcutPath(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId)) return "";
        var launcherRoot = Path.Combine(VgcSettingsStore.LocalAppDataRoot, "launchers");
        if (!Directory.Exists(launcherRoot)) return "";
        return Directory.EnumerateFiles(launcherRoot, "*.lnk")
            .FirstOrDefault(path =>
                Path.GetFileName(path).Contains(gameId, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals($"{gameId}.lnk", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static bool IsPathUnder(string value, string root)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(root)) return false;
        var normalizedValue = Normalize(value);
        var normalizedRoot = Normalize(root);
        return normalizedValue.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.StartsWith($"{normalizedRoot}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryRelative(string root, string value)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Path.GetRelativePath(root, value);
        }
        catch
        {
            return "";
        }
    }

    private static string FindCandidatePrograms(string sourceRoot, string gameName)
    {
        if (!Directory.Exists(sourceRoot)) return "";
        var candidates = BuildLaunchCandidateList(sourceRoot, gameName)
            .Take(5)
            .Select(item => TryRelative(sourceRoot, item.Path))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return string.Join("; ", candidates);
    }

    private static string DetectBestExecutable(string sourceRoot, string gameName)
    {
        if (!Directory.Exists(sourceRoot)) return "";
        var candidates = BuildLaunchCandidateList(sourceRoot, gameName);
        return candidates.Count == 0 ? "" : candidates[0].Path;
    }

    private static List<(string Path, int Score)> BuildLaunchCandidateList(string sourceRoot, string gameName)
    {
        return EnumerateLaunchCandidateFiles(sourceRoot, maxDepth: 1)
            .Select(path =>
            {
                var relative = TryRelative(sourceRoot, path);
                var depthPenalty = relative.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar) * 5;
                return (Path: path, Score: ScoreLaunchCandidate(Path.GetFileName(path), gameName) - depthPenalty);
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateLaunchCandidateFiles(string sourceRoot, int maxDepth)
    {
        var pending = new Queue<(string DirectoryPath, int Depth)>();
        pending.Enqueue((sourceRoot, 0));

        while (pending.Count > 0)
        {
            var (directoryPath, depth) = pending.Dequeue();
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth) continue;

            List<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories.Where(directory => !ShouldSkipLaunchCandidateDirectory(directory)))
            {
                pending.Enqueue((directory, depth + 1));
            }
        }
    }

    private static bool ShouldSkipLaunchCandidateDirectory(string directory)
    {
        var name = Path.GetFileName(directory).ToLowerInvariant();
        return name is "save" or "saves" or "savedata" or "userdata" or "screenshots" or "screenshot" or
            "mod" or "mods" or "plugin" or "plugins" or "redist" or "redistributable" or "runtime" or
            "www" or "node_modules" or "locales" or "swiftshader" or "renpy" or "lib" or
            "mtooldata" or "mtoolscripts" or "mcenter" or "quarantine" or "staging" or
            "tool" or "tools" or "directx" or "locale.emulator.2.4.1.0" or "locale emulator" or
            "backup" or "backups" or "bak" or "備份" or "备份" or "漢化備份" or "汉化备份";
    }

    private static int ScoreLaunchCandidate(string fileName, string gameName)
    {
        var name = fileName.ToLowerInvariant();
        if (IsAuxiliaryLaunchCandidateName(name)) return -100;
        if (name.Contains("与工具一同启动") && (name.EndsWith(".bat") || name.EndsWith(".cmd"))) return 120;
        if (IsTranslationLaunchCandidateName(name)) return 115;
        if (name == "game.exe") return 105;
        var normalizedGame = NormalizeCandidateText(gameName);
        var normalizedName = NormalizeCandidateText(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedGame) && normalizedName == normalizedGame) return 110;
        if (name.EndsWith(".exe")) return 40;
        return 20;
    }

    private static bool IsTranslationLaunchCandidateName(string name) =>
        Regex.IsMatch(name, @"(^|[_\-\s.])(cn|chs|sc|zh|zh-cn|zh_cn|zhcn|ai|gpt|claude|deepseek|galtransl|sugoi|sakura)([_\-\s.]|$|\d)", RegexOptions.IgnoreCase) ||
        name.Contains("中文") || name.Contains("简中") || name.Contains("簡中") ||
        name.Contains("汉化") || name.Contains("漢化") || name.Contains("简体") || name.Contains("簡体") ||
        name.Contains("官方中文") || name.Contains("机翻") || name.Contains("翻译") || name.Contains("翻譯");

    private static bool IsAuxiliaryLaunchCandidateName(string name) =>
        name.StartsWith("unins") || name.StartsWith("setup") || name.StartsWith("install") ||
        name.StartsWith("config") || name.StartsWith("setting") || name.StartsWith("leproc") ||
        name.StartsWith("legui") || name.StartsWith("leinstaller") || name.StartsWith("mtool") ||
        name.StartsWith("chromedriver") || name.StartsWith("nwjc") || name.StartsWith("payload") ||
        name.StartsWith("krkrextract") || name.StartsWith("xp3_upk") || name.StartsWith("syscfg") ||
        name.StartsWith("sdacti") || name.Contains("crash") || name.Contains("update") ||
        name.Contains("patch") || name.Contains("directx") || name.Contains("redist") ||
        name.Contains("locale.emulator") || name.Contains("ファイル破損チェック") ||
        name.Contains("破損チェック") || name.Contains("エンジン設定") || name.Contains("セーブファイル設定");

    private static string NormalizeCandidateText(string value)
    {
        return value.Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant()
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "");
    }

    private static string BuildCacheRoot(string? configuredRoot, string sourceRoot, string gameId)
    {
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(VgcSettingsStore.LocalAppDataRoot, "cache")
            : configuredRoot;
        var suffix = string.IsNullOrWhiteSpace(gameId) ? "manual" : gameId[^Math.Min(8, gameId.Length)..];
        return Path.Combine(root, $"{SanitizeCacheSegment(Path.GetFileName(sourceRoot))}-{suffix}");
    }

    private static string SanitizeCacheSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string((string.IsNullOrWhiteSpace(value) ? "game" : value)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "game";
        return sanitized.Length > 80 ? sanitized[..80] : sanitized;
    }
    private static long ToMb(long bytes) => bytes <= 0 ? 0 : bytes / 1024 / 1024;

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value)
        ? ""
        : Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();

    private static string ResolveProjectRoot()
    {
        var current = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(current); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VniteGameCache.sln")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    private sealed record MToolTemplateProbe(string GameExecutableName, string LoaderRelativePath);

    private sealed record LaunchCandidateDisplayRow(
        string DefaultMark,
        string FileName,
        string Role,
        int Score,
        string Confidence,
        string Status,
        string RelativePath,
        GameLaunchCandidateRecord Candidate);

    private sealed record GameRow(
        string StatusIcon,
        string Name,
        string GameId,
        string VniteId,
        ManagementIntent ManagementIntent,
        string ManagedStatus,
        string AdoptStatus,
        string CacheStatus,
        string LaunchStatus,
        string Issue,
        string LaunchMode,
        long CacheMb,
        string LastPlayed,
        string SourceRoot,
        string CacheRoot,
        string ExecutablePath,
        string ShortcutPath,
        string CandidatePrograms);

    private sealed record AdoptDiagnostics(
        string Status,
        string Reason,
        string Message,
        string Error,
        string TimestampUtc,
        int AdoptedCount,
        int CandidateCount,
        int RemainingCount,
        List<string> Roots,
        List<AdoptCandidateItem> Candidates,
        List<SkippedAdoptItem> Skipped);

    private sealed record AdoptCandidateItem(string GameId, string Name, string SourceRoot, string ExecutablePath, string Status);

    private sealed record SkippedAdoptItem(string GameId, string Name, string SourceRoot, string Reason, List<string> Candidates);

    private sealed record TakeoverResidueDiagnostics(
        string Status,
        string Reason,
        string Error,
        string TimestampUtc,
        int ResidueCount,
        List<TakeoverResidueItem> Residues);

    private sealed record TakeoverResidueItem(
        string GameId,
        string Name,
        string SourceRoot,
        string LauncherPath,
        string ShortcutPath,
        List<string> Evidence);

    private sealed record ObsoleteCleanupPlan(
        List<CleanupCandidate> DeleteCandidates,
        List<ManualReviewItem> ManualReviewItems,
        int TotalGames);

    private sealed record CleanupCandidate(GameRecord Game, string Kind, string Reason);

    private sealed record ManualReviewItem(string Kind, string DisplayName, string Reason);
}








