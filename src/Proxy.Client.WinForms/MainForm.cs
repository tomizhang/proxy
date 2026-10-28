using System;
using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Proxy.Client.Core;
using Proxy.Client.Core.Transport;
using Proxy.Client.Core.Tun;
using Proxy.Common.Crypto;
using Proxy.Common.Models;

namespace Proxy.Client.WinForms;

public partial class MainForm : Form
{
    private ProxyClientConfig _config;
    private ProxyClientEngine? _engine;
    private readonly System.Windows.Forms.Timer _speedTimer;
    private readonly System.Windows.Forms.Timer _logFlushTimer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private readonly NotifyIcon _trayIcon;

    private long _lastUploadBytes;
    private long _lastDownloadBytes;
    private long _currentTotalUpload;
    private long _currentTotalDownload;

    // 核心 UI 控件
    private Label _lblStatusDot = null!;
    private Label _lblStatusText = null!;
    private Button _btnConnectTop = null!;
    private Button _btnConnectBottom = null!;
    private Button _btnTestConnection = null!;
    private Label _lblLatency = null!;
    private Label _lblSpeedUp = null!;
    private Label _lblSpeedDown = null!;

    // 配置控件
    private TextBox _txtHost = null!;
    private NumericUpDown _numPort = null!;
    private TextBox _txtPath = null!;
    private CheckBox _chkUseTls = null!;
    private TextBox _txtSni = null!;
    private TextBox _txtPassword = null!;
    private ComboBox _cboAlgo = null!;
    private NumericUpDown _numConcurrency = null!;
    private NumericUpDown _numSocks5Port = null!;
    private CheckBox _chkEnableSystemProxy = null!;
    private CheckBox _chkEnableBypass = null!;
    private TextBox _txtBypassRules = null!;
    private CheckBox _chkEnableProcessBypass = null!;
    private Button _btnModeBlacklist = null!;
    private Button _btnModeWhitelist = null!;
    private Button _btnToggleMode = null!;
    private Label _lblProcessTip = null!;
    private CheckedListBox _clbProcesses = null!;
    private CheckBox _chkEnableTun = null!;
    private CheckBox _chkEnableLogs = null!;
    private RichTextBox _rtbLogs = null!;
    private bool _isLoadingConfig = true;

    public MainForm()
    {
        _isLoadingConfig = true;
        try
        {
            _config = ConfigManager.Load();

            InitializeComponentsCustom();

            // 速度刷新定时器 (1秒)
            _speedTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _speedTimer.Tick += OnSpeedTimerTick;

            // 日志批量异步刷新定时器 (150ms)，彻底解决长时间代理后高频日志引发的界面卡死
            _logFlushTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _logFlushTimer.Tick += OnLogFlushTimerTick;
            _logFlushTimer.Start();

            // 托盘图标
            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Shield,
                Text = "C# Cloudflare 代理客户端",
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                BringToFront();
            };

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开主界面", null, (s, e) => { Show(); WindowState = FormWindowState.Normal; });
            trayMenu.Items.Add("退出程序", null, async (s, e) => { await ExitApplicationAsync(); });
            _trayIcon.ContextMenuStrip = trayMenu;

            LoadConfigToUi();
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void InitializeComponentsCustom()
    {
        Text = "C# Cloudflare 代理工具 (自定义加密 & TUN 模式)";
        Size = new Size(840, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 650);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.FromArgb(244, 245, 247);

        // ================= 1. 顶部醒目主控制卡片 =================
        var topCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 110,
            BackColor = Color.White,
            Padding = new Padding(24, 16, 24, 16)
        };

        var statusPanel = new FlowLayoutPanel
        {
            Location = new Point(24, 18),
            Size = new Size(450, 36),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _lblStatusDot = new Label
        {
            Text = "●",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.Gray,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 0)
        };

        _lblStatusText = new Label
        {
            Text = "未连接代理 (点击右侧按钮连接)",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(64, 64, 64),
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0)
        };

        statusPanel.Controls.AddRange([_lblStatusDot, _lblStatusText]);

        var metricsPanel = new FlowLayoutPanel
        {
            Location = new Point(24, 60),
            Size = new Size(500, 32),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _lblLatency = new Label
        {
            Text = "延迟: -- ms",
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.DimGray,
            AutoSize = true,
            Margin = new Padding(0, 0, 20, 0)
        };

        _lblSpeedUp = new Label
        {
            Text = "↑ 0.0 KB/s (0.0 MB)",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 120, 215),
            AutoSize = true,
            Margin = new Padding(0, 0, 20, 0)
        };

