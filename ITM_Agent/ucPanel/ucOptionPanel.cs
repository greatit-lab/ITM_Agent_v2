// ITM_Agent/ucPanel/ucOptionPanel.cs
using System;
using System.Windows.Forms;
using ITM_Agent.Services;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using ConnectInfo;
using System.Net.Sockets;
using System.Net.Http; // [추가] HttpClient 사용
using System.Drawing.Drawing2D;
using System.Threading;

namespace ITM_Agent.ucPanel
{
    /// <summary>
    /// MenuStrip1 → tsm_Option 클릭 시 표시되는 옵션(UserControl)  
    /// Server Connection 상태는 이제 ServerConnectionManager에 의해 외부에서 제어됩니다.
    /// (수동 새로고침 시 HTTP Health Check 수행)
    /// </summary>
    public partial class ucOptionPanel : UserControl
    {
        private bool isRunning = false;
        private const string OptSection = "Option";
        private const string Key_PerfLog = "EnablePerfoLog";
        private const string Key_InfoAutoDel = "EnableInfoAutoDel";
        private const string Key_InfoRetention = "InfoRetentionDays";

        private readonly SettingsManager settingsManager;

        public event Action<bool> DebugModeChanged;

        // 동시 새로고침 방지 플래그
        private bool _isRefreshing = false;
        private readonly object _refreshLock = new object();

        // [추가] HTTP 클라이언트 (수동 체크용)
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private const int API_PORT = 8082;

        public ucOptionPanel(SettingsManager settings)
        {
            this.settingsManager = settings ?? throw new ArgumentNullException(nameof(settings));
            InitializeComponent();

            /* 1) Retention 콤보박스 고정 값 • DropDownList */
            cb_info_Retention.Items.Clear();
            cb_info_Retention.Items.AddRange(new object[] { "1", "3", "5" });
            cb_info_Retention.DropDownStyle = ComboBoxStyle.DropDownList;

            /* 2) UI 기본 비활성화 */
            UpdateRetentionControls(false);

            /* Settings.ini ↔ UI 동기화 */
            chk_infoDel.Checked = settingsManager.IsInfoDeletionEnabled;
            cb_info_Retention.Enabled = label3.Enabled = label4.Enabled = chk_infoDel.Checked;
            if (chk_infoDel.Checked)
            {
                string d = settingsManager.InfoRetentionDays.ToString();
                cb_info_Retention.SelectedItem = cb_info_Retention.Items.Contains(d) ? d : "1";
            }

            /* 3) 이벤트 연결 */
            chk_PerfoMode.CheckedChanged += chk_PerfoMode_CheckedChanged;
            chk_infoDel.CheckedChanged += chk_infoDel_CheckedChanged;
            cb_info_Retention.SelectedIndexChanged += cb_info_Retention_SelectedIndexChanged;

            /* 4) Settings.ini → UI 복원 */
            LoadOptionSettings();

            // 수동 버튼 클릭
            this.btnRefreshStatus.Click += BtnRefreshStatus_Click;

            // PictureBox를 원형으로 만들기 위한 Paint 이벤트 핸들러 연결
            this.pbDbStatus.Paint += PbStatus_Paint;
            this.pbObjStatus.Paint += PbStatus_Paint;

            // 타이머 정지 (자동 갱신 비활성화 - MainForm에서 제어)
            this.statusRefreshTimer.Stop();
        }

        #region ====== Run 상태 동기화 ======
        private void SetControlsEnabled(bool enabled)
        {
            chk_DebugMode.Enabled = enabled;
            chk_PerfoMode.Enabled = enabled;
            chk_infoDel.Enabled = enabled;
            UpdateRetentionControls(enabled && chk_infoDel.Checked);
            btnRefreshStatus.Enabled = !_isRefreshing;
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            this.isRunning = isRunning;
            SetControlsEnabled(!isRunning);
        }

        public void InitializePanel(bool isRunning)
        {
            this.isRunning = isRunning;
            SetControlsEnabled(!isRunning);
        }
        #endregion

