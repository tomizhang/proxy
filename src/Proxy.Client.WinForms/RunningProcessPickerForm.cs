using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Proxy.Client.WinForms;

/// <summary>
/// 运行中程序选择弹窗，方便用户一键勾选需要直连的应用程序
/// </summary>
public sealed class RunningProcessPickerForm : Form
{
    private readonly ListView _lvProcesses;
    private readonly TextBox _txtFilter;
    private List<ProcessItem> _allProcesses = new();

    public List<string> SelectedProcessNames { get; } = new();

    private class ProcessItem
    {
        public string ProcessName { get; set; } = "";
        public string ExeFileName { get; set; } = "";
        public string WindowTitle { get; set; } = "";
        public int Pid { get; set; }
        public string FilePath { get; set; } = "";
        public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);
    }

    public RunningProcessPickerForm(string title = "选择正在运行的应用程序")
    {
        Text = title;
        Size = new Size(680, 520);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(550, 400);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(244, 245, 247);

        // 顶部搜索过滤栏
        var topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.White
        };

        var lblFilter = new Label
        {
            Text = "🔍 快速搜索:",
            AutoSize = true,
            Location = new Point(12, 14)
        };

        _txtFilter = new TextBox
        {
            Location = new Point(95, 11),
            Width = 360,
            PlaceholderText = "输入程序名、窗口标题过滤 (如 rustdesk, wechat)..."
        };
        _txtFilter.TextChanged += (s, e) => ApplyFilter();

        var btnRefresh = new Button
        {
            Text = "🔄 刷新",
            Location = new Point(465, 9),
            Width = 80,
            Height = 28,
            BackColor = Color.FromArgb(240, 240, 240)
        };
        btnRefresh.Click += (s, e) => LoadRunningProcesses();

        topPanel.Controls.AddRange([lblFilter, _txtFilter, btnRefresh]);

        // 中间进程列表
        _lvProcesses = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            CheckBoxes = true,
            GridLines = true,
            MultiSelect = true,
            Font = new Font("Segoe UI", 9F)
        };
        _lvProcesses.Columns.Add("程序名称 (.exe)", 160);
        _lvProcesses.Columns.Add("窗口标题 / 程序描述", 240);
        _lvProcesses.Columns.Add("PID", 65);
        _lvProcesses.Columns.Add("程序完整路径", 240);

        var listContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 12, 8)
        };
        listContainer.Controls.Add(_lvProcesses);

        // 底部操作栏
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10),
            BackColor = Color.White
        };

        var btnSelectAll = new Button { Text = "全选", Location = new Point(12, 11), Width = 70, Height = 30 };
        btnSelectAll.Click += (s, e) => SetAllChecked(true);

        var btnUnselectAll = new Button { Text = "全不选", Location = new Point(88, 11), Width = 70, Height = 30 };
        btnUnselectAll.Click += (s, e) => SetAllChecked(false);

        var btnOk = new Button
        {
            Text = "✅ 添加勾选程序",
            Location = new Point(440, 10),
            Width = 120,
            Height = 32,
            BackColor = Color.FromArgb(16, 124, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Cursor = Cursors.Hand
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += OnConfirmSelection;

        var btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(570, 10),
            Width = 80,
            Height = 32,
            DialogResult = DialogResult.Cancel
        };

        bottomPanel.Controls.AddRange([btnSelectAll, btnUnselectAll, btnOk, btnCancel]);

        Controls.Add(listContainer);
        Controls.Add(bottomPanel);
        Controls.Add(topPanel);

        LoadRunningProcesses();
    }

    private void LoadRunningProcesses()
    {
        _allProcesses.Clear();
        var currentPid = Environment.ProcessId;
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var procs = Process.GetProcesses();
        var items = new List<ProcessItem>();

        foreach (var p in procs)
        {
            try
            {
                if (p.Id == 0 || p.Id == 4 || p.Id == currentPid) continue;

                string procName = p.ProcessName;
                string exeName = procName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? procName : procName + ".exe";

                string windowTitle = p.MainWindowTitle ?? "";
                string filePath = "";
                try
                {
                    filePath = p.MainModule?.FileName ?? "";
                }
                catch { }

                // 排除常见无害 Windows 后台微型服务 (可按需显示)
                if (string.IsNullOrEmpty(windowTitle) &&
                    (procName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                     procName.Equals("conhost", StringComparison.OrdinalIgnoreCase) ||
                     procName.Equals("RuntimeBroker", StringComparison.OrdinalIgnoreCase) ||
                     procName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                items.Add(new ProcessItem
                {
                    ProcessName = procName,
                    ExeFileName = exeName,
                    WindowTitle = windowTitle,
                    Pid = p.Id,
                    FilePath = filePath
                });
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }

        // 排序规则：有窗口标题的排在最前（用户常用前台应用），同类按名称排序
        _allProcesses = items
            .OrderByDescending(x => x.HasWindow)
            .ThenBy(x => x.ExeFileName)
            .ToList();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = _txtFilter.Text.Trim();
        _lvProcesses.BeginUpdate();
        _lvProcesses.Items.Clear();

        foreach (var item in _allProcesses)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                bool matchName = item.ExeFileName.Contains(filter, StringComparison.OrdinalIgnoreCase);
                bool matchTitle = item.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase);
                bool matchPath = item.FilePath.Contains(filter, StringComparison.OrdinalIgnoreCase);

                if (!matchName && !matchTitle && !matchPath) continue;
            }

            var lvi = new ListViewItem(item.ExeFileName)
            {
                Tag = item.ExeFileName
            };
            lvi.SubItems.Add(string.IsNullOrWhiteSpace(item.WindowTitle) ? "-" : item.WindowTitle);
            lvi.SubItems.Add(item.Pid.ToString());
            lvi.SubItems.Add(string.IsNullOrWhiteSpace(item.FilePath) ? "-" : item.FilePath);

            if (item.HasWindow)
            {
                lvi.ForeColor = Color.FromArgb(10, 60, 140);
                lvi.Font = new Font(_lvProcesses.Font, FontStyle.Bold);
            }

            _lvProcesses.Items.Add(lvi);
        }

        _lvProcesses.EndUpdate();
    }

    private void SetAllChecked(bool isChecked)
    {
        foreach (ListViewItem item in _lvProcesses.Items)
        {
            item.Checked = isChecked;
        }
    }

    private void OnConfirmSelection(object? sender, EventArgs e)
    {
        SelectedProcessNames.Clear();
        var selectedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ListViewItem item in _lvProcesses.CheckedItems)
        {
            if (item.Tag is string exeName && !string.IsNullOrWhiteSpace(exeName))
            {
                if (selectedSet.Add(exeName))
                {
                    SelectedProcessNames.Add(exeName);
                }
            }
        }
    }
}
