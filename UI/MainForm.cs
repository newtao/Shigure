using System.Drawing;

namespace Shigure;

public sealed class MainForm : Form, IMessageFilter
{
    private const int ResizeGripSize = 8;
    private const int RoundedCornerResizeDebounceMs = 80;
    private const string HeaderIconResourcePath = "Assets.arasaka-icon-transparent.png";
    private const string ModuleWebsiteUrl = "https://www.shigure.club";
    private static readonly Color DefaultHeaderIconColor = Color.White;
    private static readonly IReadOnlyDictionary<int, Color> ClassIconColors = new Dictionary<int, Color>
    {
        [1] = ColorTranslator.FromHtml("#C79C6E"),
        [2] = ColorTranslator.FromHtml("#F58CBA"),
        [3] = ColorTranslator.FromHtml("#ABD473"),
        [4] = ColorTranslator.FromHtml("#FFF569"),
        [5] = ColorTranslator.FromHtml("#FFFFFF"),
        [6] = ColorTranslator.FromHtml("#C41F3B"),
        [7] = ColorTranslator.FromHtml("#0070DE"),
        [8] = ColorTranslator.FromHtml("#69CCF0"),
        [9] = ColorTranslator.FromHtml("#9482C9"),
        [10] = ColorTranslator.FromHtml("#00FF96"),
        [11] = ColorTranslator.FromHtml("#FF7D0A"),
        [12] = ColorTranslator.FromHtml("#A330C9"),
        [13] = ColorTranslator.FromHtml("#33937F")
    };

    private Button _toggleKeyButton = null!;
    private ComboBox _modeComboBox = null!;
    private ComboBox _moduleComboBox = null!;
    private Label _moduleFilterLabel = null!;
    private Label _moduleCountLabel = null!;
    private Label _configSourceLabel = null!;
    private Button _updateConfigButton = null!;
    private readonly ToolTip _settingsToolTip = new();
    private Button _settingsButton = null!;
    private string _toggleKeyName = "XBUTTON2";
    private string? _selectedModuleId;
    private bool _isCapturingToggleKey;
    private bool _suppressModuleSelectionChanged;
    private string? _lastModuleSelectorSignature;
    private bool _usesDwmRoundedCorners = true;

    private Button _enableButton = null!;

    private PictureBox _headerIcon = null!;
    private Label _titleLabel = null!;
    private Label _runtimeStatusLabel = null!;
    private Bitmap? _headerIconMask;
    private Color? _currentHeaderIconColor;

    private readonly StatusForm _statusForm;
    private readonly string _baseDirectory;
    private readonly ModuleStore _moduleStore;
    private readonly ITriggerKeyState _triggerKeyState;
    private readonly WowProcessLocator _processLocator;
    private readonly FuyutsuiAddonSyncService _addonSyncService;
    private readonly ModuleDependencyService _moduleDependencyService;
    private readonly RuntimeSessionCoordinator _runtimeSession;
    private readonly ModuleEditorControl _moduleEditor;
    private readonly ClassConfigEditorControl _classConfigEditor;
    private readonly ClassMacrosEditorControl _classMacrosEditor;
    private readonly AppOptions _initialOptions;
    private readonly UiCacheState _uiCache;
    private readonly System.Windows.Forms.Timer _roundedCornerResizeTimer;
    private RenderSnapshot? _lastSnapshot;
    private string? _lastLoggedStep;
    private string? _lastLoggedStepDetails;
    private string? _lastLoggedScanFailureReason;
    private string? _lastLoggedClass;
    private string? _lastLoggedModule;
    private bool? _lastLoggedEnabled;
    private readonly object _configUpdateSync = new();
    private readonly SemaphoreSlim _moduleImportGate = new(1, 1);
    private Task _configUpdateTail = Task.CompletedTask;
    private long _runtimeRequestVersion;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    private sealed record ProjectConfigUpdateResult(
        FuyutsuiConfigConverter.UpdateResult Config,
        FuyutsuiKeymapConverter.UpdateResult? Keymap,
        FuyutsuiAddonSyncResult AddonSync);

