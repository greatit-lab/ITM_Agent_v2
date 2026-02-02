// ITM_Agent/ucPanel/ucOverrideNamesPanel.cs
using ITM_Agent.Services;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
            // Event Log 예시
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
                // Debug Log 예시
                logManager.LogDebug("[ucOverrideNamesPanel] LoadRegexFolderPaths() 시작");
            }

            cb_BaseDatePath.Items.Clear();
            var folderPaths = settingsManager
                .GetRegexList()
                .Values
                .Distinct(StringComparer.OrdinalIgnoreCase)        // [추가] 중복 제거
                .ToArray();

            cb_BaseDatePath.Items.AddRange(folderPaths);          // [수정]
            cb_BaseDatePath.SelectedIndex = -1; // 초기화

            // Event Log 예시
            logManager.LogEvent("[ucOverrideNamesPanel] 정규식 경로 목록 로드 완료");
        }  // LoadRegexFolderPaths() 끝

        private void LoadSelectedBaseDatePath()
        {
            if (settingsManager.IsDebugMode)
            {
                // Debug Log
                logManager.LogDebug("[ucOverrideNamesPanel] LoadSelectedBaseDatePath() 시작");
            }

            string selectedPath = settingsManager.GetValueFromSection("SelectedBaseDatePath", "Path");
            if (!string.IsNullOrEmpty(selectedPath) && cb_BaseDatePath.Items.Contains(selectedPath))
            {
                cb_BaseDatePath.SelectedItem = selectedPath;
                StartFolderWatcher(selectedPath);
            }

            // Event Log
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
                    // Debug Log
                    logManager.LogDebug($"[ucOverrideNamesPanel] cb_BaseDatePath_SelectedIndexChanged -> {selectedPath} 설정");
                }
            }
        }

        private void StartFolderWatcher(string path)
        {
            // 기존 감시 중지
            folderWatcher?.Dispose();

            // Event Log
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

                // (중요) "이벤트가 들어오면 Dictionary에 기록"만 수행
                folderWatcher.Created += OnFileSystemEvent;
                folderWatcher.Changed += OnFileSystemEvent;
            }
            else
            {
                // Error Log
                logManager.LogError($"[ucOverrideNamesPanel] 지정된 경로가 존재하지 않습니다: {path}");
            }
        }

        /// <summary>
        /// FileSystemWatcher 이벤트 핸들러 (Created / Changed)
        /// 파일을 바로 처리하지 않고, Dictionary에 추적 정보를 기록해둠
        /// </summary>
        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            // FileWatcherManager가 이미 5초 안정화를 거친 후 복사한 파일입니다.
            // 2초 타이머 대기를 제거하고 즉시 처리를 시도합니다.
            logManager.LogDebug($"[ucOverrideNamesPanel] File event received, processing immediately: {e.FullPath}");

            // FileSystemWatcher 이벤트 핸들러가 차단되지 않도록
            // 별도 스레드 풀 스레드에서 즉시 처리를 시작합니다.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // ProcessStableFile 내부에서 파일 읽기/잠금 등을 처리합니다.
                ProcessStableFile(e.FullPath);
            });
        }

        private void btn_BaseClear_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode)
            {
                // Debug Log
                logManager.LogDebug("[ucOverrideNamesPanel] btn_BaseClear_Click() - BaseDatePath 초기화");
            }

            cb_BaseDatePath.SelectedIndex = -1;
            settingsManager.RemoveSection("SelectedBaseDatePath"); // 저장된 값 삭제
            folderWatcher?.Dispose();

            // Event Log
            logManager.LogEvent("[ucOverrideNamesPanel] BaseDatePath 해제 및 감시 중지");
        }

        private void Btn_SelectFolder_Click(object sender, EventArgs e)
        {
            if (settingsManager.IsDebugMode)
            {
                // Debug Log
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

                        // Event Log
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
                // Debug Log
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

                    // Event Log
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

        #region 기존 메서드(읽기/처리 로직) 변경 없이 재사용

        private string CreateBaselineInfoFile(string filePath, DateTime dateTime)
        {
            if (settingsManager.IsDebugMode)
            {
                // Debug: 메서드 호출
                logManager.LogDebug($"[ucOverrideNamesPanel] CreateBaselineInfoFile() 호출 - 대상: {Path.GetFileName(filePath)}");
            }

            // 기준 폴더 유효성 검사
            string baseFolder = configPanel.BaseFolderPath;
            if (string.IsNullOrEmpty(baseFolder) || !Directory.Exists(baseFolder))
            {
                logManager.LogError("[ucOverrideNamesPanel] 기준 폴더가 설정되지 않았거나 존재하지 않습니다.");
                return null;
            }

            // Baseline 폴더 경로 준비
            string baselineFolder = System.IO.Path.Combine(baseFolder, "Baseline");
            if (!Directory.Exists(baselineFolder))
            {
                Directory.CreateDirectory(baselineFolder);
                logManager.LogEvent($"[ucOverrideNamesPanel] Baseline 폴더 생성: {baselineFolder}");
            }

            // 새로운 .info 파일명 생성
            string originalName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string newFileName = $"{dateTime:yyyyMMdd_HHmmss}_{originalName}.info";
            string newFilePath = System.IO.Path.Combine(baselineFolder, newFileName);

            try
            {
                if (File.Exists(newFilePath))
                {
                    if (settingsManager.IsDebugMode)
                    {
                        logManager.LogDebug($"[ucOverrideNamesPanel] .info 파일이 이미 존재하여 생성을 건너뜁니다: {newFilePath}");
                    }
                    return newFilePath;
                }

                using (File.Create(newFilePath)) { }

                return newFilePath;
            }
            catch (IOException)
            {
                // IOException 발생 시, 잠시 대기 후 파일이 생성되었는지 확인
                Thread.Sleep(250);
                if (File.Exists(newFilePath))
                {
                    // 파일이 존재하면, 다른 스레드가 성공적으로 생성한 것으로 간주하고 정상 처리
                    logManager.LogEvent($"[ucOverrideNamesPanel] .info 파일 생성 중 일시적 잠금 발생했으나, 최종 생성 확인됨: {newFilePath}");
                    return newFilePath;
                }
                else
                {
                    // 잠시 대기 후에도 파일이 없으면 실제 오류로 보고
                    logManager.LogError($"[ucOverrideNamesPanel] .info 파일 생성 실패 (재확인 후에도 파일 없음): {newFilePath}\n대상 파일: {filePath}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                // 이 외의 진짜 실패는 Error 레벨로 기록
                logManager.LogError($"[ucOverrideNamesPanel] .info 파일 생성 중 예기치 않은 오류 발생: {ex.Message}\n대상 파일: {filePath}");
                return null;
            }
        }

        private bool IsFileReady(string filePath)
        {
            try
            {
                // [수정] FileShare.Delete 권한을 제거하여,
                // FileWatcherManager가 FileShare.ReadWrite로 쓰는(복사) 중에도 읽기 접근이 가능하도록 허용합니다.
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite))
                {
                    return true; // 파일에 액세스 가능
                }
            }
            catch (IOException)
            {
                return false; // 파일이 잠겨 있음
            }
        }

        private bool WaitForFileReady(string filePath, int maxRetries = 30, int delayMilliseconds = 500)
        {
            int retries = 0;
            while (retries < maxRetries)
            {
                try
                {
                    using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        // 파일 열기에 성공했으므로 준비 완료
                        return true;
                    }
                }
                catch (IOException ioEx)
                {
                    if (settingsManager.IsDebugMode)
                    {
                        // 잠김 충돌은 Debug로만 기록하고 재시도
                        logManager.LogDebug($"[ucOverrideNamesPanel] 파일 잠김 대기 중: {System.IO.Path.GetFileName(filePath)} " +
                        $"(시도 {retries + 1}/{maxRetries}): {ioEx.Message}");
                    }
                    Thread.Sleep(delayMilliseconds);
                    retries++;
                }
            }
            // 최종적으로도 못 열면 false만 반환 (로그 없음)
            return false;
        }

        // [수정] 대용량 파일 OOM 방지 및 파일 잠금 시간 최소화
        private DateTime? ExtractDateTimeFromFile(string filePath)
        {
            string datePattern = @"Date and Time:\s*(\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} (AM|PM))";
            const int maxRetries = 5;
            const int delayMs = 1000;
            const int maxBytesToRead = 8192; // 8KB만 읽음 (헤더 정보 추출용)

            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] ExtractDateTimeFromFile() - 파일: {filePath}");
            }

            for (int i = 0; i < maxRetries; i++)
            {
                if (IsFileReady(filePath))
                {
                    try
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var reader = new StreamReader(fileStream))
                        {
                            // [핵심 변경] 전체 파일을 읽지 않고 앞부분 버퍼만 읽음
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
                        logManager.LogError($"[ucOverrideNamesPanel] 파일 읽기 중 오류 발생: {ex.Message}\n파일: {filePath}");
                        return null;
                    }
                    catch (OutOfMemoryException)
                    {
                        // 혹시라도 발생할 경우를 대비한 안전장치
                        logManager.LogError($"[ucOverrideNamesPanel] 메모리 부족 오류 (파일이 너무 큼): {filePath}");
                        return null;
                    }
                }
                else
                {
                    Thread.Sleep(delayMs); // 파일이 잠겨있으면 대기
                }
            }

            // [수정] 파일 잠김을 Error가 아닌 Debug 로그로 처리하여 불필요한 알림 제거
            if (settingsManager.IsDebugMode)
            {
                logManager.LogDebug($"[ucOverrideNamesPanel] 파일이 사용 중이어서 처리할 수 없습니다(Skipped). 파일: {filePath}");
            }
            return null;
        }

        #endregion

        #region BaselineWatcher (기존 그대로)

        private void InitializeBaselineWatcher()
        {
            if (baselineWatcher != null)
            {
                // 혹은 baselineWatcher.Dispose() 후 null 할당
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
                // Debug Log
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
                        string newFileName = ProcessTargetFile(targetFile, baselineData);
                        if (!string.IsNullOrEmpty(newFileName))
                        {
                            string newFilePath = Path.Combine(targetFolder, newFileName);

                            try
                            {
                                // 파일 경로 존재 여부 확인
                                if (!File.Exists(targetFile))
                                {
                                    if (settingsManager.IsDebugMode)
                                    {
                                        logManager.LogDebug($"[ucOverrideNamesPanel] 원본 파일을 찾을 수 없어 건너뜀: {targetFile}");
                                    }
                                    continue;
                                }

                                File.Move(targetFile, newFilePath);
                                // 변경 내용 로그 기록
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
                }
            }
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

            // ★★★ ucUploadPanel에게 알림 ★★★
            FileRenamed?.Invoke(newPath);
        }

        private Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> ExtractBaselineData(string[] files)
        {
            if (settingsManager.IsDebugMode)
            {
                // Debug Log
                logManager.LogDebug("[ucOverrideNamesPanel] ExtractBaselineData() 호출");
            }

            var baselineData = new Dictionary<string, (string, string, string)>();
            var regex = new Regex(@"(\d{8}_\d{6})_([^_]+?)_(C\dW\d+)");

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

            // Event Log: 추출된 데이터의 의미를 명확히 표시
            logManager.LogEvent("[ucOverrideNamesPanel] Baseline 파일에서 TimeInfo Prefix CInfo 추출 완료");
            return baselineData;
        }

        private string ProcessTargetFile(string targetFile, Dictionary<string, (string TimeInfo, string Prefix, string CInfo)> baselineData)
        {
            // ▼▼▼ [수정] 파일 이름 변경(File.Move)에 대한 재시도 로직 강화 ▼▼▼
            // 파일이 실제로 처리 가능한 상태인지 먼저 확인
            if (!WaitForFileReady(targetFile, maxRetries: 10, delayMilliseconds: 300))
            {
                // 파일이 잠겨 있거나 접근 불가능하면 Debug 로그만 남기고 처리 중단
                if (settingsManager.IsDebugMode) // isDebugMode 사용
                {
                    logManager.LogDebug($"[ucOverrideNamesPanel] ProcessTargetFile - 파일이 준비되지 않아 건너뜁니다: {targetFile}");
                }
                return null; // 처리 실패 또는 불가능
            }

            string fileName = Path.GetFileName(targetFile); // 파일 이름 추출

            // Baseline 데이터와 비교하여 변경할 새 이름 찾기
            foreach (var data in baselineData.Values)
            {
                // 파일 이름에 Baseline 데이터의 TimeInfo와 Prefix가 포함되어 있는지 확인
                if (fileName.Contains(data.TimeInfo) && fileName.Contains(data.Prefix))
                {
                    // "_#1_" 부분을 "_CInfo_" (예: "_C3W1_")로 변경하여 새 이름 생성
                    string newName = Regex.Replace(fileName, @"_#1_", $"_{data.CInfo}_");

                    // 새 이름이 원래 이름과 같다면 변경 불필요, 다음 Baseline 데이터로 넘어감
                    if (newName.Equals(fileName, StringComparison.Ordinal))
                    {
                        continue; // 변경 없음
                    }

                    // 새 파일 경로 생성
                    string newPath = Path.Combine(Path.GetDirectoryName(targetFile), newName);

                    // 파일 이동 재시도 로직 (최대 10번)
                    const int maxRetries = 10;
                    for (int i = 0; i < maxRetries; i++)
                    {
                        try
                        {
                            // File.Move 전에 파일 존재 여부 재확인 (경쟁 조건 방지)
                            if (!File.Exists(targetFile))
                            {
                                // 이동 시도 전에 파일이 사라진 경우
                                if (settingsManager.IsDebugMode) // isDebugMode 사용
                                {
                                    logManager.LogDebug($"[ucOverrideNamesPanel] 이동할 원본 파일이 사라졌습니다 (재시도 루프 내 확인): {targetFile}");
                                }
                                return null; // 이미 처리된 것으로 간주하고 종료
                            }

                            // 파일 이동 시도
                            File.Move(targetFile, newPath);
                            LogFileRename(targetFile, newPath); // 성공 로그 기록
                            return newPath; // 성공 시 새 경로 반환
                        }
                        // ▼▼▼ FileNotFoundException 처리 추가 ▼▼▼
                        catch (System.IO.FileNotFoundException)
                        {
                            // File.Move 시점에 파일이 사라진 경우 (경쟁 조건 가능성)
                            // Error 대신 Debug 로그 기록 또는 무시
                            if (settingsManager.IsDebugMode) // isDebugMode 사용
                            {
                                logManager.LogDebug($"[ucOverrideNamesPanel] 파일 이동 시 원본 파일을 찾을 수 없음 (FileNotFoundException): {fileName}");
                            }
                            // 이미 다른 곳에서 처리되었을 가능성이 있으므로 null 반환 (오류 아님)
                            return null;
                        }
                        // ▲▲▲ FileNotFoundException 처리 끝 ▲▲▲
                        catch (IOException) when (i < maxRetries - 1) // 마지막 재시도가 아닐 때만 IOException 처리
                        {
                            // 파일 잠금 등 IO 관련 예외 발생 시 재시도
                            if (settingsManager.IsDebugMode) // isDebugMode 사용
                            {
                                logManager.LogDebug($"[ucOverrideNamesPanel] 파일 이동 잠금 충돌, 재시도 ({i + 1}/{maxRetries}): {fileName}");
                            }
                            Thread.Sleep(500); // 잠시 대기 후 재시도
                        }
                        catch (Exception ex) // 그 외 예외 (UnauthorizedAccessException 등) 또는 마지막 재시도 실패 시
                        {
                            // 최종 실패 시 Error 로그 기록
                            logManager.LogError($"[ucOverrideNamesPanel] 파일 이름 변경 실패 (재시도 중 오류 발생): {fileName}. 이유: {ex.Message}");
                            return null; // 실패 시 null 반환
                        }
                    } // 재시도 루프 끝

                    // 모든 재시도 후에도 실패한 경우 (IOException으로 루프 종료)
                    logManager.LogError($"[ucOverrideNamesPanel] 파일 이름 변경 최종 실패 (최대 재시도 도달): {fileName}");
                    return null; // 최종 실패 시 null 반환
                }
            } // foreach (var data...) 끝

            // 어떤 Baseline 데이터와도 매칭되지 않은 경우
            return null;
        }

        #endregion

        #region Public Methods & Status Control

        // ▼▼▼ [핵심 수정] 컨트롤 활성화/비활성화 로직을 이곳으로 일원화 ▼▼▼
        public void UpdateStatusOnRun(bool isRunning)
        {
            // isRunning이 true (Running or Holding) -> Controls Disabled
            SetControlEnabled(!isRunning);

            string status = isRunning ? "Running" : "Stopped";
            Color statusColor = isRunning ? Color.Green : Color.Red;

            StatusUpdated?.Invoke($"Status: {status}", statusColor);

            // Event Log
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
                // Debug Log
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

            // Event Log
            logManager.LogEvent("[ucOverrideNamesPanel] 설정에서 BaseFolder 및 TargetComparePath 로드 완료");
        }

        public void RefreshUI()
        {
            LoadDataFromSettings();
        }

        public void SetControlEnabled(bool isEnabled)
        {
            // UI 스레드 안전 호출 (필요 시)
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

        // ▼▼▼ [핵심 수정] UpdateStatus는 컨트롤 상태를 변경하지 않도록 수정 ▼▼▼
        public void UpdateStatus(string status)
        {
            // 이 메서드에서는 컨트롤 활성화 여부를 건드리지 않습니다.
            // MainForm에서 UpdateStatusOnRun(bool)을 호출하여 제어합니다.

            //bool isRunning = status == "Running...";
            //SetControlEnabled(!isRunning); // 삭제됨

            if (isDebugMode)
            {
                // Debug Log
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
                        string newFileName = ProcessTargetFile(targetFile, baselineData);
                        if (string.IsNullOrEmpty(newFileName))
                            continue;  // 변경 불필요 또는 패턴 불일치

                        string originalName = Path.GetFileName(targetFile);
                        // 안전장치: 동일 이름 재이동 방지
                        if (newFileName.Equals(originalName, StringComparison.Ordinal))
                            continue;

                        string newFilePath = Path.Combine(targetFolder, newFileName);
                        File.Move(targetFile, newFilePath);
                        LogFileRename(targetFile, newFilePath);
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
            // Debug Log
            logManager.LogDebug("[ucOverrideNamesPanel] StartProcessing() 호출 - 상시 가동 루프 시작");

            while (true) // 상시 가동 상태
            {
                if (IsRunning())
                {
                    CompareAndRenameFiles();
                    System.Threading.Thread.Sleep(1000); // 작업 주기 조정
                }
            }
        }

        private bool IsRunning()
        {
            // Running 상태 확인 로직 구현
            return true; // 임시로 항상 true 반환
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                baselineWatcher?.Dispose();
                folderWatcher?.Dispose();
            }
            base.Dispose(disposing);
        }

        public string EnsureOverrideAndReturnPath(string originalPath, int timeoutMs = 180_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // (1) Baseline 폴더 및 검색 패턴
            string baselineFolder = Path.Combine(settingsManager.GetBaseFolder(), "Baseline");
            string waferId = Path.GetFileNameWithoutExtension(originalPath).Split('_').First(); // PSD276.1
            string pat = $"{waferId}*.info";                                                   // PSD276.1*.info

            string infoPath = null;

            // (2) 지정 시간(timeoutMs) 동안 .info 도착 대기
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var infos = Directory.GetFiles(baselineFolder, pat);
                if (infos.Length > 0)
                {
                    infoPath = infos[0];           // 여러 개면 첫 번째 사용
                    break;
                }
                System.Threading.Thread.Sleep(300);
            }

            // (3) .info 없으면 rename skip → 원본 경로 반환
            if (infoPath == null)
            {
                logManager.LogDebug($"[Override] .info 미발견, rename skip : {originalPath}");
                return originalPath;
            }

            // (4) .info 찾았으면 rename 시도
            string renamed = TryRenameTargetFile(originalPath, infoPath);
            return string.IsNullOrEmpty(renamed) ? originalPath : renamed;
        }

        private string TryRenameTargetFile(string srcPath, string infoPath)
        {
            if (string.IsNullOrEmpty(infoPath))
                return null;                                  // 방어 코드

            try
            {
                // ExtractBaselineData() 가 string[] 파라미터를 요구하므로 래핑
                var baselineData = ExtractBaselineData(new[] { infoPath });

                // “_#1_” → “_C3W1_” 등, 실제 새 파일명 계산
                string newName = ProcessTargetFile(srcPath, baselineData);

                if (!string.IsNullOrEmpty(newName))
                {
                    string dst = Path.Combine(Path.GetDirectoryName(srcPath), newName);

                    if (!File.Exists(dst))
                    {
                        File.Move(srcPath, dst);              // rename 실행
                        LogFileRename(srcPath, dst);          // 로그 기록
                    }
                    return dst;                               // ✅ rename 성공
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[Override] Rename 실패: {ex.Message}");
            }
            return null;                                      // 대상 파일 없음 → skip
        }
        #endregion
    }
}
