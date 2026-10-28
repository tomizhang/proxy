using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Proxy.Client.WinForms;

/// <summary>
/// 无声防卡顿日志富文本框：
/// 1. 屏蔽只读状态下键盘敲击与剪贴板按键引起的 Win32 MessageBeep 提示音；
/// 2. 彻底解决在 ReadOnly 状态下修改或修剪文本导致系统发出连续警告叮叮声 (Windows Default Beep)；
/// 3. 使用 Win32 WM_SETREDRAW 冻结绘制，消除批量写入与裁剪历史日志时的视觉闪烁与跳动。
/// </summary>
internal sealed class SilentRichTextBox : RichTextBox
{
    private const int WM_SETREDRAW = 0x000B;
    private const int WM_CHAR = 0x0102;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_PASTE = 0x0302;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public SilentRichTextBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void WndProc(ref Message m)
    {
        if (ReadOnly)
        {
            // 屏蔽在只读框中粘贴触发的系统提示音
            if (m.Msg == WM_PASTE)
            {
                return;
            }

            // 拦截字符输入，只允许复制 (Ctrl+C, char 3) 和全选 (Ctrl+A, char 1)，杜绝只读下的 MessageBeep
            if (m.Msg == WM_CHAR)
            {
                int charCode = (int)m.WParam;
                if (charCode == 3 || charCode == 1) // Ctrl+C, Ctrl+A
                {
                    base.WndProc(ref m);
                }
                return;
            }

            // 屏蔽只读状态下 Backspace 与 Delete 触发的警报声
            if (m.Msg == WM_KEYDOWN)
            {
                int key = (int)m.WParam;
                if (key == 0x08 /* Backspace */ || key == 0x2E /* Delete */)
                {
                    return;
                }
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// 批量安全追加日志，并在超出最大长度时静默截断旧日志（杜绝触发 Windows 提示音）
    /// </summary>
    public void AppendBatch(string text, int maxTextLength = 80_000, int trimLength = 20_000)
    {
        if (string.IsNullOrEmpty(text) || !IsHandleCreated || IsDisposed) return;

        bool wasReadOnly = ReadOnly;
        IntPtr hWnd = Handle;

        bool userHasSelection = Focused && SelectionLength > 0;
        int origSelStart = SelectionStart;
        int origSelLen = SelectionLength;
        int trimmed = 0;

        // 冻结重绘，避免截断和追加文本过程中的窗口重绘跳动
        SendMessage(hWnd, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (wasReadOnly) ReadOnly = false;

            // 超出阈值时截断旧日志（在 ReadOnly = false 下操作，彻底避免 Win32 RichEdit 触发 MessageBeep 提示音）
            if (TextLength > maxTextLength)
            {
                trimmed = Math.Min(trimLength, TextLength);
                Select(0, trimmed);
                SelectedText = string.Empty;
            }

            // 追加最新日志文本
            Select(TextLength, 0);
            SelectedText = text;
        }
        catch
        {
            // 容错保护
        }
        finally
        {
            if (wasReadOnly) ReadOnly = true;

            // 恢复重绘并刷新
            SendMessage(hWnd, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            Invalidate();
        }

        // 若用户正在选中查看日志，则保持其选区；否则平滑滚动至最新日志
        if (userHasSelection)
        {
            int newStart = Math.Max(0, origSelStart - trimmed);
            Select(newStart, origSelLen);
        }
        else
        {
            SelectionStart = TextLength;
            ScrollToCaret();
        }
    }
}
