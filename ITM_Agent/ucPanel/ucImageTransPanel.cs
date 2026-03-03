// ITM_Agent/ucPanel/ucImageTransPanel.cs
using ITM_Agent.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucImageTransPanel : UserControl
    {
        private static readonly HashSet<string> mergedBaseNames = new HashSet<string>();
        private readonly LogManager logManager;
        private readonly PdfMergeManager pdfMergeManager;
        private readonly SettingsManager settingsManager;
        private readonly ucConfigurationPanel configPanel;

        private FileSystemWatcher imageWatcher;

        private readonly Dictionary<string, DateTime> changedFiles = new Dictionary<string, DateTime>();
        private readonly object changedFilesLock = new object();
        private System.Threading.Timer checkTimer;

        private bool isRunning = false;
        private System.Threading.Timer _cleanupTimer;

        private System.Threading.Timer _pollingTimer;

        public event Action ImageSaveFolderChanged;

        public ucImageTransPanel(SettingsManager settingsManager, ucConfigurationPanel configPanel)
        {
            this.settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            this.configPanel = configPanel ?? throw new ArgumentNullException(nameof(configPanel));
            InitializeComponent();

            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            pdfMergeManager = new PdfMergeManager(AppDomain.CurrentDomain.BaseDirectory, logManager);

            logManager.LogEvent("[ucImageTransPanel] Initialized");

            btn_SetFolder.Click += btn_SetFolder_Click;
            btn_FolderClear.Click += btn_FolderClear_Click;
            btn_SetTime.Click += btn_SetTime_Click;
            btn_TimeClear.Click += btn_TimeClear_Click;
            btn_SelectOutputFolder.Click += btn_SelectOutputFolder_Click;

            LoadFolders();
            LoadRegexFolderPaths();
            LoadWaitTimes();
            LoadOutputFolder();
        }

        public string GetImageSaveFolder()
        {
            if (lb_ImageSaveFolder.Text.Contains("not set")) return string.Empty;
            return lb_ImageSaveFolder.Text;
        }

        #region ====== MainForm에서 실행/중지 제어 ======

        public void UpdateStatusOnRun(bool runState)
        {
            isRunning = runState;

            btn_SetFolder.Enabled = !runState;
            btn_FolderClear.Enabled = !runState;
            btn_SetTime.Enabled = !runState;
            btn_TimeClear.Enabled = !runState;
            btn_SelectOutputFolder.Enabled = !runState;
            cb_TargetImageFolder.Enabled = !runState;
            cb_WaitTime.Enabled = !runState;

            if (isRunning)
            {
                StartWatchingFolder();
                _cleanupTimer = new System.Threading.Timer(
                    _ => ClearMergedBaseNames(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24)
                );

                _pollingTimer = new System.Threading.Timer(_ => PollUnprocessedImages(), null, 5000, 5000);
            }
            else
            {
                StopWatchingFolder();
                _cleanupTimer?.Dispose();
                _cleanupTimer = null;

                _pollingTimer?.Dispose();
                _pollingTimer = null;
            }

            logManager.LogEvent($"[ucImageTransPanel] Status updated to {(runState ? "Running" : "Stopped")}");
        }

        private void ClearMergedBaseNames()
        {
            lock (mergedBaseNames)
            {
                if (mergedBaseNames.Count > 0)
                {
                    logManager.LogEvent($"[ucImageTransPanel] Clearing {mergedBaseNames.Count} items from mergedBaseNames to prevent memory leak.");
                    mergedBaseNames.Clear();
                }
            }
        }

        private void StartWatchingFolder()
        {
            StopWatchingFolder();

            string targetFolder = settingsManager.GetValueFromSection("ImageTrans", "Target");
            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder))
            {
                logManager.LogError("[ucImageTransPanel] Target folder not set or does not exist - cannot watch.");
                return;
            }

            imageWatcher = new FileSystemWatcher()
            {
                Path = targetFolder,
                Filter = "*.*",
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 65536
            };

            imageWatcher.Renamed += OnImageFileChanged;
            imageWatcher.Changed += OnImageFileChanged;
            imageWatcher.Created += OnImageFileChanged;

            imageWatcher.EnableRaisingEvents = true;

            logManager.LogEvent($"[ucImageTransPanel] StartWatchingFolder - Folder: {targetFolder}");
        }

        private void StopWatchingFolder()
        {
            if (imageWatcher != null)
            {
                imageWatcher.EnableRaisingEvents = false;
                imageWatcher.Dispose();
                imageWatcher = null;
            }

            checkTimer?.Dispose();
            checkTimer = null;

            lock (changedFilesLock)
            {
                changedFiles.Clear();
            }
        }

        #endregion

        #region ====== 누락 방지용 백그라운드 폴링 ======

        private void PollUnprocessedImages()
        {
            if (!isRunning) return;

            string targetFolder = settingsManager.GetValueFromSection("ImageTrans", "Target");
            if (string.IsNullOrEmpty(targetFolder) || !Directory.Exists(targetFolder)) return;

            try
            {
                string[] exts = { ".jpg", ".jpeg", ".png", ".tif", ".tiff" };
                var files = Directory.GetFiles(targetFolder, "*.*", SearchOption.TopDirectoryOnly)
                                     .Where(p => exts.Contains(Path.GetExtension(p).ToLower()))
                                     .ToList();

                var now = DateTime.Now;
                Regex pattern = new Regex(@"^(?<basename>.+)_(?<page>\d+)$");

                foreach (var file in files)
                {
                    string fnNoExt = Path.GetFileNameWithoutExtension(file);

                    if (fnNoExt.Contains("_#1_")) continue;

                    var match = pattern.Match(fnNoExt);
                    if (!match.Success) continue;

                    string baseName = match.Groups["basename"].Value;

                    lock (mergedBaseNames)
                    {
                        if (mergedBaseNames.Contains(baseName)) continue;
                    }

                    lock (changedFilesLock)
                    {
                        if (!changedFiles.ContainsKey(file))
                        {
                            changedFiles[file] = now;
                        }
                    }
                }

                lock (changedFilesLock)
                {
                    if (changedFiles.Count > 0 && checkTimer == null)
                    {
                        checkTimer = new System.Threading.Timer(_ => CheckFilesAfterWait(), null, 1000, 1000);
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogDebug($"[ucImageTransPanel] Polling error: {ex.Message}");
            }
        }

        #endregion

        #region ====== FileSystemWatcher 이벤트 + Timer ======

        private void OnImageFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!isRunning) return;

            if (!File.Exists(e.FullPath)) return;

            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(e.FullPath);

            if (fileNameWithoutExt.Contains("_#1_"))
            {
                return;
            }

            Regex pattern = new Regex(@"^(?<basename>.+)_(?<page>\d+)$");
            var match = pattern.Match(fileNameWithoutExt);
            if (!match.Success)
            {
                return;
            }

            lock (changedFilesLock)
            {
                changedFiles[e.FullPath] = DateTime.Now;
            }

            if (checkTimer == null)
            {
                checkTimer = new System.Threading.Timer(_ => CheckFilesAfterWait(), null, 1000, 1000);
            }

            logManager.LogEvent($"[ucImageTransPanel] Detected valid file: {e.FullPath}");
        }

        private void CheckFilesAfterWait()
        {
            if (!isRunning) return;

            int waitSec = GetWaitSeconds();
            if (waitSec <= 0) return;

            var now = DateTime.Now;
            var toProcess = new List<string>();

            lock (changedFilesLock)
            {
                var snapshot = changedFiles.ToList();
                foreach (var kv in snapshot)
                {
                    double diff = (now - kv.Value).TotalSeconds;
                    if (diff >= waitSec)
                    {
                        toProcess.Add(kv.Key);
                    }
                }
            }

            if (toProcess.Count > 0)
            {
                foreach (var filePath in toProcess)
                {
                    try
                    {
                        MergeImagesForBaseName(filePath);
                    }
                    catch (Exception ex)
                    {
                        logManager.LogError($"[ucImageTransPanel] Merge error for file {filePath}: {ex.Message}");
                    }
                }

                lock (changedFilesLock)
                {
                    foreach (var fp in toProcess)
                    {
                        changedFiles.Remove(fp);
                    }

                    if (changedFiles.Count == 0 && checkTimer != null)
                    {
                        checkTimer.Dispose();
                        checkTimer = null;
                    }
                }
            }
        }

        private int GetWaitSeconds()
        {
            string waitStr = settingsManager.GetValueFromSection("ImageTrans", "Wait");

            if (cb_WaitTime.InvokeRequired)
            {
                cb_WaitTime.Invoke(new MethodInvoker(delegate
                {
                    if (cb_WaitTime.SelectedItem is string sel) waitStr = sel;
                }));
            }
            else
            {
                if (cb_WaitTime.SelectedItem is string sel) waitStr = sel;
            }

            if (int.TryParse(waitStr, out int ws)) return ws;
            return 30;
        }
        #endregion

        private void MergeImagesForBaseName(string filePath)
        {
            string fnNoExt = Path.GetFileNameWithoutExtension(filePath);
            var m0 = Regex.Match(fnNoExt, @"^(?<base>.+)_(?<page>\d+)$");
            if (!m0.Success) return;

            string baseName = m0.Groups["base"].Value;

            // [개선] 파일명에 포함된 '.' 및 '#' 기호를 언더바('_')로 치환하여 웹 환경(URL) 호환성 확보
            string safeBaseName = baseName.Replace('.', '_').Replace('#', '_');

            string folder = Path.GetDirectoryName(filePath);

            lock (mergedBaseNames)
            {
                if (mergedBaseNames.Contains(baseName))
                {
                    if (settingsManager.IsDebugMode)
                        logManager.LogDebug($"[ucImageTransPanel] Skip duplicate merge: {baseName}");
                    return;
                }
                mergedBaseNames.Add(baseName);
            }

            string[] exts = { ".jpg", ".jpeg", ".png", ".tif", ".tiff" };
            var imgList = Directory.GetFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                                   .Where(p => exts.Contains(Path.GetExtension(p).ToLower()))
                                   .Select(p =>
                                   {
                                       var m = Regex.Match(Path.GetFileNameWithoutExtension(p),
                                                           $"^{Regex.Escape(baseName)}_(?<pg>\\d+)$",
                                                           RegexOptions.IgnoreCase);
                                       return (path: p, ok: m.Success,
                                               page: m.Success && int.TryParse(m.Groups["pg"].Value, out int n) ? n : -1);
                                   })
                                   .Where(x => x.ok)
                                   .OrderBy(x => x.page)
                                   .Select(x => x.path)
                                   .ToList();

            if (imgList.Count == 0) return;

            string outputFolder = settingsManager.GetValueFromSection("ImageTrans", "SaveFolder");
            if (string.IsNullOrEmpty(outputFolder) || !Directory.Exists(outputFolder))
            {
                outputFolder = folder;
                logManager.LogEvent("[ucImageTransPanel] SaveFolder 미설정/미존재 ▶ 이미지 폴더로 대체 저장");
            }

            string outputPdfPath = Path.Combine(outputFolder, $"{safeBaseName}.pdf");

            pdfMergeManager.MergeImagesToPdf(imgList, outputPdfPath);

            logManager.LogEvent($"[ucImageTransPanel] Created PDF: {outputPdfPath}");
        }

        #region ====== 기존 UI/설정 메서드 ======

        private void btn_SelectOutputFolder_Click(object sender, EventArgs e)
        {
            string baseFolder = configPanel.BaseFolderPath;
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
            {
                MessageBox.Show("기준 폴더(Base Folder)가 설정되지 않았습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = baseFolder;
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedFolder = folderDialog.SelectedPath;
                    lb_ImageSaveFolder.Text = selectedFolder;
                    settingsManager.SetValueToSection("ImageTrans", "SaveFolder", selectedFolder);

                    ImageSaveFolderChanged?.Invoke();

                    logManager.LogEvent($"[ucImageTransPanel] Output folder set: {selectedFolder}");
                    MessageBox.Show("출력 폴더가 설정되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void btn_SetFolder_Click(object sender, EventArgs e)
        {
            if (cb_TargetImageFolder.SelectedItem is string selectedFolder)
            {
                settingsManager.SetValueToSection("ImageTrans", "Target", selectedFolder);
                logManager.LogEvent($"[ucImageTransPanel] Target folder set: {selectedFolder}");
                MessageBox.Show($"폴더가 설정되었습니다: {selectedFolder}", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("폴더를 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btn_FolderClear_Click(object sender, EventArgs e)
        {
            if (cb_TargetImageFolder.SelectedItem != null)
            {
                cb_TargetImageFolder.SelectedIndex = -1;
                settingsManager.RemoveSection("ImageTrans");

                logManager.LogEvent("[ucImageTransPanel] Target folder cleared");
                MessageBox.Show("폴더 설정이 초기화되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("선택된 폴더가 없습니다.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void LoadRegexFolderPaths()
        {
            cb_TargetImageFolder.Items.Clear();
            var regexFolders = configPanel.GetRegexList();
            var uniqueFolders = regexFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            cb_TargetImageFolder.Items.AddRange(uniqueFolders);

            string selectedPath = settingsManager.GetValueFromSection("ImageTrans", "Target");
            if (!string.IsNullOrEmpty(selectedPath) && cb_TargetImageFolder.Items.Contains(selectedPath))
            {
                cb_TargetImageFolder.SelectedItem = selectedPath;
            }
            else
            {
                cb_TargetImageFolder.SelectedIndex = -1;
            }
        }

        private void LoadFolders()
        {
            cb_TargetImageFolder.Items.Clear();
            var folders = settingsManager.GetFoldersFromSection("[TargetFolders]");
            cb_TargetImageFolder.Items.AddRange(folders.ToArray());
        }

        public void LoadWaitTimes()
        {
            cb_WaitTime.Items.Clear();
            cb_WaitTime.Items.AddRange(new object[] { "30", "60", "120", "180", "240", "300" });
            cb_WaitTime.SelectedIndex = -1;

            string savedWaitTime = settingsManager.GetValueFromSection("ImageTrans", "Wait");
            if (!string.IsNullOrEmpty(savedWaitTime) && cb_WaitTime.Items.Contains(savedWaitTime))
            {
                cb_WaitTime.SelectedItem = savedWaitTime;
            }
        }

        private void btn_SetTime_Click(object sender, EventArgs e)
        {
            if (cb_WaitTime.SelectedItem is string selectedWaitTime && int.TryParse(selectedWaitTime, out int waitTime))
            {
                settingsManager.SetValueToSection("ImageTrans", "Wait", selectedWaitTime);
                logManager.LogEvent($"[ucImageTransPanel] Wait time set: {waitTime} seconds");
                MessageBox.Show($"대기 시간이 {waitTime}초로 설정되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("대기 시간을 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btn_TimeClear_Click(object sender, EventArgs e)
        {
            if (cb_WaitTime.SelectedItem != null)
            {
                cb_WaitTime.SelectedIndex = -1;
                settingsManager.SetValueToSection("ImageTrans", "Wait", string.Empty);

                logManager.LogEvent("[ucImageTransPanel] Wait time cleared");
                MessageBox.Show("대기 시간이 초기화되었습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("선택된 대기 시간이 없습니다.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadOutputFolder()
        {
            string outputFolder = settingsManager.GetValueFromSection("ImageTrans", "SaveFolder");

            if (!string.IsNullOrEmpty(outputFolder) && Directory.Exists(outputFolder))
            {
                lb_ImageSaveFolder.Text = outputFolder;
            }
            else
            {
                lb_ImageSaveFolder.Text = "Output folder not set or does not exist.";
            }
        }

        public void RefreshUI()
        {
            LoadRegexFolderPaths();
            LoadWaitTimes();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopWatchingFolder();
                _cleanupTimer?.Dispose();
                _pollingTimer?.Dispose();
            }
            base.Dispose(disposing);
        }

        #endregion
    }
}
