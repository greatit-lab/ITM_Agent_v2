// ITM_Agent/ucPanel/ucLampLifePanel.cs
using ITM_Agent.Services;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucLampLifePanel : UserControl
    {
        private readonly SettingsManager _settingsManager;
        private readonly LampLifeService _lampLifeService;
        private bool _isAgentRunning = false;

        public ucLampLifePanel(SettingsManager settingsManager, LampLifeService lampLifeService)
        {
            InitializeComponent();
            _settingsManager = settingsManager;
            _lampLifeService = lampLifeService;

            _lampLifeService.CollectionCompleted += OnCollectionCompleted;

            LoadSettings();
        }

        private void OnCollectionCompleted(bool success, DateTime timestamp)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateLastCollectLabel(success, timestamp)));
            }
            else
            {
                UpdateLastCollectLabel(success, timestamp);
            }
        }

        private void UpdateLastCollectLabel(bool success, DateTime timestamp)
        {
            if (success)
            {
                lblLastCollect.Text = $"Success at {timestamp:yyyy-MM-dd HH:mm:ss}";
                lblLastCollect.ForeColor = Color.Green;
            }
            else
            {
                lblLastCollect.Text = $"Failed at {timestamp:yyyy-MM-dd HH:mm:ss}";
                lblLastCollect.ForeColor = Color.Red;
            }
        }

        private void LoadSettings()
        {
            chkEnable.Checked = _settingsManager.IsLampLifeCollectorEnabled;
            // Interval 설정 로드 로직 제거 (1시간 고정)
            UpdateControlsEnabled();
        }

        private void chkEnable_CheckedChanged(object sender, EventArgs e)
        {
            if (_isAgentRunning) return;
            _settingsManager.IsLampLifeCollectorEnabled = chkEnable.Checked;
            UpdateControlsEnabled();
        }

        // Interval 변경 이벤트 핸들러 제거

        private async void btnManualCollect_Click(object sender, EventArgs e)
        {
            btnManualCollect.Enabled = false;
            lblLastCollect.Text = "Collecting...";
            lblLastCollect.ForeColor = Color.Blue;

            try
            {
                // UI 자동화 로직 1회 호출 (수동 테스트용)
                bool success = await _lampLifeService.ExecuteUiCollectionAsync();
                UpdateLastCollectLabel(success, DateTime.Now);
            }
            catch (Exception)
            {
                UpdateLastCollectLabel(false, DateTime.Now);
            }
            finally
            {
                if (!_isAgentRunning)
                {
                    btnManualCollect.Enabled = true;
                }
            }
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            _isAgentRunning = isRunning;
            UpdateControlsEnabled();
        }

        private void UpdateControlsEnabled()
        {
            bool canEditSettings = !_isAgentRunning;

            chkEnable.Enabled = canEditSettings;
            // Interval 컨트롤 활성화 로직 제거

            // 수동 버튼은 체크박스가 켜져 있을 때만 활성화
            btnManualCollect.Enabled = chkEnable.Checked;
        }
    }
}