        _lblSpeedDown = new Label
        {
            Text = "↓ 0.0 KB/s (0.0 MB)",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 167, 69),
            AutoSize = true
        };

        metricsPanel.Controls.AddRange([_lblLatency, _lblSpeedUp, _lblSpeedDown]);

        // 顶部核心【点击连接】大按钮
        _btnConnectTop = new Button
        {
            Text = "▶ 点击连接",
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Size = new Size(180, 56),
            Location = new Point(610, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.FromArgb(16, 124, 65), // 醒目绿色
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnConnectTop.FlatAppearance.BorderSize = 0;
        _btnConnectTop.Click += async (s, e) => await OnToggleClickedAsync();

        topCard.Controls.AddRange([statusPanel, metricsPanel, _btnConnectTop]);

        // ================= 2. 中部配置卡片 =================
        var tabControl = new TabControl
        {
            Dock = DockStyle.Top,
            Height = 270,
            Padding = new Point(14, 8),
            Font = new Font("Microsoft YaHei UI", 9F)
        };

        // Tab 1: 节点配置
        var tabNode = new TabPage("  远程节点配置 (Cloudflare WSS)  ");
        tabNode.BackColor = Color.White;

        var lblHost = new Label { Text = "节点地址 (优选IP/优选域名):", Location = new Point(12, 20), AutoSize = true };
        _txtHost = new TextBox { Location = new Point(195, 17), Width = 235 };

        var lblPort = new Label { Text = "端口:", Location = new Point(445, 20), AutoSize = true };
        _numPort = new NumericUpDown { Location = new Point(490, 17), Width = 75, Minimum = 1, Maximum = 65535, Value = 5000 };

        _chkUseTls = new CheckBox { Text = "开启 TLS (WSS 443)", Location = new Point(580, 18), AutoSize = true };
        _chkUseTls.CheckedChanged += (s, e) =>
        {
            if (_chkUseTls.Checked && _numPort.Value == 5000) _numPort.Value = 443;
        };

        var lblPath = new Label { Text = "WebSocket 路径:", Location = new Point(12, 60), AutoSize = true };
        _txtPath = new TextBox { Location = new Point(195, 57), Width = 150, Text = "/tunnel" };

        var lblSni = new Label { Text = "真实域名 (SNI/Host):", Location = new Point(360, 60), AutoSize = true };
        _txtSni = new TextBox { Location = new Point(490, 57), Width = 240 };

        var lblPwd = new Label { Text = "认证密码 / Token:", Location = new Point(12, 100), AutoSize = true };
        _txtPassword = new TextBox { Location = new Point(195, 97), Width = 235, UseSystemPasswordChar = true };

        var lblAlgo = new Label { Text = "自定义加密算法:", Location = new Point(445, 100), AutoSize = true };
        _cboAlgo = new ComboBox { Location = new Point(555, 97), Width = 175, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboAlgo.Items.AddRange(CryptoFactory.SupportedAlgorithms);

        var lblConcurrency = new Label { Text = "并发加速通道数:", Location = new Point(12, 133), AutoSize = true };
        _numConcurrency = new NumericUpDown { Location = new Point(195, 130), Width = 70, Minimum = 1, Maximum = 16, Value = 4 };
        var lblConcurrencyTip = new Label
        {
            Text = "(多路复用连接池：单 IP 开启多条物理隧道并发传输，消除队头阻塞并突破单连接限速；支持逗号分隔多个优选 IP)",
            Location = new Point(275, 133),
            AutoSize = true,
            ForeColor = Color.DarkSlateGray,
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };

        var lblCdnTip = new Label
        {
            Text = "💡 优选提示：若使用 Cloudflare 优选，【节点地址】填优选 IP 或优选域名，【真实域名】填您自己的域名 (如 sg.numb.dpdns.org)；若不使用优选，两者填相同真实域名即可。",
            Location = new Point(12, 162),
            Size = new Size(730, 24),
            ForeColor = Color.DarkSlateGray,
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };

        // 快捷操作按钮条（Tab 底部）
        var pnlActions = new FlowLayoutPanel
        {
            Location = new Point(12, 195),
            Size = new Size(720, 48),
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnConnectBottom = new Button
        {
            Text = "▶ 点击连接",
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
            Size = new Size(130, 40),
            BackColor = Color.FromArgb(16, 124, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 12, 0)
        };
        _btnConnectBottom.FlatAppearance.BorderSize = 0;
        _btnConnectBottom.Click += async (s, e) => await OnToggleClickedAsync();

        _btnTestConnection = new Button
        {
            Text = "⚡ 测试连接与延迟",
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Size = new Size(150, 40),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 12, 0)
        };
        _btnTestConnection.FlatAppearance.BorderSize = 0;
        _btnTestConnection.Click += async (s, e) => await OnTestConnectionClickedAsync();

        var btnSaveConfig = new Button
        {
            Text = "💾 保存当前配置",
            Font = new Font("Microsoft YaHei UI", 9.5F),
            Size = new Size(140, 40),
            BackColor = Color.FromArgb(235, 238, 242),
            ForeColor = Color.FromArgb(33, 37, 41),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnSaveConfig.FlatAppearance.BorderSize = 1;
        btnSaveConfig.FlatAppearance.BorderColor = Color.FromArgb(200, 205, 210);
        btnSaveConfig.Click += (s, e) => SaveUiToConfig();

        pnlActions.Controls.AddRange([_btnConnectBottom, _btnTestConnection, btnSaveConfig]);

        tabNode.Controls.AddRange([
            lblHost, _txtHost, lblPort, _numPort, _chkUseTls,
            lblPath, _txtPath, lblSni, _txtSni,
            lblPwd, _txtPassword, lblAlgo, _cboAlgo,
            lblConcurrency, _numConcurrency, lblConcurrencyTip,
            lblCdnTip,
            pnlActions
        ]);

        // Tab 2: 模式设置
        var tabMode = new TabPage("  代理模式与系统代理设置  ");
        tabMode.BackColor = Color.White;

        var lblSocks5 = new Label { Text = "本地代理端口 (SOCKS5/HTTP):", Location = new Point(20, 18), AutoSize = true };
        _numSocks5Port = new NumericUpDown { Location = new Point(220, 15), Width = 90, Minimum = 1000, Maximum = 65535, Value = 20808 };

        _chkEnableSystemProxy = new CheckBox
        {
            Text = "自动设置 Windows 系统代理 (推荐！开启后 Chrome/Edge 浏览器免插件直接访问 Google)",
            Location = new Point(20, 50),
            AutoSize = true,
            Checked = true,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(0, 102, 204)
        };

        _chkEnableBypass = new CheckBox
        {
            Text = "跳过无需代理的地址 (国内域名与局域网直连，如 baidu.com, *.cn, 192.168.* 等)",
            Location = new Point(20, 82),
            AutoSize = true,
            Checked = true,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 167, 69)
        };

        var lblBypassTitle = new Label { Text = "跳过规则 (分号分隔):", Location = new Point(20, 114), AutoSize = true };
        _txtBypassRules = new TextBox
        {
            Location = new Point(160, 111),
            Width = 570,
            Height = 48,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5F)
        };

        _chkEnableTun = new CheckBox
        {
            Text = "启用 TUN 全局网卡模式 (通过虚拟网卡接管全电脑游戏/命令行流量，需管理员权限)",
            Location = new Point(20, 170),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };

        var lblTunTip = new Label
        {
            Text = "💡 说明：勾选【自动设置系统代理】后，点击连接即可秒开 Google；若勾选【跳过无需代理的地址】，访问百度/腾讯等国内网站自动高速直连，不走代理隧道。",
            Location = new Point(20, 205),
            Size = new Size(720, 45),
            ForeColor = Color.FromArgb(80, 90, 100)
        };

        tabMode.Controls.AddRange([
            lblSocks5, _numSocks5Port,
            _chkEnableSystemProxy,
            _chkEnableBypass, lblBypassTitle, _txtBypassRules,
            _chkEnableTun, lblTunTip
        ]);

        // Tab 3: 应用分流 (程序名单)
        var tabProcess = new TabPage("  应用分流 (程序名单)  ");
        tabProcess.BackColor = Color.White;

        _chkEnableProcessBypass = new CheckBox
        {
            Text = "启用应用程序分流过滤",
            Location = new Point(20, 14),
            AutoSize = true,
            Checked = true,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(16, 124, 65)
        };

        _chkEnableProcessBypass.CheckedChanged += (s, e) =>
        {
            if (_isLoadingConfig) return;
            ApplyProcessBypassImmediately();
        };

        var pnlSwitch = new Panel
        {
            Location = new Point(20, 38),
            Size = new Size(725, 34)
        };

        var lblSwitchTitle = new Label
        {
            Text = "模式切换开关：",
            Location = new Point(0, 7),
            Size = new Size(95, 20),
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 60, 70)
        };

        _btnModeBlacklist = new Button
        {
            Text = "🛡️ 黑名单模式 (直连选中)",
            Location = new Point(95, 1),
            Size = new Size(185, 30),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnModeBlacklist.FlatAppearance.BorderSize = 0;
        _btnModeBlacklist.Click += (s, e) =>
        {
            if (_isLoadingConfig || _config.ProcessFilterMode == ProcessProxyMode.BypassSelected) return;
            UpdateModeSwitchUi(ProcessProxyMode.BypassSelected);
            ApplyProcessBypassImmediately();
        };

        _btnModeWhitelist = new Button
        {
            Text = "🚀 白名单模式 (仅代理选中)",
            Location = new Point(285, 1),
            Size = new Size(185, 30),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnModeWhitelist.FlatAppearance.BorderSize = 0;
        _btnModeWhitelist.Click += (s, e) =>
        {
            if (_isLoadingConfig || _config.ProcessFilterMode == ProcessProxyMode.ProxySelected) return;
            UpdateModeSwitchUi(ProcessProxyMode.ProxySelected);
            ApplyProcessBypassImmediately();
        };

        _btnToggleMode = new Button
        {
            Text = "🔀 点击切换模式",
            Location = new Point(475, 1),
            Size = new Size(140, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 244, 248),
            ForeColor = Color.FromArgb(0, 102, 204),
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
        };
        _btnToggleMode.FlatAppearance.BorderColor = Color.FromArgb(200, 215, 230);
        _btnToggleMode.Click += (s, e) =>
        {
            if (_isLoadingConfig) return;
            var nextMode = _config.ProcessFilterMode == ProcessProxyMode.ProxySelected
                ? ProcessProxyMode.BypassSelected
                : ProcessProxyMode.ProxySelected;
            UpdateModeSwitchUi(nextMode);
            ApplyProcessBypassImmediately();
        };

        pnlSwitch.Controls.AddRange([lblSwitchTitle, _btnModeBlacklist, _btnModeWhitelist, _btnToggleMode]);

        _lblProcessTip = new Label
        {
            Text = "💡 提示：名单中打勾的程序（如远程桌面 RustDesk、微信、QQ、网盘、Steam）网络请求自动直连，不消耗代理流量，极速且低延迟。",
            Location = new Point(20, 74),
            Size = new Size(720, 20),
            ForeColor = Color.FromArgb(80, 90, 100),
            Font = new Font("Microsoft YaHei UI", 8.5F)
        };

        _clbProcesses = new CheckedListBox
        {
            Location = new Point(20, 92),
            Size = new Size(530, 125),
            CheckOnClick = true,
            Font = new Font("Segoe UI", 9.5F)
        };
        _clbProcesses.ItemCheck += (s, e) => SafeBeginInvoke(ApplyProcessBypassImmediately);

        var pnlProcButtons = new Panel
        {
            Location = new Point(565, 92),
            Size = new Size(180, 125)
        };

        var btnAddRunning = new Button
        {
            Text = "➕ 正在运行的程序...",
            Location = new Point(0, 0),
            Size = new Size(175, 32),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnAddRunning.FlatAppearance.BorderSize = 0;
        btnAddRunning.Click += OnAddRunningProcessClicked;

        var btnBrowseExe = new Button
        {
            Text = "📁 浏览添加 .exe...",
            Location = new Point(0, 38),
            Size = new Size(175, 32),
            BackColor = Color.FromArgb(240, 243, 246),
            Cursor = Cursors.Hand
        };
        btnBrowseExe.Click += OnBrowseExeClicked;

        var btnRemoveProc = new Button
        {
            Text = "❌ 移除所选",
            Location = new Point(0, 76),
            Size = new Size(85, 30),
            BackColor = Color.FromArgb(240, 243, 246),
            Cursor = Cursors.Hand
        };
        btnRemoveProc.Click += OnRemoveProcessClicked;

        var btnResetProc = new Button
        {
            Text = "🔄 恢复推荐",
            Location = new Point(90, 76),
            Size = new Size(85, 30),
            BackColor = Color.FromArgb(240, 243, 246),
            Cursor = Cursors.Hand
        };
        btnResetProc.Click += OnResetDefaultProcessesClicked;

        var btnSelectAllProc = new Button
        {
            Text = "全选",
            Location = new Point(0, 112),
            Size = new Size(85, 28),
            BackColor = Color.FromArgb(240, 243, 246),
            Cursor = Cursors.Hand
        };
        btnSelectAllProc.Click += (s, e) => SetClbAllChecked(true);

        var btnUnselectAllProc = new Button
        {
            Text = "全不选",
            Location = new Point(90, 112),
            Size = new Size(85, 28),
            BackColor = Color.FromArgb(240, 243, 246),
            Cursor = Cursors.Hand
        };
        btnUnselectAllProc.Click += (s, e) => SetClbAllChecked(false);

        pnlProcButtons.Controls.AddRange([
            btnAddRunning, btnBrowseExe, btnRemoveProc, btnResetProc, btnSelectAllProc, btnUnselectAllProc
        ]);

        tabProcess.Controls.AddRange([
            _chkEnableProcessBypass, pnlSwitch, _lblProcessTip, _clbProcesses, pnlProcButtons
        ]);

        tabControl.TabPages.Add(tabNode);
        tabControl.TabPages.Add(tabMode);
        tabControl.TabPages.Add(tabProcess);

        // ================= 3. 底部日志视窗 =================
        var logPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 16),
            BackColor = Color.FromArgb(244, 245, 247)
        };

        var logHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 32 };
        var lblLogTitle = new Label { Text = "运行日志与事件监控:", Location = new Point(0, 5), AutoSize = true, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
        _chkEnableLogs = new CheckBox
        {
            Text = "开启实时日志显示",
            Location = new Point(160, 4),
            AutoSize = true,
            Checked = true,
            Font = new Font("Microsoft YaHei UI", 9F)
        };
        _chkEnableLogs.CheckedChanged += (s, e) =>
        {
            _config.EnableLogging = _chkEnableLogs.Checked;
            if (!_chkEnableLogs.Checked) _rtbLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] ℹ️ 实时日志输出已暂停显示。\n");
            else _rtbLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] ℹ️ 实时日志输出已恢复显示。\n");
        };

        var btnClearLog = new Button { Text = "清空日志", Dock = DockStyle.Right, Width = 80, Height = 28 };
        btnClearLog.Click += (s, e) =>
        {
            while (_logQueue.TryDequeue(out _)) { }
            _rtbLogs.Clear();
        };
        logHeaderPanel.Controls.AddRange([lblLogTitle, _chkEnableLogs, btnClearLog]);

        _rtbLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(25, 28, 34),
            ForeColor = Color.FromArgb(220, 225, 230),
            Font = new Font("Consolas", 9.5F)
        };

        logPanel.Controls.Add(_rtbLogs);
        logPanel.Controls.Add(logHeaderPanel);

        // ================= 关键：正确的 WinForms 控件停靠添加顺序 =================
        // WinForms 必须先 Add 边缘停靠控件 (Top/Bottom)，最后 Add Fill 控件！
        Controls.Add(logPanel);    // DockStyle.Fill (最后添加在底层填满剩余区域)
        Controls.Add(tabControl);  // DockStyle.Top (居中)
        Controls.Add(topCard);     // DockStyle.Top (最顶端)

        topCard.BringToFront();
    }

    private void LoadConfigToUi()
    {
        _isLoadingConfig = true;
        try
        {
            _txtHost.Text = _config.ServerHost;
            _numPort.Value = _config.ServerPort;
            _txtPath.Text = _config.Path;
            _chkUseTls.Checked = _config.UseTls;
            _txtSni.Text = _config.SniHost ?? "";
            _txtPassword.Text = _config.Password;
            _cboAlgo.SelectedItem = _config.Algorithm;
            if (_cboAlgo.SelectedIndex < 0 && _cboAlgo.Items.Count > 0) _cboAlgo.SelectedIndex = 0;
            _numConcurrency.Value = Math.Clamp(_config.ConcurrencyChannels, 1, 16);
            _numSocks5Port.Value = _config.LocalSocks5Port;
            _chkEnableSystemProxy.Checked = _config.EnableSystemProxy;
            _chkEnableBypass.Checked = _config.EnableBypass;
            _txtBypassRules.Text = _config.BypassRules;
            _chkEnableTun.Checked = _config.EnableTun;
            _chkEnableLogs.Checked = _config.EnableLogging;
            _chkEnableProcessBypass.Checked = _config.EnableProcessBypass;
            UpdateModeSwitchUi(_config.ProcessFilterMode);
            PopulateClbFromConfig(_config.BypassProcessList);
        }
        finally
        {
            _isLoadingConfig = false;
        }
    }

    private void UpdateModeSwitchUi(ProcessProxyMode mode)
    {
        _config.ProcessFilterMode = mode;
        if (_btnModeBlacklist == null || _btnModeWhitelist == null || _btnToggleMode == null || _lblProcessTip == null) return;

        if (mode == ProcessProxyMode.ProxySelected)
        {
            // 白名单模式
            _btnModeBlacklist.BackColor = Color.FromArgb(240, 243, 246);
            _btnModeBlacklist.ForeColor = Color.FromArgb(100, 110, 120);
            _btnModeBlacklist.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            _btnModeWhitelist.BackColor = Color.FromArgb(0, 120, 215);
            _btnModeWhitelist.ForeColor = Color.White;
            _btnModeWhitelist.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            _btnToggleMode.Text = "🔀 切为【黑名单模式】";
            _lblProcessTip.Text = "💡 提示：【🚀 白名单模式】生效中！仅勾选的程序网络走代理隧道，其余所有未勾选的程序一律直连本地网络。";
            _lblProcessTip.ForeColor = Color.FromArgb(0, 102, 204);
        }
        else
        {
            // 黑名单模式
            _btnModeBlacklist.BackColor = Color.FromArgb(16, 124, 65);
            _btnModeBlacklist.ForeColor = Color.White;
            _btnModeBlacklist.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            _btnModeWhitelist.BackColor = Color.FromArgb(240, 243, 246);
            _btnModeWhitelist.ForeColor = Color.FromArgb(100, 110, 120);
            _btnModeWhitelist.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

            _btnToggleMode.Text = "🔀 切为【白名单模式】";
            _lblProcessTip.Text = "💡 提示：【🛡️ 黑名单模式】生效中！名单中勾选的程序（如微信、QQ、RustDesk、网盘）直连本地网络，其余所有程序走代理。";
            _lblProcessTip.ForeColor = Color.FromArgb(16, 124, 65);
        }
    }

    private void SaveUiToConfig()
    {
        _config.ServerHost = _txtHost.Text.Trim();
        _config.ServerPort = (int)_numPort.Value;
        _config.Path = _txtPath.Text.Trim();
        _config.UseTls = _chkUseTls.Checked;
        _config.SniHost = string.IsNullOrWhiteSpace(_txtSni.Text) ? null : _txtSni.Text.Trim();
        _config.Password = _txtPassword.Text;
        _config.Algorithm = _cboAlgo.SelectedItem?.ToString() ?? "aes-256-gcm";
        _config.ConcurrencyChannels = (int)_numConcurrency.Value;
        _config.LocalSocks5Port = (int)_numSocks5Port.Value;
        _config.EnableSystemProxy = _chkEnableSystemProxy.Checked;
        _config.EnableBypass = _chkEnableBypass.Checked;
        _config.BypassRules = _txtBypassRules.Text.Trim();
        _config.EnableTun = _chkEnableTun.Checked;
        _config.EnableLogging = _chkEnableLogs.Checked;
        _config.EnableProcessBypass = _chkEnableProcessBypass.Checked;

        var procTokens = new List<string>();
        for (int i = 0; i < _clbProcesses.Items.Count; i++)
        {
            string item = _clbProcesses.Items[i].ToString()!;
            bool isChecked = _clbProcesses.GetItemChecked(i);
            procTokens.Add(isChecked ? item : "!" + item);
        }
        _config.BypassProcessList = string.Join(";", procTokens);

        ConfigManager.Save(_config);

        if (_engine != null && _engine.IsRunning)
        {
            _engine.UpdateProcessBypass(_config.BypassProcessList, _config.EnableProcessBypass, _config.ProcessFilterMode);
        }

        AppendLog("✅ 配置已成功保存！");
    }

    #region 程序分流事件处理

    private void SafeBeginInvoke(Action action)
    {
        if (_isLoadingConfig || !IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(action);
        }
        catch { }
    }

    /// <summary>
    /// 即时保存并应用程序直连分流设置，若当前代理正在运行则立刻动态生效，无需重启重连
    /// </summary>
    private void ApplyProcessBypassImmediately()
    {
        if (_isLoadingConfig) return;

        _config.EnableProcessBypass = _chkEnableProcessBypass.Checked;

        var procTokens = new List<string>();
        for (int i = 0; i < _clbProcesses.Items.Count; i++)
        {
            string item = _clbProcesses.Items[i].ToString()!;
            bool isChecked = _clbProcesses.GetItemChecked(i);
            procTokens.Add(isChecked ? item : "!" + item);
        }
        _config.BypassProcessList = string.Join(";", procTokens);

        ConfigManager.Save(_config);

        if (_engine != null && _engine.IsRunning)
        {
            _engine.UpdateProcessBypass(_config.BypassProcessList, _config.EnableProcessBypass, _config.ProcessFilterMode);
            int activeCount = _clbProcesses.CheckedItems.Count;
            string modeDesc = _config.ProcessFilterMode == ProcessProxyMode.ProxySelected ? "只为选中的应用进行代理" : "仅直连选中的应用";
            AppendLog($"⚡ [即时生效] 应用分流已动态更新 (当前模式: {modeDesc}，选中 {activeCount} 个应用)！");
        }
    }

    private void OnAddRunningProcessClicked(object? sender, EventArgs e)
    {
        using var picker = new RunningProcessPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            int addedCount = 0;
            foreach (var name in picker.SelectedProcessNames)
            {
                if (AddProcessToClb(name, isChecked: true))
                {
                    addedCount++;
                }
            }
            if (addedCount > 0)
            {
                ApplyProcessBypassImmediately();
                AppendLog($"✅ 已添加 {addedCount} 个正在运行的程序，已即时生效！");
            }
        }
    }

    private void OnBrowseExeClicked(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择需要直连不走代理的应用程序 (.exe)",
            Multiselect = true
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            int addedCount = 0;
            foreach (var file in ofd.FileNames)
            {
                string exeName = System.IO.Path.GetFileName(file);
                if (AddProcessToClb(exeName, isChecked: true))
                {
                    addedCount++;
                }
            }
            if (addedCount > 0)
            {
                ApplyProcessBypassImmediately();
                AppendLog($"✅ 已浏览添加 {addedCount} 个程序，已即时生效！");
            }
        }
    }

    private void OnRemoveProcessClicked(object? sender, EventArgs e)
    {
        var selectedIndices = _clbProcesses.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
        if (selectedIndices.Count == 0)
        {
            MessageBox.Show("请先在列表中点击选择要移除的程序行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (int idx in selectedIndices)
        {
            _clbProcesses.Items.RemoveAt(idx);
        }
        ApplyProcessBypassImmediately();
        AppendLog("✅ 已从直连名单中移除所选程序，已即时生效。");
    }

    private void OnResetDefaultProcessesClicked(object? sender, EventArgs e)
    {
        _clbProcesses.Items.Clear();
        string defaults = "rustdesk.exe;wechat.exe;qq.exe;dingtalk.exe;feishu.exe;baidunetdisk.exe;steam.exe";
        PopulateClbFromConfig(defaults);
        ApplyProcessBypassImmediately();
        AppendLog("🔄 已恢复推荐的常用直连程序名单，已即时生效！");
    }

    private void SetClbAllChecked(bool isChecked)
    {
        for (int i = 0; i < _clbProcesses.Items.Count; i++)
        {
            _clbProcesses.SetItemChecked(i, isChecked);
        }
        ApplyProcessBypassImmediately();
    }

    private bool AddProcessToClb(string rawName, bool isChecked)
    {
        string name = rawName.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        string fileName = System.IO.Path.GetFileName(name).ToLowerInvariant();
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".exe";
        }

        // 避免重复添加
        for (int i = 0; i < _clbProcesses.Items.Count; i++)
        {
            if (string.Equals(_clbProcesses.Items[i]?.ToString(), fileName, StringComparison.OrdinalIgnoreCase))
            {
                _clbProcesses.SetItemChecked(i, isChecked);
                return false;
            }
        }

        _clbProcesses.Items.Add(fileName, isChecked);
        return true;
    }

    private void PopulateClbFromConfig(string processList)
    {
        _clbProcesses.Items.Clear();
        if (string.IsNullOrWhiteSpace(processList)) return;

        var tokens = processList.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            bool isChecked = true;
            if (token.StartsWith('!'))
            {
                isChecked = false;
                token = token.Substring(1).Trim();
            }

            string fileName = System.IO.Path.GetFileName(token).ToLowerInvariant();
            if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".exe";
            }

            AddProcessToClb(fileName, isChecked);
        }
    }

    #endregion

    private async Task OnToggleClickedAsync()
    {
        SetButtonsEnabled(false);
        try
        {
            if (_engine != null && _engine.IsRunning)
            {
                await StopEngineAsync();
            }
            else
            {
                SaveUiToConfig();
                await StartEngineAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 操作失败: {ex.Message}");
            MessageBox.Show(ex.Message, "连接错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnConnectTop.Enabled = enabled;
        _btnConnectBottom.Enabled = enabled;
        _btnTestConnection.Enabled = enabled;
    }

    private async Task StartEngineAsync()
    {
        if (_config.EnableTun && !TunManager.IsAdministrator())
        {
            var res = MessageBox.Show(
                "检测到已勾选 TUN 虚拟网卡模式，但当前程序未以【管理员权限】运行。\n\n" +
                "是否以管理员身份重新启动本程序？\n" +
                "• 点击【是 (Yes)】：自动以管理员身份重新启动本程序 (触发 UAC)\n" +
                "• 点击【否 (No)】：取消勾选 TUN 模式，仅以常规系统代理模式启动\n" +
                "• 点击【取消 (Cancel)】：中止本次连接操作",
                "管理员权限提示",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);

            if (res == DialogResult.Yes)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    Process.Start(psi);
                    Application.Exit();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"提权重启失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else if (res == DialogResult.No)
            {
                _config.EnableTun = false;
                _chkEnableTun.Checked = false;
            }
            else
            {
                return;
            }
        }

        UpdateConnectButtonsState(isConnecting: true);

        _engine = new ProxyClientEngine(_config);
        _engine.StatusChanged += (state, err) => SafeBeginInvoke(() => UpdateConnectionStatus(state, err));
        _engine.LatencyUpdated += ms => SafeBeginInvoke(() => _lblLatency.Text = $"延迟: {ms} ms");
        _engine.TrafficUpdated += (up, down) =>
        {
            _currentTotalUpload = up;
            _currentTotalDownload = down;
        };
        _engine.LogMessage += msg => AppendLog(msg);

        _speedTimer.Start();
        await _engine.StartAsync();

        UpdateConnectButtonsState(isConnected: true);
    }

    private async Task StopEngineAsync()
    {
        _speedTimer.Stop();
        if (_engine != null)
        {
            await _engine.StopAsync();
            await _engine.DisposeAsync();
            _engine = null;
        }

        UpdateConnectButtonsState(isConnected: false);
        _lblStatusDot.ForeColor = Color.Gray;
        _lblStatusText.Text = "未连接代理 (点击右侧按钮连接)";
        _lblLatency.Text = "延迟: -- ms";
        _lblSpeedUp.Text = "↑ 0.0 KB/s (0.0 MB)";
        _lblSpeedDown.Text = "↓ 0.0 KB/s (0.0 MB)";
    }

    private void UpdateConnectButtonsState(bool isConnected = false, bool isConnecting = false)
    {
        if (isConnecting)
        {
            _btnConnectTop.Text = "⏳ 正在连接...";
            _btnConnectTop.BackColor = Color.FromArgb(255, 140, 0);
            _btnConnectBottom.Text = "⏳ 正在连接...";
            _btnConnectBottom.BackColor = Color.FromArgb(255, 140, 0);
        }
        else if (isConnected)
        {
            _btnConnectTop.Text = "⏹ 断开连接";
            _btnConnectTop.BackColor = Color.FromArgb(216, 59, 1); // 醒目红
            _btnConnectBottom.Text = "⏹ 断开连接";
            _btnConnectBottom.BackColor = Color.FromArgb(216, 59, 1);
        }
        else
        {
            _btnConnectTop.Text = "▶ 点击连接";
            _btnConnectTop.BackColor = Color.FromArgb(16, 124, 65); // 醒目绿
            _btnConnectBottom.Text = "▶ 点击连接";
            _btnConnectBottom.BackColor = Color.FromArgb(16, 124, 65);
        }
    }

    private async Task OnTestConnectionClickedAsync()
    {
        SaveUiToConfig();
        AppendLog($"正在测试连接到 {_config.ServerHost}:{_config.ServerPort} (路径: {_config.Path})...");
        _btnTestConnection.Enabled = false;

        var sw = Stopwatch.StartNew();
        try
        {
            using var ws = new ClientWebSocket();

            string scheme = _config.UseTls ? "wss" : "ws";
            string cleanPath = _config.Path.StartsWith('/') ? _config.Path : "/" + _config.Path;
            
            string targetDomain = !string.IsNullOrWhiteSpace(_config.SniHost) ? _config.SniHost.Trim() : _config.ServerHost.Trim();
            var uri = new Uri($"{scheme}://{targetDomain}:{_config.ServerPort}{cleanPath}?token={Uri.EscapeDataString(_config.Password)}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            if (!string.IsNullOrWhiteSpace(_config.SniHost) && !string.Equals(_config.SniHost.Trim(), _config.ServerHost.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (context, ct) =>
                    {
                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        await socket.ConnectAsync(_config.ServerHost.Trim(), _config.ServerPort, ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                };
                var invoker = new HttpMessageInvoker(handler);
                await ws.ConnectAsync(uri, invoker, cts.Token);
            }
            else
            {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                await ws.ConnectAsync(uri, cts.Token);
            }

            sw.Stop();

            AppendLog($"🎉 测试连接成功！往返握手耗时: {sw.ElapsedMilliseconds} ms (目标: {_config.ServerHost} -> SNI: {targetDomain})");
            MessageBox.Show($"连接成功！\n节点: {_config.ServerHost}:{_config.ServerPort}\nSNI: {targetDomain}\n握手延迟: {sw.ElapsedMilliseconds} ms", "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test Done", CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLog($"❌ 测试连接失败: {ex.Message}");

            string hint = "";
            if (ex.Message.Contains("'200'"))
            {
                hint = "\r\n\r\n💡【错误诊断 (返回 200 说明请求被普通 Web 页面拦截，未能成功升级为 WebSocket)】：\r\n" +
                       "1.【路径是否一致】：客户端填写的“WebSocket 路径”（如 /tunnel）必须与服务端启动时的路径完全一致，不能填根路径 / ；\r\n" +
                       "2.【Nginx / 反向代理配置】：若服务端前面挂了 Nginx/宝塔/Caddy，必须在反代配置中加上：\r\n" +
                       "     proxy_http_version 1.1;\r\n" +
                       "     proxy_set_header Upgrade $http_upgrade;\r\n" +
                       "     proxy_set_header Connection \"upgrade\";\r\n" +
                       "3.【Cloudflare WebSockets 开关】：登录 Cloudflare 控制台 -> 选中域名 -> 点击左侧 [Network (网络)] -> 确认 [WebSockets] 处于 开启 (On) 状态；\r\n" +
                       "4.【服务端是否在运行】：请确保 VPS 上的 Proxy.Server 服务端已启动。";

                AppendLog("--------------------------------------------------");
                AppendLog("💡 故障诊断建议已生成，请参考弹窗或日志提示核对配置。");
                AppendLog("--------------------------------------------------");
            }
            else if (ex.Message.Contains("'403'"))
            {
                hint = "\r\n\r\n💡【错误诊断 (返回 403 Forbidden)】：认证密码/Token 错误，请检查客户端密码是否与服务端配置一致！";
            }
            else if (ex.Message.Contains("'400'"))
            {
                hint = "\r\n\r\n💡【错误诊断 (返回 400 BadRequest)】：请求未被识别为合法 WebSocket，中间代理可能篡改了 Upgrade 请求头。";
            }

            MessageBox.Show($"测试连接失败！\r\n错误信息: {ex.Message}{hint}", "测试结果诊断", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _btnTestConnection.Enabled = true;
        }
    }

    private void UpdateConnectionStatus(TransportState state, string? error)
    {
        switch (state)
        {
            case TransportState.Connecting:
                _lblStatusDot.ForeColor = Color.Orange;
                _lblStatusText.Text = "正在连接到代理服务器...";
                break;
            case TransportState.Connected:
                _lblStatusDot.ForeColor = Color.FromArgb(16, 124, 65);
                int totalCh = _engine?.TotalChannelCount ?? 1;
                int actCh = _engine?.ActiveChannelCount ?? 1;
                _lblStatusText.Text = totalCh > 1
                    ? $"已连接 ({actCh}/{totalCh} 条多路并发通道运行中)"
                    : "已连接 (代理保护中)";
                break;
            case TransportState.Reconnecting:
                _lblStatusDot.ForeColor = Color.FromArgb(216, 59, 1);
                _lblStatusText.Text = "连接中断，正在自动重连...";
                break;
            case TransportState.Disconnected:
                _lblStatusDot.ForeColor = Color.Gray;
                _lblStatusText.Text = "未连接代理 (点击右侧按钮连接)";
                break;
        }

        if (!string.IsNullOrEmpty(error))
        {
            AppendLog($"[状态] {error}");
        }
    }

    private void OnSpeedTimerTick(object? sender, EventArgs e)
    {
        long curUp = _engine?.TotalUploadBytes ?? _currentTotalUpload;
        long curDown = _engine?.TotalDownloadBytes ?? _currentTotalDownload;

        long deltaUp = curUp - _lastUploadBytes;
        long deltaDown = curDown - _lastDownloadBytes;
        _lastUploadBytes = curUp;
        _lastDownloadBytes = curDown;

        double upKb = deltaUp / 1024.0;
        double downKb = deltaDown / 1024.0;
        double totalUpMb = curUp / (1024.0 * 1024.0);
        double totalDownMb = curDown / (1024.0 * 1024.0);

        _lblSpeedUp.Text = $"↑ {upKb:F1} KB/s ({totalUpMb:F1} MB)";
        _lblSpeedDown.Text = $"↓ {downKb:F1} KB/s ({totalDownMb:F1} MB)";
    }

    private void AppendLog(string message)
    {
        if (_chkEnableLogs != null && !_chkEnableLogs.Checked) return;
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void OnLogFlushTimerTick(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty || !IsHandleCreated || IsDisposed) return;

        var sb = new System.Text.StringBuilder();
        int count = 0;
        while (_logQueue.TryDequeue(out var line) && count < 100)
        {
            sb.AppendLine(line);
            count++;
        }

        if (sb.Length == 0) return;

        // 保持 RichTextBox 文本长度在合理区间 (约 1000 行)，严禁使用 _rtbLogs.Text.Length (该属性会导致全量字符复制并分配大对象引发 Full GC)
        int currentLength = _rtbLogs.TextLength;
        if (currentLength > 80_000)
        {
            _rtbLogs.Select(0, 20_000);
            _rtbLogs.SelectedText = string.Empty;
        }

        _rtbLogs.AppendText(sb.ToString());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _engine != null && _engine.IsRunning)
        {
            var res = MessageBox.Show("代理正在运行中，确定要断开并退出吗？", "退出提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _speedTimer.Dispose();
        _logFlushTimer.Stop();
        _logFlushTimer.Dispose();

        if (_engine != null)
        {
            _engine.StopAsync().GetAwaiter().GetResult();
        }

        base.OnFormClosing(e);
    }

    private async Task ExitApplicationAsync()
    {
        if (_engine != null)
        {
            await _engine.StopAsync();
        }
        Application.Exit();
    }
}