        private void LoadOptionSettings()
        {
            chk_DebugMode.Checked = settingsManager.IsDebugMode;
            bool perf = settingsManager.GetValueFromSection(OptSection, Key_PerfLog) == "1";
            chk_PerfoMode.Checked = perf;
            bool infoDel = settingsManager.GetValueFromSection(OptSection, Key_InfoAutoDel) == "1";
            chk_infoDel.Checked = infoDel;
            string days = settingsManager.GetValueFromSection(OptSection, Key_InfoRetention);
            if (days == "1" || days == "3" || days == "5")
                cb_info_Retention.SelectedItem = days;
            UpdateRetentionControls(infoDel);
        }

        private void UpdateRetentionControls(bool enableCombo)
        {
            cb_info_Retention.Enabled = enableCombo && !isRunning;
            label3.Enabled = label4.Enabled = chk_infoDel.Checked;
        }

        private void chk_PerfoMode_CheckedChanged(object sender, EventArgs e)
        {
            bool enable = chk_PerfoMode.Checked;
            PerformanceMonitor.Instance.StartSampling();
            PerformanceMonitor.Instance.SetFileLogging(enable);
            settingsManager.IsPerformanceLogging = enable;
        }

        private void chk_infoDel_CheckedChanged(object sender, EventArgs e)
        {
            bool enabled = chk_infoDel.Checked;
            settingsManager.IsInfoDeletionEnabled = enabled;
            settingsManager.InfoRetentionDays = enabled
                                                    ? int.Parse(cb_info_Retention.SelectedItem?.ToString() ?? "1")
                                                    : 0;
            UpdateRetentionControls(enabled && !isRunning);
            if (enabled)
            {
                if (cb_info_Retention.SelectedIndex < 0)
                    cb_info_Retention.SelectedItem = "1";
            }
            else
            {
                cb_info_Retention.SelectedIndex = -1;
            }
        }

        private void cb_info_Retention_SelectedIndexChanged(object s, EventArgs e)
        {
            if (!chk_infoDel.Checked) return;
            object item = cb_info_Retention.SelectedItem;
            if (item == null) return;
            if (int.TryParse(item.ToString(), out int days))
                settingsManager.InfoRetentionDays = days;
        }

        private void chk_DebugMode_CheckedChanged(object sender, EventArgs e)
        {
            bool isDebug = chk_DebugMode.Checked;
            settingsManager.IsDebugMode = isDebug;
            LogManager.GlobalDebugEnabled = isDebug;
            ITM_Agent.Services.LogManager.BroadcastPluginDebug(isDebug);
            DebugModeChanged?.Invoke(isDebug);
        }

        private static readonly Regex _ipMaskRegex = new Regex(
            @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b",
            RegexOptions.Compiled);

        private string MaskIpAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "N/A";
            return _ipMaskRegex.Replace(ip, "*.*.*.$4");
        }

