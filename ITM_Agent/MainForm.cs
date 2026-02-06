// ITM_Agent/MainForm.cs
using ITM_Agent.Services;
using ITM_Agent.Startup;
using ITM_Agent.ucPanel;
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ITM_Agent
{
    public partial class MainForm : Form
    {
        private bool isExiting = false;
        private SettingsManager settingsManager;
        private LogManager logManager;
        private FileWatcherManager fileWatcherManager;
        private EqpidManager eqpidManager;
        private InfoRetentionCleaner infoCleaner;
        private LampLifeService lampLifeService;

        // ConfigUpdateService 필드
        private ConfigUpdateService configUpdateService;

        // 서버 연결 상태 관리자
        private ServerConnectionManager serverConnectionManager;

        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem titleItem;
        private ToolStripMenuItem runItem;
        private ToolStripMenuItem stopItem;
        private ToolStripMenuItem quitItem;

        internal static string VersionInfo
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                    return $"v{fvi.FileVersion}";
                }
                catch
                {
                    return "vUnknown";
                }
            }
        }

        ucPanel.ucConfigurationPanel ucSc1;

        private ucConfigurationPanel ucConfigPanel;
        private ucOverrideNamesPanel ucOverrideNamesPanel;
        private ucImageTransPanel ucImageTransPanel;

        private bool isRunning = false; // 현재 상태 플래그
        private bool isDebugMode = false;   // 디버그 모드 상태
        private ucOptionPanel ucOptionPanel;  // ← 옵션 패널

        private ucUploadPanel ucUploadPanel;
        private ucPluginPanel ucPluginPanel;
        private ucLampLifePanel ucLampLifePanel;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            PerformanceWarmUp.Run();
        }

        public MainForm(SettingsManager settingsManager)
        {
            this.settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            this.settingsManager = settingsManager;

            InitializeComponent();

            this.HandleCreated += (sender, e) => UpdateMainStatus("Stopped", Color.Red);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logManager = new LogManager(baseDir);

            lampLifeService = new LampLifeService(this.settingsManager, this.logManager, this);

            InitializeUserControls();
            RegisterMenuEvents();

            ucImageTransPanel.ImageSaveFolderChanged += ucUploadPanel.LoadImageSaveFolder_PathChanged;

            ucSc1 = new ucPanel.ucConfigurationPanel(settingsManager);
            ucOverrideNamesPanel = new ucOverrideNamesPanel(settingsManager, this.ucConfigPanel, this.logManager, this.settingsManager.IsDebugMode);

            fileWatcherManager = new FileWatcherManager(settingsManager, logManager, isDebugMode);

            eqpidManager = new EqpidManager(settingsManager, logManager, VersionInfo);
            eqpidManager.InitializeEqpid();

            string eqpid = settingsManager.GetEqpid();
            if (!string.IsNullOrEmpty(eqpid))
            {
                ProceedWithMainFunctionality(eqpid);
                configUpdateService = new ConfigUpdateService(settingsManager, logManager, this, eqpid);
            }

            infoCleaner = new InfoRetentionCleaner(settingsManager);

            // ServerConnectionManager 초기화 및 이벤트 구독
            serverConnectionManager = new ServerConnectionManager(logManager);
            serverConnectionManager.ConnectionStatusChanged += OnServerConnectionStatusChanged;

            SetFormIcon();

            this.Text = $"ITM Agent - {VersionInfo}";
            this.MaximizeBox = false;

            InitializeTrayIcon();
            this.FormClosing += MainForm_FormClosing;

            fileWatcherManager.InitializeWatchers();

            btn_Run.Click += btn_Run_Click;
            btn_Stop.Click += btn_Stop_Click;

            UpdateUIBasedOnSettings();
        }

        // [수정] 서버 연결 상태 변경 핸들러
        private void OnServerConnectionStatusChanged(bool isConnected, bool isDbOk, bool isFtpOk, string message)
        {
            // UI 스레드에서 실행 보장
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => OnServerConnectionStatusChanged(isConnected, isDbOk, isFtpOk, message)));
                return;
            }

            logManager.LogEvent($"[MainForm] Server Status Update: DB={isDbOk}, API={isFtpOk} ({message})");

            // ucOptionPanel의 상태 표시등 즉시 동기화
            ucOptionPanel?.SetDirectConnectionStatus(isDbOk, isFtpOk);

            // ─────────────────────────────────────────────────────────────
            // [개선 1] DB 의존 기능 독립 제어 (성능 정보, 램프 수명)
            // ─────────────────────────────────────────────────────────────
            if (isDbOk)
            {
                // DB가 정상이면 성능 데이터 수집 즉시 재개 (API 상태 무관)
                PerformanceDbWriter.Start(lb_eqpid.Text, eqpidManager);

                // 램프 서비스 재개 (자동 복구 시 UI 자동화는 건너뜀)
                lampLifeService?.Start(true);
            }
            else
            {
                // DB가 끊겼을 때만 중단
                PerformanceDbWriter.Stop();
                lampLifeService?.Stop();
            }

            // ─────────────────────────────────────────────────────────────
            // [개선 2] 파일 업로드 의존 기능 (파일 감시, API 필요)
            // ─────────────────────────────────────────────────────────────
            // 파일 업로드는 DB와 API가 모두 살아야 의미가 있음 (기존 isConnected 유지)
            if (isConnected)
            {
                // 상태 메시지가 이미 Running인 경우 중복 갱신 방지
                if (ts_Status.Text != "Running (Recovered)")
                {
                    UpdateMainStatus("Running (Recovered)", Color.Blue);
                    
                    // 1. 파일 감시 재개
                    fileWatcherManager?.ResumeWatching();
                    ucUploadPanel?.ResumeWatching();

                    // 2. 누락 파일 복구 스캔 (비동기) - Slow Recovery
                    Task.Run(() => fileWatcherManager?.StartRecoveryScan());
                }
            }
            else
            {
                // API나 DB 중 하나라도 안 되면 파일 처리는 보류
                if (!statusStrip1.Text.StartsWith("Holding"))
                {
                    UpdateMainStatus("Holding (Unstable Connection)", Color.Red);
                    
                    // 1. 파일 감시 일시 정지
                    fileWatcherManager?.PauseWatching();
                    ucUploadPanel?.PauseWatching();
                }
            }
        }

        private void SetFormIcon()
        {
            this.Icon = new Icon(@"Resources\Icons\icon.ico");
        }

        private void ProceedWithMainFunctionality(string eqpid)
        {
            lb_eqpid.Text = $"Eqpid: {eqpid}";
        }

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();

            titleItem = new ToolStripMenuItem(this.Text);
            titleItem.Click += (sender, e) => RestoreMainForm();
            trayMenu.Items.Add(titleItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            runItem = new ToolStripMenuItem("Run", null, Tray_Run_Click);
            stopItem = new ToolStripMenuItem("Stop", null, Tray_Stop_Click);
            quitItem = new ToolStripMenuItem("Quit", null, Tray_Quit_Click);

            trayMenu.Items.AddRange(new ToolStripItem[] { runItem, stopItem, quitItem });

            trayIcon = new NotifyIcon
            {
                Icon = new Icon(@"Resources\Icons\icon.ico"),
                ContextMenuStrip = trayMenu,
                Visible = true,
                Text = this.Text
            };
            trayIcon.DoubleClick += (sender, e) => RestoreMainForm();
        }

        private void Tray_Run_Click(object sender, EventArgs e)
        {
            if (btn_Run.Enabled)
                btn_Run_Click(sender, e);
        }

        private void Tray_Stop_Click(object sender, EventArgs e)
        {
            if (btn_Stop.Enabled)
                btn_Stop_Click(sender, e);
        }

        private void Tray_Quit_Click(object sender, EventArgs e)
        {
            if (btn_Quit.Enabled)
                btn_Quit_Click(sender, e);
        }

        private void RestoreMainForm()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            titleItem.Enabled = false;
        }

        private void UpdateTrayMenuStatus()
        {
            if (runItem != null) runItem.Enabled = btn_Run.Enabled;
            if (stopItem != null) stopItem.Enabled = btn_Stop.Enabled;
            if (quitItem != null) quitItem.Enabled = btn_Quit.Enabled;
        }

        public void ShowTemporarilyForAutomation()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => ShowTemporarilyForAutomation()));
                return;
            }

            this.TopMost = true;

            if (!this.Visible)
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
            }

            this.Activate();
            this.BringToFront();
        }

        public void HideToTrayAfterAutomation()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => HideToTrayAfterAutomation()));
                return;
            }
            this.Hide();
            this.TopMost = false;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !isExiting)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.BalloonTipTitle = "ITM Agent";
                trayIcon.BalloonTipText = "ITM Agent가 백그라운드에서 실행 중입니다.";
                trayIcon.ShowBalloonTip(3000);
                return;
            }

            if (!isExiting)
            {
                e.Cancel = true;
                isExiting = true;
                PerformQuit();
            }
        }

        private void UpdateUIBasedOnSettings()
        {
            if (settingsManager.IsReadyToRun())
            {
                UpdateMainStatus("Ready to Run", Color.Green);
                btn_Run.Enabled = true;
            }
            else
            {
                UpdateMainStatus("Stopped!", Color.Red);
                btn_Run.Enabled = false;
            }
            btn_Stop.Enabled = false;
            btn_Quit.Enabled = true;
        }

        private void UpdateMainStatus(string status, Color color)
        {
            ts_Status.Text = status;
            ts_Status.ForeColor = color;

            // [중요] Holding 상태도 'Running'의 일종(실행 중 대기)으로 취급하여 UI를 잠급니다.
            bool isActiveRunning = status.StartsWith("Running") || status.StartsWith("Holding");

            ucOverrideNamesPanel?.UpdateStatus(status);
            ucConfigPanel?.UpdateStatusOnRun(isActiveRunning);
            ucOverrideNamesPanel?.UpdateStatusOnRun(isActiveRunning);
            ucImageTransPanel?.UpdateStatusOnRun(isActiveRunning);
            ucUploadPanel?.UpdateStatusOnRun(isActiveRunning);
            ucPluginPanel?.UpdateStatusOnRun(isActiveRunning);
            ucOptionPanel?.UpdateStatusOnRun(isActiveRunning);
            ucLampLifePanel?.UpdateStatusOnRun(isActiveRunning);

            logManager.LogEvent($"Status updated to: {status}");
            if (isDebugMode)
                logManager.LogDebug($"Status updated to: {status}. Running state: {isActiveRunning}");

            if (status == "Stopped!")
            {
                btn_Run.Enabled = false;
                btn_Stop.Enabled = false;
                btn_Quit.Enabled = true;
            }
            else if (status == "Ready to Run")
            {
                btn_Run.Enabled = true;
                btn_Stop.Enabled = false;
                btn_Quit.Enabled = true;
            }
            else if (isActiveRunning) // Running, Recovered, or Holding
            {
                btn_Run.Enabled = false;
                btn_Stop.Enabled = true; // 멈출 수는 있어야 함
                btn_Quit.Enabled = false;
            }
            else
            {
                btn_Run.Enabled = false;
                btn_Stop.Enabled = false;
                btn_Quit.Enabled = false;
            }

            UpdateTrayMenuStatus();
            UpdateMenuItemsState(isActiveRunning); // 메뉴도 비활성화
            UpdateButtonsState();
        }

        private void UpdateMenuItemsState(bool isRunning)
        {
            if (menuStrip1 != null)
            {
                foreach (ToolStripMenuItem item in menuStrip1.Items)
                {
                    if (item.Text == "File")
                    {
                        foreach (ToolStripItem subItem in item.DropDownItems)
                        {
                            if (subItem.Text == "New" || subItem.Text == "Open" || subItem.Text == "Quit")
                            {
                                subItem.Enabled = !isRunning;
                            }
                        }
                    }
                }
            }
        }

        private void btn_Run_Click(object sender, EventArgs e)
        {
            logManager.LogEvent("Run button clicked.");
            PerformRunLogic();
        }

        private void PerformRunLogic()
        {
            if (ucUploadPanel != null)
            {
                string validationError;
                if (ucUploadPanel.HasInvalidRules(out validationError))
                {
                    MessageBox.Show($"실행할 수 없습니다. Upload 패널 설정을 확인하세요.\n\n{validationError}",
                                    "실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    logManager.LogEvent($"Run blocked: {validationError}");
                    return;
                }
            }

            try
            {
                if (eqpidManager != null)
                {
                    eqpidManager.InitializeEqpid();
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"Error updating agent_info during Run logic: {ex.Message}");
            }

            try
            {
                fileWatcherManager.StartWatching();
                ucUploadPanel?.UpdateStatusOnRun(true);

                PerformanceDbWriter.Start(lb_eqpid.Text, this.eqpidManager);

                // [수정] 수동 실행 시에는 UI 자동화를 수행함 (false)
                lampLifeService.Start(false);

                // 서버 모니터링 시작
                serverConnectionManager.Start();

                isRunning = true;
                UpdateMainStatus("Running...", Color.Blue);

                if (isDebugMode)
                {
                    logManager.LogDebug("FileWatcherManager and ucUploadPanel Watchers started successfully.");
                }

                if (settingsManager.AutoRunOnStart)
                {
                    logManager.LogEvent("[MainForm] AutoRun successful. Resetting flag and confirming update.");
                    settingsManager.AutoRunOnStart = false;

                    if (configUpdateService != null)
                    {
                        _ = configUpdateService.ConfirmUpdateSuccessAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"Error starting monitoring: {ex.Message}");
                UpdateMainStatus("Stopped!", Color.Red);
            }
        }

        private void btn_Stop_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "프로그램을 중지하시겠습니까?\n모든 파일 감시 및 업로드 기능이 중단됩니다.",
                "작업 중지 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
            {
                logManager.LogEvent("Stop button clicked and confirmed.");
                PerformStopLogic();
            }
            else
            {
                logManager.LogEvent("Stop action was canceled by the user.");
            }
        }

        private void PerformStopLogic()
        {
            try
            {
                // 서버 모니터링 중지
                serverConnectionManager.Stop();

                fileWatcherManager.StopWatchers();
                ucUploadPanel?.UpdateStatusOnRun(false);

                PerformanceDbWriter.Stop();
                lampLifeService.Stop();

                isRunning = false;

                bool isReady = ucConfigPanel?.IsReadyToRun() ?? false;
                UpdateMainStatus(isReady ? "Ready to Run" : "Stopped!",
                                 isReady ? Color.Green : Color.Red);

                if (isDebugMode)
                    logManager.LogDebug("FileWatcherManager & ucUploadPanel Watchers stopped successfully.");
            }
            catch (Exception ex)
            {
                logManager.LogError($"Error stopping processes: {ex.Message}");
                UpdateMainStatus("Error Stopping!", Color.Red);
            }
        }

        private void UpdateButtonsState()
        {
            UpdateTrayMenuStatus();
        }

        private void btn_Quit_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "프로그램을 완전히 종료하시겠습니까?",
                "종료 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                PerformQuit();
            }
        }

        private void PerformQuit()
        {
            logManager?.LogEvent("[MainForm] Quit requested.");

            try
            {
                fileWatcherManager?.StopWatchers();
                fileWatcherManager = null;

                lampLifeService?.Stop();
                lampLifeService = null;

                configUpdateService?.Dispose();
                configUpdateService = null;

                // 모니터링 매니저 정리
                serverConnectionManager?.Dispose();
                serverConnectionManager = null;

                infoCleaner?.Dispose();
                infoCleaner = null;

                PerformanceDbWriter.Stop();

                settingsManager = null;
            }
            catch (Exception ex)
            {
                logManager?.LogError($"[MainForm] Clean-up error during service stop: {ex}");
            }

            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
                trayMenu?.Dispose();
                trayMenu = null;
            }
            catch (Exception ex)
            {
                logManager?.LogError($"[MainForm] Tray clean-up error: {ex}");
            }

            try { infoCleaner?.Dispose(); } catch { /* ignore */ }

            BeginInvoke(new Action(() =>
            {
                logManager?.LogEvent("[MainForm] Application.Exit invoked.");
                Application.Exit();
            }));
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            pMain.Controls.Add(ucSc1);
            UpdateMenusBasedOnType();

            ShowUserControl(ucConfigPanel);

            bool isReady = ucConfigPanel.IsReadyToRun();

            if (settingsManager.AutoRunOnStart && isReady)
            {
                logManager.LogEvent("[MainForm] AutoRunOnStart=1 detected on load. Starting Run logic...");
                this.BeginInvoke(new Action(() => {
                    PerformRunLogic();
                }));
            }
            else
            {
                if (isReady)
                {
                    UpdateMainStatus("Ready to Run", Color.Green);
                }
                else
                {
                    UpdateMainStatus("Stopped!", Color.Red);
                }
            }
        }

        private void RefreshUI()
        {
            string eqpid = settingsManager.GetEqpid();
            lb_eqpid.Text = $"Eqpid: {eqpid}";

            ucSc1.RefreshUI();
            ucConfigPanel?.RefreshUI();
            ucOverrideNamesPanel?.RefreshUI();

            UpdateUIBasedOnSettings();
        }

        private void NewMenuItem_Click(object sender, EventArgs e)
        {
            settingsManager.ResetExceptEqpid();
            MessageBox.Show("Settings 초기화 완료 (Eqpid 제외)", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshUI();
        }

        private void OpenMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        settingsManager.LoadFromFile(openFileDialog.FileName);
                        MessageBox.Show("새로운 Settings.ini 파일이 로드되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);

                        RefreshUI();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"파일 로드 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void SaveAsMenuItem_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "INI files (*.ini)|*.ini|All files (*.*)|*.*";
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        settingsManager.SaveToFile(saveFileDialog.FileName);
                        MessageBox.Show("Settings.ini가 저장되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"파일 저장 중 오류 발생: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void QuitMenuItem_Click(object sender, EventArgs e)
        {
            btn_Quit.PerformClick();
        }

        private void InitializeUserControls()
        {
            ucConfigPanel = new ucConfigurationPanel(settingsManager);
            ucPluginPanel = new ucPluginPanel(settingsManager);
            ucOverrideNamesPanel = new ucOverrideNamesPanel(
                settingsManager, ucConfigPanel, logManager, settingsManager.IsDebugMode);
            ucImageTransPanel = new ucImageTransPanel(settingsManager, ucConfigPanel);
            ucUploadPanel = new ucUploadPanel(
                ucConfigPanel, ucPluginPanel, settingsManager, ucOverrideNamesPanel, ucImageTransPanel);
            ucOptionPanel = new ucOptionPanel(settingsManager);
            ucOptionPanel.DebugModeChanged += OptionPanel_DebugModeChanged;
            ucLampLifePanel = new ucLampLifePanel(settingsManager, lampLifeService);

            this.Controls.Add(ucOverrideNamesPanel);

            ucConfigPanel.InitializePanel(isRunning);
            ucOverrideNamesPanel.InitializePanel(isRunning);
            ucPluginPanel.InitializePanel(isRunning);
            ucOptionPanel.InitializePanel(isRunning);
        }

        private void RegisterMenuEvents()
        {
            tsm_Categorize.Click += (s, e) => ShowUserControl(ucConfigPanel);
            tsm_OverrideNames.Click += (s, e) => ShowUserControl(ucOverrideNamesPanel);
            tsm_ImageTrans.Click += (s, e) => ShowUserControl(ucImageTransPanel);
            tsm_UploadData.Click += (s, e) => ShowUserControl(ucUploadPanel);
            tsm_LampLifeCollector.Click += (s, e) => ShowUserControl(ucLampLifePanel);
            tsm_PluginList.Click += (s, e) => ShowUserControl(ucPluginPanel);
            tsm_Option.Click += (s, e) => ShowUserControl(ucOptionPanel);
            tsm_AboutInfo.Click += tsm_AboutInfo_Click;
        }

        private void OptionPanel_DebugModeChanged(bool isDebug)
        {
            isDebugMode = isDebug;
            fileWatcherManager.UpdateDebugMode(isDebugMode);

            if (isDebugMode)
            {
                logManager.LogEvent("Debug Mode: Enabled");
                logManager.LogDebug("debug mode enabled.");
            }
            else
            {
                logManager.LogEvent("Debug Mode: Disabled");
                logManager.LogDebug("debug mode disabled.");
            }
        }

        private void ShowUserControl(UserControl control)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(control);
            control.Dock = DockStyle.Fill;

            // [수정] 각 패널의 상태 초기화 (메뉴 이동 시 잠금 풀림 방지)
            if (control is ucConfigurationPanel cfg) cfg.InitializePanel(isRunning);
            else if (control is ucOverrideNamesPanel ov) ov.InitializePanel(isRunning);
            else if (control is ucPluginPanel plg) plg.InitializePanel(isRunning);
            else if (control is ucOptionPanel opt) opt.InitializePanel(isRunning);
            else if (control is ucUploadPanel upload) upload.InitializePanel(isRunning); // [추가] Upload 패널 초기화

            if (control == ucOptionPanel)
            {
                ucOptionPanel.ActivatePanel();
            }
            else
            {
                ucOptionPanel?.DeactivatePanel();
            }
        }

        private void UpdateMenusBasedOnType()
        {
            string type = settingsManager.GetEqpType();
            if (type == "ONTO")
            {
                tsm_Nova.Visible = false;
                tsm_Onto.Visible = true;
            }
            else if (type == "NOVA")
            {
                tsm_Onto.Visible = false;
                tsm_Nova.Visible = true;
            }
            else
            {
                tsm_Onto.Visible = false;
                tsm_Nova.Visible = false;
                return;
            }

            tsm_Onto.Visible = type.Equals("ONTO", StringComparison.OrdinalIgnoreCase);
            tsm_Nova.Visible = type.Equals("NOVA", StringComparison.OrdinalIgnoreCase);
        }

        private void InitializeMainMenu()
        {
            UpdateMenusBasedOnType();
        }

        public MainForm()
            : this(new SettingsManager(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini")))
        {
        }

        private void tsm_AboutInfo_Click(object sender, EventArgs e)
        {
            using (var dlg = new AboutInfoForm())
            {
                dlg.ShowDialog(this);
            }
        }

        public void TriggerRestartCycle()
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => TriggerRestartCycle()));
                return;
            }

            _ = RestartAsync();
        }

        private async Task RestartAsync()
        {
            logManager.LogEvent("[MainForm] RestartCycle triggered by ConfigUpdateService.");

            if (btn_Stop.Enabled)
            {
                logManager.LogEvent("[MainForm] Calling Stop logic...");
                PerformStopLogic();
            }

            logManager.LogEvent("[MainForm] Waiting 10 seconds before auto-run...");

            await Task.Delay(10000);

            if (btn_Run.Enabled)
            {
                logManager.LogEvent("[MainForm] Calling Run logic (AutoRun)...");
                PerformRunLogic();
            }
            else
            {
                logManager.LogEvent("[MainForm] AutoRun canceled (Button disabled or settings invalid).");
            }
        }
    }
}
