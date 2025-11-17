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
using System.Threading.Tasks; // (Task.Delay 사용)

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

        // ▼▼▼ [추가] ConfigUpdateService 필드 ▼▼▼
        private ConfigUpdateService configUpdateService;
        // ▲▲▲ [추가] 완료 ▲▲▲

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
                    // 현재 실행 중인 어셈블리의 파일 버전 정보를 가져옵니다.
                    var assembly = Assembly.GetExecutingAssembly();
                    var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                    // "v" 접두사를 붙여 반환합니다 (예: "v1.2.3.4")
                    return $"v{fvi.FileVersion}";
                }
                catch
                {
                    // 버전 정보를 가져오는 데 실패하면 기본값 반환
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

        // MainForm.cs 상단 (다른 user control 변수들과 함께)
        private ucUploadPanel ucUploadPanel;
        private ucPluginPanel ucPluginPanel;
        private ucLampLifePanel ucLampLifePanel;

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // ▼▼▼ [수정] DB 접속 준비가 완료된 후(생성자 이후) WarmUp 실행 ▼▼▼
            PerformanceWarmUp.Run();
        }

        public MainForm(SettingsManager settingsManager)
        {
            this.settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            this.settingsManager = settingsManager;

            InitializeComponent();

            // 폼 핸들이 생성된 직후 상태 표시(Stopped, 빨간색)
            this.HandleCreated += (sender, e) => UpdateMainStatus("Stopped", Color.Red);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            logManager = new LogManager(baseDir);

            lampLifeService = new LampLifeService(this.settingsManager, this.logManager, this);

            InitializeUserControls();
            RegisterMenuEvents();

            ucImageTransPanel.ImageSaveFolderChanged += ucUploadPanel.LoadImageSaveFolder_PathChanged;

            // 설정 패널
            ucSc1 = new ucPanel.ucConfigurationPanel(settingsManager);
            // Override Names 패널 (Designer에서 배치된 ucConfigPanel 컨트롤 인스턴스 전달)
            ucOverrideNamesPanel = new ucOverrideNamesPanel(settingsManager, this.ucConfigPanel, this.logManager, this.settingsManager.IsDebugMode);

            // FileWatcherManager 생성 (SettingsManager, LogManager, 디버그 모드 플래그)
            fileWatcherManager = new FileWatcherManager(settingsManager, logManager, isDebugMode);
            
            // ▼▼▼ [수정] EQPID 초기화 (이 시점에서 Connection.ini는 준비 완료됨) ▼▼▼
            eqpidManager = new EqpidManager(settingsManager, logManager, VersionInfo);
            eqpidManager.InitializeEqpid(); // (내부적으로 (구)DB의 agent_info 업데이트)
            
            string eqpid = settingsManager.GetEqpid();
            if (!string.IsNullOrEmpty(eqpid))
            {
                ProceedWithMainFunctionality(eqpid);

                // ▼▼▼ [추가] Eqpid 로드 후 ConfigUpdateService 시작 ▼▼▼
                // (MainForm, 즉 'this'를 전달하여 Stop/Run 사이클 트리거 가능)
                configUpdateService = new ConfigUpdateService(settingsManager, logManager, this, eqpid);
                // ▲▲▲ [추가] 완료 ▲▲▲
            }
            
            // 기존에 없던 InfoRetentionCleaner 즉시 실행
            infoCleaner = new InfoRetentionCleaner(settingsManager);

            // 아이콘 설정
            SetFormIcon();

            this.Text = $"ITM Agent - {VersionInfo}";
            this.MaximizeBox = false;

            InitializeTrayIcon();
            this.FormClosing += MainForm_FormClosing;
            
            fileWatcherManager.InitializeWatchers();

            btn_Run.Click += btn_Run_Click;
            btn_Stop.Click += btn_Stop_Click;

            UpdateUIBasedOnSettings();
            // infoCleaner = new InfoRetentionCleaner(settingsManager); // (중복 제거)
        }

        private void SetFormIcon()
        {
            // 제목줄 아이콘 설정
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

            /* ▼ Tray 전용 핸들러 연결 ▼ */
            runItem = new ToolStripMenuItem("Run", null, Tray_Run_Click);
            stopItem = new ToolStripMenuItem("Stop", null, Tray_Stop_Click);
            quitItem = new ToolStripMenuItem("Quit", null, Tray_Quit_Click);

            trayMenu.Items.AddRange(new ToolStripItem[] { runItem, stopItem, quitItem });

            trayIcon = new NotifyIcon
            {
                Icon = new Icon(@"Resources\Icons\icon.ico"), // TrayIcon에 사용할 아이콘
                ContextMenuStrip = trayMenu,
                Visible = true,
                Text = this.Text
            };
            trayIcon.DoubleClick += (sender, e) => RestoreMainForm();
        }

        private void Tray_Run_Click(object sender, EventArgs e)
        {
            if (btn_Run.Enabled)         // 안전 가드
                btn_Run_Click(sender, e);   // 내부 로직 직접 호출
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
            titleItem.Enabled = false;  // 트레이 메뉴 비활성화
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

            bool isRunning = status.StartsWith("Running");

            // --- 모든 UserControl에 상태 전달 ---------------------
            ucOverrideNamesPanel?.UpdateStatus(status);
            ucConfigPanel?.UpdateStatusOnRun(isRunning);
            ucOverrideNamesPanel?.UpdateStatusOnRun(isRunning);
            ucImageTransPanel?.UpdateStatusOnRun(isRunning);
            ucUploadPanel?.UpdateStatusOnRun(isRunning);
            ucPluginPanel?.UpdateStatusOnRun(isRunning);
            ucOptionPanel?.UpdateStatusOnRun(isRunning);
            ucLampLifePanel?.UpdateStatusOnRun(isRunning);

            logManager.LogEvent($"Status updated to: {status}");
            if (isDebugMode)
                logManager.LogDebug($"Status updated to: {status}. Running state: {isRunning}");

            /* ---------- 버튼 / 트레이 메뉴 활성화 ---------------- */
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
            else if (isRunning)
            {
                btn_Run.Enabled = false;
                btn_Stop.Enabled = true;
                btn_Quit.Enabled = false;
            }
            else
            {
                btn_Run.Enabled = false;
                btn_Stop.Enabled = false;
                btn_Quit.Enabled = false;
            }

            UpdateTrayMenuStatus();
            UpdateMenuItemsState(isRunning);
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
                                subItem.Enabled = !isRunning; // Running 상태에서 비활성화
                            }
                        }
                    }
                }
            }
        }

        // ▼▼▼ [수정] btn_Run_Click 로직 분리 ▼▼▼
        private void btn_Run_Click(object sender, EventArgs e)
        {
            logManager.LogEvent("Run button clicked.");
            PerformRunLogic();
        }

        /// <summary>
        /// Run 버튼 클릭 또는 자동 재시작 시 호출되는 핵심 Run 로직
        /// </summary>
        private void PerformRunLogic()
        {
            // [핵심 추가] Upload 패널 유효성 검사
            if (ucUploadPanel != null)
            {
                string validationError;
                if (ucUploadPanel.HasInvalidRules(out validationError))
                {
                    MessageBox.Show($"실행할 수 없습니다. Upload 패널 설정을 확인하세요.\n\n{validationError}",
                                    "실행 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    logManager.LogEvent($"Run blocked: {validationError}");
                    return; // 실행 중단
                }
            }

            try
            {
                /*──────── File Watcher (Type A) 시작 ────────*/
                fileWatcherManager.StartWatching();

                /*──────── ucUploadPanel (Type A & B) 시작 ────────*/
                ucUploadPanel?.UpdateStatusOnRun(true);

                PerformanceDbWriter.Start(lb_eqpid.Text, this.eqpidManager);
                lampLifeService.Start();

                isRunning = true; // 상태 업데이트
                UpdateMainStatus("Running...", Color.Blue);

                if (isDebugMode)
                {
                    logManager.LogDebug("FileWatcherManager and ucUploadPanel Watchers started successfully.");
                }

                // ▼▼▼ [추가] 1회성 AutoRun 플래그 초기화 및 완료 보고 ▼▼▼
                if (settingsManager.AutoRunOnStart)
                {
                    logManager.LogEvent("[MainForm] AutoRun successful. Resetting flag and confirming update.");
                    // (즉시 0으로 리셋)
                    settingsManager.AutoRunOnStart = false;

                    // (비동기) 신규 DB에 완료 보고
                    if (configUpdateService != null)
                    {
                        // (async void 메서드 호출이지만, Task 반환)
                        _ = configUpdateService.ConfirmUpdateSuccessAsync();
                    }
                }
                // ▲▲▲ [추가] 완료 ▲▲▲
            }
            catch (Exception ex)
            {
                logManager.LogError($"Error starting monitoring: {ex.Message}");
                UpdateMainStatus("Stopped!", Color.Red);
            }
        }
        // ▲▲▲ [수정] 완료 ▲▲▲


        // ▼▼▼ [수정] btn_Stop_Click 로직 분리 ▼▼▼
        private void btn_Stop_Click(object sender, EventArgs e)
        {
            // 경고창 표시 (사용자 클릭 시에만)
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

        /// <summary>
        /// Stop 버튼 클릭 또는 자동 재시작 시 호출되는 핵심 Stop 로직
        /// </summary>
        private void PerformStopLogic()
        {
            try
            {
                /*─ FileWatcher (Type A) + Performance 로깅 중지 ─*/
                fileWatcherManager.StopWatchers();

                /*─ ucUploadPanel (Type A & B) 중지 ─*/
                ucUploadPanel?.UpdateStatusOnRun(false);

                PerformanceDbWriter.Stop();
                lampLifeService.Stop();

                isRunning = false;

                /*─ 상태 표시 반영 ─*/
                bool isReady = ucConfigPanel?.IsReadyToRun() ?? false;
                UpdateMainStatus(isReady ? "Ready to Run" : "Stopped!",
                                 isReady ? Color.Green : Color.Red);

                /*─ 패널 동기화 ─*/
                // (UpdateMainStatus 내부에서 이미 호출됨)
            }
            catch (Exception ex)
            {
                logManager.LogError($"Error stopping processes: {ex.Message}");
                UpdateMainStatus("Error Stopping!", Color.Red);
            }
        }
        // ▲▲▲ [수정] 완료 ▲▲▲

        private void UpdateButtonsState()
        {
            UpdateTrayMenuStatus(); // Tray 아이콘 상태 업데이트
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

                // ▼▼▼ [추가] ConfigUpdateService 타이머 중지 ▼▼▼
                configUpdateService?.Dispose();
                configUpdateService = null;
                // ▲▲▲ [추가] 완료 ▲▲▲

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
            // (1) 폼 로드시 실행할 로직
            pMain.Controls.Add(ucSc1);
            UpdateMenusBasedOnType();   // 메뉴 상태 업데이트

            // (2) 초기 패널 설정 및 UserControl 상태 동기화
            ShowUserControl(ucConfigPanel); // 가장 먼저 ucConfigPanel 보여줌

            // (3) ucConfigurationPanel 에서 현재 Target/Folder/Regex 등이 모두 세팅되었는지 확인
            bool isReady = ucConfigPanel.IsReadyToRun();

            // ▼▼▼ [수정] AutoRunOnStart 플래그 확인 로직 추가 ▼▼▼
            if (settingsManager.AutoRunOnStart && isReady)
            {
                // (4.1) 자동 실행 플래그가 켜져 있으면 즉시 Run 호출
                logManager.LogEvent("[MainForm] AutoRunOnStart=1 detected on load. Starting Run logic...");
                // (지연을 주어 폼 로드가 완료되도록 함)
                this.BeginInvoke(new Action(() => {
                    PerformRunLogic();
                }));
            }
            else
            {
                // (4.2) 자동 실행이 아니면 기존 상태 로직 따름
                if (isReady)
                {
                    UpdateMainStatus("Ready to Run", Color.Green);
                }
                else
                {
                    UpdateMainStatus("Stopped!", Color.Red);
                }
            }
            // ▲▲▲ [수정] 완료 ▲▲▲
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
                logManager.LogDebug("Debug mode enabled.");
            }
            else
            {
                logManager.LogEvent("Debug Mode: Disabled");
                logManager.LogDebug("Debug mode disabled.");
            }
        }

        private void ShowUserControl(UserControl control)
        {
            pMain.Controls.Clear();
            pMain.Controls.Add(control);
            control.Dock = DockStyle.Fill;

            // 상태 동기화
            if (control is ucConfigurationPanel cfg) cfg.InitializePanel(isRunning);
            else if (control is ucOverrideNamesPanel ov) ov.InitializePanel(isRunning);
            else if (control is ucPluginPanel plg) plg.InitializePanel(isRunning);
            else if (control is ucOptionPanel opt) opt.InitializePanel(isRunning);
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

        // ▼▼▼ [추가] ConfigUpdateService가 호출할 공개 메서드 ▼▼▼
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
        // ▲▲▲ [추가] 완료 ▲▲▲
    }
}