        private string ExtractHostFromConnectionString(string cs)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(cs);
                return builder.Host;
            }
            catch { return "Invalid CS"; }
        }

        private void PbStatus_Paint(object sender, PaintEventArgs e)
        {
            PictureBox pb = sender as PictureBox;
            if (pb == null) return;
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(0, 0, pb.Width - 1, pb.Height - 1);
                pb.Region = new Region(gp);
            }
            using (SolidBrush brush = new SolidBrush(pb.BackColor))
            {
                e.Graphics.FillEllipse(brush, 0, 0, pb.Width - 1, pb.Height - 1);
            }
            using (Pen pen = new Pen(Color.Black, 1))
            {
                e.Graphics.DrawEllipse(pen, 0, 0, pb.Width - 1, pb.Height - 1);
            }
        }

        private void BtnRefreshStatus_Click(object sender, EventArgs e)
        {
            _ = RefreshStatusAsync(true);
        }

        public void ActivatePanel()
        {
            // 화면 진입 시 1회 갱신 (타이머는 사용 안 함)
            _ = RefreshStatusAsync(true);
        }

        public void DeactivatePanel()
        {
            statusRefreshTimer.Stop();
        }

        private void statusRefreshTimer_Tick(object sender, EventArgs e)
        {
            // 사용 안 함
        }

        // ▼▼▼ 외부(MainForm)에서 상태 주입 ▼▼▼
        public void SetDirectConnectionStatus(bool dbOk, bool apiOk)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetDirectConnectionStatus(dbOk, apiOk)));
                return;
            }

            pbDbStatus.BackColor = dbOk ? Color.LimeGreen : Color.Red;
            pbObjStatus.BackColor = apiOk ? Color.LimeGreen : Color.Red;

            lblDbHost.Text = dbOk ? "Connected" : "Disconnected";
            lblObjHost.Text = apiOk ? "Connected" : "Disconnected";
        }
        // ▲▲▲ 추가 끝 ▲▲▲

        private async Task RefreshStatusAsync(bool force = false)
        {
            lock (_refreshLock)
            {
                if (_isRefreshing && !force) return;
                _isRefreshing = true;
            }

            this.Invoke(new Action(() =>
            {
                if (lblDbHost.Text != "Connected" && lblDbHost.Text != "Disconnected")
                {
                    pbDbStatus.BackColor = Color.Gray;
                    lblDbHost.Text = "Checking...";
                    pbObjStatus.BackColor = Color.Gray;
                    lblObjHost.Text = "Checking...";
                }
                btnRefreshStatus.Enabled = false;
            }));

            try
            {
                await Task.WhenAll(CheckDatabaseAsync(), CheckObjectStoryAsync());
            }
            finally
            {
                lock (_refreshLock)
                {
                    _isRefreshing = false;
                }
                this.Invoke(new Action(() =>
                {
                    btnRefreshStatus.Enabled = true;
                }));
            }
        }

        private async Task CheckDatabaseAsync()
        {
            string host = "N/A";
            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();
                host = ExtractHostFromConnectionString(cs);
                
                if (!cs.Contains("Pooling=")) cs += ";Pooling=false";
                cs += ";Timeout=3";

                this.Invoke(new Action(() => lblDbHost.Text = MaskIpAddress(host)));

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();
                    using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                    {
                        await cmd.ExecuteScalarAsync();
                    }
                }

                this.Invoke(new Action(() => pbDbStatus.BackColor = Color.LimeGreen));
            }
            catch (Exception)
            {
                this.Invoke(new Action(() =>
                {
                    pbDbStatus.BackColor = Color.Red;
                    if (host != "N/A" && host != "Invalid CS")
                        lblDbHost.Text = MaskIpAddress(host);
                    else
                        lblDbHost.Text = "Connection Failed";
                }));
            }
        }

        // [수정] FTP 연결 대신 Web API Health Check 수행
        private async Task CheckObjectStoryAsync()
        {
            string host = "N/A";
            try
            {
                var ftpInfo = FtpsInfo.CreateDefault();
                host = ftpInfo.Host;

                if (string.IsNullOrEmpty(host)) throw new Exception("API Host not configured.");

                this.Invoke(new Action(() => lblObjHost.Text = MaskIpAddress(host)));

                // HTTP Health Check (Port 8080)
                string url = $"http://{host}:{API_PORT}/api/FileUpload/health";
                bool connected = await Task.Run(async () =>
                {
                    try
                    {
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        {
                            var response = await _httpClient.GetAsync(url, cts.Token);
                            return response.IsSuccessStatusCode;
                        }
                    }
                    catch { return false; }
                });

                if (connected)
                {
                    this.Invoke(new Action(() => pbObjStatus.BackColor = Color.LimeGreen));
                }
                else
                {
                    throw new Exception($"Failed to connect to API ({url})");
                }
            }
            catch (Exception)
            {
                this.Invoke(new Action(() =>
                {
                    pbObjStatus.BackColor = Color.Red;
                    if (host != "N/A")
                        lblObjHost.Text = MaskIpAddress(host);
                    else
                        lblObjHost.Text = "Connection Failed";
                }));
            }
        }
    }
}
