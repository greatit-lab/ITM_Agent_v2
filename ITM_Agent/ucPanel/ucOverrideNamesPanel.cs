// ITM_Agent/ucPanel/ucOverrideNamesPanel.cs
using ITM_Agent.Services;
using System;
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

        private FileSystemWatcher folderWatcher;   // 폴더 감시기

        public event Action<string, Color> StatusUpdated;
        public event Action<string> FileRenamed;
        private FileSystemWatcher baselineWatcher;

        // [추가] _#1_ 파일이 나중에 복사되어 들어오는 경우를 대비한 타겟 폴더 쌍방향 감시기
        private readonly List<FileSystemWatcher> targetWatchers = new List<FileSystemWatcher>();

        private bool isRunning = false;

        // ----------------------------
        // (1) 안정화 감지를 위한 필드
        // ----------------------------

        public ucOverrideNamesPanel(SettingsManager settingsManager, ucConfigurationPanel configPanel, LogManager logManager, bool isDebugMode)
        {
            // 필수 인자 null 체크
            this.settingsManager = settingsManager
                ?? throw new ArgumentNullException(nameof(settingsManager));
            this.configPanel = configPanel
                ?? throw new ArgumentNullException(nameof(configPanel));
            this.logManager = logManager
                ?? throw new ArgumentNullException(nameof(logManager));
            this.isDebugMode = isDebugMode;

            InitializeComponent();

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] 생성자 호출 - 초기화 시작");
            }

            InitializeBaselineWatcher();
            InitializeCustomEvents();

            // 데이터 로드
            LoadDataFromSettings();
            LoadRegexFolderPaths(); // 초기화 시 목록 로드
            LoadSelectedBaseDatePath(); // 저장된 선택 값 불러오기

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] 생성자 호출 - 초기화 완료");
            }
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
                        // 파일명만 추출
                        string fileName = Path.GetFileName(filePath);

                        // Baseline .info 파일 생성
                        string infoPath = CreateBaselineInfoFile(filePath, dateTimeInfo.Value);

                        if (!string.IsNullOrEmpty(infoPath))
                        {
                            // 감지 및 생성 성공을 한 줄로 기록
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

        /// <summary>
        /// 안전하게 파일 크기를 구하는 헬퍼
        /// </summary>
        private long GetFileSizeSafe(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var fi = new FileInfo(filePath);
                    return fi.Length;
                }
            }
            catch { /* 무시 */ }
            return 0;
        }

        /// <summary>
        /// 안전하게 LastWriteTime을 구하는 헬퍼
        /// </summary>
        private DateTime GetLastWriteTimeSafe(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    return File.GetLastWriteTime(filePath);
                }
            }
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
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] LoadRegexFolderPaths() 시작");
            }

            cb_BaseDatePath.Items.Clear();
            var folderPaths = settingsManager
                .GetRegexList()
                .Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            cb_BaseDatePath.Items.AddRange(folderPaths);
            cb_BaseDatePath.SelectedIndex = -1; // 초기화

            logManager.LogEvent("[ucOverrideNamesPanel] 정규식 경로 목록 로드 완료");
        }

        private void LoadSelectedBaseDatePath()
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] LoadSelectedBaseDatePath() 시작");
            }

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

                if (settingsManager.IsDebugMode)
                {
                    logManager.LogDebug($"[ucOverrideNamesPanel] cb_BaseDatePath_SelectedIndexChanged -> {selectedPath} 설정");
                }
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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                ProcessStableFile(e.FullPath);
            });
        }

        private void btn_BaseClear_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] btn_BaseClear_Click() - BaseDatePath 초기화");
            }

            cb_BaseDatePath.SelectedIndex = -1;
            settingsManager.RemoveSection("SelectedBaseDatePath");
            folderWatcher?.Dispose();

            logManager.LogEvent("[ucOverrideNamesPanel] BaseDatePath 해제 및 감시 중지");
        }

        private void Btn_SelectFolder_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] Btn_SelectFolder_Click() 호출");
            }

            var baseFolder = settingsManager.GetFoldersFromSection("[BaseFolder]").FirstOrDefault()
                             ?? AppDomain.CurrentDomain.BaseDirectory;

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
                        if (settingsManager.IsDebugMode)
                        {
                            logManager.LogDebug("[ucOverrideNamesPanel] 이미 추가된 폴더 선택됨");
                        }
                    }
                }
            }
        }

        private void Btn_Remove_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] Btn_Remove_Click() 호출");
            }

            if (lb_TargetComparePath.SelectedItems.Count > 0)
            {
                var confirmResult = MessageBox.Show("선택한 항목을 삭제하시겠습니까?", "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (confirmResult == DialogResult.Yes)
                {
                    var selectedItems = lb_TargetComparePath.SelectedItems.Cast<string>().ToList();
                    foreach (var item in selectedItems)
                    {
                        lb_TargetComparePath.Items.Remove(item);
                    }

                    UpdateTargetComparePathInSettings();

                    logManager.LogEvent("[ucOverrideNamesPanel] 선택한 비교 경로 삭제 완료");
                }
            }
            else
            {
                MessageBox.Show("삭제할 항목을 선택하세요.", "경고", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (settingsManager.IsDebugMode)
                {
                    logManager.LogDebug("[ucOverrideNamesPanel] 삭제할 항목 미선택");
                }
            }
        }

        private void UpdateTargetComparePathInSettings()
        {
            var folders = lb_TargetComparePath.Items.Cast<string>().ToList();
            settingsManager.SetFoldersToSection("[TargetComparePath]", folders);
        }

        #endregion

        #region 파일 처리 및 정보 추출

        private string CreateBaselineInfoFile(string filePath, DateTime dateTime)
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] CreateBaselineInfoFile() 호출 - 대상: {Path.GetFileName(filePath)}");
            }

            string baseFolder = configPanel.BaseFolderPath;
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
            {
                logManager.LogError("[ucOverrideNamesPanel] 기준 폴더가 설정되지 않았거나 존재하지 않습니다.");
                return null;
            }

            string baselineFolder = System.IO.Path.Combine(baseFolder, "Baseline");
            if (!Directory.Exists(baselineFolder))
            {
                Directory.CreateDirectory(baselineFolder);
                logManager.LogEvent($"[ucOverrideNamesPanel] Baseline 폴더 생성: {baselineFolder}");
            }

            string originalName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string newFileName = $"{dateTime:yyyyMMdd_HHmmss}_{originalName}.info";
            string newFilePath = System.IO.Path.Combine(baselineFolder, newFileName);

            try
            {
                if (File.Exists(newFilePath))
                {
                    if (settingsManager.IsDebugMode)
                        logManager.LogDebug($"[ucOverrideNamesPanel] .info 파일이 이미 존재하여 생성을 건너뜁니다: {newFilePath}");
                    return newFilePath;
                }

                using (File.Create(newFilePath)) { }

                return newFilePath;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
                if (File.Exists(newFilePath))
                {
                    logManager.LogEvent($"[ucOverrideNamesPanel] .info 파일 생성 중 일시적 잠금 발생했으나, 최종 생성 확인됨: {newFilePath}");
                    return newFilePath;
                }
                else
                {
                    logManager.LogError($"[ucOverrideNamesPanel] .info 파일 생성 실패 (재확인 후에도 파일 없음): {newFilePath}\n대상 파일: {filePath}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[ucOverrideNamesPanel] .info 파일 생성 중 예기치 않은 오류 발생: {ex.Message}\n대상 파일: {filePath}");
                return null;
            }
        }

        private bool IsFileReady(string filePath)
        {
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        private bool WaitForFileReady(string filePath, int maxRetries = 30, int delayMilliseconds = 500)
        {
            int retries = 0;
            while (retries < maxRetries)
            {
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        return true;
                    }
                }
                catch (IOException ioEx)
                {
                    if (settingsManager.IsDebugMode)
                    {
                        logManager.LogDebug($"[ucOverrideNamesPanel] 파일 잠김 대기 중: {System.IO.Path.GetFileName(filePath)} " +
                        $"(시도 {retries + 1}/{maxRetries}): {ioEx.Message}");
                    }
                    Thread.Sleep(delayMilliseconds);
                    retries++;
                }
                catch (UnauthorizedAccessException) // [핵심 방어] 권한 오류로 전체 루프 파괴 방지
                {
                    logManager.LogError($"[ucOverrideNamesPanel] 파일 접근 권한 거부 (파일 격리): {filePath}");
                    return false;
                }
                catch (Exception ex) // [핵심 방어] 예기치 않은 예외로 인한 루프 파괴 방지
                {
                    logManager.LogError($"[ucOverrideNamesPanel] 파일 접근 중 예기치 않은 오류: {ex.Message}");
                    return false;
                }
            }
            return false;
        }

        private DateTime? ExtractDateTimeFromFile(string filePath)
        {
            string datePattern = @"Date and Time:\s*(\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} (AM|PM))";
            const int maxRetries = 10;
            const int delayMs = 1000;
            const int maxBytesToRead = 8192; // 8KB만 읽음 (헤더 정보 추출용)

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] ExtractDateTimeFromFile() - 파일: {filePath}");
            }

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
                        if (match.Success && DateTime.TryParse(match.Groups[1].Value, out DateTime result))
                        {
                            return result;
                        }
                    }
                }
                catch (IOException ex)
                {
                    if (i == maxRetries - 1)
                    {
                        logManager.LogError($"[ucOverrideNamesPanel] 파일 읽기 최종 실패 (잠김 해결 불가): {ex.Message}\n파일: {filePath}");
                        return null;
                    }

                    if (settingsManager.IsDebugMode)
                    {
                        logManager.LogDebug($"[ucOverrideNamesPanel] 파일 읽기 재시도 중 ({i + 1}/{maxRetries}): {ex.Message}");
                    }
                }
                catch (OutOfMemoryException)
                {
                    logManager.LogError($"[ucOverrideNamesPanel] 메모리 부족 오류 (파일이 너무 큼): {filePath}");
                    return null;
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[ucOverrideNamesPanel] 예기치 않은 읽기 오류: {ex.Message}\n파일: {filePath}");
                    return null;
                }

                Thread.Sleep(delayMs);
            }

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] 파일에서 Date and Time 정보를 찾을 수 없거나 끝까지 사용 중: {filePath}");
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

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] InitializeBaselineWatcher() 호출");
            }

            var baseFolder = settingsManager.GetFoldersFromSection("[BaseFolder]").FirstOrDefault();
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
            {
                logManager.LogError("[ucOverrideNamesPanel] 유효하지 않은 BaseFolder로 인해 BaselineWatcher 초기화 불가");
                return;
            }

            var baselineFolder = Path.Combine(baseFolder, "Baseline");
            if (!Directory.Exists(baselineFolder))
            {
                logManager.LogError("[ucOverrideNamesPanel] Baseline 폴더가 존재하지 않아 BaselineWatcher 초기화 불가");
                return;
            }

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
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] OnBaselineFileChanged() - Baseline 파일 변경 감지: {e.FullPath}");
            }

            if (File.Exists(e.FullPath))
            {
                var baselineData = ExtractBaselineData(new[] { e.FullPath });

                foreach (string targetFolder in lb_TargetComparePath.Items)
                {
                    if (!Directory.Exists(targetFolder)) continue;

                    var targetFiles = Directory.GetFiles(targetFolder);
                    foreach (var targetFile in targetFiles)
                    {
                        // [핵심 방어벽] 문제의 독사과(단일 에러 파일)가 루프를 파괴하지 못하도록 내부에 try-catch 배치
                        try
                        {
                            string newFileName = ProcessTargetFile(targetFile, baselineData);
                            if (!string.IsNullOrEmpty(newFileName))
                            {
                                string newFilePath = Path.Combine(targetFolder, newFileName);

                                try
                                {
                                    if (!File.Exists(targetFile))
                                    {
                                        if (settingsManager.IsDebugMode)
                                            logManager.LogDebug($"[ucOverrideNamesPanel] 원본 파일을 찾을 수 없어 건너뜁니다: {targetFile}");
                                        continue;
                                    }

                                    // [추가] 목적지 파일 덮어쓰기 에러 방지 선행 삭제
                                    if (File.Exists(newFilePath))
                                    {
                                        try { File.Delete(newFilePath); } catch { }
                                    }

                                    File.Move(targetFile, newFilePath);
                                    LogFileRename(targetFile, newFilePath);
                                }
                                catch (IOException ioEx)
                                {
                                    logManager.LogError($"[ucOverrideNamesPanel] 파일 이동 중 오류 발생: {ioEx.Message}\n파일: {targetFile}");
                                }
                                catch (Exception ex)
                                {
                                    logManager.LogError($"[ucOverrideNamesPanel] 예기치 않은 오류 발생: {ex.Message}\n파일: {targetFile}");
                                }
                            }
                        }
                        catch (Exception innerEx)
                        {
                            // 오류가 나도 로그만 남기고 루프는 정상적으로 다음 파일로 진행됨
                            logManager.LogError($"[ucOverrideNamesPanel] OnBaselineFileChanged 개별 파일 오류 무시됨: {innerEx.Message}");
                        }
                    }
                }
            }
        }

        // [추가] Target 폴더를 직접 감시 (쌍방향 감시 체계)
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
            logManager.LogEvent($"[ucOverrideNamesPanel] Target Watchers 초기화 완료 ({targetWatchers.Count}개 폴더 감시 중)");
        }

        private void StopTargetWatchers()
        {
            foreach (var w in targetWatchers)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            targetWatchers.Clear();
        }

        // [추가] Target 파일(_#1_)이 나중에 들어온 경우 Baseline에서 정보 조회
        private void OnTargetFileEvent(object sender, FileSystemEventArgs e)
        {
            if (!isRunning) return;
            if (!e.Name.Contains("_#1_")) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!WaitForFileReady(e.FullPath, maxRetries: 20, delayMilliseconds: 500)) return;

                    string baselineFolder = Path.Combine(configPanel.BaseFolderPath, "Baseline");
                    if (!Directory.Exists(baselineFolder)) return;

                    var baselineFiles = Directory.GetFiles(baselineFolder, "*.info");
                    if (baselineFiles.Length == 0) return;

                    var baselineData = ExtractBaselineData(baselineFiles);

                    ProcessTargetFile(e.FullPath, baselineData);
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[ucOverrideNamesPanel] OnTargetFileEvent Error: {ex.Message}");
                }
            });
        }

        private void LogFileRename(string oldPath, string newPath)
        {
            string changedFileName = Path.GetFileName(newPath);
            string logMessage = $"[ucOverrideNamesPanel] 파일 이름 변경: {oldPath} -> {changedFileName}";
            logManager.LogEvent(logMessage);

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] 파일 변경 상세 로그 기록: {logMessage}");
            }

            FileRenamed?.Invoke(newPath);
        }

        private Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> ExtractBaselineData(string[] files)
        {
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] ExtractBaselineData() 호출");
            }

            var baselineData = new Dictionary<string, (string, string, string)>();
            // C\dW\d+ 로 수정하여 두자리수 슬롯 완벽 지원 (C1W22 등)
            var regex = new Regex(@"(\d{8}_\d{6})_(.+?)_(C\dW\d+)", RegexOptions.IgnoreCase);

            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                var match = regex.Match(fileName);
                if (match.Success)
                {
                    string timeInfo = match.Groups[1].Value;
                    string prefix = match.Groups[2].Value;
                    string cInfo = match.Groups[3].Value;

                    baselineData[fileName] = (timeInfo, prefix, cInfo);
                }
            }

            logManager.LogEvent("[ucOverrideNamesPanel] Baseline 파일에서 TimeInfo Prefix CInfo 추출 완료");
            return baselineData;
        }

        private string ProcessTargetFile(string targetFile, Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> baselineData)
        {
            if (!WaitForFileReady(targetFile, maxRetries: 10, delayMilliseconds: 300))
            {
                if (settingsManager.IsDebugMode)
                {
                    logManager.LogDebug($"[ucOverrideNamesPanel] ProcessTargetFile - 파일이 준비되지 않아 건너뜁니다: {targetFile}");
                }
                return null;
            }

            string fileName = Path.GetFileName(targetFile);
            if (!fileName.Contains("_#1_")) return null; // _#1_ 이 없으면 변경 불필요

            var sortedData = baselineData.Values.OrderByDescending(d => d.TimeInfo).ToList();

            // 특수문자가 제거된 파일명(비교용)
            string cleanFileName = Regex.Replace(fileName, @"[^a-zA-Z0-9]", "").ToUpperInvariant();

            foreach (var data in sortedData)
            {
                // [필수 1] 시간 문자열 완벽 일치 (시간이 다르면 다른 웨이퍼/스텝이므로 즉시 패스)
                if (!fileName.Contains(data.TimeInfo))
                {
                    continue;
                }

                // [필수 2] Prefix를 조각내어 타겟 파일명에 100% 모두 포함되어 있는지 확인
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

                // 시간과 모든 토큰이 완벽히 일치할 때만 CInfo(예: C2W1)로 변경
                if (lotMatch)
                {
                    string newName = fileName.Replace("_#1_", $"_{data.CInfo}_");

                    if (newName.Equals(fileName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string newPath = Path.Combine(Path.GetDirectoryName(targetFile), newName);

                    const int maxRetries = 10;
                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            if (!File.Exists(targetFile))
                            {
                                if (settingsManager.IsDebugMode)
                                {
                                    logManager.LogDebug($"[ucOverrideNamesPanel] 이동할 원본 파일이 사라졌습니다 (재시도 루프 내 확인): {targetFile}");
                                }
                                return null;
                            }

                            // [추가] 목적지 파일 덮어쓰기 에러 방지 선행 삭제
                            if (File.Exists(newPath))
                            {
                                try { File.Delete(newPath); } catch { }
                            }

                            File.Move(targetFile, newPath);
                            LogFileRename(targetFile, newPath);
                            return newPath;
                        }
                        catch (System.IO.FileNotFoundException)
                        {
                            if (settingsManager.IsDebugMode)
                            {
                                logManager.LogDebug($"[ucOverrideNamesPanel] 파일 이동 시 원본 파일을 찾을 수 없음 (FileNotFoundException): {fileName}");
                            }
                            return null;
                        }
                        catch (UnauthorizedAccessException) // [핵심 방어] 권한 에러 격리
                        {
                            return null;
                        }
                        catch (IOException) when (i < maxRetries - 1)
                        {
                            if (settingsManager.IsDebugMode)
                            {
                                logManager.LogDebug($"[ucOverrideNamesPanel] 파일 이동 잠금 충돌, 재시도 ({i + 1}/{maxRetries}): {fileName}");
                            }
                            Thread.Sleep(500);
                        }
                        catch (Exception ex)
                        {
                            logManager.LogError($"[ucOverrideNamesPanel] 파일 이름 변경 실패 (재시도 중 오류 발생): {fileName}. 이유: {ex.Message}");
                            return null;
                        }
                    }

                    logManager.LogError($"[ucOverrideNamesPanel] 파일 이름 변경 최종 실패 (최대 재시도 도달): {fileName}");
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
                InitializeBaselineWatcher();
                InitializeTargetWatchers();
                Task.Run(() => CompareAndRenameFiles()); // 기존 쌓인 파일 일괄 스윕
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

            logManager.LogEvent($"[ucOverrideNamesPanel] 상태 업데이트 - {status}");
        }

        public void InitializePanel(bool isRunning)
        {
            UpdateStatusOnRun(isRunning);
        }

        public void LoadDataFromSettings()
        {
            if (isDebugMode)
            {
                logManager.LogDebug("[ucOverrideNamesPanel] LoadDataFromSettings() 호출");
            }

            var baseFolders = settingsManager.GetFoldersFromSection("[BaseFolder]");
            cb_BaseDatePath.Items.Clear();
            cb_BaseDatePath.Items.AddRange(baseFolders.ToArray());

            var comparePaths = settingsManager.GetFoldersFromSection("[TargetComparePath]");
            lb_TargetComparePath.Items.Clear();
            foreach (var path in comparePaths)
            {
                lb_TargetComparePath.Items.Add(path);
            }

            logManager.LogEvent("[ucOverrideNamesPanel] 설정에서 BaseFolder 및 TargetComparePath 로드 완료");
        }

        public void RefreshUI()
        {
            LoadDataFromSettings();
        }

        public void SetControlEnabled(bool isEnabled)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetControlEnabled(isEnabled)));
                return;
            }

            btn_BaseClear.Enabled = isEnabled;
            btn_SelectFolder.Enabled = isEnabled;
            btn_Remove.Enabled = isEnabled;
            cb_BaseDatePath.Enabled = isEnabled;
            lb_TargetComparePath.Enabled = isEnabled;
        }

        public void UpdateStatus(string status)
        {
            if (isDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] UpdateStatus() - 현재 상태: {status}");
            }
        }

        public void CompareAndRenameFiles()
        {
            logManager.LogDebug("[ucOverrideNamesPanel] CompareAndRenameFiles() 호출");

            try
            {
                string baselineFolder = Path.Combine(settingsManager.GetBaseFolder(), "Baseline");
                if (!Directory.Exists(baselineFolder))
                {
                    logManager.LogError("[ucOverrideNamesPanel] Baseline 폴더가 존재하지 않습니다.");
                    return;
                }

                var baselineFiles = Directory.GetFiles(baselineFolder, "*.info");
                var baselineData = ExtractBaselineData(baselineFiles);

                if (baselineData.Count == 0)
                {
                    logManager.LogEvent("[ucOverrideNamesPanel] Baseline 폴더에 유효한 .info 파일이 없습니다.");
                    return;
                }

                foreach (string targetFolder in lb_TargetComparePath.Items.Cast<string>())
                {
                    if (!Directory.Exists(targetFolder)) continue;

                    foreach (var targetFile in Directory.GetFiles(targetFolder))
                    {
                        // [핵심 방어벽] 문제의 독사과(단일 에러 파일)가 루프를 파괴하지 못하도록 내부에 try-catch 배치
                        try
                        {
                            if (targetFile.Contains("_#1_"))
                            {
                                string newFileName = ProcessTargetFile(targetFile, baselineData);
                                if (string.IsNullOrEmpty(newFileName))
                                    continue;

                                string originalName = Path.GetFileName(targetFile);
                                if (newFileName.Equals(originalName, StringComparison.Ordinal))
                                    continue;
                            }
                        }
                        catch (Exception innerEx)
                        {
                            // 오류가 나도 로그만 남기고 루프는 정상적으로 다음 파일로 진행됨
                            logManager.LogError($"[ucOverrideNamesPanel] 개별 파일({Path.GetFileName(targetFile)}) 처리 중 무시된 오류: {innerEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[ucOverrideNamesPanel] CompareAndRenameFiles() 중 예기치 않은 오류: {ex.Message}");
            }
        }

        public void StartProcessing()
        {
            logManager.LogDebug("[ucOverrideNamesPanel] StartProcessing() 호출 - 상시 가동 루프 시작");

            while (true)
            {
                if (IsRunning())
                {
                    CompareAndRenameFiles();
                    System.Threading.Thread.Sleep(1000);
                }
            }
        }

        private bool IsRunning()
        {
            return isRunning;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                baselineWatcher?.Dispose();
                folderWatcher?.Dispose();
                StopTargetWatchers();
            }
            base.Dispose(disposing);
        }

        public string EnsureOverrideAndReturnPath(string originalPath, int timeoutMs = 180_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string baseFolder = settingsManager.GetBaseFolder();
            if (string.IsNullOrEmpty(baseFolder)) return originalPath;

            string baselineFolder = Path.Combine(baseFolder, "Baseline");

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!Directory.Exists(baselineFolder))
                {
                    System.Threading.Thread.Sleep(500);
                    continue;
                }

                var infos = Directory.GetFiles(baselineFolder, "*.info");
                if (infos.Length > 0)
                {
                    var baselineData = ExtractBaselineData(infos);
                    string renamed = TryRenameTargetFile(originalPath, baselineData);
                    if (!string.IsNullOrEmpty(renamed))
                    {
                        return renamed;
                    }
                }

                System.Threading.Thread.Sleep(500);
            }

            logManager.LogDebug($"[Override] .info 매핑 실패 (Timeout), rename skip : {originalPath}");
            return originalPath;
        }

        private string TryRenameTargetFile(string srcPath, Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> baselineData)
        {
            try
            {
                string newName = ProcessTargetFile(srcPath, baselineData);

                if (!string.IsNullOrEmpty(newName))
                {
                    return newName;
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[Override] Rename 실패: {ex.Message}");
            }
            return null;
        }

        #endregion
    }
}
