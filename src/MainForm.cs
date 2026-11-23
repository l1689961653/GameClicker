// MainForm.cs —— 主界面（全部 UI 与交互逻辑）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GameClicker
{
    internal sealed class MainForm : Form
    {
        // ---- 核心组件 ----
        private readonly ClickEngine _engine = new ClickEngine();
        private readonly InputHook _hook = new InputHook();
        private AppConfig _cfg;

        // ---- 托盘 ----
        private NotifyIcon _tray;
        private ToolStripMenuItem _trayToggleItem;
        private ToolStripMenuItem _trayStatusItem;
        private ToolStripMenuItem _trayPointItem;
        private ToolStripMenuItem _trayBatchItem;
        private ToolStripMenuItem _trayBatchDoneItem;
        private bool _reallyExit;

        // ---- 控件 ----
        private RadioButton _rbKeyboard;
        private RadioButton _rbMouse;
        private TextBox _txtKey;
        private Button _btnCapture;
        private ComboBox _cboTriggerMouse;
        private ComboBox _cboMode;
        private ComboBox _cboEmergency;
        private CheckBox _chkSwallow;

        private NumericUpDown _numInterval;
        private TrackBar _sldInterval;
        private Label _lblIntervalHint;
        private ComboBox _cboButton;
        private NumericUpDown _numHold;
        private RadioButton _rbFollow;
        private RadioButton _rbLock;
        private CheckBox _chkTrackPixels;
        private Button _btnRelock;

        // ---- 坐标点（可多个） ----
        // ---- 游戏配置（Profile） ----
        private ComboBox _cboProfile;
        private TextBox _txtGameProc;
        private string _activeProfile = "";              // 当前生效的 Profile 名（"" = 根配置）
        private string _lastFgMatched = "@none";         // 上次匹配到的进程（去抖，避免重复切换）

        private ListView _lstPoints;
        private Button _btnBatchRecord;
        private Button _btnPointDel;
        private Button _btnPointKey;
        private Button _btnPointClearKey;
        private Button _btnPointUp;
        private Button _btnPointDown;
        private NumericUpDown _numSeqGap;
        /// <summary>选中序列内部的点列表（序号/备注/坐标），对应批量录点时的屏幕标记。</summary>
        private ListView _lstSeqPoints;
        private TextBox _txtPointName;
        private ComboBox _cboPointMode;
        private CheckBox _chkPointSwallow;
        private CheckBox _chkPointReturn;
        private Label _lblPointHint;
        /// <summary>正在编辑/改键的坐标点在配置中的索引；-1 表示没有选中。</summary>
        private int _selPoint = -1;
        /// <summary>抑制控件事件导致的递归（刷新列表时不要反过来写配置）。</summary>
        private bool _pointEditing;

        /// <summary>本进程 PID：前台检测时要排除自己的窗口。</summary>
        private readonly uint _selfPid = (uint)Process.GetCurrentProcess().Id;

        /// <summary>屏幕顶部的启停状态条（已启动常驻 / 已关闭短暂提示）。</summary>
        private StatusBanner _statusBanner;

        // ---- 批量录点 ----
        /// <summary>是否正在批量录点（窗口已让开，点击正在被收集）。</summary>
        private bool _batchRecording;
        /// <summary>本次批量录点攒下的坐标，确认后才写入配置。</summary>
        private List<SeqPoint> _batchRecorded;
        /// <summary>录点期间专门用来提示"点我回来确认"的托盘图标。</summary>
        private NotifyIcon _batchTip;
        /// <summary>录点结束后的热键设置对话框（用户点确定/跳过后关闭）。</summary>
        private HotkeyPromptForm _hotkeyPrompt;
        /// <summary>录点时的屏幕迷你导航条（暂停/继续/完成）。</summary>
        private BatchNavBar _batchNav;
        /// <summary>录点是否处于暂停状态（暂停=不取新点，已取的保留）。</summary>
        private bool _batchPaused;


        private CheckBox _chkAutoStart;
        private CheckBox _chkTrayOnClose;
        private CheckBox _chkStartMin;
        private CheckBox _chkBalloon;
        private CheckBox _chkRunAsAdmin;
        private Button _btnElevateNow;
        private Label _lblPermState;

        private Panel _contentPanel;
        private Panel _statusPanel;
        private Panel _bottomBar;
        private Label _lblStatus;
        private Label _lblCount;
        private System.Windows.Forms.Timer _uiTimer;

        private Font _font;
        private Font _fontBold;

        private bool _capturing;
        private bool _suppressEvents;
        private bool _armedHint = true;

        // 逻辑布局尺寸（96 DPI 下的像素；高 DPI 时整体缩放）
        private const int FormW = 560;   // 固定窗口宽度（用户确认的尺寸）
        private const int ContentH = 510;   // 固定内容区高度（用户确认的尺寸）
        private const int StatusH = 60;
        private const int BottomH = 48;

        public MainForm()
        {
            // 无「默认/根配置」概念：启动时确保至少有一个 Profile，并加载它
            string startProfile = ProfileManager.EnsureAny();
            ProfileManager.Item startIt = ProfileManager.Find(startProfile);
            _activeProfile = startProfile;
            AppConfig.ActiveConfigDir = startIt != null ? startIt.Dir : AppPaths.DataDir;
            _cfg = AppConfig.LoadFromDir(AppConfig.ActiveConfigDir);
            _cfg.ProfileName = _activeProfile;

            BuildUi();
            ApplyDpiScale();

            RefreshProfileList();
            SyncProfileEditors();

            // 还原用户上次的窗口状态（最大化标志；尺寸已在 ApplyDpiScale 里应用）
            if (_cfg.WindowMaximized && !Program.SelfTestMode)
                WindowState = FormWindowState.Maximized;

            LoadConfigIntoUi();
            WireEvents();
            _hook.Install();
            ApplyConfig();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 400;
            _uiTimer.Tick += delegate
            {
                RefreshLiveStatus();
                CheckForegroundProfile();   // 检测游戏在前台时自动切配置
            };
            _uiTimer.Start();

            Native.TimeBeginPeriod(1);           // 提升系统计时精度，连点间隔更稳

            if (!_hook.Installed && _hook.InstallError != null)
            {
                Shown += delegate
                {
                    MessageBox.Show(this,
                        "全局按键监听安装失败，触发键可能无法生效。" + Environment.NewLine + Environment.NewLine +
                        _hook.InstallError + Environment.NewLine + Environment.NewLine +
                        "请点击下方「以管理员身份重启」再试一次。",
                        "游戏连点器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                };
            }
        }

        // =========================================================
        //  界面构建
        // =========================================================

        private void BuildUi()
        {
            _font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
            _fontBold = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            Text = "游戏连点器";
            Font = _font;
            AutoScaleMode = AutoScaleMode.None;   // 由 ApplyDpiScale 统一处理缩放
            // 允许拖拽调节主界面大小（尺寸保存进 config，下次启动还原）
            FormBorderStyle = FormBorderStyle.FixedSingle;   // 窗口尺寸固定（用户要求）
            MaximizeBox = false;
            // MinimumSize 已随固定尺寸取消
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 251);
            Icon = TrayIconFactory.CreateIcon(32);

            // ---- 底部按钮条（固定，不随内容滚动） ----
            _bottomBar = new Panel();
            _bottomBar.Dock = DockStyle.Bottom;
            _bottomBar.Height = BottomH;
            _bottomBar.BackColor = Color.FromArgb(245, 247, 251);

            Button btnSave = MakeButton("保存设置", 12, 8, 110, 32);
            btnSave.Click += delegate { SaveAndNotify(); };
            _bottomBar.Controls.Add(btnSave);

            Button btnHide = MakeButton("隐藏到托盘", 130, 8, 110, 32);
            btnHide.Click += delegate { HideToTray(); };
            _bottomBar.Controls.Add(btnHide);

            Button btnAdmin = MakeButton("以管理员身份重启", 248, 8, 150, 32);
            btnAdmin.Click += delegate { RestartAsAdmin(); };
            _bottomBar.Controls.Add(btnAdmin);

            Button btnLog = MakeButton("查看日志", 406, 8, 84, 32);
            btnLog.Click += delegate { OpenLogFile(); };
            _bottomBar.Controls.Add(btnLog);

            // ---- 状态条（固定） ----
            _statusPanel = new Panel();
            _statusPanel.Dock = DockStyle.Bottom;
            _statusPanel.Height = StatusH;
            _statusPanel.BackColor = Color.FromArgb(232, 236, 244);

            _lblStatus = new Label();
            _lblStatus.Text = "待机中";
            _lblStatus.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            _lblStatus.ForeColor = Color.FromArgb(120, 128, 140);
            _lblStatus.SetBounds(18, 8, 320, 28);
            _statusPanel.Controls.Add(_lblStatus);

            _lblCount = MakeLabel("", 20, 36, 400);
            _lblCount.ForeColor = Color.FromArgb(110, 118, 132);
            _statusPanel.Controls.Add(_lblCount);


            // ---- 内容区（主页面本身不滚动：滚动由各标签页自己负责） ----
            _contentPanel = new Panel();
            _contentPanel.Dock = DockStyle.Fill;
            _contentPanel.AutoScroll = false;
            _contentPanel.BackColor = Color.FromArgb(245, 247, 251);

            BuildContent(_contentPanel);

            // 停靠顺序：后添加的优先占位（Bottom 先占，Fill 取剩余）
            Controls.Add(_contentPanel);
            Controls.Add(_statusPanel);
            Controls.Add(_bottomBar);

            BuildTray();

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && _cfg.MinimizeToTrayOnClose) HideToTray();
                DebouncedSaveWindowSize();
            };
            Shown += delegate
{
                // DPI 缩放会在构造后改变窗口尺寸，CenterScreen 只在创建时生效——
                // 这里按最终尺寸再居中一次，保证打开时窗口在屏幕正中间
                if (!Program.SelfTestMode)
                {
                    Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                    Location = new Point(
                        wa.Left + (wa.Width - Width) / 2,
                        wa.Top + (wa.Height - Height) / 2);
                }
                // 自检模式必须保持窗口可见，否则 PrintWindow 抓到的是空白
                if (!Program.SelfTestMode && (_cfg.StartMinimized || Program.StartHidden))
                {
                    BeginInvoke(new Action(HideToTray));
                }
            };
        }

        /// <summary>拖拽/最大化后延迟落盘的定时器（拖动过程会触发几十次 Resize，不能每次都写盘）。</summary>
        private System.Windows.Forms.Timer _sizeSaveTimer;

        /// <summary>
        /// 窗口尺寸变化后延迟 800ms 再保存。
        /// 这样即使用户改完没点"保存设置"就被强杀，拖出来的大小也不会丢；
        /// 又不会在拖动过程中狂写配置文件。
        /// </summary>
        private void DebouncedSaveWindowSize()
        {
            if (Program.SelfTestMode) return;   // 自检会频繁改窗口大小，绝不写盘

            if (_sizeSaveTimer == null)
            {
                _sizeSaveTimer = new System.Windows.Forms.Timer { Interval = 800 };
                _sizeSaveTimer.Tick += delegate
                {
                    _sizeSaveTimer.Stop();
                    try
                    {
                        SaveWindowSize();
                        _cfg.Save();
                    }
                    catch { }
                };
            }
            _sizeSaveTimer.Stop();
            _sizeSaveTimer.Start();
        }

        /// <summary>当前选中的标签页（触发键 0 / 坐标点 1 / 程序行为 2）。</summary>
        private TabControl _tabs;

        private void BuildContent(Panel host)
        {
            // ---- 三个标签页：设置按用途分开，不用来回滚动找 ----
            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Font = _fontBold;
            _tabs.ItemSize = new Size(96, 30);

            TabPage tabTrigger = new TabPage("触发键");
            TabPage tabPoints  = new TabPage("坐标点");
            TabPage tabApp     = new TabPage("程序行为");
            foreach (TabPage tp in new TabPage[] { tabTrigger, tabPoints, tabApp })
            {
                tp.Font = _font;
                tp.BackColor = Color.FromArgb(245, 247, 251);
                tp.Padding = new Padding(12, 8, 12, 8);
                tp.AutoScroll = true;   // 单页内容超高时自己出滚动条
                _tabs.TabPages.Add(tp);
            }
            host.Controls.Add(_tabs);

            int y = 8;

            // ================= 触发键 =================
            GroupBox gT = MakeGroup("触发键（在游戏里按这个键开始 / 停止连点）", y, 124);
            tabTrigger.Controls.Add(gT);

            _rbKeyboard = MakeRadio("键盘按键", 16, 22, 88);
            _rbKeyboard.CheckedChanged += delegate { OnTriggerKindChanged(); };
            gT.Controls.Add(_rbKeyboard);

            _rbMouse = MakeRadio("鼠标按键", 118, 22, 88);
            gT.Controls.Add(_rbMouse);

            _cboTriggerMouse = new ComboBox();
            _cboTriggerMouse.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboTriggerMouse.SetBounds(212, 22, 136, 24);
            _cboTriggerMouse.Items.AddRange(new object[] { "鼠标左键", "鼠标右键", "鼠标中键", "鼠标侧键 X1", "鼠标侧键 X2" });
            _cboTriggerMouse.SelectedIndexChanged += delegate { ApplyLiveSettings(); };
            gT.Controls.Add(_cboTriggerMouse);

            Label lblEmergency = MakeLabel("紧急停止", 368, 24, 64);
            gT.Controls.Add(lblEmergency);

            _cboEmergency = new ComboBox();
            _cboEmergency.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboEmergency.SetBounds(434, 22, 82, 24);
            _cboEmergency.Items.AddRange(new object[] { "禁用", "F12", "F11", "F10", "Pause", "End", "Delete", "Home" });
            _cboEmergency.SelectedIndexChanged += delegate { ApplyLiveSettings(); };
            gT.Controls.Add(_cboEmergency);

            _txtKey = new TextBox();
            _txtKey.SetBounds(16, 50, 130, 24);
            _txtKey.ReadOnly = true;
            _txtKey.TabStop = false;
            _txtKey.BackColor = Color.White;
            _txtKey.Font = _fontBold;
            _txtKey.TextAlign = HorizontalAlignment.Center;
            gT.Controls.Add(_txtKey);

            _btnCapture = MakeButton("按下以设置按键", 152, 49, 150, 26);
            _btnCapture.Click += delegate { StartTriggerCapture(); };
            gT.Controls.Add(_btnCapture);

            Label lblMode = MakeLabel("触发方式", 302, 53, 62);
            gT.Controls.Add(lblMode);

            _cboMode = new ComboBox();
            _cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboMode.SetBounds(368, 50, 148, 24);
            _cboMode.Items.AddRange(new object[] { "切换：按一下开/关", "按住：按住才连点" });
            _cboMode.SelectedIndexChanged += delegate { ApplyLiveSettings(); };
            gT.Controls.Add(_cboMode);

            _chkSwallow = MakeCheck("触发时吞掉该键，游戏不会同时收到这个键（避免误触发游戏自身功能）", 16, 78, 500);
            _chkSwallow.CheckedChanged += delegate { ApplyLiveSettings(); };
            gT.Controls.Add(_chkSwallow);

            Label hint1 = MakeLabel("• 连点进行中，再按一次触发键 = 立即停止；紧急停止键随时可用", 16, 100, 500);
            hint1.ForeColor = Color.FromArgb(120, 128, 140);
            gT.Controls.Add(hint1);
            y += 132;

            // ================= 点击设置 =================
            GroupBox gC = MakeGroup("点击设置", y, 192);
            tabTrigger.Controls.Add(gC);

            Label lblInterval = MakeLabel("点击间隔", 16, 24, 60);
            gC.Controls.Add(lblInterval);

            _numInterval = new NumericUpDown();
            _numInterval.SetBounds(80, 21, 90, 24);
            _numInterval.Minimum = 1;
            _numInterval.Maximum = 60000;
            _numInterval.Increment = 5;
            _numInterval.ValueChanged += delegate { SyncSliderFromNumber(); ApplyLiveSettings(); };
            gC.Controls.Add(_numInterval);

            _lblIntervalHint = MakeLabel("", 180, 24, 330);
            _lblIntervalHint.ForeColor = Color.FromArgb(48, 110, 200);
            gC.Controls.Add(_lblIntervalHint);

            _sldInterval = new TrackBar();
            _sldInterval.SetBounds(74, 46, 440, 40);
            _sldInterval.Minimum = 5;
            _sldInterval.Maximum = 500;
            _sldInterval.TickFrequency = 25;
            _sldInterval.SmallChange = 1;
            _sldInterval.LargeChange = 25;
            _sldInterval.ValueChanged += delegate { SyncNumberFromSlider(); ApplyLiveSettings(); };
            gC.Controls.Add(_sldInterval);

            string[] presetText = new string[] { "10ms", "20ms", "50ms", "100ms", "200ms" };
            int[] presetVal = new int[] { 10, 20, 50, 100, 200 };
            for (int i = 0; i < presetText.Length; i++)
            {
                int val = presetVal[i];
                Button b = MakeButton(presetText[i], 80 + i * 62, 90, 56, 26);
                b.Click += delegate { _numInterval.Value = val; };
                gC.Controls.Add(b);
            }

            Label lblClick = MakeLabel("点击", 16, 122, 46);
            gC.Controls.Add(lblClick);

            _cboButton = new ComboBox();
            _cboButton.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboButton.SetBounds(64, 119, 90, 24);
            _cboButton.Items.AddRange(new object[] { "左键", "右键", "中键" });
            _cboButton.SelectedIndexChanged += delegate { ApplyLiveSettings(); };
            gC.Controls.Add(_cboButton);

            Label lblHold = MakeLabel("按下时长", 158, 122, 60);
            gC.Controls.Add(lblHold);

            _numHold = new NumericUpDown();
            _numHold.SetBounds(222, 119, 74, 24);
            _numHold.Minimum = 1;
            _numHold.Maximum = 1000;
            _numHold.Increment = 5;
            _numHold.ValueChanged += delegate { ApplyLiveSettings(); };
            gC.Controls.Add(_numHold);

            Label lblHoldMs = MakeLabel("毫秒（太短游戏可能不识别）", 302, 122, 210);
            lblHoldMs.ForeColor = Color.FromArgb(120, 128, 140);
            gC.Controls.Add(lblHoldMs);

            _rbFollow = MakeRadio("跟随鼠标当前位置", 16, 144, 150);
            _rbFollow.CheckedChanged += delegate { OnPositionModeChanged(); };
            gC.Controls.Add(_rbFollow);

            _rbLock = MakeRadio("锁定启动时的位置", 172, 144, 150);
            gC.Controls.Add(_rbLock);

            _btnRelock = MakeButton("把锁定点设到鼠标处", 328, 142, 186, 26);
            _btnRelock.Click += delegate
            {
                _engine.RelockHere();
                ShowBalloon("锁定点已更新", "锁定点已改到当前鼠标位置。");
            };
            gC.Controls.Add(_btnRelock);

            _chkTrackPixels = MakeCheck("锁定模式下，自动把被移开的指针拉回锁定点", 16, 168, 380);
            _chkTrackPixels.CheckedChanged += delegate { ApplyLiveSettings(); };
            gC.Controls.Add(_chkTrackPixels);
            y += 200;

            // ================= 坐标点（可多个，各自热键 / 开关 / 备注） =================
            // ---- 顶部：游戏配置选择（每套配置 = 一组序列 + 自己绑定的游戏） ----
            GroupBox gProf = MakeGroup("游戏配置（每个游戏一套；检测到游戏在前台时自动切换）", 8, 88);
            tabPoints.Controls.Add(gProf);

            Label lblProf = MakeLabel("当前配置", 16, 26, 64);
            gProf.Controls.Add(lblProf);

            _cboProfile = new ComboBox();
            _cboProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboProfile.SetBounds(84, 22, 190, 24);
            _cboProfile.SelectedIndexChanged += delegate { OnProfileSwitched(); };
            gProf.Controls.Add(_cboProfile);

            Button btnProfNew = MakeButton("新建…", 282, 21, 64, 26);
            btnProfNew.Click += delegate { CreateProfile(); };
            gProf.Controls.Add(btnProfNew);

            Button btnProfDel = MakeButton("删除", 352, 21, 52, 26);
            btnProfDel.Click += delegate { DeleteProfile(); };
            gProf.Controls.Add(btnProfDel);

            Label lblGame = MakeLabel("绑定游戏进程", 16, 56, 88);
            gProf.Controls.Add(lblGame);

            _txtGameProc = new TextBox();
            _txtGameProc.SetBounds(106, 53, 168, 24);
            _txtGameProc.TextChanged += delegate { OnGameProcEdited(); };
            gProf.Controls.Add(_txtGameProc);

            Button btnBrowseGame = MakeButton("浏览…", 282, 52, 64, 24);
            btnBrowseGame.Click += delegate { BrowseGameExe(); };
            gProf.Controls.Add(btnBrowseGame);

            Label lblGameHint = MakeLabel("游戏在前台时自动切换", 352, 56, 172);
            lblGameHint.ForeColor = Color.FromArgb(130, 138, 150);
            gProf.Controls.Add(lblGameHint);

            Button btnProfRename = MakeButton("改名", 410, 21, 52, 26);
            btnProfRename.Click += delegate { RenameProfile(); };
            gProf.Controls.Add(btnProfRename);

            GroupBox gPt = MakeGroup("坐标点序列（一串坐标共用一个热键，按录制顺序依次点过去）", 104, 500);
            tabPoints.Controls.Add(gPt);

            // 列表：勾选框 = 是否启用；列 = 备注名 / 坐标 / 热键 / 点击方式
            _lstPoints = new ListView();
            _lstPoints.SetBounds(16, 24, 496, 140);
            _lstPoints.View = View.Details;
            _lstPoints.CheckBoxes = true;
            _lstPoints.FullRowSelect = true;
            _lstPoints.MultiSelect = true;        // Ctrl/Shift 多选，配合"批量删除/启停"
            _lstPoints.HideSelection = false;
            _lstPoints.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            // 「序号」列：对应批量录点时屏幕上圆圈标记里的数字，
            // 采集完回到界面就能对上"我先点的是哪个"
            _lstPoints.Columns.Add("序号", 44);
            _lstPoints.Columns.Add("备注名", 132);
            _lstPoints.Columns.Add("坐标", 110);
            _lstPoints.Columns.Add("热键", 70);
            _lstPoints.Columns.Add("点击方式", 148);
            _lstPoints.SelectedIndexChanged += delegate { OnPointSelectionChanged(); };
            _lstPoints.ItemChecked += OnPointCheckedHandler;   // 命名处理器（供重建期摘除）
            gPt.Controls.Add(_lstPoints);

            // ---- 第一行：批量录点 / 改键 / 清除热键 / 删除 ----
            // （「点过去」「记录当前鼠标位置」已按需求移除：
            //   录点走批量录点，试位置双击序列行即可）
            _btnBatchRecord = MakeButton("记录坐标…", 16, 170, 100, 26);
            _btnBatchRecord.Click += delegate { StartBatchRecord(); };
            gPt.Controls.Add(_btnBatchRecord);

            _btnPointKey = MakeButton("改键", 122, 170, 64, 26);
            _btnPointKey.Click += delegate { BeginPointKeyCapture(); };
            gPt.Controls.Add(_btnPointKey);

            _btnPointClearKey = MakeButton("清除热键", 192, 170, 82, 26);
            _btnPointClearKey.Click += delegate { ClearSelectedPointHotkey(); };
            gPt.Controls.Add(_btnPointClearKey);

            _btnPointDel = MakeButton("删除所选", 280, 170, 82, 26);
            _btnPointDel.Click += delegate { DeleteSelectedPoint(); };
            gPt.Controls.Add(_btnPointDel);

            // ---- 第二行：顺序 + 序列间隔 ----
            _btnPointUp = MakeButton("上移", 16, 200, 52, 26);
            _btnPointUp.Click += delegate { MoveSelectedPoint(-1); };
            gPt.Controls.Add(_btnPointUp);

            _btnPointDown = MakeButton("下移", 72, 200, 52, 26);
            _btnPointDown.Click += delegate { MoveSelectedPoint(1); };
            gPt.Controls.Add(_btnPointDown);

            Label lblOrderHint = MakeLabel("顺序 = 多条序列共用一热键时的先后", 132, 204, 218);
            lblOrderHint.ForeColor = Color.FromArgb(130, 138, 150);
            gPt.Controls.Add(lblOrderHint);

            // 序列间隔：这条序列里相邻两个点之间等多久
            Label lblSeqGap = MakeLabel("序列间隔", 356, 204, 62);
            gPt.Controls.Add(lblSeqGap);

            _numSeqGap = new NumericUpDown();
            _numSeqGap.SetBounds(422, 200, 66, 24);
            _numSeqGap.Minimum = 0;
            _numSeqGap.Maximum = 60000;
            _numSeqGap.Value = 60;
            _numSeqGap.ValueChanged += delegate { OnSequenceGapEdited(); };
            gPt.Controls.Add(_numSeqGap);

            Label lblSeqMs = MakeLabel("毫秒", 462, 204, 40);
            lblSeqMs.ForeColor = Color.FromArgb(130, 138, 150);
            gPt.Controls.Add(lblSeqMs);

            // ---- 备注名 + 点击方式（作用于选中的那个点）----
            Label lblPtName = MakeLabel("备注名", 16, 237, 52);
            gPt.Controls.Add(lblPtName);

            _txtPointName = new TextBox();
            _txtPointName.SetBounds(70, 234, 160, 24);
            _txtPointName.MaxLength = 40;
            _txtPointName.TextChanged += delegate { OnPointNameEdited(); };
            gPt.Controls.Add(_txtPointName);

            Label lblPtMode = MakeLabel("点击方式", 240, 237, 60);
            gPt.Controls.Add(lblPtMode);

            _cboPointMode = new ComboBox();
            _cboPointMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboPointMode.SetBounds(302, 234, 210, 24);
            _cboPointMode.Items.AddRange(new object[] { "按一次 = 点一次", "按一下开始 / 再按停止（连点）" });
            _cboPointMode.SelectedIndexChanged += delegate { OnPointModeEdited(); };
            gPt.Controls.Add(_cboPointMode);





            _chkPointSwallow = MakeCheck("吞掉热键，游戏不会收到这些键", 16, 296, 260);
            _chkPointSwallow.CheckedChanged += delegate { ApplyLiveSettings(); };
            gPt.Controls.Add(_chkPointSwallow);

            _chkPointReturn = MakeCheck("点完把鼠标送回原处", 286, 296, 160);
            _chkPointReturn.CheckedChanged += delegate { OnPointReturnEdited(); };
            gPt.Controls.Add(_chkPointReturn);

            // ---- 选中序列内的点（序号 = 批量录点时屏幕上的标记数字） ----
            Label lblSeqPts = MakeLabel("该序列包含的点（顺序 = 触发顺序，序号对应屏幕标记）", 16, 324, 380);
            lblSeqPts.ForeColor = Color.FromArgb(110, 118, 132);
            gPt.Controls.Add(lblSeqPts);

            _lstSeqPoints = new ListView();
            _lstSeqPoints.SetBounds(16, 344, 496, 96);
            _lstSeqPoints.View = View.Details;
            _lstSeqPoints.FullRowSelect = true;
            _lstSeqPoints.MultiSelect = false;
            _lstSeqPoints.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _lstSeqPoints.Columns.Add("序号", 44);
            _lstSeqPoints.Columns.Add("备注名", 176);
            _lstSeqPoints.Columns.Add("坐标", 116);
            gPt.Controls.Add(_lstSeqPoints);

            _lblPointHint = MakeLabel("", 16, 448, 496);
            _lblPointHint.ForeColor = Color.FromArgb(120, 128, 140);
            gPt.Controls.Add(_lblPointHint);

            Label lblPtWarn = MakeLabel("• 双击序列行 = 从第 1 个点开始跑；一条序列里的点共用一个热键", 16, 470, 496);
            lblPtWarn.ForeColor = Color.FromArgb(150, 156, 168);
            gPt.Controls.Add(lblPtWarn);
            y += 402;

            // ================= 程序行为 =================
            GroupBox gA = MakeGroup("程序行为", 8, 100);
            tabApp.Controls.Add(gA);

            _chkAutoStart = MakeCheck("开机自动后台启动", 16, 22, 180);
            _chkAutoStart.CheckedChanged += delegate { ApplyConfig(); };
            gA.Controls.Add(_chkAutoStart);

            _chkTrayOnClose = MakeCheck("关闭窗口时最小化到托盘", 16, 46, 220);
            _chkTrayOnClose.CheckedChanged += delegate { ApplyConfig(); };
            gA.Controls.Add(_chkTrayOnClose);

            _chkStartMin = MakeCheck("启动后直接隐藏到托盘", 250, 22, 200);
            _chkStartMin.CheckedChanged += delegate { ApplyConfig(); };
            gA.Controls.Add(_chkStartMin);

            _chkBalloon = MakeCheck("显示托盘气泡提示", 250, 46, 180);
            _chkBalloon.CheckedChanged += delegate { ApplyConfig(); };
            gA.Controls.Add(_chkBalloon);

            _chkRunAsAdmin = MakeCheck("默认以管理员身份运行（游戏里点击无效时开启）", 16, 70, 330);
            _chkRunAsAdmin.CheckedChanged += delegate { OnRunAsAdminChanged(); };
            gA.Controls.Add(_chkRunAsAdmin);

            _btnElevateNow = MakeButton("立即提权重启", 352, 69, 106, 24);
            _btnElevateNow.Click += delegate { RestartAsAdmin(); };
            gA.Controls.Add(_btnElevateNow);
            y += 108;

            Label foot = new Label();
            foot.Text = "配置与日志保存在：" + AppPaths.DataDir;
            foot.Font = _font;
            foot.ForeColor = Color.FromArgb(150, 156, 168);
            foot.SetBounds(16, y, 500, 20);
            tabApp.Controls.Add(foot);
            y += 22;

            // 权限提示：很多游戏以更高完整性运行，未提权时 Windows 会静默丢弃注入的点击
            _lblPermState = new Label();
            _lblPermState.Font = _font;
            _lblPermState.SetBounds(16, y, 500, 20);
            tabApp.Controls.Add(_lblPermState);
            UpdatePermissionLabel();
            y += 24;

            // 主页面不滚动，各标签页的高度由 FitTabPages() 按内容动态管理
        }

        /// <summary>未自定义窗口尺寸时，按"最高的那一页"推算默认窗口高度。</summary>
        private int DefaultContentHeight()
        {
            int maxPage = 0;
            if (_tabs != null)
            {
                foreach (TabPage tp in _tabs.TabPages)
                {
                    int bottom = 0;
                    foreach (Control c in tp.Controls)
                    {
                        int b = c.Top + c.Height;
                        if (b > bottom) bottom = b;
                    }
                    if (bottom > maxPage) maxPage = bottom;
                }
            }
            return Math.Max(maxPage + 40, ContentH);
        }

        /// <summary>按当前显示器 DPI 整体缩放界面，保证高分屏下不模糊、布局不错位。</summary>
        private void ApplyDpiScale()
        {
            int contentNeeded = DefaultContentHeight();
            int intendedH = contentNeeded + StatusH + BottomH;

            // 取真实 DPI。注意不能用 Control.DeviceDpi：
            // 在 AutoScaleMode=None 且进程按监视器 DPI 感知时，它可能仍返回 96，
            // 而字体实际按 120(125%) 渲染 —— 于是文字比布局宽 33%，全被截断。
            // 这里依次尝试 GetDpiForWindow、屏幕 LOGPIXELSX，最后才退回 DeviceDpi。
            int dpi = DpiInfo.OfWindow(Handle);
            if (dpi <= 0) dpi = DpiInfo.OfScreen();
            if (dpi <= 0)
            {
                try { dpi = DeviceDpi; } catch { dpi = 96; }
            }
            if (dpi <= 0) dpi = 96;

            float scale = dpi / 96f;
            if (scale < 0.5f || scale > 4f) scale = 1f;

            SuspendLayout();
            try
            {
                if (Math.Abs(scale - 1f) > 0.01f)
                {
                    Scale(new SizeF(scale, scale));
                }

                // 窗口尺寸固定（用户要求：永远保持当前大小，不读配置、不随内容变化）
                int wantedW = (int)Math.Round(FormW * scale);
                int wantedH = (int)Math.Round((ContentH + StatusH + BottomH) * scale);

                // 屏幕装不下时收紧，超出的内容靠滚动条访问
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                int maxH = wa.Height - 40;
                if (wantedH > maxH) wantedH = maxH;
                if (wantedW > wa.Width - 20) wantedW = wa.Width - 20;

                ClientSize = new Size(wantedW, wantedH);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private GroupBox MakeGroup(string text, int y, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.Font = _fontBold;
            g.ForeColor = Color.FromArgb(58, 66, 82);
            g.SetBounds(12, y, 516, h);
            g.BackColor = Color.White;
            g.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            return g;
        }

        private Label MakeLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 20);
            l.Font = _font;
            l.ForeColor = Color.FromArgb(58, 66, 82);
            l.BackColor = Color.Transparent;
            return l;
        }

        private RadioButton MakeRadio(string text, int x, int y, int w)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.SetBounds(x, y, w, 24);
            r.Font = _font;
            r.ForeColor = Color.FromArgb(48, 56, 72);
            r.BackColor = Color.Transparent;
            return r;
        }

        private CheckBox MakeCheck(string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, w, 22);
            c.Font = _font;
            c.ForeColor = Color.FromArgb(48, 56, 72);
            c.BackColor = Color.Transparent;
            return c;
        }

        private Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.Font = _font;
            b.FlatStyle = FlatStyle.System;
            b.UseVisualStyleBackColor = true;
            return b;
        }

        // =========================================================
        //  托盘
        // =========================================================

        private void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = _font;

            _trayStatusItem = new ToolStripMenuItem("待机中");
            _trayStatusItem.Enabled = false;
            menu.Items.Add(_trayStatusItem);
            menu.Items.Add(new ToolStripSeparator());

            _trayToggleItem = new ToolStripMenuItem("开始连点", null, delegate { ToggleClicking(); });
            menu.Items.Add(_trayToggleItem);

            // 托盘里点"点序列"：跑当前选中的那条（没选中就取第一条）
            _trayPointItem = new ToolStripMenuItem("点坐标点", null, delegate
            {
                int idx = _selPoint;
                if (idx < 0 || idx >= _cfg.Sequences.Count) idx = _cfg.Sequences.Count > 0 ? 0 : -1;
                if (idx < 0) { ShowBalloon("还没有坐标点", "请先用记录坐标录一组坐标。"); return; }
                // 二次防御：确保 idx 在当前配置范围内（切换配置后 _selPoint 可能残留旧值）
                if (idx >= _cfg.Sequences.Count) idx = 0;
                OnPointHotkey(new int[] { idx });
            });
            menu.Items.Add(_trayPointItem);


            // 批量录点：从这里进入 / 结束，让用户在全屏游戏里也能操作
            _trayBatchItem = new ToolStripMenuItem("批量录点（窗口让开，点哪儿记哪儿）", null,
                delegate { StartBatchRecord(); });
            menu.Items.Add(_trayBatchItem);

            _trayBatchDoneItem = new ToolStripMenuItem("录点完成，回来确认", null,
                delegate { FinishBatchRecord(); });
            _trayBatchDoneItem.Visible = false;
            menu.Items.Add(_trayBatchDoneItem);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("显示主窗口", null, delegate { ShowFromTray(); }));
            menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, delegate { OpenConfigFile(); }));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate
            {
                _reallyExit = true;
                Close();
            }));

            _tray = new NotifyIcon();
            _tray.Icon = TrayIconFactory.CreateIcon(16);
            _tray.Text = "游戏连点器";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate
            {
                // 批量录点期间双击托盘 = "我录完了，回来确认"
                if (_batchRecording) FinishBatchRecord();
                else ShowFromTray();
            };
        }

        private void HideToTray()
        {
            Hide();
            ShowBalloon("连点器仍在后台运行", "触发键：" + DescribeTrigger() + "。双击托盘图标可打开设置。");
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void ShowBalloon(string title, string text)
        {
            if (!_cfg.ShowBalloonTips || _tray == null) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(2500);
            }
            catch { }
        }

        private void OpenConfigFile()
        {
            try
            {
                if (!File.Exists(AppConfig.ConfigPath)) _cfg.Save();
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppConfig.ConfigPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppPaths.Log("打开配置失败: " + ex.Message); }
        }

        private void OpenLogFile()
        {
            try
            {
                if (!File.Exists(AppPaths.LogPath))
                {
                    MessageBox.Show(this, "暂无日志，说明程序运行正常。", "游戏连点器",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppPaths.LogPath + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppPaths.Log("打开日志失败: " + ex.Message); }
        }

        // =========================================================
        //  配置 <-> 界面
        // =========================================================

        private static readonly int[] EmergencyVks = new int[] { 0, 0x7B, 0x7A, 0x79, 0x13, 0x23, 0x2E, 0x24 };

        private void LoadConfigIntoUi()
        {
            _suppressEvents = true;
            try
            {
                _rbKeyboard.Checked = _cfg.TriggerKind == TriggerKind.Keyboard;
                _rbMouse.Checked = _cfg.TriggerKind == TriggerKind.Mouse;
                _txtKey.Text = KeyNames.Describe(_cfg.TriggerVk);
                _cboTriggerMouse.SelectedIndex = Clamp(0, 4, _cfg.TriggerMouse);
                _cboMode.SelectedIndex = _cfg.Mode == TriggerMode.Hold ? 1 : 0;
                _chkSwallow.Checked = _cfg.SwallowTrigger;

                int eidx = 0;
                for (int i = 0; i < EmergencyVks.Length; i++)
                    if (EmergencyVks[i] == _cfg.EmergencyVk) { eidx = i; break; }
                _cboEmergency.SelectedIndex = eidx;

                _numInterval.Value = Clamp((int)_numInterval.Minimum, (int)_numInterval.Maximum, _cfg.IntervalMs);
                _sldInterval.Value = Clamp(_sldInterval.Minimum, _sldInterval.Maximum, _cfg.IntervalMs);
                _cboButton.SelectedIndex = Clamp(0, 2, (int)_cfg.Button);
                _numHold.Value = Clamp((int)_numHold.Minimum, (int)_numHold.Maximum, _cfg.HoldMs);
                _rbFollow.Checked = _cfg.FollowCursor;
                _rbLock.Checked = !_cfg.FollowCursor;
                _chkTrackPixels.Checked = _cfg.TrackPixels;

                _pointEditing = true;
                try
                {
                    _chkPointSwallow.Checked = _cfg.PointHotkeySwallow;
                    if (_numSeqGap != null)
                        _numSeqGap.Value = Clamp((int)_numSeqGap.Minimum, (int)_numSeqGap.Maximum, _cfg.SequenceGapMs);
                }
                finally { _pointEditing = false; }

                _chkAutoStart.Checked = _cfg.AutoStartWithWindows;
                _chkTrayOnClose.Checked = _cfg.MinimizeToTrayOnClose;
                _chkStartMin.Checked = _cfg.StartMinimized;
                _chkBalloon.Checked = _cfg.ShowBalloonTips;
                _chkRunAsAdmin.Checked = _cfg.RunAsAdmin;
            }
            finally
            {
                _suppressEvents = false;
            }

            UpdateIntervalHint();
            ApplyTriggerKindVisibility();
            ApplyPositionModeVisibility();
            RefreshPointList();
        }

        // =========================================================
        //  序列列表与编辑
        // =========================================================

        /// <summary>
        /// 用配置里的序列重建列表显示（勾选状态 = 是否启用该序列的热键）。
        /// 重建期间屏蔽 ItemChecked/SelectedIndexChanged，避免反过来又写回配置。
        /// </summary>
        private void RefreshPointList()
        {
            if (_lstPoints == null) return;

            _pointEditing = true;
            try
            {
                _lstPoints.BeginUpdate();
                // Clear() 会同步触发 ItemChecked，先摘掉处理器防止越界
                _lstPoints.ItemChecked -= OnPointCheckedHandler;
                _lstPoints.Items.Clear();

                for (int i = 0; i < _cfg.Sequences.Count; i++)
                {
                    SequenceEntry s = _cfg.Sequences[i];
                    // 第 0 列序号：对应批量录点时屏幕上圆圈标记的数字
                    ListViewItem it = new ListViewItem((i + 1).ToString());
                    it.SubItems.Add(s.DisplayName(i));
                    it.SubItems.Add(s.SizeText);
                    it.SubItems.Add(s.HotkeyVk > 0 ? KeyNames.Describe(s.HotkeyVk) : "未设置");
                    it.SubItems.Add(s.Points.Count > 1
                        ? ("依次点完，间隔 " + s.GapMs + "ms")
                        : (s.Mode == 1 ? "按一下连点" : "按一次点一次"));
                    it.Checked = s.Enabled;
                    it.Tag = i;
                    if (!s.Enabled) it.ForeColor = Color.FromArgb(150, 156, 168);
                    _lstPoints.Items.Add(it);
                }
                _lstPoints.EndUpdate();
                _lstPoints.ItemChecked += OnPointCheckedHandler;

                // 尽量保持原来选中的那一项
                if (_selPoint >= 0 && _selPoint < _lstPoints.Items.Count)
                    _lstPoints.Items[_selPoint].Selected = true;
                else if (_lstPoints.Items.Count > 0)
                {
                    _selPoint = 0;
                    _lstPoints.Items[0].Selected = true;
                }
                else _selPoint = -1;
            }
            finally
            {
                _pointEditing = false;
            }

            RefreshSeqPointEditor();
            SyncPointEditors();
            UpdatePointHint();
        }

        /// <summary>把选中序列的属性同步到下方编辑控件；没有选中则禁用它们。</summary>
        private void SyncPointEditors()
        {
            bool has = _selPoint >= 0 && _selPoint < _cfg.Sequences.Count;

            if (_txtPointName != null) _txtPointName.Enabled = has;
            if (_cboPointMode != null) _cboPointMode.Enabled = has;
            if (_chkPointReturn != null) _chkPointReturn.Enabled = has;
            if (_btnPointDel != null) _btnPointDel.Enabled = has;
            if (_btnPointKey != null) _btnPointKey.Enabled = has;
            if (_btnPointClearKey != null) _btnPointClearKey.Enabled = has;
            if (_btnPointUp != null) _btnPointUp.Enabled = has && _selPoint > 0;
            if (_btnPointDown != null) _btnPointDown.Enabled = has && _selPoint < _cfg.Sequences.Count - 1;

            if (!has)
            {
                _pointEditing = true;
                try
                {
                    if (_txtPointName != null) _txtPointName.Text = "";
                    if (_cboPointMode != null) _cboPointMode.SelectedIndex = 0;
                    if (_chkPointReturn != null) _chkPointReturn.Checked = false;
                    if (_numSeqGap != null) _numSeqGap.Value = Clamp((int)_numSeqGap.Minimum, (int)_numSeqGap.Maximum, 60);
                    if (_lstSeqPoints != null) _lstSeqPoints.Items.Clear();
                }
                finally { _pointEditing = false; }
                return;
            }

            SequenceEntry s = _cfg.Sequences[_selPoint];
            _pointEditing = true;
            try
            {
                if (_txtPointName != null) _txtPointName.Text = s.Name;
                if (_cboPointMode != null) _cboPointMode.SelectedIndex = s.Mode == 1 ? 1 : 0;
                if (_chkPointReturn != null) _chkPointReturn.Checked = s.ReturnCursor;
                if (_numSeqGap != null) _numSeqGap.Value = Clamp((int)_numSeqGap.Minimum, (int)_numSeqGap.Maximum, s.GapMs);
                // 多点序列固定"依次点完一轮"，间隔才有意义；单点序列间隔不起作用
                if (_numSeqGap != null) _numSeqGap.Enabled = has && s.Points.Count > 1;
                if (_cboPointMode != null) _cboPointMode.Enabled = has && s.Points.Count == 1;
                if (_lstSeqPoints != null)
                {
                    _lstSeqPoints.Items.Clear();
                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        SeqPoint p = s.Points[i];
                        ListViewItem it = new ListViewItem((i + 1).ToString());
                        it.SubItems.Add(p.DisplayName(i));
                        it.SubItems.Add(p.X + ", " + p.Y);
                        it.Tag = i;
                        _lstSeqPoints.Items.Add(it);
                    }
                }
            }
            finally { _pointEditing = false; }
        }

        /// <summary>刷新"序列内的点"小列表（改备注名/顺序时用）。</summary>
        private void RefreshSeqPointEditor()
        {
            SyncPointEditors();
        }

        /// <summary>列表选中项变化：切换正在编辑的序列。</summary>
        private void OnPointSelectionChanged()
        {
            if (_pointEditing) return;
            _selPoint = _lstPoints.SelectedIndices.Count > 0 ? _lstPoints.SelectedIndices[0] : -1;
            SyncPointEditors();
            UpdatePointHint();
        }

        /// <summary>勾选框变化 = 启用/停用该序列的热键。</summary>
        /// <summary>ItemChecked 事件的命名处理器：重建列表期间可整体摘除。</summary>
        private void OnPointCheckedHandler(object sender, ItemCheckedEventArgs e)
        {
            OnPointChecked();
        }

        private void OnPointChecked()
        {
            if (_pointEditing) return;
            try
            {
                // 防御：配置切换/列表重建期间事件可能带着旧索引到达，否则越界崩溃
                if (_cfg == null || _cfg.Sequences == null ||
                    _lstPoints == null || _lstPoints.Items.Count != _cfg.Sequences.Count) return;

                bool changed = false;
                for (int i = 0; i < _lstPoints.Items.Count && i < _cfg.Sequences.Count; i++)
                {
                    bool want = _lstPoints.Items[i].Checked;
                    if (_cfg.Sequences[i].Enabled != want)
                    {
                        _cfg.Sequences[i].Enabled = want;
                        _lstPoints.Items[i].ForeColor = want
                            ? _lstPoints.ForeColor
                            : Color.FromArgb(150, 156, 168);
                        changed = true;
                    }
                }

                if (changed)
                {
                    ApplyLiveSettings();
                    _cfg.Save();
                    UpdatePointHint();
                }
            }
            catch (Exception ex)
            {
                // 此事件在 ListView 消息循环内触发，抛出会变成未处理异常弹窗干扰用户。
                try { AppPaths.Log("勾选事件处理异常(已忽略): " + ex.Message); } catch { }
            }
        }



        // =========================================================
        //  事件接线
        // =========================================================

        private void WireEvents()
        {
            _hook.TriggerToggle += delegate { SafeUi(ToggleClicking); };
            _hook.TriggerDown += delegate { SafeUi(StartClicking); };
            _hook.TriggerUp += delegate { SafeUi(StopClicking); };
            _hook.EmergencyPressed += delegate
            {
                SafeUi(delegate
                {
                    if (_engine.IsActive) StopClicking();
                });
            };
            _hook.KeyCaptured += delegate (int vk) { SafeUi(delegate { OnKeyCaptured(vk); }); };
            _hook.MouseCaptured += delegate (int code) { SafeUi(delegate { OnMouseCaptured(code); }); };
            _hook.PointKeyCaptured += delegate (int vk, int forIndex) { SafeUi(delegate { OnPointKeyCaptured(vk, forIndex); }); };
            _hook.BatchPointCaptured += delegate (int action, int bx, int by) { SafeUi(delegate { OnBatchPointCaptured(action, bx, by); }); };
            _hook.BatchPointFinishRequested += delegate { SafeUi(delegate { FinishBatchRecord(); }); };

            FormClosing += OnFormClosing;
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && _cfg.MinimizeToTrayOnClose) HideToTray();
                DebouncedSaveWindowSize();
                FitTabPages();
            };
        }

        // ---- 自动保存（防抖） ----
        private System.Windows.Forms.Timer _autoSaveTimer;

        /// <summary>设置改动后延迟 1 秒自动落盘。</summary>
        private void DebouncedAutoSave()
        {
            if (Program.SelfTestMode) return;   // 自检不改用户配置

            if (_autoSaveTimer == null)
            {
                _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _autoSaveTimer.Tick += delegate
                {
                    _autoSaveTimer.Stop();
                    try
                    {
                        CollectConfig();
                        _cfg.Save();
                    }
                    catch { }
                };
            }
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        /// <summary>窗口尺寸已固定，此方法保留空实现兼容旧调用点。</summary>

        private void SaveWindowSize() { }

        private void SaveAndNotify()
        {
            CollectConfig();
            ApplyConfig();
            ApplyStartupRegistration();
            _cfg.Save();
            ShowBalloon("设置已保存", "配置已写入 " + AppConfig.ConfigPath);

            if (_cfg.AutoStartWithWindows && !string.IsNullOrEmpty(_startupDetail))
                ShowBalloon("开机自启状态", _startupDetail);
        }

        private void ApplyLiveSettings()
        {
            if (_suppressEvents) return;
            CollectConfig();
            _hook.ApplySettings(_cfg);
            if (_engine.IsActive)
                _engine.UpdateLive(_cfg.IntervalMs, _cfg.Button, _cfg.HoldMs, _cfg.FollowCursor, _cfg.TrackPixels);
            RefreshLiveStatus();
            DebouncedAutoSave();
        }

        private void ApplyConfig()
        {
            if (_suppressEvents) return;
            CollectConfig();
            _hook.ApplySettings(_cfg);
            if (_engine.IsActive)
                _engine.UpdateLive(_cfg.IntervalMs, _cfg.Button, _cfg.HoldMs, _cfg.FollowCursor, _cfg.TrackPixels);
            RefreshLiveStatus();
            DebouncedAutoSave();
        }

        private void ApplyStartupRegistration()
        {
            try
            {
                string detail;
                bool ok = Startup.Apply(_cfg.AutoStartWithWindows, out detail);
                _startupDetail = ok ? detail : ("设置开机自启失败：" + detail);
                AppPaths.Log("开机自启：" + _startupDetail);
            }
            catch (Exception ex)
            {
                _startupDetail = "设置开机自启异常：" + ex.Message;
                AppPaths.Log("写入开机自启失败: " + ex.Message);
            }
        }

        private string _startupDetail = "";

        // =========================================================
        //  连点控制
        // =========================================================

        private void ToggleClicking()
        {
            if (_engine.IsActive) StopClicking();
            else StartClicking();
        }

        private void StartClicking()
        {
            if (_engine.IsActive) return;
            CollectConfig();
            _engine.LastError = null;
            _engine.Start(_cfg.IntervalMs, _cfg.Button, _cfg.FollowCursor, _cfg.HoldMs, _cfg.TrackPixels);
            RefreshLiveStatus();
            ShowStatusBanner(true);   // 屏幕顶部常驻提示已启动
        }

        private void StopClicking()
        {
            if (!_engine.IsActive) return;
            _engine.Stop();
            RefreshLiveStatus();
            ShowStatusBanner(false);   // 短暂提示已关闭
        }

        /// <summary>屏幕顶部状态条：启动=绿色常驻，停止=灰色短暂提示。</summary>
        private void ShowStatusBanner(bool on)
        {
            if (Program.SelfTestMode) return;   // 自检截图不要状态条入镜
            try
            {
                if (_statusBanner == null || _statusBanner.IsDisposed)
                    _statusBanner = new StatusBanner();
                if (on) _statusBanner.ShowOn(DescribeTrigger());
                else _statusBanner.ShowOff();
            }
            catch (Exception ex)
            {
                AppPaths.Log("[banner] 创建/显示异常: " + ex.Message);
            }
        }

        private void RefreshLiveStatus()
        {
            if (_lblStatus == null) return;

            bool active = _engine.IsActive;
            bool pointMode = _engine.IsPointMode;
            bool seqMode = _engine.IsSequenceMode;

            if (active)
            {
                _lblStatus.Text = seqMode ? "序列触发中" : (pointMode ? "连点坐标点" : "连点中");
                _lblStatus.ForeColor = Color.FromArgb(24, 148, 88);
            }
            else if (_armedHint)
            {
                _lblStatus.Text = "已就绪";
                _lblStatus.ForeColor = Color.FromArgb(48, 110, 200);
            }
            else
            {
                _lblStatus.Text = "待机中";
                _lblStatus.ForeColor = Color.FromArgb(120, 128, 140);
            }

            _statusPanel.BackColor = active
                ? (seqMode ? Color.FromArgb(224, 246, 234) : Color.FromArgb(230, 238, 252))
                : (_armedHint ? Color.FromArgb(230, 238, 252) : Color.FromArgb(232, 236, 244));

            _lblCount.Text = "触发键：" + DescribeTrigger() + "　·　已点击 " + _engine.ClickCount + " 次";
            _lblCount.ForeColor = Color.FromArgb(110, 118, 132);

            if (_trayToggleItem != null) _trayToggleItem.Text = active ? "停止连点" : "开始连点";
            if (_trayPointItem != null)
            {
                string ptName = "序列";
                int pi = _engine.LastPointIndex;
                if (pointMode && pi >= 0 && pi < _cfg.Sequences.Count)
                    ptName = "「" + _cfg.Sequences[pi].DisplayName(pi) + "」";

                if (seqMode) ptName = "（" + _engine.SequenceLength + " 个点）";
                _trayPointItem.Text = pointMode ? ("停止 " + ptName) : ("点" + ptName);
                _trayPointItem.Enabled = _cfg.Sequences.Count > 0;
            }
            if (_trayStatusItem != null)
                _trayStatusItem.Text = active
                    ? ((seqMode ? "序列触发中 · " : (pointMode ? "连点坐标点 · " : "连点中 · ")) + "已点击 " + _engine.ClickCount + " 次")
                    : (_armedHint ? "已就绪 · 等待触发键" : "待机中");
        }

        private string DescribeTrigger()
        {
            if (_cfg.TriggerKind == TriggerKind.Keyboard) return KeyNames.Describe(_cfg.TriggerVk);
            string[] names = new string[] { "鼠标左键", "鼠标右键", "鼠标中键", "鼠标侧键X1", "鼠标侧键X2" };
            return names[Clamp(0, names.Length - 1, _cfg.TriggerMouse)];
        }

        private static int Clamp(int lo, int hi, int v)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // =========================================================
        //  按键捕获（触发键）
        // =========================================================

        private void EndCapture(bool applied)
        {
            _capturing = false;
            _hook.CancelCapture();
            _btnCapture.Text = "按下以设置按键";
            if (_btnPointKey != null) _btnPointKey.Text = "改键";
            if (!applied) _txtKey.Text = KeyNames.Describe(_cfg.TriggerVk);
        }

        private void OnKeyCaptured(int vk)
        {
            if (!_capturing) return;

            if (vk == 0x1B) { EndCapture(false); return; }   // Esc = 取消

            if (IsModifierKey(vk))
            {
                _txtKey.Text = "该键不可用，请换一个";
                return;
            }

            string conflict = FindKeyConflict(vk, "trigger", -1);
            if (conflict != null)
            {
                _txtKey.Text = "与" + conflict + "冲突";
                ShowBalloon("热键冲突", "该键已用作" + conflict + "，请换一个。");
                return;
            }

            _cfg.TriggerVk = vk;
            _txtKey.Text = KeyNames.Describe(vk);
            EndCapture(true);
            ApplyLiveSettings();
            ShowBalloon("触发键已更新", "当前触发键：" + KeyNames.Describe(vk));
        }

        private void OnMouseCaptured(int code)
        {
            if (!_capturing) return;
            _cfg.TriggerMouse = code;
            _cboTriggerMouse.SelectedIndex = Clamp(0, 4, code);
            EndCapture(true);
            ApplyLiveSettings();
            ShowBalloon("触发键已更新", "当前触发键：" + DescribeTrigger());
        }


        // =========================================================
        //  触发键类型/位置模式切换与可见性
        // =========================================================

        private void OnTriggerKindChanged()
        {
            if (_suppressEvents) return;
            CollectConfig();
            ApplyTriggerKindVisibility();
            ApplyLiveSettings();
        }

        private void ApplyTriggerKindVisibility()
        {
            bool kb = (_cfg.TriggerKind == TriggerKind.Keyboard);
            if (_rbKeyboard != null) _rbKeyboard.Checked = kb;
            if (_rbMouse != null) _rbMouse.Checked = !kb;
            if (_cboTriggerMouse != null) _cboTriggerMouse.Visible = !kb;
            if (_txtKey != null) _txtKey.Visible = kb;
            if (_btnCapture != null) _btnCapture.Visible = kb;
        }

        private void ApplyPositionModeVisibility()
        {
            if (_chkTrackPixels != null)
                _chkTrackPixels.Enabled = !_cfg.FollowCursor;
        }

        private void OnPositionModeChanged()
        {
            if (_suppressEvents) return;
            CollectConfig();
            ApplyPositionModeVisibility();
            ApplyLiveSettings();
        }

        private void OnRunAsAdminChanged()
        {
            if (_suppressEvents) return;

            _cfg.RunAsAdmin = _chkRunAsAdmin.Checked;
            _cfg.Save();          // 立刻落盘：下次启动就要用到这个值
            UpdatePermissionLabel();

            if (_chkRunAsAdmin.Checked && !Elevation.IsElevated)
            {
                ShowBalloon("已开启管理员模式", "下次启动将请求管理员权限。游戏里点击无效时开这个。");
            }
            else if (_chkRunAsAdmin.Checked && Elevation.IsElevated)
            {
                ShowBalloon("设置已保存", "下次启动仍会以管理员身份运行。");
            }

            if (_btnElevateNow != null) _btnElevateNow.Enabled = !Elevation.IsElevated;
        }

        private void UpdatePermissionLabel()
        {
            bool elevated = Elevation.IsElevated;

            if (_lblPermState != null)
            {
                if (elevated)
                {
                    _lblPermState.Text = "当前以管理员身份运行 ✓";
                    _lblPermState.ForeColor = Color.FromArgb(24, 148, 88);
                }
                else
                {
                    _lblPermState.Text = "当前是普通权限。游戏里点击无效时请以管理员身份重启。";
                    _lblPermState.ForeColor = Color.FromArgb(170, 110, 40);
                }
            }

            if (_btnElevateNow != null) _btnElevateNow.Enabled = !elevated;
        }

        private void RestartAsAdmin()
        {
            CollectConfig();
            _cfg.Save();

            string err;
            if (Elevation.RelaunchElevated(new string[] { "--tray" }, out err))
            {
                _reallyExit = true;
                Close();
            }
            else
            {
                MessageBox.Show(this,
                    "未能以管理员身份启动：" + Environment.NewLine + err + Environment.NewLine + Environment.NewLine +
                    "程序将继续以普通权限运行。若游戏里点击无效，请手动右键 exe 选择「以管理员身份运行」。",
                    "游戏连点器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void SyncSliderFromNumber()
        {
            if (_suppressEvents || _sldInterval == null) return;
            int v = (int)_numInterval.Value;
            _sldInterval.Value = Clamp(_sldInterval.Minimum, _sldInterval.Maximum, v);
            UpdateIntervalHint();
        }

        private void SyncNumberFromSlider()
        {
            if (_suppressEvents || _numInterval == null) return;
            _numInterval.Value = Clamp((int)_numInterval.Minimum, (int)_numInterval.Maximum, _sldInterval.Value);
            UpdateIntervalHint();
        }

        private void UpdateIntervalHint()
        {
            if (_lblIntervalHint == null) return;
            int ms = (int)_numInterval.Value;
            double rate = 1000.0 / ms;
            _lblIntervalHint.Text = "当前：每 " + ms + " 毫秒点一次（约 " + rate.ToString("0.#") + " 次/秒）";
        }

        // =========================================================
        //  坐标点：列表交互
        // =========================================================

        private void OnPointNameEdited()
        {
            if (_pointEditing) return;
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            string name = _txtPointName.Text;
            if (_cfg.Sequences[_selPoint].Name == name) return;

            _cfg.Sequences[_selPoint].Name = name;

            if (_selPoint < _lstPoints.Items.Count)
                _lstPoints.Items[_selPoint].SubItems[1].Text =
                    _cfg.Sequences[_selPoint].DisplayName(_selPoint);

            _cfg.Save();
        }

        private void OnPointModeEdited()
        {
            if (_pointEditing) return;
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            int mode = _cboPointMode.SelectedIndex == 1 ? 1 : 0;
            if (_cfg.Sequences[_selPoint].Mode == mode) return;

            _cfg.Sequences[_selPoint].Mode = mode;
            ApplyLiveSettings();
            _cfg.Save();
            UpdatePointHint();
        }

        private void OnPointReturnEdited()
        {
            if (_pointEditing) return;
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            _cfg.Sequences[_selPoint].ReturnCursor = _chkPointReturn.Checked;
            ApplyLiveSettings();
            _cfg.Save();
            UpdatePointHint();
        }

        private void DeleteSelectedPoint()
        {
            List<int> sel = SelectedPointIndices();
            if (sel.Count == 0) return;

            string name = _cfg.Sequences[sel[0]].DisplayName(sel[0]);
            string what = sel.Count == 1
                ? "序列「" + name + "」（" + _cfg.Sequences[sel[0]].Points.Count + " 个点）"
                : sel.Count + " 条序列（含「" + name + "」等）";
            if (MessageBox.Show(this, "确定删除" + what + "？",
                    "游戏连点器", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            if (_engine.IsSequenceMode || _engine.IsPointMode) _engine.Stop();

            for (int i = sel.Count - 1; i >= 0; i--) _cfg.Sequences.RemoveAt(sel[i]);

            if (_selPoint >= _cfg.Sequences.Count) _selPoint = _cfg.Sequences.Count - 1;

            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();
            ShowBalloon("已删除", "删除了 " + sel.Count + " 条序列");
        }

        private List<int> SelectedPointIndices()
        {
            List<int> list = new List<int>();
            if (_lstPoints == null) return list;
            foreach (int i in _lstPoints.SelectedIndices) list.Add(i);
            list.Sort();
            return list;
        }

        private void ClearSelectedPointHotkey()
        {
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            _cfg.Sequences[_selPoint].HotkeyVk = 0;
            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();
            ShowBalloon("已清除热键", "该序列只能从界面启动");
        }

        private void MoveSelectedPoint(int delta)
        {
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            int target = _selPoint + delta;
            if (target < 0 || target >= _cfg.Sequences.Count) return;

            SequenceEntry tmp = _cfg.Sequences[_selPoint];
            _cfg.Sequences[_selPoint] = _cfg.Sequences[target];
            _cfg.Sequences[target] = tmp;

            _selPoint = target;
            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();
            UpdatePointHint();
        }

        private void OnSequenceGapEdited()
        {
            if (_pointEditing) return;
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            int v = (int)_numSeqGap.Value;
            if (_cfg.Sequences[_selPoint].GapMs == v) return;

            _cfg.Sequences[_selPoint].GapMs = v;
            if (_selPoint < _lstPoints.Items.Count)
                _lstPoints.Items[_selPoint].SubItems[4].Text =
                    _cfg.Sequences[_selPoint].Points.Count > 1
                        ? ("依次点完，间隔 " + v + "ms")
                        : "按一次点一次";
            _cfg.Save();
            UpdatePointHint();
        }

        private void UpdatePointHint()
        {
            if (_lblPointHint == null) return;

            int total = _cfg.Sequences.Count;
            int enabled = 0, pointTotal = 0;
            for (int i = 0; i < total; i++)
            {
                if (_cfg.Sequences[i].Enabled && _cfg.Sequences[i].HotkeyVk > 0) enabled++;
                pointTotal += _cfg.Sequences[i].Points.Count;
            }

            if (total == 0)
            {
                _lblPointHint.Text = "用法：点「记录坐标…」→ 在屏幕上点出你要的位置 → 按 Esc 回来确认";
                _lblPointHint.ForeColor = Color.FromArgb(170, 110, 40);
                return;
            }

            if (_selPoint >= 0 && _selPoint < total)
            {
                SequenceEntry s = _cfg.Sequences[_selPoint];
                string keyDesc = s.HotkeyVk > 0 ? KeyNames.Describe(s.HotkeyVk) : null;
                string how;
                if (keyDesc == null)
                    how = "该序列未设热键，只能从界面启动";
                else if (!s.Enabled)
                    how = "热键 " + keyDesc + " 已停用（勾选列表左侧方框可启用）";
                else if (s.Points.Count > 1)
                    how = "按一下 " + keyDesc + " → 依次点这 " + s.Points.Count +
                          " 个点（间隔 " + s.GapMs + "ms），跑完一轮自动停";
                else
                    how = s.Mode == 1
                        ? "按一下 " + keyDesc + " 开始连点该坐标，再按一下停止"
                        : "每按一次 " + keyDesc + " 点一次";

                _lblPointHint.Text = "已选「" + s.DisplayName(_selPoint) + "」：" + how +
                                     (s.ReturnCursor ? "，点完鼠标自动回原位" : "");
            }
            else
            {
                _lblPointHint.Text = "共 " + total + " 条序列（" + pointTotal + " 个点），其中 " +
                                     enabled + " 条热键已启用";
            }

            _lblPointHint.ForeColor = enabled > 0 || (_selPoint >= 0)
                ? Color.FromArgb(48, 110, 200)
                : Color.FromArgb(170, 110, 40);
        }

        private string FindKeyConflict(int vk, string selfWhich, int selfPointIndex)
        {
            if (selfWhich != "trigger" && vk == _cfg.TriggerVk && _cfg.TriggerKind == TriggerKind.Keyboard)
                return "连点触发键";
            if (selfWhich != "emergency" && vk == _cfg.EmergencyVk && _cfg.EmergencyVk > 0)
                return "紧急停止键";
            return null;
        }

        private static bool IsModifierKey(int vk)
        {
            return vk == Native.VK_SHIFT || vk == Native.VK_CONTROL || vk == Native.VK_MENU ||
                   vk == Native.VK_LWIN || vk == Native.VK_RWIN;
        }

        private int CountPointsUsingKey(int vk)
        {
            if (vk <= 0) return 0;
            int n = 0;
            for (int i = 0; i < _cfg.Sequences.Count; i++)
            {
                if (_cfg.Sequences[i].Enabled && _cfg.Sequences[i].HotkeyVk == vk) n++;
            }
            return n;
        }

        // =========================================================
        //  坐标点热键捕获
        // =========================================================

        private void BeginPointKeyCapture()
        {
            if (_selPoint < 0 || _selPoint >= _cfg.Sequences.Count) return;

            _hook.CancelCapture();
            _hook.BeginPointKeyCapture(_selPoint);
            _btnPointKey.Text = "按键…";
            Activate();
            BringToFront();
        }

        private void EndPointKeyCapture()
        {
            _hook.CancelCapture();
            if (_btnPointKey != null) _btnPointKey.Text = "改键";
        }

        private void OnPointKeyCaptured(int vk, int forIndex)
        {
            int idx = forIndex >= 0 && forIndex < _cfg.Sequences.Count ? forIndex : _selPoint;
            if (idx < 0 || idx >= _cfg.Sequences.Count) { EndPointKeyCapture(); return; }

            if (vk == 0x1B)
            {
                EndPointKeyCapture();
                if (_hotkeyPrompt != null) _hotkeyPrompt.ResetCapture();
                return;
            }

            if (IsModifierKey(vk))
            {
                ShowBalloon("该键不可用", "功能键（Shift/Ctrl/Alt/Win）不能作为热键。");
                EndPointKeyCapture();
                if (_hotkeyPrompt != null) _hotkeyPrompt.ResetCapture();
                return;
            }

            string conflict = FindKeyConflict(vk, "point", idx);
            if (conflict != null)
            {
                ShowBalloon("热键冲突", "该键已用作" + conflict + "，请换一个。");
                EndPointKeyCapture();
                if (_hotkeyPrompt != null) _hotkeyPrompt.ResetCapture();
                return;
            }

            _cfg.Sequences[idx].HotkeyVk = vk;
            _cfg.Sequences[idx].Enabled = true;
            EndPointKeyCapture();

            if (_hotkeyPrompt != null)
                _hotkeyPrompt.SetHotkey(vk, KeyNames.Describe(vk));

            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();

            int shared = CountPointsUsingKey(vk);
            if (shared > 1)
            {
                ShowBalloon("热键已设置（多条序列共用）",
                    KeyNames.Describe(vk) + " 现在会依次触发 " + shared + " 条序列。顺序见列表，可用上移/下移调整。");
            }
            else
            {
                ShowBalloon("热键已设置", "「" + _cfg.Sequences[idx].DisplayName(idx) + "」→ " + KeyNames.Describe(vk));
            }
        }

        // =========================================================
        //  批量录点（记录坐标）
        // =========================================================

        private void StartBatchRecord()
        {
            if (_batchRecording) return;

            if (_engine.IsActive) StopClicking();

            _batchRecording = true;
            _batchRecorded = new List<SeqPoint>();
            try { BatchMarkForm.CloseAll(); } catch { }

            HideToTray();

            _hook.CancelCapture();
            _hook.BeginCapture(CaptureKind.BatchPoint);
            _hook.ResetBatchPointState();

            _batchPaused = false;

            _batchNav = new BatchNavBar();
            _batchNav.PauseToggled += delegate (bool paused)
            {
                _batchPaused = paused;
                _batchNav.SetPausedVisual(paused);
                AppPaths.Log(paused ? "批量录点：暂停取点" : "批量录点：继续取点");
            };
            _batchNav.DoneClicked += delegate { FinishBatchRecord(); };
            _batchNav.Show();

            _batchTip = new NotifyIcon();
            _batchTip.Icon = _tray != null ? _tray.Icon : SystemIcons.Application;
            _batchTip.Visible = true;
            _batchTip.Text = "连点器正在收录坐标";
            _batchTip.BalloonTipTitle = "记录坐标已开始";
            _batchTip.BalloonTipText = "左键点你要的位置取点，右键撤销上一个点。按 Esc 或点「完成」结束。";
            _batchTip.BalloonTipIcon = ToolTipIcon.Info;
            _batchTip.DoubleClick += delegate { FinishBatchRecord(); };
            _batchTip.ShowBalloonTip(6000);

            if (_trayBatchItem != null) _trayBatchItem.Visible = false;
            if (_trayBatchDoneItem != null) _trayBatchDoneItem.Visible = true;

            AppPaths.Log("进入批量录点模式");
        }

        private void FinishBatchRecord()
        {
            if (!_batchRecording) return;

            _batchRecording = false;

            _hook.CancelCapture();
            _hook.ResetBatchPointState();

            try { BatchMarkForm.CloseAll(); } catch { }

            if (_batchNav != null)
            {
                try { _batchNav.Close(); _batchNav.Dispose(); } catch { }
                _batchNav = null;
            }

            if (_batchTip != null)
            {
                try { _batchTip.Visible = false; _batchTip.Dispose(); } catch { }
                _batchTip = null;
            }

            if (_trayBatchItem != null) _trayBatchItem.Visible = true;
            if (_trayBatchDoneItem != null) _trayBatchDoneItem.Visible = false;

            ShowFromTray();

            if (_batchRecorded == null || _batchRecorded.Count == 0)
            {
                ShowBalloon("没有记录到坐标", "本次没有点击到任何位置。");
                AppPaths.Log("批量录点结束：0 个坐标");
                return;
            }

            SequenceEntry seq = SequenceEntry.FromPoints("", _batchRecorded, 60);
            _cfg.Sequences.Add(seq);
            int added = seq.Points.Count;
            _batchRecorded.Clear();

            _selPoint = _cfg.Sequences.Count - 1;
            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();

            AppPaths.Log("批量录点结束：新增序列（" + added + " 个点，间隔 " + seq.GapMs + "ms），弹出热键设置");

            int seqIndex = _selPoint;
            if (_hotkeyPrompt != null) { try { _hotkeyPrompt.Close(); } catch { } _hotkeyPrompt = null; }
            HotkeyPromptForm hk = new HotkeyPromptForm(
                "新序列：" + seq.DisplayName(seqIndex) + "（" + added + " 个点）");
            _hotkeyPrompt = hk;
            hk.ClearRequested += delegate
            {
                SafeUi(delegate
                {
                    if (seqIndex >= 0 && seqIndex < _cfg.Sequences.Count)
                    {
                        _cfg.Sequences[seqIndex].HotkeyVk = 0;
                        RefreshPointList();
                        ApplyLiveSettings();
                        _cfg.Save();
                    }
                });
            };
            hk.CaptureRequested += delegate
            {
                SafeUi(delegate { BeginPointKeyCapture(); });
            };
            hk.Confirmed += delegate (int vk)
            {
                SafeUi(delegate
                {
                    _hotkeyPrompt = null;
                    _hook.CancelCapture();
                    if (seqIndex >= 0 && seqIndex < _cfg.Sequences.Count)
                    {
                        _cfg.Sequences[seqIndex].HotkeyVk = vk;
                        RefreshPointList();
                        ApplyLiveSettings();
                        _cfg.Save();
                    }
                    AppPaths.Log("热键设置完成：" + (vk > 0 ? ("vk=0x" + vk.ToString("X2")) : "未设置"));
                });
            };
            hk.Show();

            ShowBalloon("已添加序列（" + added + " 个点）",
                "屏幕标记 1~" + added + " 对应序列里的第 1~" + added + " 个点。");
        }

        private void OnBatchPointCaptured(int action, int x, int y)
        {
            if (!_batchRecording || _batchRecorded == null) return;

            if (action == InputHook.BatchActionUndo)
            {
                UndoLastBatchPoint();
                return;
            }

            if (_batchPaused) return;

            if (_batchNav != null && _batchNav.ContainsPoint(x, y)) return;

            foreach (SeqPoint e in _batchRecorded)
            {
                if (Math.Abs(e.X - x) <= 2 && Math.Abs(e.Y - y) <= 2) return;
            }

            _batchRecorded.Add(new SeqPoint("", x, y));

            try { BatchMarkForm.ShowMark(x, y, _batchRecorded.Count); } catch { }
            if (_batchNav != null) _batchNav.SetCount(_batchRecorded.Count);

            AppPaths.Log("批量录点：" + _batchRecorded.Count + " → (" + x + ", " + y + ")");
        }

        private void UndoLastBatchPoint()
        {
            if (_batchRecorded == null || _batchRecorded.Count == 0) return;

            SeqPoint last = _batchRecorded[_batchRecorded.Count - 1];
            _batchRecorded.RemoveAt(_batchRecorded.Count - 1);
            BatchMarkForm.RemoveLast();

            if (_batchNav != null) _batchNav.SetCount(_batchRecorded.Count);
            AppPaths.Log("批量录点：撤销 (" + last.X + ", " + last.Y + ")，剩 " + _batchRecorded.Count + " 个");
        }

        // =========================================================
        //  坐标点热键触发
        // =========================================================

        private void OnPointHotkey(int[] indices)
        {
            if (indices == null || indices.Length == 0) return;
            if (_cfg.Sequences == null || _cfg.Sequences.Count == 0) return;

            List<int> valid = new List<int>(indices.Length);
            foreach (int i in indices)
            {
                if (i >= 0 && i < _cfg.Sequences.Count) valid.Add(i);
            }
            if (valid.Count == 0) return;

            bool wasPointMode = _engine.IsPointMode;
            int wasIndex = _engine.LastPointIndex;

            if (wasPointMode)
            {
                StopClicking();
                if (valid.Count == 1 && wasIndex == valid[0]) return;
            }
            else if (_engine.IsActive)
            {
                StopClicking();
            }

            CollectConfig();
            _engine.LastError = null;

            if (valid.Count > 1)
            {
                int total = 0;
                foreach (int i in valid) total += _cfg.Sequences[i].Points.Count;

                int[] xs = new int[total];
                int[] ys = new int[total];
                bool returnCursor = false;
                System.Text.StringBuilder names = new System.Text.StringBuilder();

                int w = 0;
                foreach (int i in valid)
                {
                    SequenceEntry s = _cfg.Sequences[i];
                    foreach (SeqPoint p in s.Points)
                    {
                        xs[w] = p.X;
                        ys[w] = p.Y;
                        w++;
                    }
                    if (s.ReturnCursor) returnCursor = true;
                    if (names.Length > 0) names.Append(" → ");
                    names.Append(s.DisplayName(i));
                }

                _engine.LastSequenceFirstPoint = valid[0];
                _engine.StartSequence(xs, ys, _cfg.Sequences[valid[0]].GapMs, _cfg.IntervalMs,
                    _cfg.Button, _cfg.HoldMs, returnCursor);

                AppPaths.Log("序列热键触发：" + names +
                    "（" + total + " 个点，间隔 " + _cfg.Sequences[valid[0]].GapMs + "ms）");
            }
            else
            {
                int index = valid[0];
                SequenceEntry s = _cfg.Sequences[index];

                if (s.Points.Count > 1)
                {
                    int n = s.Points.Count;
                    int[] xs = new int[n];
                    int[] ys = new int[n];
                    for (int k = 0; k < n; k++) { xs[k] = s.Points[k].X; ys[k] = s.Points[k].Y; }

                    _engine.LastSequenceFirstPoint = index;
                    _engine.StartSequence(xs, ys, s.GapMs, _cfg.IntervalMs,
                        _cfg.Button, _cfg.HoldMs, s.ReturnCursor);

                    AppPaths.Log("序列热键触发：「" + s.DisplayName(index) + "」" + n +
                        " 个点，间隔 " + s.GapMs + "ms");
                }
                else
                {
                    SeqPoint p = s.Points[0];
                    bool oneShot = s.Mode == 0;

                    _engine.LastPointIndex = index;
                    _engine.StartAtPoint(p.X, p.Y, _cfg.IntervalMs, _cfg.Button,
                        _cfg.HoldMs, oneShot, s.ReturnCursor);

                    AppPaths.Log("坐标点热键触发：「" + s.DisplayName(index) + "」(" + p.X + ", " + p.Y + ") " +
                        (oneShot ? "点一次" : "连点中"));
                }
            }

            RefreshLiveStatus();
        }

        private void ApplyRecordedPoint(int x, int y, bool notify)
        {
            SequenceEntry s = new SequenceEntry();
            s.Points.Add(new SeqPoint("", x, y));
            s.HotkeyVk = 0;
            s.Enabled = true;

            _cfg.Sequences.Add(s);
            _selPoint = _cfg.Sequences.Count - 1;

            RefreshPointList();
            ApplyLiveSettings();
            _cfg.Save();
            AppPaths.Log("新增坐标点：(" + x + ", " + y + ")，共 " + _cfg.Sequences.Count + " 条序列");
        }

        // =========================================================
        //  游戏配置（Profile）
        // =========================================================

        private string ActiveProfileDir
        {
            get
            {
                if (string.IsNullOrEmpty(_activeProfile)) return AppPaths.DataDir;
                ProfileManager.Item it = ProfileManager.Find(_activeProfile);
                return it != null ? it.Dir : AppPaths.DataDir;
            }
        }

        private void RefreshProfileList()
        {
            if (_cboProfile == null) return;

            _pointEditing = true;
            try
            {
                _cboProfile.Items.Clear();
                foreach (ProfileManager.Item it in ProfileManager.List())
                    _cboProfile.Items.Add(it.Name);

                int idx = _cboProfile.Items.IndexOf(_activeProfile);
                _cboProfile.SelectedIndex = idx >= 0 ? idx : (_cboProfile.Items.Count > 0 ? 0 : -1);
            }
            finally { _pointEditing = false; }

            SyncProfileEditors();
        }

        private void SyncProfileEditors()
        {
            if (_txtGameProc == null) return;

            string game = _cfg.GameProcess ?? "";

            _pointEditing = true;
            try { _txtGameProc.Text = game; }
            finally { _pointEditing = false; }
        }

        private void OnProfileSwitched()
        {
            if (_pointEditing) return;

            string name = Convert.ToString(_cboProfile.SelectedItem);
            if (string.IsNullOrEmpty(name) ||
                string.Equals(name, _activeProfile, StringComparison.OrdinalIgnoreCase)) return;

            try
            {
                CollectConfig();
                AppConfig.SaveToDir(ActiveProfileDir, _cfg);
            }
            catch { }

            _activeProfile = name;
            AppConfig.ActiveConfigDir = ActiveProfileDir;
            _cfg = AppConfig.LoadFromDir(ActiveProfileDir);
            _cfg.ProfileName = _activeProfile;
            _selPoint = -1;

            LoadConfigIntoUi();
            RefreshPointList();
            ApplyLiveSettings();
            SyncProfileEditors();
            AppPaths.Log("切换配置 → " + name);
        }

        private void CreateProfile()
        {
            string name = PromptInput("新建游戏配置", "配置名称（也是目录名，例如\"星穹铁道\"）：", "");
            if (string.IsNullOrEmpty(name)) return;
            if (!ProfileManager.ValidName(name))
            {
                MessageBox.Show(this, "名称不能为空、含非法字符或前后空格。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string game = PromptInput("绑定游戏进程", "游戏进程名（如 starrail.exe，可留空之后填）：", "");
            string dir = ProfileManager.Create(name, game);
            if (dir == null)
            {
                MessageBox.Show(this, "创建失败：同名配置可能已存在。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _activeProfile = name;
            AppConfig.ActiveConfigDir = dir;
            _cfg = AppConfig.LoadFromDir(dir);
            _cfg.ProfileName = name;
            _selPoint = -1;

            RefreshProfileList();
            LoadConfigIntoUi();
            RefreshPointList();
            ApplyLiveSettings();
            SyncProfileEditors();
            AppPaths.Log("新建配置 " + name + "（空白，绑定 " + (game.Length > 0 ? game : "无") + "）");
        }

        private void RenameProfile()
        {
            string old = _activeProfile;
            if (string.IsNullOrEmpty(old))
            {
                MessageBox.Show(this, "请先在下拉框里选中要改名的配置。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string n = PromptInput("重命名配置", "新名称：", old);
            if (string.IsNullOrEmpty(n) || n == old) return;
            if (!ProfileManager.ValidName(n))
            {
                MessageBox.Show(this, "名称不能为空、含非法字符或前后空格。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ProfileManager.Find(n) != null)
            {
                MessageBox.Show(this, "同名配置已存在。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                CollectConfig();
                AppConfig.SaveToDir(ActiveProfileDir, _cfg);
            }
            catch { }

            string err;
            if (!ProfileManager.Rename(old, n, out err))
            {
                MessageBox.Show(this, "改名失败：" + err, "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _activeProfile = n;
            AppConfig.ActiveConfigDir = ActiveProfileDir;
            _cfg = AppConfig.LoadFromDir(ActiveProfileDir);
            _cfg.ProfileName = n;
            _selPoint = -1;

            RefreshProfileList();
            SyncProfileEditors();
            AppPaths.Log("配置 " + old + " 已改名为 " + n);
        }

        private void DeleteProfile()
        {
            string name = _activeProfile;
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "请先在下拉框里选中要删除的配置。", "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this, "确定删除配置「" + name + "」？其中的序列会一并删除。",
                    "游戏连点器", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            ProfileManager.Delete(name);

            string fresh = ProfileManager.EnsureAny();
            _activeProfile = fresh;
            AppConfig.ActiveConfigDir = ActiveProfileDir;
            _cfg = AppConfig.LoadFromDir(ActiveProfileDir);
            _cfg.ProfileName = fresh;
            _selPoint = -1;

            RefreshProfileList();
            LoadConfigIntoUi();
            RefreshPointList();
            ApplyLiveSettings();
            SyncProfileEditors();
            AppPaths.Log("删除配置 " + name);
        }

        private void OnGameProcEdited()
        {
            if (_pointEditing) return;
            string v = _txtGameProc.Text.Trim();
            if (_cfg.GameProcess == v) return;

            _cfg.GameProcess = v;
            _cfg.Save();
            AppPaths.Log("配置 " + (_activeProfile.Length > 0 ? _activeProfile : "默认") + " 绑定游戏进程: " + (v.Length > 0 ? v : "无"));
        }

        private void BrowseGameExe()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择游戏主程序（exe）";
                dlg.Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string exeName = Path.GetFileName(dlg.FileName);
                _txtGameProc.Text = exeName;
                AppPaths.Log("通过文件选择器绑定游戏进程: " + exeName);
            }
        }

        /// <summary>
        /// 前台窗口检测：检测到绑定的游戏在前台就自动切换配置。
        /// 进程名查询用 QueryFullProcessImageNameW（轻量，实测 0.03ms/次）。
        /// </summary>
        private void CheckForegroundProfile()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                if (pid == 0 || pid == _selfPid) return;

                string proc = GetProcessNameFast(pid);
                if (proc.Length == 0) return;

                ProfileManager.Item it = ProfileManager.MatchByProcess(proc);
                string want = it != null ? it.Name : "";

                if (want == _lastFgMatched) return;
                _lastFgMatched = want;

                if (it == null) return;

                if (string.Equals(it.Name, _activeProfile, StringComparison.OrdinalIgnoreCase)) return;

                _pointEditing = true;
                try
                {
                    try
                    {
                        CollectConfig();
                        AppConfig.SaveToDir(ActiveProfileDir, _cfg);
                    }
                    catch { }

                    _activeProfile = it.Name;
                    AppConfig.ActiveConfigDir = it.Dir;
                    _cfg = AppConfig.LoadFromDir(it.Dir);
                    _cfg.ProfileName = it.Name;
                    _selPoint = -1;

                    LoadConfigIntoUi();
                    RefreshPointList();
                    ApplyLiveSettings();
                    SyncProfileEditors();
                    AppPaths.Log("检测到游戏 " + proc + " 在前台，自动切换配置 → " + it.Name);
                    ShowBalloon("已切换到「" + it.Name + "」", "检测到 " + proc + " 在前台。");
                }
                finally { _pointEditing = false; }
            }
            catch { }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr proc, uint flags, StringBuilder name, ref uint size);
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static string GetProcessNameFast(uint pid)
        {
            IntPtr pm = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (pm == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(1024);
            uint size = 1024;
            bool ok = QueryFullProcessImageNameW(pm, 0, sb, ref size);
            CloseHandle(pm);
            if (!ok || size == 0) return "";

            string full = sb.ToString(0, (int)size);
            int i = full.LastIndexOf('\\');
            return i >= 0 ? full.Substring(i + 1) : full;
        }

        private string PromptInput(string title, string label, string preset)
        {
            Form f = new Form();
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = false; f.MaximizeBox = false;
            f.ClientSize = new Size(380, 116);
            f.Font = _font;

            Label l = new Label { Text = label, Left = 12, Top = 12, Width = 356 };
            TextBox tb = new TextBox { Left = 12, Top = 36, Width = 356, Text = preset };
            Button ok = new Button { Text = "确定", Left = 196, Top = 72, Width = 80, DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "取消", Left = 288, Top = 72, Width = 80, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;

            DialogResult r = f.ShowDialog(this);
            string v = r == DialogResult.OK ? tb.Text.Trim() : null;
            f.Dispose();
            return v;
        }

        // =========================================================
        //  托盘菜单
        // =========================================================



        /// <summary>线程安全地投递到 UI 线程执行。</summary>
        private void SafeUi(Action action)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }

        // ---- 触发键捕获状态 ----

        /// <summary>触发键捕获开始（点「按下以设置按键」按钮）。</summary>
        private void StartTriggerCapture()
        {
            _capturing = true;
            _hook.BeginCapture(CaptureKind.TriggerKey);
            _btnCapture.Text = "按下以设置按键…";
            Activate();
            BringToFront();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            bool userClose = (e.CloseReason == CloseReason.UserClosing);

            if (userClose && !_reallyExit && _cfg.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            try
            {
                CollectConfig();
                if (!Program.SelfTestMode) SaveWindowSize();
                _cfg.Save();
                ApplyStartupRegistration();
            }
            catch { }

            Cleanup();
        }

        // =========================================================
        //  自检：渲染界面截图 + 输出布局报告（--selftest）
        // =========================================================

        internal void RunSelfTest(string outDir)
        {
            try
            {
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                RefreshLiveStatus();

                ShowInTaskbar = true;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(40, 40);
                Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(700);
                Application.DoEvents();
                Refresh();
                Application.DoEvents();

                StringBuilder report = new StringBuilder();
                report.AppendLine("DeviceDpi = " + DeviceDpi);
                Rectangle waDbg = Screen.FromControl(this).WorkingArea;
                report.AppendLine("WorkingArea = " + waDbg);
                report.AppendLine("PrimaryScreen.Bounds = " + Screen.PrimaryScreen.Bounds);
                report.AppendLine("AutoScaleMode = " + AutoScaleMode);
                report.AppendLine("实际 DPI (GetDpiForWindow) = " + DpiInfo.OfWindow(Handle));
                report.AppendLine("实际 DPI (LOGPIXELSX) = " + DpiInfo.OfScreen());
                report.AppendLine("Form ClientSize = " + ClientSize.Width + " x " + ClientSize.Height);
                report.AppendLine();
                report.AppendLine("控件位置（相对父容器）：");
                DumpControls(this, "", report);

                File.WriteAllText(Path.Combine(outDir, "layout-report.txt"), report.ToString(), new UTF8Encoding(false));

                File.WriteAllText(Path.Combine(outDir, "layout-check.txt"),
                    LayoutCheck(), new UTF8Encoding(false));

                using (Bitmap bmp = new Bitmap(Width, Height))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        try { Native.PrintWindow(Handle, hdc, 2 /*PW_RENDERFULLCONTENT*/); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    bmp.Save(Path.Combine(outDir, "window.png"), System.Drawing.Imaging.ImageFormat.Png);
                }

                // 逐页截图：三个标签页各抓一张
                if (_tabs != null)
                {
                    for (int tIdx = 0; tIdx < _tabs.TabPages.Count; tIdx++)
                    {
                        _tabs.SelectedIndex = tIdx;
                        Application.DoEvents();
                        Refresh();
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(120);

                        using (Bitmap bmp = new Bitmap(Width, Height))
                        {
                            using (Graphics g = Graphics.FromImage(bmp))
                            {
                                IntPtr hdc = g.GetHdc();
                                try { Native.PrintWindow(Handle, hdc, 2); }
                                finally { g.ReleaseHdc(hdc); }
                            }
                            string name = _tabs.TabPages[tIdx].Text;
                            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                            bmp.Save(Path.Combine(outDir, "tab-" + tIdx + "-" + name + ".png"),
                                System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    _tabs.SelectedIndex = 0;
                    Application.DoEvents();
                }

                AppPaths.Log("自检完成，输出目录：" + outDir);
            }
            catch (Exception ex)
            {
                try { AppPaths.Log("自检异常: " + ex); } catch { }
                File.WriteAllText(Path.Combine(outDir, "selftest-error.txt"),
                    ex.ToString(), new UTF8Encoding(false));
            }
        }

        private void DumpControls(Control root, string indent, StringBuilder sb)
        {
            foreach (Control c in root.Controls)
            {
                sb.AppendLine(string.Format("{0}{1} {2}  [{3}  {4}x{5}] \"{6}\"",
                    indent, c.GetType().Name,
                    c.Visible ? "" : "(隐藏)",
                    c.Left, c.Top, c.Width, c.Height,
                    (c.Text ?? "").Length > 40 ? (c.Text ?? "").Substring(0, 40) + "…" : (c.Text ?? "")));
                DumpControls(c, indent + "  ", sb);
            }
        }

        private string LayoutCheck()
        {
            int issueCount = 0, hintCount = 0;
            StringBuilder problems = new StringBuilder();
            StringBuilder hints = new StringBuilder();

            // 简化版：检查文字截断与控件重叠（逐 Tab 页内检查）
            if (_tabs != null)
            {
                foreach (TabPage tp in _tabs.TabPages)
                {
                    foreach (Control c in tp.Controls)
                    {
                        if (c is GroupBox)
                        {
                            foreach (Control inner in c.Controls)
                            {
                                if (!inner.Visible) continue;
                                CheckTextFit(inner, problems, hints, ref issueCount, ref hintCount);
                                if (inner.Right > c.ClientSize.Width + 6)
                                {
                                    hintCount++;
                                    hints.AppendLine(string.Format("越出分组框 {0}px: {1} \"{2}\"",
                                        inner.Right - c.ClientSize.Width, inner.GetType().Name,
                                        (inner.Text ?? "").Length > 30 ? (inner.Text ?? "").Substring(0, 30) : (inner.Text ?? "")));
                                }
                            }
                        }
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(issueCount == 0
                ? "布局体检通过：无文字截断、无控件重叠。"
                : ("布局体检发现 " + issueCount + " 处问题（需修复）："));
            sb.AppendLine();
            if (issueCount > 0) sb.Append(problems);
            if (hintCount > 0)
            {
                sb.AppendLine("另有 " + hintCount + " 处提示（控件盒子略超出容器，但文字完整，不影响观感）：");
                sb.Append(hints);
            }
            sb.AppendLine();
            sb.AppendLine("内容区所需高度 = " + DefaultContentHeight() +
                          "，可视高度 = " + _tabs.Height);
            return sb.ToString();
        }

        private void CheckTextFit(Control c, StringBuilder problems, StringBuilder hints,
            ref int issueCount, ref int hintCount)
        {
            if (!c.Visible) return;
            if (c is GroupBox || c is ListView) return;   // 容器类不检查

            try
            {
                Size pref = TextRenderer.MeasureText(c.Text, c.Font);
                int pad = 22;
                if (c is Button) pad = 10;
                else if (c is GroupBox) pad = 18;
                else if (c is Label) pad = 2;
                int need = pref.Width + pad;
                if (need > c.Width + 2)
                {
                    issueCount++;
                    problems.AppendLine(string.Format("文字可能被截断: {0} \"{1}\" 需要约 {2}px，控件宽 {3}px（差 {4}px）",
                        c.GetType().Name, (c.Text ?? "").Length > 30 ? (c.Text ?? "").Substring(0, 30) : (c.Text ?? ""),
                        need, c.Width, need - c.Width));
                }
            }
            catch { }
        }


        /// <summary>把界面上所有控件的当前值收集进 _cfg（保存与实时应用共用）。</summary>
        private void CollectConfig()
        {
            if (_suppressEvents) return;

            _cfg.TriggerKind = _rbKeyboard != null && _rbKeyboard.Checked
                ? TriggerKind.Keyboard : TriggerKind.Mouse;
            _cfg.TriggerVk = _txtKey != null && _txtKey.Tag is int
                ? (int)_txtKey.Tag : _cfg.TriggerVk;
            _cfg.TriggerMouse = _cboTriggerMouse != null && _cboTriggerMouse.SelectedIndex >= 0
                ? _cboTriggerMouse.SelectedIndex : _cfg.TriggerMouse;
            _cfg.Mode = (TriggerMode)(_cboMode != null && _cboMode.SelectedIndex == 1 ? 1 : 0);
            _cfg.EmergencyVk = _cboEmergency != null && _cboEmergency.SelectedIndex >= 0
                ? EmergencyVks[Clamp(0, EmergencyVks.Length - 1, _cboEmergency.SelectedIndex)] : _cfg.EmergencyVk;
            _cfg.SwallowTrigger = _chkSwallow != null ? _chkSwallow.Checked : _cfg.SwallowTrigger;

            _cfg.IntervalMs = _numInterval != null ? (int)_numInterval.Value : _cfg.IntervalMs;
            _cfg.Button = (ClickButton)Math.Max(0, _cboButton != null ? _cboButton.SelectedIndex : 0);
            _cfg.HoldMs = _numHold != null ? (int)_numHold.Value : _cfg.HoldMs;
            _cfg.FollowCursor = _rbFollow != null ? _rbFollow.Checked : _cfg.FollowCursor;
            _cfg.TrackPixels = _chkTrackPixels != null ? _chkTrackPixels.Checked : _cfg.TrackPixels;

            _cfg.PointHotkeySwallow = _chkPointSwallow != null ? _chkPointSwallow.Checked : _cfg.PointHotkeySwallow;

            _cfg.AutoStartWithWindows = _chkAutoStart != null ? _chkAutoStart.Checked : _cfg.AutoStartWithWindows;
            _cfg.MinimizeToTrayOnClose = _chkTrayOnClose != null ? _chkTrayOnClose.Checked : _cfg.MinimizeToTrayOnClose;
            _cfg.StartMinimized = _chkStartMin != null ? _chkStartMin.Checked : _cfg.StartMinimized;
            _cfg.ShowBalloonTips = _chkBalloon != null ? _chkBalloon.Checked : _cfg.ShowBalloonTips;
            _cfg.RunAsAdmin = _chkRunAsAdmin != null ? _chkRunAsAdmin.Checked : _cfg.RunAsAdmin;

            AppConfig.Sanitize(_cfg);
        }

        /// <summary>
        /// 按各标签页实际内容高度设置滚动：内容不满一页就关掉滚动条，
        /// 超出一页才允许滚动。窗口尺寸变化后也要重算。
        /// </summary>
        private void FitTabPages()
        {
            if (_tabs == null) return;
            foreach (TabPage tp in _tabs.TabPages)
            {
                int bottom = 0;
                foreach (Control c in tp.Controls)
                {
                    int b = c.Top + c.Height;
                    if (b > bottom) bottom = b;
                }
                int needed = bottom + 20;
                int viewH = _tabs.Height - _tabs.ItemSize.Height - 18;
                tp.AutoScroll = needed > viewH;
                if (tp.AutoScroll)
                {
                    tp.AutoScrollMinSize = new Size(0, needed);
                }
            }
        }

        /// <summary>开始捕获触发键（窗体层的辅助，由设置按钮调用）。</summary>
        private void BeginTriggerCapture()
        {
            _capturing = true;
            _hook.BeginCapture(CaptureKind.TriggerKey);
            Activate();
            BringToFront();
        }

        private bool _cleaned;

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            if (_engine.IsActive) _engine.Stop();
            try { if (_uiTimer != null) _uiTimer.Stop(); } catch { }
            try { if (_sizeSaveTimer != null) { _sizeSaveTimer.Stop(); _sizeSaveTimer.Dispose(); } } catch { }
            try { if (_autoSaveTimer != null) { _autoSaveTimer.Stop(); _autoSaveTimer.Dispose(); } } catch { }
            try { if (_statusBanner != null) { _statusBanner.Close(); _statusBanner.Dispose(); } } catch { }
            try { _engine.Dispose(); } catch { }
            try { _hook.Dispose(); } catch { }
            try { Native.TimeEndPeriod(1); } catch { }
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                    _tray = null;
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Cleanup();
            Application.ExitThread();
        }
    }          // MainForm 类结束


    // =========================================================
    //  程序入口
    // =========================================================

    internal static class Program
    {
        private const string MutexName = "Local\\GameClicker_SingleInstance_v1";
        private const string ShowEventName = "Local\\GameClicker_ShowWindow_v1";

        /// <summary>由命令行 --tray / --minimized 控制，启动即隐藏到托盘。</summary>
        public static bool StartHidden;

        /// <summary>自检模式：渲染界面并截图，不受用户配置里的自动隐藏/自动退出影响。</summary>
        public static bool SelfTestMode;

        [STAThread]
        private static void Main(string[] args)
        {
            bool selfTest = false;
            string selfTestDir = null;

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase) ||
                             a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) StartHidden = true;
                    else if (a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                    {
                        selfTest = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) selfTestDir = args[i + 1];
                    }
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- 按设置决定是否提权 ----
            // 必须放在获取单实例互斥体之前：否则未提权的进程会先占住互斥体，
            // 提权后的新进程反而被判定为"已有实例"而直接退出。
            // 自检模式不提权（避免自动化测试被 UAC 卡住）。
            if (!selfTest)
            {
                AppConfig bootCfg = AppConfig.Load();
                if (Elevation.ShouldElevate(bootCfg, args, selfTest))
                {
                    string elevErr;
                    if (Elevation.RelaunchElevated(args, out elevErr))
                        return;   // 提权进程已接管，本进程退出

                    // 提权失败或用户取消：继续以普通权限运行，但要说清楚后果
                    MessageBox.Show(
                        "已设置为默认以管理员身份运行，但本次未能提权：" + Environment.NewLine +
                        elevErr + Environment.NewLine + Environment.NewLine +
                        "程序将以普通权限继续运行。这种情况下，如果游戏本身以管理员权限运行，" + Environment.NewLine +
                        "Windows 会静默丢弃连点器发出的点击（表现为点了没反应）。",
                        "游戏连点器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            bool createdNew;
            Mutex mutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                // 已有实例在跑：把它唤到前台，然后自己退出。
                // 静默唤起（守护/开机自启）时不允许弹窗，见 SignalExistingInstance 说明。
                SignalExistingInstance(!StartHidden);
                return;
            }

            try
            {
                AppPaths.Log("=== 启动 v" + AppInfo.Version + " ===");

                if (selfTest)
                {
                    if (string.IsNullOrEmpty(selfTestDir)) selfTestDir = Path.Combine(AppPaths.BaseDir, "selftest");
                    SelfTestMode = true;
                    using (MainForm f = new MainForm())
                    {
                        f.RunSelfTest(selfTestDir);
                    }
                    AppPaths.Log("自检模式结束");
                    return;
                }

                MainForm form = new MainForm();
                StartShowListener();

                Application.Run(form);
            }
            catch (Exception ex)
            {
                AppPaths.Log("致命错误: " + ex);
                MessageBox.Show("程序启动失败：" + ex.Message, "游戏连点器",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        /// <summary>监听"显示窗口"信号，实现再次双击 exe 时唤出已有窗口。</summary>
        private static void StartShowListener()
        {
            try
            {
                EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                Thread t = new Thread(delegate ()
                {
                    while (true)
                    {
                        try
                        {
                            ev.WaitOne();
                            MainForm target = null;
                            foreach (Form f in Application.OpenForms)
                            {
                                target = f as MainForm;
                                if (target != null) break;
                            }
                            if (target == null) continue;
                            try
                            {
                                target.Invoke((MethodInvoker)delegate
                                {
                                    target.Show();
                                    target.WindowState = FormWindowState.Normal;
                                    target.ShowInTaskbar = true;
                                    target.Activate();
                                    target.BringToFront();
                                });
                            }
                            catch { }
                        }
                        catch { return; }
                    }
                });
                t.IsBackground = true;
                t.Name = "GameClicker.ShowListener";
                t.Start();
            }
            catch (Exception ex)
            {
                AppPaths.Log("显示信号监听启动失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 通知已有实例把自己显示出来。
        /// allowDialog=false 用于被守护进程/开机自启静默唤起的场景：绝不能弹模态框，
        /// 否则该进程会一直挂着，并占住命名互斥体，使后续"实例是否存活"的判断永远为真。
        /// </summary>
        private static void SignalExistingInstance(bool allowDialog)
        {
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out ev))
                {
                    using (ev)
                    {
                        ev.Set();
                        return;
                    }
                }
            }
            catch { }

            if (allowDialog)
            {
                MessageBox.Show("游戏连点器已经在运行了。请查看任务栏右下角的托盘图标。",
                    "游戏连点器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppPaths.Log("已有实例在运行，静默退出（不弹窗）");
            }
        }
    }

    internal static class AppInfo
    {
        public const string Version = "1.0.0";
    }

    // =========================================================
    //  托盘图标（运行时绘制，无需外部资源文件）
    // =========================================================

    internal static class TrayIconFactory
    {
        public static Icon CreateIcon(int size)
        {
            try
            {
                using (Bitmap bmp = new Bitmap(size, size))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);

                        float s = size;
                        RectangleF body = new RectangleF(s * 0.08f, s * 0.08f, s * 0.84f, s * 0.84f);

                        using (LinearGradientBrush bg = new LinearGradientBrush(body,
                            Color.FromArgb(74, 132, 244), Color.FromArgb(38, 78, 188), 55f))
                        {
                            g.FillEllipse(bg, body);
                        }

                        float ring = Math.Max(1f, s * 0.055f);
                        using (Pen p = new Pen(Color.FromArgb(255, 255, 255), ring))
                        {
                            g.DrawEllipse(p, new RectangleF(body.X + ring / 2, body.Y + ring / 2,
                                body.Width - ring, body.Height - ring));
                        }

                        PointF[] tri = new PointF[]
                        {
                            new PointF(s * 0.36f, s * 0.28f),
                            new PointF(s * 0.68f, s * 0.56f),
                            new PointF(s * 0.51f, s * 0.59f),
                            new PointF(s * 0.45f, s * 0.76f),
                        };
                        using (SolidBrush wb = new SolidBrush(Color.White))
                        {
                            g.FillPolygon(wb, tri);
                        }
                    }

                    IntPtr h = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                        {
                            return (Icon)tmp.Clone();
                        }
                    }
                    finally
                    {
                        Native.DestroyIcon(h);
                    }
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }
    }
}