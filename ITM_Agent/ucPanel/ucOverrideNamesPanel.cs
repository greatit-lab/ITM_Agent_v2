// ITM_Agent/ucPanel/ucOverrideNamesPanel.cs
using ITM_Agent.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ITM_Agent.ucPanel
{
    public partial class ucOverrideNamesPanel : UserControl
    {
        private readonly SettingsManager settingsManager;
        private readonly ucConfigurationPanel configPanel;
        private readonly LogManager logManager;
        private readonly bool isDebugMode;

        private FileSystemWatcher folderWatcher;
        public event Action<string, Color> StatusUpdated;
        public event Action<string> FileRenamed;
        private FileSystemWatcher baselineWatcher;

        private readonly List<FileSystemWatcher> targetWatchers = new List<FileSystemWatcher>();
        private bool isRunning = false;

        private readonly ConcurrentDictionary<string, (string TimeInfo, string Prefix, string CInfo)> _baselineCache =
            new ConcurrentDictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

        public ucOverrideNamesPanel(SettingsManager settingsManager, ucConfigurationPanel configPanel, LogManager logManager, bool isDebugMode)
        {
            this.settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            this.configPanel = configPanel ?? throw new ArgumentNullException(nameof(configPanel));
            this.logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            this.isDebugMode = isDebugMode;

            InitializeComponent();

            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] 생성자 호출 - 초기화 시작");

            InitializeBaselineWatcher();
            InitializeCustomEvents();

            LoadDataFromSettings();
            LoadRegexFolderPaths();
            LoadSelectedBaseDatePath();

            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] 생성자 호출 - 초기화 완료");
        }

        #region 안정화 감지용 내부 클래스/메서드

        private void ProcessStableFile(string filePath)
        {
            try
            {
                if (!WaitForFileReady(filePath, maxRetries: 60, delayMilliseconds: 1000))
                {
                    logManager.LogEvent($"[ucOverrideNamesPanel] 파일을 처리할 수 없습니다.(장기 잠김): {filePath}");
                    return;
                }

                if (File.Exists(filePath))
                {
                    DateTime? dateTimeInfo = ExtractDateTimeFromFile(filePath);
                    if (dateTimeInfo.HasValue)
                    {
                        string fileName = Path.GetFileName(filePath);
                        string infoPath = CreateBaselineInfoFile(filePath, dateTimeInfo.Value);

                        if (!string.IsNullOrEmpty(infoPath))
                        {
                            logManager.LogEvent($"[ucOverrideNamesPanel] Baseline 대상 파일 감지: {fileName} -> info 파일 생성");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[ucOverrideNamesPanel] ProcessStableFile() 중 오류: {ex.Message}\n파일: {filePath}");
            }
        }

        private long GetFileSizeSafe(string filePath)
        {
            try { if (File.Exists(filePath)) { var fi = new FileInfo(filePath); return fi.Length; } }
            catch { /* 무시 */ }
            return 0;
        }

        private DateTime GetLastWriteTimeSafe(string filePath)
        {
            try { if (File.Exists(filePath)) return File.GetLastWriteTime(filePath); }
            catch { /* 무시 */ }
            return DateTime.MinValue;
        }

        #endregion

        #region 기존 로직 + FileSystemWatcher 이벤트 처리 수정

        private void InitializeCustomEvents()
        {
            logManager.LogEvent("[ucOverrideNamesPanel] InitializeCustomEvents() 호출됨");
            cb_BaseDatePath.SelectedIndexChanged += cb_BaseDatePath_SelectedIndexChanged;
            btn_BaseClear.Click += btn_BaseClear_Click;
            btn_SelectFolder.Click += Btn_SelectFolder_Click;
            btn_Remove.Click += Btn_Remove_Click;
        }

        private void LoadRegexFolderPaths()
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] LoadRegexFolderPaths() 시작");
            cb_BaseDatePath.Items.Clear();
            var folderPaths = settingsManager.GetRegexList().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            cb_BaseDatePath.Items.AddRange(folderPaths);
            cb_BaseDatePath.SelectedIndex = -1;
            logManager.LogEvent("[ucOverrideNamesPanel] 정규식 경로 목록 로드 완료");
        }

        private void LoadSelectedBaseDatePath()
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] LoadSelectedBaseDatePath() 시작");
            string selectedPath = settingsManager.GetValueFromSection("SelectedBaseDatePath", "Path");
            if (!string.IsNullOrEmpty(selectedPath) && cb_BaseDatePath.Items.Contains(selectedPath))
            {
                cb_BaseDatePath.SelectedItem = selectedPath;
                StartFolderWatcher(selectedPath);
            }
            logManager.LogEvent("[ucOverrideNamesPanel] 저장된 BaseDatePath 로드 및 감시 시작");
        }

        private void cb_BaseDatePath_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cb_BaseDatePath.SelectedItem is string selectedPath)
            {
                settingsManager.SetValueToSection("SelectedBaseDatePath", "Path", selectedPath);
                StartFolderWatcher(selectedPath);
                if (settingsManager.IsDebugMode) logManager.LogDebug($"[ucOverrideNamesPanel] cb_BaseDatePath_SelectedIndexChanged -> {selectedPath} 설정");
            }
        }

        private void StartFolderWatcher(string path)
        {
            folderWatcher?.Dispose();
            logManager.LogEvent($"[ucOverrideNamesPanel] StartFolderWatcher() 호출 - 감시 경로: {path}");

            if (Directory.Exists(path))
            {
                folderWatcher = new FileSystemWatcher
                {
                    Path = path,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    Filter = "*.*",
                    EnableRaisingEvents = true
                };
                folderWatcher.Created += OnFileSystemEvent;
                folderWatcher.Changed += OnFileSystemEvent;
            }
            else
            {
                logManager.LogError($"[ucOverrideNamesPanel] 지정된 경로가 존재하지 않습니다: {path}");
            }
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (!isRunning) return;
            logManager.LogDebug($"[ucOverrideNamesPanel] File event received, processing immediately: {e.FullPath}");
            ThreadPool.QueueUserWorkItem(_ => { ProcessStableFile(e.FullPath); });
        }

        private void btn_BaseClear_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] btn_BaseClear_Click() - BaseDatePath 초기화");
            cb_BaseDatePath.SelectedIndex = -1;
            settingsManager.RemoveSection("SelectedBaseDatePath");
            folderWatcher?.Dispose();
            logManager.LogEvent("[ucOverrideNamesPanel] BaseDatePath 해제 및 감시 중지");
        }

        private void Btn_SelectFolder_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] Btn_SelectFolder_Click() 호출");
            var baseFolder = settingsManager.GetFoldersFromSection("[BaseFolder]").FirstOrDefault() ?? AppDomain.CurrentDomain.BaseDirectory;

            using (var folderDialog = new FolderBrowserDialog())
            {
                folderDialog.SelectedPath = baseFolder;
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    if (!lb_TargetComparePath.Items.Contains(folderDialog.SelectedPath))
                    {
                        lb_TargetComparePath.Items.Add(folderDialog.SelectedPath);
                        UpdateTargetComparePathInSettings();
                        logManager.LogEvent($"[ucOverrideNamesPanel] 새로운 비교 경로 추가: {folderDialog.SelectedPath}");
                    }
                    else
                    {
                        MessageBox.Show("해당 폴더는 이미 추가되어 있습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }

        private void Btn_Remove_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug("[ucOverrideNamesPanel] Btn_Remove_Click() 호출");

            if (lb_TargetComparePath.SelectedItems.Count > 0)
            {
                if (MessageBox.Show("선택한 항목을 삭제하시겠습니까?", "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    var selectedItems = lb_TargetComparePath.SelectedItems.Cast<string>().ToList();
                    foreach (var item in selectedItems) lb_TargetComparePath.Items.Remove(item);
                    UpdateTargetComparePathInSettings();
                    logManager.LogEvent("[ucOverrideNamesPanel] 선택한 비교 경로 삭제 완료");
                }
            }
            else
            {
                MessageBox.Show("삭제할 항목을 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void UpdateTargetComparePathInSettings()
        {
            var folders = lb_TargetComparePath.Items.Cast<string>().ToList();
            settingsManager.SetFoldersToSection("[TargetComparePath]", folders);
        }

        #endregion

        #region 파일 처리 및 정보 추출

        private void RefreshBaselineCache()
        {
            string baseFolder = settingsManager.GetBaseFolder();
            if (string.IsNullOrEmpty(baseFolder)) return;

            string baselineFolder = Path.Combine(baseFolder, "Baseline");
            if (Directory.Exists(baselineFolder))
            {
                var files = Directory.GetFiles(baselineFolder, "*.info");
                var newData = ExtractBaselineData(files);

                _baselineCache.Clear();
                foreach (var kvp in newData) _baselineCache[kvp.Key] = kvp.Value;

                if (settingsManager.IsDebugMode) logManager.LogDebug($"[ucOverrideNamesPanel] Baseline cache refreshed. Items: {_baselineCache.Count}");
            }
        }

        private string CreateBaselineInfoFile(string filePath, DateTime dateTime)
        {
            if (settingsManager.IsDebugMode) logManager.LogDebug($"[ucOverrideNamesPanel] CreateBaselineInfoFile() 호출 - 대상: {Path.GetFileName(filePath)}");

            string baseFolder = configPanel.BaseFolderPath;
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder)) return null;

            string baselineFolder = System.IO.Path.Combine(baseFolder, "Baseline");
            if (!Directory.Exists(baselineFolder))
            {
                Directory.CreateDirectory(baselineFolder);
                logManager.LogEvent($"[ucOverrideNamesPanel] Baseline 폴더 생성: {baselineFolder}");
            }

            // 원본 파일명 (예: ABC001.1_ABC001.1_01_BB04_00PT 또는 ABC001.1_C3W13_BB#2_00PT)
            string originalName = System.IO.Path.GetFileNameWithoutExtension(filePath);

            // ⭐️ [핵심 방어 로직] 이미 AMAT 형식(C\dW\d+)이 포함된 파일은 건드리지 않고 EBARA(순수 숫자)일 때만 C5W 포맷으로 정규화합니다.
            if (!Regex.IsMatch(originalName, @"C\dW\d+", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(originalName, @"_(\d{2})_");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int waferNum))
                {
                    // 원본 파일명의 _01_ 또는 _22_ 부분을 _C5W1_, _C5W22_ 등으로 완벽하게 덮어씁니다.
                    originalName = originalName.Substring(0, match.Index) + $"_C5W{waferNum}_" + originalName.Substring(match.Index + match.Length);
                }
            }

            // 최종적으로 저장될 파일명 (예: 20260324_133400_ABC001.1_ABC001.1_C5W1_BB04_00PT.info)
            string newFileName = $"{dateTime:yyyyMMdd_HHmmss}_{originalName}.info";
            string newFilePath = System.IO.Path.Combine(baselineFolder, newFileName);

            try
            {
                if (File.Exists(newFilePath)) return newFilePath;
                using (File.Create(newFilePath)) { } // 파일 생성
                return newFilePath;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
                if (File.Exists(newFilePath)) return newFilePath;
                return null;
            }
            catch (Exception ex)
            {
                logManager.LogError($"[ucOverrideNamesPanel] .info 파일 생성 중 오류: {ex.Message}");
                return null;
            }
        }

        private bool WaitForFileReady(string filePath, int maxRetries = 30, int delayMilliseconds = 500)
        {
            int retries = 0;
            while (retries < maxRetries)
            {
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) return true;
                }
                catch (IOException) { Thread.Sleep(delayMilliseconds); retries++; }
                catch (UnauthorizedAccessException) { return false; }
                catch (Exception) { return false; }
            }
            return false;
        }

        private DateTime? ExtractDateTimeFromFile(string filePath)
        {
            string datePattern = @"Date and Time:\s*(\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} (AM|PM))";
            const int maxRetries = 10;
            const int delayMs = 1000;
            const int maxBytesToRead = 8192;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fileStream))
                    {
                        char[] buffer = new char[maxBytesToRead];
                        int charsRead = reader.Read(buffer, 0, buffer.Length);
                        string fileContent = new string(buffer, 0, charsRead);

                        Match match = Regex.Match(fileContent, datePattern);
                        if (match.Success && DateTime.TryParse(match.Groups[1].Value, out DateTime result)) return result;
                    }
                }
                catch (IOException) { if (i == maxRetries - 1) return null; }
                catch (Exception) { return null; }
                Thread.Sleep(delayMs);
            }
            return null;
        }

        #endregion

        #region BaselineWatcher & TargetWatcher 감시

        private void InitializeBaselineWatcher()
        {
            if (baselineWatcher != null)
            {
                baselineWatcher.EnableRaisingEvents = false;
                baselineWatcher.Dispose();
                baselineWatcher = null;
            }

            var baseFolder = settingsManager.GetFoldersFromSection("[BaseFolder]").FirstOrDefault();
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder)) return;

            var baselineFolder = Path.Combine(baseFolder, "Baseline");
            if (!Directory.Exists(baselineFolder)) return;

            baselineWatcher = new FileSystemWatcher(baselineFolder, "*.info")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };

            baselineWatcher.Created += OnBaselineFileChanged;
            baselineWatcher.Changed += OnBaselineFileChanged;
            baselineWatcher.EnableRaisingEvents = true;

            logManager.LogEvent($"[ucOverrideNamesPanel] BaselineWatcher 초기화 완료 - 경로: {baselineFolder}");
        }

        private void OnBaselineFileChanged(object sender, FileSystemEventArgs e)
        {
            if (File.Exists(e.FullPath))
            {
                var baselineData = ExtractBaselineData(new[] { e.FullPath });
                if (baselineData.Count == 0) return;

                foreach (var kvp in baselineData) _baselineCache[kvp.Key] = kvp.Value;
                var timeInfo = baselineData.Values.First().TimeInfo;

                foreach (string targetFolder in lb_TargetComparePath.Items)
                {
                    if (!Directory.Exists(targetFolder)) continue;

                    try
                    {
                        // ⭐️ _#1_ 이라는 고정 텍스트 대신 정규식을 통과한 _#숫자_ 파일들만 추출
                        var targetFiles = Directory.GetFiles(targetFolder, $"*{timeInfo}*_#*_*.*")
                                                   .Where(f => Regex.IsMatch(Path.GetFileName(f), @"_#\d+_"));

                        foreach (var targetFile in targetFiles)
                        {
                            try { ProcessTargetFile(targetFile, baselineData); }
                            catch (Exception innerEx) { logManager.LogError($"[ucOverrideNamesPanel] 오류: {innerEx.Message}"); }
                        }
                    }
                    catch (Exception ex) { logManager.LogError($"[ucOverrideNamesPanel] 스캔 오류: {ex.Message}"); }
                }
            }
        }

        private void InitializeTargetWatchers()
        {
            StopTargetWatchers();
            foreach (string folder in lb_TargetComparePath.Items)
            {
                if (!Directory.Exists(folder)) continue;
                var watcher = new FileSystemWatcher(folder)
                {
                    Filter = "*.*",
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnTargetFileEvent;
                watcher.Changed += OnTargetFileEvent;
                watcher.Renamed += OnTargetFileEvent;
                watcher.EnableRaisingEvents = true;
                targetWatchers.Add(watcher);
            }
        }

        private void StopTargetWatchers()
        {
            foreach (var w in targetWatchers) { w.EnableRaisingEvents = false; w.Dispose(); }
            targetWatchers.Clear();
        }

        private void OnTargetFileEvent(object sender, FileSystemEventArgs e)
        {
            if (!isRunning) return;
            // ⭐️ _#1_ 이 아닌 모든 웨이퍼 번호(_#13_, _#22_ 등)를 감지
            if (!Regex.IsMatch(e.Name, @"_#\d+_")) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!WaitForFileReady(e.FullPath, maxRetries: 20, delayMilliseconds: 500)) return;
                    ProcessTargetFile(e.FullPath, _baselineCache);
                }
                catch (Exception ex) { logManager.LogError($"[ucOverrideNamesPanel] OnTargetFileEvent Error: {ex.Message}"); }
            });
        }

        private void LogFileRename(string oldPath, string newPath)
        {
            string changedFileName = Path.GetFileName(newPath);
            logManager.LogEvent($"[ucOverrideNamesPanel] 파일 이름 변경: {oldPath} -> {changedFileName}");
            FileRenamed?.Invoke(newPath);
        }

        private Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> ExtractBaselineData(string[] files)
        {
            var baselineData = new Dictionary<string, (string, string, string)>();
            // 이미 Info 생성 단계에서 정규화가 완료되었으므로 안전하게 추출
            var regex = new Regex(@"(\d{8}_\d{6})_(.+?)_(C\dW\d+|\d{2})(?:_|\.|$)", RegexOptions.IgnoreCase);

            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                var match = regex.Match(fileName);
                if (match.Success)
                {
                    string timeInfo = match.Groups[1].Value;
                    string prefix = match.Groups[2].Value;
                    string cInfo = match.Groups[3].Value;

                    if (int.TryParse(cInfo, out int waferNum))
                    {
                        cInfo = $"C5W{waferNum}";
                    }

                    baselineData[fileName] = (timeInfo, prefix, cInfo);
                }
            }
            return baselineData;
        }

        private string ProcessTargetFile(string targetFile, IDictionary<string, (string TimeInfo, string Prefix, string CInfo)> baselineData)
        {
            if (!WaitForFileReady(targetFile, maxRetries: 10, delayMilliseconds: 300)) return null;

            string fileName = Path.GetFileName(targetFile);

            // ⭐️ 대상 파일명에 존재하는 실제 동적 웨이퍼 패턴(_#13_ 등)을 정밀하게 추출
            var targetWaferMatch = Regex.Match(fileName, @"_#(\d+)_");
            if (!targetWaferMatch.Success) return null;
            string targetWaferStr = targetWaferMatch.Value; // (예: _#13_)

            var sortedData = baselineData.Values.OrderByDescending(d => d.TimeInfo).ToList();
            string cleanFileName = Regex.Replace(fileName, @"[^a-zA-Z0-9]", "").ToUpperInvariant();

            foreach (var data in sortedData)
            {
                if (!fileName.Contains(data.TimeInfo)) continue;

                string[] prefixTokens = data.Prefix.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                bool lotMatch = true;
                foreach (string token in prefixTokens)
                {
                    string cleanToken = Regex.Replace(token, @"[^a-zA-Z0-9]", "").ToUpperInvariant();
                    if (!cleanFileName.Contains(cleanToken))
                    {
                        lotMatch = false;
                        break;
                    }
                }

                if (lotMatch)
                {
                    // ⭐️ 추출한 _#01_ 패턴을 Info에서 가져온 _C5W1_ 등으로 정확히 덮어쓰기
                    string newName = fileName.Replace(targetWaferStr, $"_{data.CInfo}_");

                    if (newName.Equals(fileName, StringComparison.Ordinal)) continue;

                    string newPath = Path.Combine(Path.GetDirectoryName(targetFile), newName);

                    const int maxRetries = 10;
                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            if (!File.Exists(targetFile)) return null;
                            if (File.Exists(newPath)) { try { File.Delete(newPath); } catch { } }

                            File.Move(targetFile, newPath);
                            LogFileRename(targetFile, newPath);
                            return newPath;
                        }
                        catch (System.IO.FileNotFoundException) { return null; }
                        catch (UnauthorizedAccessException) { return null; }
                        catch (IOException) when (i < maxRetries - 1) { Thread.Sleep(500); }
                        catch (Exception) { return null; }
                    }
                    return null;
                }
            }
            return null;
        }

        #endregion

        #region Public Methods & Status Control

        public void UpdateStatusOnRun(bool isRunning)
        {
            this.isRunning = isRunning;
            SetControlEnabled(!isRunning);

            if (isRunning)
            {
                RefreshBaselineCache();
                InitializeBaselineWatcher();
                InitializeTargetWatchers();
                Task.Run(() => CompareAndRenameFiles());
            }
            else
            {
                baselineWatcher?.Dispose();
                baselineWatcher = null;
                StopTargetWatchers();
            }

            string status = isRunning ? "Running" : "Stopped";
            Color statusColor = isRunning ? Color.Green : Color.Red;
            StatusUpdated?.Invoke($"Status: {status}", statusColor);
        }

        public void InitializePanel(bool isRunning) { UpdateStatusOnRun(isRunning); }
        public void LoadDataFromSettings()
        {
            cb_BaseDatePath.Items.Clear();
            cb_BaseDatePath.Items.AddRange(settingsManager.GetFoldersFromSection("[BaseFolder]").ToArray());
            lb_TargetComparePath.Items.Clear();
            foreach (var path in settingsManager.GetFoldersFromSection("[TargetComparePath]")) lb_TargetComparePath.Items.Add(path);
        }

        public void RefreshUI() { LoadDataFromSettings(); }

        public void SetControlEnabled(bool isEnabled)
        {
            if (this.InvokeRequired) { this.Invoke(new Action(() => SetControlEnabled(isEnabled))); return; }
            btn_BaseClear.Enabled = isEnabled; btn_SelectFolder.Enabled = isEnabled; btn_Remove.Enabled = isEnabled;
            cb_BaseDatePath.Enabled = isEnabled; lb_TargetComparePath.Enabled = isEnabled;
        }

        public void UpdateStatus(string status) { }

        public void CompareAndRenameFiles()
        {
            try
            {
                RefreshBaselineCache();
                if (_baselineCache.Count == 0) return;

                foreach (string targetFolder in lb_TargetComparePath.Items.Cast<string>())
                {
                    if (!Directory.Exists(targetFolder)) continue;

                    var targetFiles = Directory.GetFiles(targetFolder, "*_#*_*.*")
                                               .Where(f => Regex.IsMatch(Path.GetFileName(f), @"_#\d+_"));

                    foreach (var targetFile in targetFiles)
                    {
                        try { ProcessTargetFile(targetFile, _baselineCache); }
                        catch (Exception innerEx) { logManager.LogError($"[ucOverrideNamesPanel] 오류: {innerEx.Message}"); }
                    }
                }
            }
            catch (Exception ex) { logManager.LogError($"[ucOverrideNamesPanel] CompareAndRenameFiles() 중 오류: {ex.Message}"); }
        }

        public void StartProcessing()
        {
            while (true)
            {
                if (IsRunning()) { CompareAndRenameFiles(); System.Threading.Thread.Sleep(1000); }
            }
        }

        private bool IsRunning() { return isRunning; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { baselineWatcher?.Dispose(); folderWatcher?.Dispose(); StopTargetWatchers(); }
            base.Dispose(disposing);
        }

        public string EnsureOverrideAndReturnPath(string originalPath, int timeoutMs = 180_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string baseFolder = settingsManager.GetBaseFolder();
            if (string.IsNullOrEmpty(baseFolder)) return originalPath;

            string baselineFolder = Path.Combine(baseFolder, "Baseline");
            string fileName = Path.GetFileName(originalPath);
            var timeMatch = Regex.Match(fileName, @"\d{8}_\d{6}");
            string searchFilter = timeMatch.Success ? $"*{timeMatch.Value}*.info" : "*.info";

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Directory.Exists(baselineFolder)) { System.Threading.Thread.Sleep(500); continue; }

                var infos = Directory.GetFiles(baselineFolder, searchFilter);
                if (infos.Length > 0)
                {
                    var baselineData = ExtractBaselineData(infos);
                    string renamed = TryRenameTargetFile(originalPath, baselineData);
                    if (!string.IsNullOrEmpty(renamed)) return renamed;
                }
                System.Threading.Thread.Sleep(500);
            }
            return originalPath;
        }

        private string TryRenameTargetFile(string srcPath, IDictionary<string, (string TimeInfo, string Prefix, string CInfo)> baselineData)
        {
            try
            {
                string newName = ProcessTargetFile(srcPath, baselineData);
                if (!string.IsNullOrEmpty(newName)) return newName;
            }
            catch (Exception ex) { logManager.LogError($"[Override] Rename 실패: {ex.Message}"); }
            return null;
        }

        #endregion
    }
}
