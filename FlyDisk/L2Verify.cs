// Copyright (C) 2026 youke1686 (https://github.com/youke1686)
//
// This file is part of FlyDisk.
//
// FlyDisk is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// FlyDisk is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with FlyDisk.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// 「校验 L2」对话框：先只读逐块比对（绝不写），看到结果后再由用户决定是否「修复」
    /// （把不一致的容器槽用源盘数据覆写）。
    ///
    /// 控件全部用代码构建（不配 designer/resx）——整个校验/修复功能只占这一个文件，`Form1` 只挂一个菜单入口。
    /// 设计定稿见 `后续待办.md` 第二节「L2 校验 / 修复：施工级定稿」。
    /// </summary>
    public sealed class L2VerifyForm : ThemedForm, ILocalizable
    {
        private readonly DiskConfig _config;
        private readonly Label _lblInfo = new();
        private readonly ProgressBar _bar = new();
        private readonly Label _lblCount = new();
        private readonly Button _btnPrimary = new();
        private readonly Button _btnClose = new();
        private readonly Action<Engine.L2VerifyProgress> _onProgress;

        private Engine.L2Verifier? _verifier;
        private CancellationTokenSource? _cts;
        private Engine.L2VerifyResult? _verify;
        private bool _busy;
        private bool _closeWhenDone;
        private bool _repairedCleanly;

        /// <summary>当前说明行对应的动作键（`l2v.action.verify` / `l2v.action.repair`），供切语言时重刷</summary>
        private string _actionKey = "l2v.action.verify";

        /// <summary>
        /// 本次会话结束时，L2 容器与源盘是否**已经一致**（= 这份账本可以安全采信）。
        /// 只有「不一致 = 0 且无读失败」或「不一致已全部修复成功」才算 true；
        /// 取消、中止、修复有失败、或压根没跑完，一律 false。
        /// 启动流程据此决定：干净 ⇒ 带着这份 L2 继续启动（并把容器头的身份戳写回索引里的值）；
        /// 否则 ⇒ 中止启动、不做补救动作（容器头的戳停在进窗口时轮换的新值上，下次启动仍会要求校验）。
        /// </summary>
        public bool LedgerIsConsistent =>
            _repairedCleanly ||
            (_verify is { Cancelled: false, Aborted: false, MismatchedBlocks: 0, ReadFailedBlocks: 0 });

        /// <summary>本次要校验的目标盘号（由调用方在打开本窗口前选定）</summary>
        private readonly int _diskNumber;

        /// <summary>目标盘信息（含盘符），说明行用；只探测一次，失败后不再重试</summary>
        private Engine.PhysicalDiskInfo? _diskInfo;
        private bool _diskInfoLoaded;

        /// <summary>控件登记表（见 <see cref="ILocalizable"/>）：静态文本键 → 控件</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        /// <summary>登记一条键（一条键可绑多个控件）</summary>
        private void Bind(string key, params Control[] controls)
        {
            if (!TextBindings.TryGetValue(key, out List<Control>? list))
            {
                list = new List<Control>();
                TextBindings[key] = list;
            }
            list.AddRange(controls);
        }

        /// <summary>
        /// 动态文本：窗体标题不是控件、说明行随动作（校验/修复）变、主按钮随运行状态变，都要重刷（见 <see cref="ILocalizable"/>）。
        /// 本窗口是模态的（主窗菜单在它打开期间点不到），实践中不会在开着时切语言；这里仍按契约实现以求自洽。
        /// </summary>
        public void ApplyDynamicText()
        {
            Text = Locale.T("l2v.title");
            _lblInfo.Text = BuildInfoText(_actionKey);
            UpdateButtons();
        }

        public L2VerifyForm(DiskConfig config, int diskNumber)
        {
            _config = config;
            _diskNumber = diskNumber;
            _onProgress = OnProgress;

            Text = Locale.T("l2v.title");
            ClientSize = new Size(470, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = _btnClose;

            _lblInfo.AutoSize = false;
            _lblInfo.Location = new Point(12, 10);
            _lblInfo.Size = new Size(446, 34);
            _lblInfo.Text = BuildInfoText(_actionKey);

            _bar.Location = new Point(12, 48);
            _bar.Size = new Size(446, 18);
            _bar.Maximum = 1000;
            _bar.Style = ProgressBarStyle.Marquee;

            _lblCount.AutoSize = false;
            _lblCount.Location = new Point(12, 74);
            _lblCount.Size = new Size(446, 48);
            _lblCount.Text = Locale.T("l2v.preparing");

            _btnPrimary.Location = new Point(236, 130);
            _btnPrimary.Size = new Size(110, 28);
            _btnPrimary.Text = Locale.T("common.cancel");
            _btnPrimary.UseVisualStyleBackColor = true;
            _btnPrimary.Click += BtnPrimary_Click;

            _btnClose.Location = new Point(348, 130);
            _btnClose.Size = new Size(110, 28);
            _btnClose.Text = Locale.T("common.close");
            _btnClose.UseVisualStyleBackColor = true;
            _btnClose.Visible = false;
            _btnClose.Click += (s, e) => Close();

            Controls.Add(_lblInfo);
            Controls.Add(_bar);
            Controls.Add(_lblCount);
            Controls.Add(_btnPrimary);
            Controls.Add(_btnClose);

            // 静态文本登记（关掉「关闭」不随状态变）；说明行与主按钮随状态走 ApplyDynamicText
            Bind("common.close", _btnClose);
        }

        /// <summary>说明行：把"这次在干什么、盘什么状态、会写哪里"一次讲清（不弹任何确认框）</summary>
        private string BuildInfoText(string actionKey)
        {
            string disk;
            if (_diskNumber < 0)
            {
                disk = Locale.T("l2v.diskNone");
            }
            else
            {
                disk = TryGetDiskInfo() is { } info
                    ? DiskText.Full(info)
                    : DiskText.Short(_diskNumber);
            }
            return Locale.T("l2v.info", Locale.T(actionKey), disk);
        }

        /// <summary>
        /// 目标盘信息（只探测一次）。**必须补盘符**：<see cref="Engine.PhysicalDiskHandle.Probe"/> 只读磁盘属性，
        /// 不扫卷；而盘名要带盘符，脱机之后卷就消失了，那时再补就来不及。
        /// </summary>
        private Engine.PhysicalDiskInfo? TryGetDiskInfo()
        {
            if (_diskInfoLoaded) return _diskInfo;
            _diskInfoLoaded = true;
            try
            {
                Engine.PhysicalDiskInfo info = Engine.PhysicalDiskHandle.Probe(_diskNumber);
                Engine.PhysicalDiskHandle.FillDriveLetters(new[] { info });
                _diskInfo = info;
            }
            catch
            {
                // 探测失败就只显示盘号：真正的错误会在打开会话时如实报出
                _diskInfo = null;
            }
            return _diskInfo;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartVerify();
        }

        #region 两个动作

        private void StartVerify()
        {
            _busy = true;
            _repairedCleanly = false;
            _actionKey = "l2v.action.verify";
            _lblInfo.Text = BuildInfoText(_actionKey);
            _lblCount.Text = Locale.T("l2v.loadingLedger");
            _bar.Style = ProgressBarStyle.Marquee;
            UpdateButtons();

            CancellationTokenSource cts = new();
            _cts = cts;
            Engine.L2Verifier verifier = _verifier ??= new Engine.L2Verifier(_config, _diskNumber);

            Task.Run(() =>
            {
                try
                {
                    verifier.Open();
                    Engine.L2VerifyResult result = verifier.Verify(_onProgress, cts.Token);
                    Post(() => OnVerified(result));
                }
                catch (Exception ex)
                {
                    Post(() => OnFailed(ex.Message));
                }
            });
        }

        private void StartRepair()
        {
            if (_verifier == null) return;

            _busy = true;
            _actionKey = "l2v.action.repair";
            _lblInfo.Text = BuildInfoText(_actionKey);
            _lblCount.Text = Locale.T("l2v.writingBack");
            _bar.Style = ProgressBarStyle.Marquee;
            UpdateButtons();

            CancellationTokenSource cts = new();
            _cts = cts;
            Engine.L2Verifier verifier = _verifier;

            Task.Run(() =>
            {
                try
                {
                    Engine.L2RepairResult result = verifier.Repair(_onProgress, cts.Token);
                    Post(() => OnRepaired(result));
                }
                catch (Exception ex)
                {
                    Post(() => OnFailed(ex.Message));
                }
            });
        }

        #endregion

        #region 结果落地

        private void OnVerified(Engine.L2VerifyResult result)
        {
            _busy = false;
            _verify = result;

            string text = DescribeVerify(result);
            if (result.MismatchedBlocks > 0 && !result.Cancelled && !result.Aborted)
            {
                text += Locale.T("l2v.detailHint");
            }
            _lblCount.Text = text;

            string summary = $"L2 校验：{text}";
            Engine.LogService.DebugFile(summary);

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void OnRepaired(Engine.L2RepairResult result)
        {
            _busy = false;
            _repairedCleanly = !result.Cancelled && result.WriteFailedBlocks == 0 && result.ReadFailedBlocks == 0;

            string text = _verify != null ? DescribeVerify(_verify) : Locale.T("l2v.repair");
            text += result.Cancelled
                ? Locale.T("l2v.repairCancelled", result.RepairedBlocks.ToString("N0"))
                : Locale.T("l2v.repaired", result.RepairedBlocks.ToString("N0"));
            if (result.ReadFailedBlocks > 0) text += Locale.T("l2v.readFailed", result.ReadFailedBlocks.ToString("N0"));
            if (result.WriteFailedBlocks > 0) text += Locale.T("l2v.writeFailed", result.WriteFailedBlocks.ToString("N0"));
            _lblCount.Text = text;

            string summary = $"L2 修复：已回写 {result.RepairedBlocks:N0} 块（读失败 {result.ReadFailedBlocks:N0} / " +
                             $"写入失败 {result.WriteFailedBlocks:N0}）· 用时 {FormatDuration(result.ElapsedMs)}";
            Engine.LogService.DebugFile(summary);

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void OnFailed(string message)
        {
            _busy = false;
            _verify = null;
            _repairedCleanly = false;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 0;
            _lblCount.Text = Locale.T("l2v.failed", message);

            Engine.LogService.DebugFile($"L2校验/修复失败：{message}");

            // 打开/扫描失败：把会话收干净（释放锁与句柄，盘保持脱机）
            _verifier?.Dispose();
            _verifier = null;

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void UpdateButtons()
        {
            if (_busy)
            {
                _btnPrimary.Text = Locale.T("common.cancel");
                _btnPrimary.Enabled = true;
                _btnClose.Visible = false;
                return;
            }

            bool canRepair = _verify is { MismatchedBlocks: > 0, Cancelled: false, Aborted: false } && !_repairedCleanly;
            _btnPrimary.Text = Locale.T("l2v.repair");
            _btnPrimary.Enabled = canRepair;
            _btnClose.Visible = true;
        }

        #endregion

        #region 进度与按钮

        /// <summary>进度回调来自后台线程：一律切回 UI 线程再碰控件</summary>
        private void OnProgress(Engine.L2VerifyProgress progress)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action<Engine.L2VerifyProgress>(RenderProgress), progress);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RenderProgress(Engine.L2VerifyProgress progress)
        {
            if (IsDisposed) return;
            if (progress.Total <= 0) return;

            if (_bar.Style != ProgressBarStyle.Continuous)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 1000;
            }
            _bar.Value = (int)Math.Clamp(progress.Processed * 1000 / progress.Total, 0, 1000);

            string phase = Locale.T(progress.Phase == Engine.L2VerifyPhase.Writeback
                ? "l2v.phase.writeback"
                : "l2v.phase.compare");
            string text = Locale.T("l2v.progress", phase,
                progress.Processed.ToString("N0"), progress.Total.ToString("N0"), progress.Mismatched.ToString("N0"));
            if (progress.Repaired > 0) text += Locale.T("l2v.repaired", progress.Repaired.ToString("N0"));
            _lblCount.Text = text;
        }

        private void BtnPrimary_Click(object? sender, EventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                _btnPrimary.Enabled = false;
                _lblCount.Text = Locale.T("l2v.cancelling");
                return;
            }
            StartRepair();
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        #endregion

        #region 文案

        private static string DescribeVerify(Engine.L2VerifyResult r)
        {
            string duration = FormatDuration(r.ElapsedMs);
            string speed = r.ScannedBlocks > 0
                ? FormatSpeed(r.ScannedBlocks * (long)ServiceConstants.BlockSize, r.ElapsedMs)
                : "—";

            if (r.Aborted)
                return Locale.T("l2v.result.aborted", r.AbortReason,
                    r.ScannedBlocks.ToString("N0"), r.PlannedBlocks.ToString("N0"), r.MismatchedBlocks.ToString("N0"));
            if (r.Cancelled)
                return Locale.T("l2v.result.cancelled",
                    r.ScannedBlocks.ToString("N0"), r.PlannedBlocks.ToString("N0"), r.MismatchedBlocks.ToString("N0"));
            if (r.PlannedBlocks == 0)
                return Locale.T("l2v.result.noBlocks",
                    r.OccupiedSlots.ToString("N0"), r.OutOfRangeBlocks.ToString("N0"), r.DuplicateBlocks.ToString("N0"));
            if (r.MismatchedBlocks == 0)
                return Locale.T("l2v.result.consistent", r.ScannedBlocks.ToString("N0"), duration, speed);

            double rate = (double)r.MismatchedBlocks / r.ScannedBlocks;
            string tail = r.ReadFailedBlocks > 0 ? Locale.T("l2v.readFailed", r.ReadFailedBlocks.ToString("N0")) : string.Empty;
            return Locale.T("l2v.result.mismatch",
                r.MismatchedBlocks.ToString("N0"), rate.ToString("P4"), r.ScannedBlocks.ToString("N0"), duration, speed, tail);
        }

        private static string FormatDuration(double milliseconds)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(milliseconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private static string FormatSpeed(long bytes, double milliseconds)
        {
            double mbPerSecond = bytes / 1048576.0 / Math.Max(0.001, milliseconds / 1000.0);
            return mbPerSecond >= 1024 ? $"{mbPerSecond / 1024:F2} GB/s" : $"{mbPerSecond:F1} MB/s";
        }

        #endregion

        #region 生命周期

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                // 后台还在跑：先取消，等它收尾（避免拆掉正在用的句柄），收尾后再关
                _closeWhenDone = true;
                _cts?.Cancel();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            // 校验干净（不一致 = 0 或已全部修复成功）⇒ 把容器头的身份戳**写回索引里的那个值**，
            // 让引擎下次启动能正常载入这份账本。没干净就**不写**：戳会停在进窗口时轮换的新值上，
            // 与索引对不上 ⇒ 下次启动仍会要求校验（这正是"进过校验窗口但没验完"要留下的痕迹）。
            if (LedgerIsConsistent)
            {
                try
                {
                    _verifier?.ConfirmLedger();
                }
                catch (Exception ex)
                {
                    Engine.LogService.DebugFile($"L2 校验：写回账本身份戳失败（下次启动会再次要求校验）：{ex.Message}");
                }
            }

            _verifier?.Dispose();   // 释放单实例锁与句柄（盘保持脱机）
            _verifier = null;

            base.OnFormClosed(e);
        }

        #endregion
    }
}