    internal MainForm(
        AppOptions initialOptions,
        string baseDirectory,
        ModuleStore moduleStore,
        ITriggerKeyState triggerKeyState,
        WowProcessLocator processLocator,
        RuntimeSessionCoordinator runtimeSession)
    {
        _initialOptions = initialOptions;
        _baseDirectory = baseDirectory;
        _moduleStore = moduleStore;
        _triggerKeyState = triggerKeyState;
        _processLocator = processLocator;
        var localAddonRoot = Path.Combine(_baseDirectory, "Fuyutsui");
        _addonSyncService = new FuyutsuiAddonSyncService(localAddonRoot, _processLocator);
        _moduleDependencyService = new ModuleDependencyService(_baseDirectory);
        _runtimeSession = runtimeSession;
        _uiCache = UiCacheStore.Load();
        _statusForm = new StatusForm();
        _roundedCornerResizeTimer = new System.Windows.Forms.Timer
        {
            Interval = RoundedCornerResizeDebounceMs
        };
        _roundedCornerResizeTimer.Tick += (_, _) =>
        {
            _roundedCornerResizeTimer.Stop();
            if (IsHandleCreated && !_usesDwmRoundedCorners)
            {
                UiTheme.ApplyFallbackRoundedCorners(this);
            }
        };
        Application.AddMessageFilter(this);
        InitializeComponent();
        _statusForm.AttachSettingsPanel(BuildSettingsPanel());
        _moduleEditor = new ModuleEditorControl(
            _moduleStore,
            RestartRuntimeFromEditorAsync,
            _moduleDependencyService.Capture,
            ReloadModulesWithDependenciesAsync,
            _baseDirectory);
        _statusForm.AttachModuleEditor(_moduleEditor);
        _classConfigEditor = new ClassConfigEditorControl(
            () => Path.Combine(_addonSyncService.SourceRoot, "class"),
            UpdateConfigAfterSaveAsync);
        _statusForm.AttachConfigEditor(_classConfigEditor);
        _classConfigEditor.DirtyStateChanged += dirty => _statusForm.SetPageDirty(SettingsPage.Config, dirty);
        _classMacrosEditor = new ClassMacrosEditorControl(
            () => Path.Combine(_addonSyncService.SourceRoot, "core", "classmacros.lua"),
            UpdateConfigAfterSaveAsync);
        _statusForm.AttachMacrosEditor(_classMacrosEditor);
        _classMacrosEditor.DirtyStateChanged += dirty => _statusForm.SetPageDirty(SettingsPage.Macros, dirty);
        _statusForm.FormClosing += (_, _) =>
        {
            CancelToggleKeyCapture();
            SaveUiCache();
        };
        TryApplyApplicationIcon();
        ApplyCachedWindowState();
        ApplyInitialOptions();
        WireSettingEvents();
        _runtimeSession.SnapshotUpdated += HandleSnapshotUpdated;
        _runtimeSession.RuntimeFailed += HandleRuntimeFailed;
        _runtimeSession.RuntimeStopped += HandleRuntimeStopped;
        SetRuntimeControls(running: false);
        AppendLog("界面已就绪");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UiTheme.ApplyDarkTitleBar(this);
        UiTheme.ApplyTranslucentBackground(this);
        _usesDwmRoundedCorners = UiTheme.ApplyRoundedCorners(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 亚克力背景首次合成时，子控件(标题/开启关闭/设置/关闭按钮)可能未被绘制，
        // 表现为只有图标(PictureBox)可见、其余控件需截图触发重绘后才出现。
        // 此处强制一次全量重绘以修正初始空白。
        ForceRepaintAfterShown();

        var dependenciesUpdated = await ImportModuleDependenciesAsync(reloadStore: true, showFeedback: true);
        if (!dependenciesUpdated)
        {
            await SynchronizeAddonAtStartupAsync();
        }
        await StartRuntimeAsync();
        ForceRepaintAfterShown();
    }

    private void ForceRepaintAfterShown()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            Invalidate(true);
            Update();
        });
    }

    private Task ReloadModulesWithDependenciesAsync()
        => ImportModuleDependenciesAsync(reloadStore: true, showFeedback: true);

    private async Task<bool> ImportModuleDependenciesAsync(bool reloadStore, bool showFeedback)
    {
        await _moduleImportGate.WaitAsync();
        try
        {
            return await ImportModuleDependenciesCoreAsync(reloadStore, showFeedback);
        }
        finally
        {
            _moduleImportGate.Release();
        }
    }

    private async Task<bool> ImportModuleDependenciesCoreAsync(bool reloadStore, bool showFeedback)
    {
        if (_classConfigEditor.HasUnsavedChanges || _classMacrosEditor.HasUnsavedChanges)
        {
            if (showFeedback)
            {
                MessageBox.Show(
                    "配置或宏页面存在未保存修改。请先保存或放弃修改，再刷新模块。",
                    "模块依赖未导入",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }

        if (reloadStore)
        {
            _moduleStore.Reload();
        }

        ModuleDependencyImportResult result;
        try
        {
            // 合并阶段保持在 UI 线程，避免配置/宏编辑器在检查脏状态后又并发写同一 Lua。
            result = _moduleDependencyService.Import(_moduleStore.GetModules());
        }
        catch (Exception ex)
        {
            AppendLog($"模块依赖导入失败: {ex.Message}");
            if (showFeedback)
            {
                MessageBox.Show(ex.Message, "模块依赖导入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        _moduleStore.RejectModules(result.Rejected.Select(item => item.ModuleId));
        _moduleEditor.ReloadModulesFromStore(reloadStore: false);
        RefreshModuleSelector(_lastSnapshot, forceRefresh: false);

        foreach (var rejected in result.Rejected)
        {
            AppendLog($"模块“{rejected.ModuleName}”未导入: {rejected.Reason}");
        }
        foreach (var conflict in result.Conflicts.Take(50))
        {
            AppendLog($"模块依赖冲突: {conflict}");
        }

        string? postUpdateError = null;
        if (result.HasChanges)
        {
            AppendLog(
                $"已从模块补充本地依赖: 配置 {result.ConfigAdded} 项，宏 {result.MacrosAdded} 项；模块 {string.Join("、", result.ChangedModules)}");
            _classConfigEditor.ReloadFromAddon();
            _classMacrosEditor.ReloadFromAddon();
            try
            {
                await QueueProjectConfigUpdateAsync(savedAddonFilePath: null);
            }
            catch (Exception ex)
            {
                postUpdateError = ex.Message;
                AppendLog($"模块依赖已写入，但后续配置更新失败: {ex.Message}");
            }
        }

        if (showFeedback && (result.HasChanges || result.Rejected.Count > 0 || result.Conflicts.Count > 0))
        {
            var lines = new List<string>();
            if (result.HasChanges)
            {
                lines.Add($"成功补充配置 {result.ConfigAdded} 项、宏 {result.MacrosAdded} 项。");
            }
            if (result.Rejected.Count > 0)
            {
                lines.Add("未导入模块：");
                lines.AddRange(result.Rejected.Select(item => $"- {item.ModuleName}: {item.Reason}"));
            }
            if (result.Conflicts.Count > 0)
            {
                lines.Add($"发现 {result.Conflicts.Count} 项冲突，均已保留本地内容；详情见日志。");
            }
            if (!string.IsNullOrWhiteSpace(postUpdateError))
            {
                lines.Add($"本地依赖已写入，但 config/keymap 或游戏同步更新失败：{postUpdateError}");
            }
            var hasWarning = result.Rejected.Count > 0 || postUpdateError is not null;
            MessageBox.Show(
                string.Join(Environment.NewLine, lines),
                hasWarning ? "模块导入完成（有警告）" : "模块导入完成",
                MessageBoxButtons.OK,
                hasWarning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        return result.HasChanges;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_shutdownCompleted)
        {
            e.Cancel = true;
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                SaveUiCache();
                _roundedCornerResizeTimer.Stop();
                Application.RemoveMessageFilter(this);
                _ = CompleteShutdownAsync();
            }

            base.OnFormClosing(e);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _roundedCornerResizeTimer.Dispose();
        base.OnFormClosed(e);
    }

    private async Task CompleteShutdownAsync()
    {
        _runtimeSession.SnapshotUpdated -= HandleSnapshotUpdated;
        _runtimeSession.RuntimeFailed -= HandleRuntimeFailed;
        _runtimeSession.RuntimeStopped -= HandleRuntimeStopped;

        try
        {
            var runtimeShutdown = _runtimeSession.DisposeAsync().AsTask();
            await Task.WhenAll(runtimeShutdown, GetPendingConfigUpdateTask());
        }
        catch (Exception ex)
        {
            AppendLog($"停止运行失败: {ex.Message}");
        }
        finally
        {
            _statusForm.Dispose();
            _shutdownCompleted = true;
            if (!IsDisposed)
            {
                Close();
            }
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && !_usesDwmRoundedCorners)
        {
            ScheduleFallbackRoundedCornerUpdate();
        }
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (IsHandleCreated && !_usesDwmRoundedCorners)
        {
            _roundedCornerResizeTimer.Stop();
            UiTheme.ApplyFallbackRoundedCorners(this);
        }
    }

    private void ScheduleFallbackRoundedCornerUpdate()
    {
        _roundedCornerResizeTimer.Stop();
        _roundedCornerResizeTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        const int WmNcHitTest = 0x0084;
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (m.Result == NativeMethods.HtClient)
            {
                m.Result = HitTestResizeGrip(PointToClient(Cursor.Position));
            }

            return;
        }

        base.WndProc(ref m);
    }

    private void InitializeComponent()
    {
        SuspendLayout();

        Text = "Shigure";

        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ClientSize = new Size(680, 64);
        MinimumSize = new Size(420, 56);
        BackColor = Color.FromArgb(18, 21, 26);
        ForeColor = UiTheme.Text;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(12),
            RowCount = 1,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(BuildTopBar(), 0, 0);

        ResumeLayout(false);
    }

    private Control BuildTopBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var brand = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(0)
        };

        _headerIcon = CreateHeaderIcon();
        UpdateHeaderIconColor(null);

        _titleLabel = new Label
        {
            Text = "Shigure",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            ForeColor = UiTheme.Text,
            Margin = new Padding(8, 0, 0, 0)
        };

        brand.Controls.Add(_headerIcon);
        brand.Controls.Add(_titleLabel);

        _runtimeStatusLabel = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Muted
        };

        EnableDrag(bar);
        EnableDrag(brand);
        EnableDrag(_headerIcon);
        EnableDrag(_titleLabel);
        EnableDrag(_runtimeStatusLabel);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        _enableButton = UiTheme.CreateButton("开关", UiTheme.Field, UiTheme.Text);
        ConfigureTopBarButton(_enableButton);
        _enableButton.Click += (_, _) => ToggleEnabled();

        _settingsButton = UiTheme.CreateButton("设置", UiTheme.Field, UiTheme.Text);
        ConfigureTopBarButton(_settingsButton);
        _settingsButton.Click += (_, _) => ShowSettingsView();

        var closeButton = UiTheme.CreateButton("✕", UiTheme.Field, UiTheme.Muted);
        ConfigureTopBarButton(closeButton);
        closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(153, 27, 21);
        closeButton.Click += (_, _) => Close();

        buttons.Controls.AddRange(new Control[] { _enableButton, _settingsButton, closeButton });

        bar.Controls.Add(brand, 0, 0);
        bar.Controls.Add(_runtimeStatusLabel, 1, 0);
        bar.Controls.Add(buttons, 2, 0);
        return bar;
    }

    private static PictureBox CreateHeaderIcon()
    {
        var box = new PictureBox
        {
            Size = new Size(32, 32),
            MinimumSize = new Size(32, 32),
            MaximumSize = new Size(32, 32),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left
        };

        return box;
    }

    private void UpdateHeaderIconColor(int? classId)
    {
        var color = ResolveClassIconColor(classId);
        if (_currentHeaderIconColor == color)
        {
            return;
        }

        _currentHeaderIconColor = color;
        _headerIconMask ??= LoadHeaderIconMask();
        if (_headerIconMask is null)
        {
            return;
        }

        var previous = _headerIcon.Image;
        _headerIcon.Image = TintHeaderIcon(_headerIconMask, color);
        previous?.Dispose();
    }

    private static Color ResolveClassIconColor(int? classId)
        => classId is not null && ClassIconColors.TryGetValue(classId.Value, out var color)
            ? color
            : DefaultHeaderIconColor;

    private static Bitmap? LoadHeaderIconMask()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(GetHeaderIconResourceName());
        if (stream is null)
        {
            return null;
        }

        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static string GetHeaderIconResourceName() =>
        $"{typeof(MainForm).Namespace}.{HeaderIconResourcePath}";

    private static Bitmap TintHeaderIcon(Bitmap mask, Color color)
    {
        var bitmap = new Bitmap(mask.Width, mask.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetResolution(mask.HorizontalResolution, mask.VerticalResolution);

        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                var pixel = mask.GetPixel(x, y);
                if (pixel.A == 0)
                {
                    continue;
                }

                bitmap.SetPixel(x, y, Color.FromArgb(pixel.A, color));
            }
        }

        return bitmap;
    }

    private Control BuildSettingsPanel()
    {
        var scrollHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Surface,
            AutoScroll = true,
            Margin = new Padding(0)
        };
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Surface,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        const int settingsCardHeight = 232;
        const int settingsCardGap = 14;
        const int settingsActionButtonHeight = 36;
        // 第一行包含 14px 下间距；第二行无需间距，因此卡片的实际高度均为 232px。
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsCardHeight + settingsCardGap));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsCardHeight));

        Label CreateTitle(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Text,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        Label CreateDescription(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = UiTheme.Muted,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        Label CreateSettingLabel(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0)
        };

        var inputCard = new UiCardPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 7, 14)
        };
        inputCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        inputCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsActionButtonHeight + 8));
        inputCard.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsActionButtonHeight + 8));
        inputCard.Controls.Add(CreateTitle("输入与运行"), 0, 0);
        inputCard.SetColumnSpan(inputCard.GetControlFromPosition(0, 0)!, 2);
        inputCard.Controls.Add(CreateDescription("设置触发方式；修改后运行循环会自动重启"), 0, 1);
        inputCard.SetColumnSpan(inputCard.GetControlFromPosition(0, 1)!, 2);

        _toggleKeyButton = UiTheme.CreateButton("XBUTTON2", UiTheme.ButtonKind.Secondary);
        _toggleKeyButton.AutoSize = false;
        _toggleKeyButton.Size = new Size(190, settingsActionButtonHeight);
        _toggleKeyButton.TextAlign = ContentAlignment.MiddleCenter;
        _toggleKeyButton.Anchor = AnchorStyles.Left;
        _toggleKeyButton.Margin = new Padding(0);
        _toggleKeyButton.Click += (_, _) => BeginCaptureToggleKey();
        _settingsToolTip.SetToolTip(_toggleKeyButton, "点击后按下新的键盘键或鼠标侧键");
        inputCard.Controls.Add(CreateSettingLabel("触发键"), 0, 2);
        inputCard.Controls.Add(_toggleKeyButton, 1, 2);

        _modeComboBox = new ComboBox();
        UiTheme.StyleComboBox(_modeComboBox);
        _modeComboBox.Items.AddRange(new object[] { "开关", "单击", "按住" });
        _modeComboBox.SelectedIndex = 0;
        _modeComboBox.Width = 190;
        _modeComboBox.Anchor = AnchorStyles.Left;
        _modeComboBox.Margin = new Padding(0);
        _settingsToolTip.SetToolTip(_modeComboBox, "开关：按一次切换；单击：每次触发发送一次；按住：持续按下时运行");
        inputCard.Controls.Add(CreateSettingLabel("发送模式"), 0, 3);
        inputCard.Controls.Add(_modeComboBox, 1, 3);

        var configCard = new UiCardPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            Margin = new Padding(7, 0, 0, 14)
        };
        configCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        configCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        configCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        configCard.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsActionButtonHeight));
        configCard.Controls.Add(CreateTitle("配置同步"), 0, 0);
        configCard.Controls.Add(CreateDescription("从项目 Fuyutsui 生成 config/keymap，并同步到游戏"), 0, 1);
        _configSourceLabel = CreateInfoLabel("项目目录是唯一配置源；尚未执行手动更新");
        _configSourceLabel.Dock = DockStyle.Fill;
        _configSourceLabel.AutoSize = false;
        _configSourceLabel.AutoEllipsis = true;
        _configSourceLabel.TextAlign = ContentAlignment.TopLeft;
        _configSourceLabel.Margin = new Padding(0, 10, 0, 8);
        _settingsToolTip.SetToolTip(_configSourceLabel, _configSourceLabel.Text);
        configCard.Controls.Add(_configSourceLabel, 0, 2);
        _updateConfigButton = UiTheme.CreateButton("更新配置", UiTheme.ButtonKind.Secondary);
        _updateConfigButton.AutoSize = false;
        _updateConfigButton.Size = new Size(122, settingsActionButtonHeight);
        _updateConfigButton.Dock = DockStyle.Left;
        _updateConfigButton.Margin = new Padding(0);
        _updateConfigButton.Click += async (_, _) => await UpdateConfigFromProjectWithFeedbackAsync();
        configCard.Controls.Add(_updateConfigButton, 0, 3);

        var moduleCard = new UiCardPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 7, 0)
        };
        moduleCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        moduleCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        moduleCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        moduleCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        moduleCard.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsActionButtonHeight));
        moduleCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        moduleCard.Controls.Add(CreateTitle("模块选择"), 0, 0);
        moduleCard.SetColumnSpan(moduleCard.GetControlFromPosition(0, 0)!, 2);
        moduleCard.Controls.Add(CreateDescription("按实时职业与专精自动匹配，或手动指定模块"), 0, 1);
        moduleCard.SetColumnSpan(moduleCard.GetControlFromPosition(0, 1)!, 2);
        _moduleComboBox = new ComboBox();
        UiTheme.StyleComboBox(_moduleComboBox);
        _moduleComboBox.Dock = DockStyle.Fill;
        _moduleComboBox.Margin = new Padding(0, 0, 14, 0);
        _settingsToolTip.SetToolTip(_moduleComboBox, "列表会根据当前游戏状态筛选可用模块");
        moduleCard.Controls.Add(_moduleComboBox, 0, 2);
        var refreshModulesButton = UiTheme.CreateButton("刷新模块", UiTheme.ButtonKind.Secondary);
        refreshModulesButton.AutoSize = false;
        refreshModulesButton.Dock = DockStyle.Fill;
        refreshModulesButton.Margin = new Padding(0);
        refreshModulesButton.Click += async (_, _) =>
        {
            await ReloadModulesWithDependenciesAsync();
            RefreshModuleSelector(_lastSnapshot, forceRefresh: false);
        };
        moduleCard.Controls.Add(refreshModulesButton, 1, 2);
        var moduleInfoText = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0)
        };
        _moduleFilterLabel = CreateInfoLabel("筛选: 等待游戏状态");
        _moduleCountLabel = CreateInfoLabel("可选模块: 0");
        moduleInfoText.Controls.Add(_moduleFilterLabel);
        moduleInfoText.Controls.Add(_moduleCountLabel);
        moduleCard.Controls.Add(moduleInfoText, 0, 3);
        moduleCard.SetColumnSpan(moduleInfoText, 2);

        var getModulesCard = new UiCardPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            Margin = new Padding(7, 0, 0, 0)
        };
        getModulesCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        getModulesCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        getModulesCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        getModulesCard.RowStyles.Add(new RowStyle(SizeType.Absolute, settingsActionButtonHeight));
        getModulesCard.Controls.Add(CreateTitle("获取模块"), 0, 0);
        getModulesCard.Controls.Add(CreateDescription("访问 Shigure 官网，浏览并获取可用模块"), 0, 1);

        var moduleWebsiteLabel = CreateInfoLabel(ModuleWebsiteUrl);
        moduleWebsiteLabel.Dock = DockStyle.Fill;
        moduleWebsiteLabel.AutoSize = false;
        moduleWebsiteLabel.AutoEllipsis = true;
        moduleWebsiteLabel.TextAlign = ContentAlignment.TopLeft;
        moduleWebsiteLabel.ForeColor = UiTheme.Accent;
        moduleWebsiteLabel.Cursor = Cursors.Hand;
        moduleWebsiteLabel.Margin = new Padding(0, 10, 0, 8);
        moduleWebsiteLabel.Click += (_, _) => OpenModuleWebsite();
        _settingsToolTip.SetToolTip(moduleWebsiteLabel, $"在默认浏览器中打开 {ModuleWebsiteUrl}");
        getModulesCard.Controls.Add(moduleWebsiteLabel, 0, 2);

        var moduleActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        var moduleWebsiteButtonColor = Color.FromArgb(252, 238, 10);
        var openModuleWebsiteButton = UiTheme.CreateButton("获取模块", moduleWebsiteButtonColor, Color.Black);
        openModuleWebsiteButton.AutoSize = false;
        openModuleWebsiteButton.Size = new Size(160, settingsActionButtonHeight);
        openModuleWebsiteButton.Margin = new Padding(0, 0, 10, 0);
        openModuleWebsiteButton.Padding = new Padding(0, 2, 24, 2);
        openModuleWebsiteButton.FlatAppearance.BorderColor = moduleWebsiteButtonColor;
        openModuleWebsiteButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 244, 64);
        openModuleWebsiteButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(220, 207, 8);
        openModuleWebsiteButton.Paint += (_, e) => UiTheme.DrawExternalLinkIcon(
            e.Graphics,
            openModuleWebsiteButton.ClientRectangle,
            openModuleWebsiteButton.Text,
            openModuleWebsiteButton.Font,
            openModuleWebsiteButton.ForeColor,
            openModuleWebsiteButton.DeviceDpi / 96F);
        openModuleWebsiteButton.Click += (_, _) => OpenModuleWebsite();

        var openModuleDirectoryButton = UiTheme.CreateButton("打开模块目录", UiTheme.ButtonKind.Secondary);
        openModuleDirectoryButton.AutoSize = false;
        openModuleDirectoryButton.Size = new Size(160, settingsActionButtonHeight);
        openModuleDirectoryButton.Margin = new Padding(0);
        openModuleDirectoryButton.Click += (_, _) => OpenModuleDirectory();
        _settingsToolTip.SetToolTip(openModuleDirectoryButton, "在资源管理器中打开本地模块目录");

        moduleActions.Controls.Add(openModuleWebsiteButton);
        moduleActions.Controls.Add(openModuleDirectoryButton);
        getModulesCard.Controls.Add(moduleActions, 0, 3);

        panel.Controls.Add(inputCard, 0, 0);
        panel.Controls.Add(configCard, 1, 0);
        panel.Controls.Add(moduleCard, 0, 1);
        panel.Controls.Add(getModulesCard, 1, 1);
        scrollHost.Controls.Add(panel);
        scrollHost.Resize += (_, _) => panel.Width = Math.Max(0, scrollHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        return scrollHost;
    }

    private void OpenModuleWebsite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ModuleWebsiteUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"无法打开模块网站：{ex.Message}",
                "Shigure",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OpenModuleDirectory()
    {
        var moduleDirectory = _moduleStore.ModuleDirectory;
        try
        {
            Directory.CreateDirectory(moduleDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{moduleDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"无法打开模块目录：{ex.Message}",
                "Shigure",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task UpdateConfigFromProjectWithFeedbackAsync()
    {
        _updateConfigButton.Enabled = false;
        _updateConfigButton.Text = "更新中…";
        _configSourceLabel.ForeColor = UiTheme.Warning;
        _configSourceLabel.Text = "正在生成配置并同步游戏插件…";
        try
        {
            var updated = await UpdateConfigFromProjectAsync();
            _configSourceLabel.ForeColor = updated ? UiTheme.Success : UiTheme.Danger;
        }
        catch
        {
            _configSourceLabel.ForeColor = UiTheme.Danger;
            throw;
        }
        finally
        {
            _updateConfigButton.Text = "更新配置";
            _updateConfigButton.Enabled = true;
            _settingsToolTip.SetToolTip(_configSourceLabel, _configSourceLabel.Text);
        }
    }

    private async Task SynchronizeAddonAtStartupAsync()
    {
        try
        {
            var result = await Task.Run(_addonSyncService.SynchronizeAll);
            LogAddonSyncResult("启动插件同步", result);
        }
        catch (Exception ex)
        {
            AppendLog($"启动插件同步失败，程序将继续运行: {ex.Message}");
        }
    }

    private async Task<bool> UpdateConfigFromProjectAsync()
    {
        try
        {
            var result = await QueueProjectConfigUpdateAsync(savedAddonFilePath: null);
            if (!_shutdownStarted)
            {
                ShowProjectConfigUpdateResult(result);
            }
            return true;
        }
        catch (Exception ex)
        {
            if (_shutdownStarted)
            {
                return false;
            }

            AppendLog($"更新配置失败: {ex.Message}");
            MessageBox.Show(ex.Message, "更新配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _configSourceLabel.Text = $"更新失败：{ex.Message}";
            return false;
        }
    }

    private async Task<string?> UpdateConfigAfterSaveAsync(string savedAddonFilePath)
    {
        var result = await QueueProjectConfigUpdateAsync(savedAddonFilePath);
        return DescribeAddonSyncIssue(result.AddonSync);
    }

    private Task<ProjectConfigUpdateResult> QueueProjectConfigUpdateAsync(string? savedAddonFilePath)
    {
        lock (_configUpdateSync)
        {
            if (_shutdownStarted)
            {
                return Task.FromException<ProjectConfigUpdateResult>(
                    new OperationCanceledException("程序正在关闭。"));
            }

            var updateTask = RunQueuedConfigUpdateAsync(_configUpdateTail, savedAddonFilePath);
            _configUpdateTail = updateTask;
            return updateTask;
        }
    }

    private async Task<ProjectConfigUpdateResult> RunQueuedConfigUpdateAsync(
        Task previousUpdate,
        string? savedAddonFilePath)
    {
        await Task.Yield();
        try
        {
            await previousUpdate;
        }
        catch
        {
            // 前一个调用方会收到自己的异常；队列仍继续处理后续更新。
        }

        if (_shutdownStarted)
        {
            throw new OperationCanceledException("程序正在关闭。");
        }

        return await UpdateConfigFromProjectCoreAsync(savedAddonFilePath);
    }

    private Task GetPendingConfigUpdateTask()
    {
        lock (_configUpdateSync)
        {
            return _configUpdateTail;
        }
    }

    private async Task WaitForPendingConfigUpdatesAsync()
    {
        while (true)
        {
            var pending = GetPendingConfigUpdateTask();
            await pending;
            lock (_configUpdateSync)
            {
                if (ReferenceEquals(pending, _configUpdateTail))
                {
                    return;
                }
            }
        }
    }

    private async Task<ProjectConfigUpdateResult> UpdateConfigFromProjectCoreAsync(string? savedAddonFilePath)
    {
        if (_shutdownStarted)
        {
            throw new OperationCanceledException("程序正在关闭。");
        }

        var classDirectory = Path.Combine(_addonSyncService.SourceRoot, "class");
        var classMacrosPath = Path.Combine(_addonSyncService.SourceRoot, "core", "classmacros.lua");
        if (!Directory.Exists(classDirectory))
        {
            throw new DirectoryNotFoundException($"找不到项目 Fuyutsui class 目录: {classDirectory}");
        }

        _configSourceLabel.Text = File.Exists(classMacrosPath)
            ? $"项目 Fuyutsui: {classDirectory} + classmacros.lua"
            : $"项目 Fuyutsui class: {classDirectory}";
        var configDirectory = ConfigService.ResolveConfigPath(_baseDirectory);
        if (!Directory.Exists(configDirectory))
        {
            throw new DirectoryNotFoundException($"配置目录不存在: {configDirectory}");
        }

        var keymapDirectory = Path.Combine(_baseDirectory, "keymap");

        try
        {
            UseWaitCursor = true;
            var result = await Task.Run(() =>
            {
                var configResult = FuyutsuiConfigConverter.UpdateFromClassDirectory(classDirectory, configDirectory);
                FuyutsuiKeymapConverter.UpdateResult? keymapResult = null;
                if (File.Exists(classMacrosPath))
                {
                    keymapResult = FuyutsuiKeymapConverter.UpdateFromClassMacros(classMacrosPath, keymapDirectory);
                }

                var addonSync = string.IsNullOrWhiteSpace(savedAddonFilePath)
                    ? _addonSyncService.SynchronizeAll()
                    : _addonSyncService.SynchronizeFile(savedAddonFilePath);
                return new ProjectConfigUpdateResult(configResult, keymapResult, addonSync);
            });

            if (_shutdownStarted)
            {
                throw new OperationCanceledException("程序正在关闭。");
            }

            _moduleEditor.ReloadCatalogs();
            AppendLog($"已从项目 Fuyutsui 更新配置: {result.Config.UpdatedFiles.Count} 个文件 ← {result.Config.ClassDirectory}");
            foreach (var warning in result.Config.Warnings.Take(20))
            {
                AppendLog($"配置警告: {warning}");
            }

            if (result.Keymap is { } keymap)
            {
                AppendLog($"已从 classmacros 更新 keymap: {keymap.UpdatedFiles.Count} 个文件 ← {keymap.ClassMacrosPath}");
                foreach (var warning in keymap.Warnings.Take(20))
                {
                    AppendLog($"keymap 警告: {warning}");
                }
            }
            else
            {
                AppendLog("项目 Fuyutsui 中未找到 core\\classmacros.lua，已跳过 keymap 更新");
            }

            LogAddonSyncResult(
                string.IsNullOrWhiteSpace(savedAddonFilePath) ? "游戏插件全量同步" : "游戏插件文件同步",
                result.AddonSync);

            if (_runtimeSession.HasSession)
            {
                AppendLog("配置已更新, 重新启动运行");
                await StartOrRestartRuntimeAsync(restart: true, waitForConfigUpdates: false);
            }

            if (_shutdownStarted)
            {
                throw new OperationCanceledException("程序正在关闭。");
            }

            return result;
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void ShowProjectConfigUpdateResult(ProjectConfigUpdateResult result)
    {
        var warningCount = result.Config.Warnings.Count + (result.Keymap?.Warnings.Count ?? 0);
        var warningText = warningCount == 0
            ? string.Empty
            : $"\n转换警告 {warningCount} 条（详见日志）。";
        var keymapText = result.Keymap is { } keymap
            ? $"\nkeymap: {keymap.UpdatedFiles.Count} 个文件"
            : "\nkeymap: 未更新（缺少 classmacros.lua）";
        var syncIssue = DescribeAddonSyncIssue(result.AddonSync);
        var syncText = syncIssue is null
            ? $"\n游戏插件: 已复制 {result.AddonSync.CopiedFiles.Count}，哈希相同 {result.AddonSync.SkippedFiles.Count}\n{result.AddonSync.TargetRoot}"
            : $"\n游戏插件: {syncIssue}";

        _configSourceLabel.Text = syncIssue is null && warningCount == 0
            ? $"已更新 {result.Config.UpdatedFiles.Count} 个配置文件，并完成游戏同步"
            : $"配置已更新；{syncIssue ?? $"存在 {warningCount} 条转换警告"}";
        _configSourceLabel.ForeColor = syncIssue is null && warningCount == 0
            ? UiTheme.Success
            : UiTheme.Warning;
        _settingsToolTip.SetToolTip(_configSourceLabel, _configSourceLabel.Text);

        if (syncIssue is not null || warningCount > 0)
        {
            MessageBox.Show(
                $"已从项目 Fuyutsui 更新 {result.Config.UpdatedFiles.Count} 个职业配置。{keymapText}{syncText}{warningText}",
                "更新配置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void LogAddonSyncResult(string operation, FuyutsuiAddonSyncResult result)
    {
        if (!result.TargetFound)
        {
            AppendLog($"{operation}: {result.SkippedReason}");
            return;
        }

        AppendLog(
            $"{operation}: 已复制 {result.CopiedFiles.Count}，哈希相同 {result.SkippedFiles.Count} → {result.TargetRoot}");
        foreach (var failure in result.Failures.Take(20))
        {
            AppendLog($"插件同步失败: {failure.RelativePath}: {failure.Message}");
        }

        if (result.Failures.Count > 20)
        {
            AppendLog($"插件同步另有 {result.Failures.Count - 20} 个失败文件未展开。");
        }
    }

    private static string? DescribeAddonSyncIssue(FuyutsuiAddonSyncResult result)
    {
        if (!result.TargetFound)
        {
            return result.SkippedReason;
        }

        if (result.Failures.Count == 0)
        {
            return null;
        }

        var first = result.Failures[0];
        return result.Failures.Count == 1
            ? $"{first.RelativePath}: {first.Message}"
            : $"{result.Failures.Count} 个文件同步失败；首个失败为 {first.RelativePath}: {first.Message}";
    }

    private void ApplyInitialOptions()
    {
        var cachedToggleKey = _uiCache.ToggleKey?.Trim();
        var initialToggleKey = !string.IsNullOrWhiteSpace(cachedToggleKey)
            ? cachedToggleKey
            : _initialOptions.ToggleKey.Trim();
        initialToggleKey = string.IsNullOrWhiteSpace(initialToggleKey) ? "XBUTTON2" : initialToggleKey;
        _toggleKeyName = IsUnsupportedToggleKey(initialToggleKey) ? "XBUTTON2" : initialToggleKey;
        _selectedModuleId = string.IsNullOrWhiteSpace(_uiCache.SelectedModuleId)
            ? null
            : _uiCache.SelectedModuleId.Trim();
        SetToggleKeyButtonText();
        _modeComboBox.SelectedIndex = _initialOptions.Mode switch
        {
            SendMode.Click => 1,
            SendMode.Hold => 2,
            _ => 0
        };
        RefreshModuleSelector(_lastSnapshot, forceRefresh: false);
    }

    private void WireSettingEvents()
    {
        _modeComboBox.SelectedIndexChanged += HandleSettingCommitted;
        _moduleComboBox.SelectedIndexChanged += HandleModuleSelectionChanged;
    }

    private async void HandleSettingCommitted(object? sender, EventArgs e)
    {
        await RestartRuntimeAfterSettingChangeAsync();
    }

    private async Task StartRuntimeAsync()
    {
        if (_runtimeSession.IsRunning)
        {
            return;
        }

        await StartOrRestartRuntimeAsync(restart: false);
    }

    private async Task<bool> StartOrRestartRuntimeAsync(
        bool restart,
        bool waitForConfigUpdates = true)
    {
        if (_shutdownStarted)
        {
            return false;
        }

        var options = BuildOptions();
        if (!ValidateRuntimeOptions(options))
        {
            return false;
        }

        var requestVersion = Interlocked.Increment(ref _runtimeRequestVersion);

        try
        {
            if (waitForConfigUpdates)
            {
                await WaitForPendingConfigUpdatesAsync();
                if (_shutdownStarted || requestVersion != Volatile.Read(ref _runtimeRequestVersion))
                {
                    return false;
                }
            }

            if (restart)
            {
                await _runtimeSession.RestartAsync(options, requestVersion);
            }
            else
            {
                await _runtimeSession.StartAsync(options, requestVersion);
            }
        }
        catch (Exception ex)
        {
            if (_shutdownStarted || requestVersion != Volatile.Read(ref _runtimeRequestVersion))
            {
                return false;
            }

            var operation = restart ? "重启" : "启动";
            MessageBox.Show(ex.Message, $"{operation}失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AppendLog($"{operation}失败: {ex.Message}");
            SetRuntimeControls(running: _runtimeSession.IsRunning);
            return false;
        }

        if (_shutdownStarted || requestVersion != Volatile.Read(ref _runtimeRequestVersion))
        {
            return false;
        }

        if (!_runtimeSession.IsRunning)
        {
            SetRuntimeControls(running: false);
            return false;
        }

        ResetRuntimeLogState();
        SetRuntimeControls(running: true);
        AppendLog($"运行已{(restart ? "重启" : "启动")}: {_processLocator.DescribeConfiguredProcesses()} / {options.ToggleKey} / {ModeLabel(options.Mode)}");
        return true;
    }

    private bool ValidateRuntimeOptions(AppOptions options)
    {
        if (IsUnsupportedToggleKey(options.ToggleKey))
        {
            MessageBox.Show("触发键不支持 ALT，请选择其他按键。", "Shigure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (_triggerKeyState.ResolveVirtualKey(options.ToggleKey) is null)
        {
            MessageBox.Show($"无法识别触发键: {options.ToggleKey}", "Shigure", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void ResetRuntimeLogState()
    {
        _lastLoggedStep = null;
        _lastLoggedStepDetails = null;
        _lastLoggedScanFailureReason = null;
        _lastLoggedClass = null;
        _lastLoggedModule = null;
        _lastLoggedEnabled = null;
    }

    private async Task RestartRuntimeFromEditorAsync()
    {
        RefreshModuleSelector(_lastSnapshot, forceRefresh: false);
        if (!_runtimeSession.HasSession)
        {
            return;
        }

        AppendLog("模块已变更, 重新启动运行");
        await StartOrRestartRuntimeAsync(restart: true);
    }

    private void ToggleEnabled()
    {
        if (!_runtimeSession.IsRunning)
        {
            return;
        }

        _runtimeSession.ToggleEnabled();
    }

    private AppOptions BuildOptions()
    {
        var toggleKey = string.IsNullOrWhiteSpace(_toggleKeyName)
            ? "XBUTTON2"
            : _toggleKeyName.Trim();

        return _initialOptions with { ToggleKey = toggleKey, Mode = ReadMode(), ModuleId = _selectedModuleId };
    }

    private SendMode ReadMode()
    {
        return _modeComboBox.SelectedIndex switch
        {
            1 => SendMode.Click,
            2 => SendMode.Hold,
            _ => SendMode.Switch
        };
    }

    private void HandleSnapshotUpdated(long sessionId, RenderSnapshot snapshot)
    {
        PostToUi(() =>
        {
            if (_runtimeSession.CurrentSessionId == sessionId)
            {
                ApplySnapshot(snapshot);
            }
        });
    }

    private void HandleRuntimeFailed(long sessionId, Exception exception)
    {
        PostToUi(() =>
        {
            if (_runtimeSession.CurrentSessionId != sessionId)
            {
                return;
            }

            AppendLog($"运行异常: {exception.Message}");
            _titleLabel.ForeColor = UiTheme.Danger;
            SetRuntimeControls(running: false);
        });
    }

    private void HandleRuntimeStopped(long sessionId)
    {
        PostToUi(() =>
        {
            if (_runtimeSession.CurrentSessionId == sessionId)
            {
                SetRuntimeControls(running: false);
            }
        });
    }

    private void ApplySnapshot(RenderSnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        UpdateHeaderIconColor(snapshot.ClassId);
        UpdateLogicStatusLabel(snapshot.Enabled);
        _enableButton.Text = snapshot.Enabled ? "关闭" : "开启";

        RefreshModuleSelector(snapshot, forceRefresh: false);
        _statusForm.ApplySnapshot(snapshot);
        WriteSnapshotLog(snapshot);
    }

    private void RefreshModuleSelector(RenderSnapshot? snapshot, bool forceRefresh)
    {
        if (_moduleComboBox is null)
        {
            return;
        }

        var hasValidState = snapshot?.State?.GetBool("有效性") == true;
        var (classId, specId, partyType, heroTalent, filterText) = GetModuleFilter(snapshot, hasValidState);
        var modules = !hasValidState
            ? _moduleStore.GetModules()
            : _moduleStore.FindMatches(classId, specId, partyType, heroTalent);
        var signature = BuildModuleSelectorSignature(
            hasValidState,
            classId,
            specId,
            partyType,
            heroTalent,
            modules);
        if (!forceRefresh && signature == _lastModuleSelectorSignature)
        {
            return;
        }

        _lastModuleSelectorSignature = signature;

        _suppressModuleSelectionChanged = true;
        try
        {
            _moduleComboBox.BeginUpdate();
            try
            {
                _moduleComboBox.Items.Clear();
                _moduleComboBox.Items.Add(ModuleSelectionOption.Auto);
                foreach (var module in modules)
                {
                    _moduleComboBox.Items.Add(new ModuleSelectionOption(module.Id, ModuleDisplay.FormatListItem(module)));
                }

                var selectedIndex = 0;
                var selectedModuleVisible = string.IsNullOrWhiteSpace(_selectedModuleId);
                if (!string.IsNullOrWhiteSpace(_selectedModuleId))
                {
                    for (var i = 1; i < _moduleComboBox.Items.Count; i++)
                    {
                        if (_moduleComboBox.Items[i] is ModuleSelectionOption option
                            && string.Equals(option.ModuleId, _selectedModuleId, StringComparison.OrdinalIgnoreCase))
                        {
                            selectedIndex = i;
                            selectedModuleVisible = true;
                            break;
                        }
                    }
                }

                _moduleComboBox.SelectedIndex = selectedIndex;
                _moduleCountLabel.Text = selectedModuleVisible
                    ? $"可选模块: {modules.Count}"
                    : $"可选模块: {modules.Count}，已选模块不符合当前筛选";
            }
            finally
            {
                _moduleComboBox.EndUpdate();
            }
        }
        finally
        {
            _suppressModuleSelectionChanged = false;
        }

        _moduleFilterLabel.Text = filterText;
    }

    private string BuildModuleSelectorSignature(
        bool hasValidState,
        int? classId,
        int? specId,
        int? partyType,
        int? heroTalent,
        IReadOnlyList<ModuleDefinition> modules)
    {
        var moduleText = string.Join("|", modules.Select(module => $"{module.Id}:{module.Name}:{ModuleDisplay.FormatMatch(module.Match)}"));
        return $"{hasValidState}:{classId}:{specId}:{partyType}:{heroTalent}:{_selectedModuleId}:{moduleText}";
    }

    private static (int? ClassId, int? SpecId, int? PartyType, int? HeroTalent, string Text) GetModuleFilter(
        RenderSnapshot? snapshot,
        bool hasValidState)
    {
        if (!hasValidState || snapshot?.State is null)
        {
            return (null, null, null, null, "筛选: 等待游戏状态，暂时显示全部模块");
        }

        var partyType = snapshot.State.GetInt("队伍类型");
        var heroTalent = snapshot.State.GetInt("英雄天赋");
        return (
            snapshot.ClassId,
            snapshot.SpecId,
            partyType,
            heroTalent,
            $"筛选: {ModuleDisplay.FormatState(snapshot)}");
    }

    private async void HandleModuleSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressModuleSelectionChanged)
        {
            return;
        }

        _selectedModuleId = _moduleComboBox.SelectedItem is ModuleSelectionOption option
            ? option.ModuleId
            : null;
        SaveUiCache();
        AppendLog($"模块选择: {(_selectedModuleId is null ? "自动选择" : _moduleComboBox.Text)}");
        await RestartRuntimeAfterSettingChangeAsync();
    }

    private async Task RestartRuntimeAfterSettingChangeAsync()
    {
        var options = BuildOptions();
        if (_runtimeSession.IsRunning && options == _runtimeSession.CurrentOptions)
        {
            return;
        }

        AppendLog("设置已变更, 重新启动运行");
        await StartOrRestartRuntimeAsync(restart: _runtimeSession.HasSession);
    }

    private void WriteSnapshotLog(RenderSnapshot snapshot)
    {
        if (!string.Equals(snapshot.ScanFailureReason, _lastLoggedScanFailureReason, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(snapshot.ScanFailureReason))
            {
                if (!string.IsNullOrWhiteSpace(_lastLoggedScanFailureReason))
                {
                    AppendLog("扫描已恢复");
                }
            }
            else
            {
                AppendLog($"扫描失败: {snapshot.ScanFailureReason}");
            }

            _lastLoggedScanFailureReason = snapshot.ScanFailureReason;
        }

        var classSpec = snapshot.ClassName is null ? null : $"{snapshot.ClassName} / {snapshot.SpecName ?? "-"}";
        if (!string.IsNullOrWhiteSpace(classSpec) && classSpec != _lastLoggedClass)
        {
            _lastLoggedClass = classSpec;
            AppendLog($"识别职业: {classSpec}");
        }

        if (_lastLoggedEnabled != snapshot.Enabled)
        {
            _lastLoggedEnabled = snapshot.Enabled;
            AppendLog(snapshot.Enabled ? "逻辑已开启" : "逻辑已关闭");
        }

        if (snapshot.ModuleName != _lastLoggedModule)
        {
            _lastLoggedModule = snapshot.ModuleName;
            if (!string.IsNullOrWhiteSpace(snapshot.ModuleName))
            {
                AppendLog($"匹配模块: {snapshot.ModuleName}");
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentStep))
        {
            var details = BuildStepLogDetails(snapshot);
            if (snapshot.CurrentStep != _lastLoggedStep || details != _lastLoggedStepDetails)
            {
                _lastLoggedStep = snapshot.CurrentStep;
                _lastLoggedStepDetails = details;
                AppendLog($"步骤: {snapshot.CurrentStep}{details}");
            }
        }
    }

    private static string BuildStepLogDetails(RenderSnapshot snapshot)
    {
        var fields = new (string Key, string Label)[]
        {
            ("动作单位", "目标"),
            ("动作按键", "按键"),
            ("动作延迟", "动作延迟"),
            ("逻辑延迟", "逻辑延迟"),
            ("规则编号", "规则编号"),
            ("限流键", "限流键"),
            ("发送失败", "发送失败")
        };
        var details = new List<string>();
        foreach (var (key, label) in fields)
        {
            if (!snapshot.UnitInfo.TryGetValue(key, out var value))
            {
                continue;
            }

            var text = UiTheme.FormatValue(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                details.Add($"{label}: {text}");
            }
        }

        return details.Count == 0 ? string.Empty : $"，{string.Join("，", details)}";
    }

    private void SetRuntimeControls(bool running)
    {
        if (!running)
        {
            UpdateHeaderIconColor(null);
            UpdateLogicStatusLabel(enabled: false);
        }

        _enableButton.Enabled = running;
    }

    private void UpdateLogicStatusLabel(bool enabled)
    {
        _runtimeStatusLabel.Text = string.Empty;
        _titleLabel.ForeColor = enabled ? UiTheme.Accent : UiTheme.Text;
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Form is closing.
            }

            return;
        }

        action();
    }

    private void AppendLog(string message)
    {
        _statusForm.AppendLog(message);
    }

    private void BeginCaptureToggleKey()
    {
        ShowSettingsView();

        if (_isCapturingToggleKey)
        {
            return;
        }

        _isCapturingToggleKey = true;
        _toggleKeyButton.Text = "请按任意键...";
        ActiveControl = null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_isCapturingToggleKey)
        {
            return TryHandleCapturedKey(keyData & Keys.KeyCode);
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool TryHandleCapturedKey(Keys key)
    {
        if (key is Keys.Escape)
        {
            _isCapturingToggleKey = false;
            SetToggleKeyButtonText();
            AppendLog("已取消按键录入");
            return true;
        }

        if (IsUnsupportedToggleKey(key.ToString()))
        {
            _toggleKeyButton.Text = "ALT 不支持";
            AppendLog("触发键不支持 ALT, 请重试");
            _ = ResetCaptureButtonTextAsync();
            _isCapturingToggleKey = false;
            return true;
        }

        var keyName = TryMapKeyToHotkey(key);
        if (keyName is null)
        {
            _toggleKeyButton.Text = "不支持";
            AppendLog("该按键暂不支持, 请重试");
            _ = ResetCaptureButtonTextAsync();
            _isCapturingToggleKey = false;
            return true;
        }

        _isCapturingToggleKey = false;
        _toggleKeyName = keyName;
        SetToggleKeyButtonText();
        SaveUiCache();
        AppendLog($"已录入触发键: {_toggleKeyName}");
        HandleSettingCommitted(this, EventArgs.Empty);
        return true;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (!_isCapturingToggleKey)
        {
            return false;
        }

        const int WmXButtonDown = 0x020B;
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        if (m.Msg is WmKeyDown or WmSysKeyDown)
        {
            return TryHandleCapturedKey((Keys)(int)m.WParam);
        }

        if (m.Msg != WmXButtonDown)
        {
            return false;
        }

        var xButton = (((int)m.WParam) >> 16) & 0xFFFF;
        var keyName = xButton switch
        {
            1 => "XBUTTON1",
            2 => "XBUTTON2",
            _ => null
        };

        if (keyName is null)
        {
            return false;
        }

        _isCapturingToggleKey = false;
        _toggleKeyName = keyName;
        SetToggleKeyButtonText();
        SaveUiCache();
        AppendLog($"已录入触发键: {_toggleKeyName}");
        HandleSettingCommitted(this, EventArgs.Empty);
        return true;
    }

    private void ApplyCachedWindowState()
    {
        if (_uiCache.MainWindowBounds is { } mainBounds)
        {
            var restoredBounds = new Rectangle(
                mainBounds.X,
                mainBounds.Y,
                Math.Max(MinimumSize.Width, mainBounds.Width),
                Math.Max(MinimumSize.Height, mainBounds.Height));
            if (UiCacheStore.IsBoundsVisible(restoredBounds))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = restoredBounds;
            }
        }
        else if (_uiCache.MainWindowLocation is { } mainLocation)
        {
            var restoredBounds = new Rectangle(mainLocation.X, mainLocation.Y, Width, Height);
            if (UiCacheStore.IsBoundsVisible(restoredBounds))
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(mainLocation.X, mainLocation.Y);
            }
        }

        _statusForm.ApplyCachedBounds(_uiCache.SettingsWindowBounds);
        _statusForm.ApplyCachedPage(_uiCache.SelectedSettingsPage);
    }

    private void SaveUiCache()
    {
        var latestCache = UiCacheStore.Load();
        _uiCache.ModuleRulesGridColumns = latestCache.ModuleRulesGridColumns;

        _uiCache.MainWindowBounds = new WindowBounds
        {
            X = Left,
            Y = Top,
            Width = Width,
            Height = Height
        };
        _uiCache.MainWindowLocation = new WindowLocation
        {
            X = Left,
            Y = Top
        };

        if (_statusForm.HasKnownBounds)
        {
            _uiCache.SettingsWindowBounds = _statusForm.GetCachedBounds();
        }

        _uiCache.SelectedSettingsPage = _statusForm.SelectedPageKey;

        _uiCache.ToggleKey = _toggleKeyName;
        _uiCache.SelectedModuleId = _selectedModuleId;
        UiCacheStore.Save(_uiCache);
    }

    private void ShowSettingsView()
    {
        RefreshModuleSelector(_lastSnapshot, forceRefresh: false);
        _statusForm.ShowSettings(_lastSnapshot);
    }

    private async Task ResetCaptureButtonTextAsync()
    {
        await Task.Delay(1000);
        if (!IsDisposed)
        {
            PostToUi(SetToggleKeyButtonText);
        }
    }

    private void CancelToggleKeyCapture()
    {
        if (!_isCapturingToggleKey)
        {
            return;
        }

        _isCapturingToggleKey = false;
        SetToggleKeyButtonText();
    }

    private void SetToggleKeyButtonText()
    {
        _toggleKeyButton.Text = _toggleKeyName;
    }

    private nint HitTestResizeGrip(Point clientPoint)
    {
        var left = clientPoint.X <= ResizeGripSize;
        var right = clientPoint.X >= ClientSize.Width - ResizeGripSize;
        var top = clientPoint.Y <= ResizeGripSize;
        var bottom = clientPoint.Y >= ClientSize.Height - ResizeGripSize;

        if (top && left)
        {
            return NativeMethods.HtTopLeft;
        }

        if (top && right)
        {
            return NativeMethods.HtTopRight;
        }

        if (bottom && left)
        {
            return NativeMethods.HtBottomLeft;
        }

        if (bottom && right)
        {
            return NativeMethods.HtBottomRight;
        }

        if (left)
        {
            return NativeMethods.HtLeft;
        }

        if (right)
        {
            return NativeMethods.HtRight;
        }

        if (top)
        {
            return NativeMethods.HtTop;
        }

        if (bottom)
        {
            return NativeMethods.HtBottom;
        }

        return NativeMethods.HtClient;
    }

    private string? TryMapKeyToHotkey(Keys key)
    {
        var keyName = key.ToString().ToUpperInvariant();
        if (IsUnsupportedToggleKey(keyName))
        {
            return null;
        }

        if (key is >= Keys.D0 and <= Keys.D9)
        {
            return ((char)('0' + (key - Keys.D0))).ToString();
        }

        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
        {
            return $"NUMPAD{key - Keys.NumPad0}";
        }

        return keyName switch
        {
            "OEMCOMMA" => ",",
            "OEMPERIOD" => ".",
            "OEMQUESTION" => "/",
            "OEMSEMICOLON" => ";",
            "OEMQUOTES" => "'",
            "OEMOPENBRACKETS" => "[",
            "OEMCLOSEBRACKETS" => "]",
            "OEMPLUS" => "=",
            "OEMMINUS" => "-",
            "OEMTILDE" => "`",
            "OEMBACKSLASH" => "\\",
            "DECIMAL" => "NUMPADDECIMAL",
            "ADD" => "NUMPADPLUS",
            "SUBTRACT" => "NUMPADMINUS",
            "MULTIPLY" => "NUMPADMULTIPLY",
            "DIVIDE" => "NUMPADDIVIDE",
            _ => _triggerKeyState.ResolveVirtualKey(keyName) is not null ? keyName : null
        };
    }

    private static bool IsUnsupportedToggleKey(string keyName)
    {
        var key = keyName.Trim().ToUpperInvariant();
        return key is "ALT" or "MENU" or "LMENU" or "RMENU";
    }

    private static Label CreateInfoLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = UiTheme.Muted,
            Margin = new Padding(0, 0, 0, 4)
        };
    }

    private void EnableDrag(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessageW(Handle, NativeMethods.WmNcLButtonDown, NativeMethods.HtCaption, 0);
            }
        };
    }

    private static void ConfigureTopBarButton(Button button)
    {
        button.AutoSize = false;
        button.Size = new Size(88, 36);
        button.Padding = new Padding(4, 1, 4, 1);
    }

    private sealed record ModuleSelectionOption(string? ModuleId, string Text)
    {
        public static readonly ModuleSelectionOption Auto = new(null, "自动选择（最匹配）");

        public override string ToString()
        {
            return Text;
        }
    }

    private static Icon? LoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }

    private void TryApplyApplicationIcon()
    {
        var icon = LoadApplicationIcon();
        if (icon != null)
        {
            Icon = icon;
        }
    }

    private static string ModeLabel(SendMode mode)
    {
        return mode switch
        {
            SendMode.Click => "单击",
            SendMode.Hold => "按住",
            _ => "开关"
        };
    }
}
