// ITM_Agent/Services/LogManager.cs
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO.Compression; // [필수] ZipArchive 클래스 사용

namespace ITM_Agent.Services
{
    /// <summary>
    /// 이벤트 로그, 디버그 로그, 에러 로그 등을 기록하고,
    /// 5MB 초과 시 회전(Rotation)하며, 오래된 로그를 압축 및 정리하는 클래스입니다.
    /// </summary>
    public class LogManager
    {
        private readonly string logFolderPath;
        private const long MAX_LOG_SIZE = 5 * 1024 * 1024; // 5MB

        // 전역 디버그 플래그
        private static volatile bool _globalDebugEnabled = false;
        public static bool GlobalDebugEnabled
        {
            get => _globalDebugEnabled;
            set => _globalDebugEnabled = value;
        }

        // IP 마스킹 정규식
        private static readonly Regex _ipMaskRegex = new Regex(
            @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b",
            RegexOptions.Compiled);

        // ─────────────────────────────────────────────────────────────
        // 로그 유지보수(압축/삭제)를 위한 정적 멤버
        // ─────────────────────────────────────────────────────────────
        private static Timer _maintenanceTimer;
        private static readonly object _maintenanceLock = new object();
        private static bool _isMaintenanceStarted = false;
        private const int RETENTION_DAYS = 30; // 보관 주기 30일

        public LogManager(string baseDir)
        {
            logFolderPath = Path.Combine(baseDir, "Logs");
            Directory.CreateDirectory(logFolderPath);

            // 유지보수 타이머 시작 (앱 실행 중 최초 1회만 기동)
            StartMaintenanceTimer(logFolderPath);
        }

        private static void StartMaintenanceTimer(string logDir)
        {
            lock (_maintenanceLock)
            {
                if (_isMaintenanceStarted) return;
                _isMaintenanceStarted = true;

                // 1분 후 첫 실행, 이후 1시간마다 반복
                _maintenanceTimer = new Timer(
                    state => PerformLogMaintenance((string)state),
                    logDir,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromHours(1));
            }
        }

        /// <summary>
        /// 백그라운드에서 오래된 로그를 압축하고 삭제합니다.
        /// </summary>
        private static void PerformLogMaintenance(string targetDir)
        {
            try
            {
                if (!Directory.Exists(targetDir)) return;

                DateTime now = DateTime.Now;
                DateTime retentionLimit = now.AddDays(-RETENTION_DAYS);
                string todayStr = now.ToString("yyyyMMdd");

                // 1. 오래된 파일 삭제 (Retention Policy)
                //    - .log, .zip 등 모든 파일 대상
                //    - 30일이 지난 파일 삭제
                var allFiles = Directory.GetFiles(targetDir);
                foreach (var file in allFiles)
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < retentionLimit)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { /* 사용 중이거나 권한 없음 - 무시 */ }
                }

                // 2. 로그 파일 압축 (Compression) 및 원본 삭제
                //    대상: .log 파일 중
                //      A) 오늘 날짜가 아닌 파일 (과거 로그)
                //      B) 오늘 날짜라도 회전된 파일 (_1, _2 등)
                var logFiles = Directory.GetFiles(targetDir, "*.log");
                foreach (var logFile in logFiles)
                {
                    try
                    {
                        string fileName = Path.GetFileName(logFile);
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(logFile);

                        // 조건 A: 날짜가 오늘이 아닌 파일 (예: 20250101_event.log)
                        bool isPastLog = !fileName.StartsWith(todayStr);

                        // 조건 B: 회전된 파일 (예: ..._event_1.log)
                        // 정규식: 끝이 _숫자 로 끝나는지 확인
                        bool isRotatedLog = Regex.IsMatch(fileNameNoExt, @"_\d+$");

                        // 압축 대상이면 진행
                        if (isPastLog || isRotatedLog)
                        {
                            string zipPath = Path.Combine(targetDir, fileNameNoExt + ".zip");

                            // [핵심 수정] 
                            // 1단계: 압축 파일이 없다면 생성
                            if (!File.Exists(zipPath))
                            {
                                using (var zipStream = new FileStream(zipPath, FileMode.Create))
                                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                                {
                                    // 엔트리 생성
                                    var entry = archive.CreateEntry(fileName);

                                    // 원본 파일 스트림 -> 압축 엔트리 스트림 복사
                                    using (var entryStream = entry.Open())
                                    using (var sourceStream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    {
                                        sourceStream.CopyTo(entryStream);
                                    }
                                }
                            }

                            // 2단계: 압축 파일이 존재하면 원본 로그 삭제 
                            // (방금 생성했거나, 이전 주기에 생성 후 삭제만 실패했던 경우 모두 커버)
                            if (File.Exists(zipPath))
                            {
                                File.Delete(logFile);
                            }
                        }
                    }
                    catch
                    {
                        // 압축 또는 삭제 중 오류(파일 잠김 등) 발생 시 다음 주기에 재처리됨
                    }
                }
            }
            catch
            {
                // 유지보수 로직 전체 실패 시 무시 (메인 로직 영향 없음)
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 기존 로깅 메서드
        // ─────────────────────────────────────────────────────────────

        public void LogEvent(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logFileName = $"{DateTime.Now:yyyyMMdd}_event.log";
            string logLine = $"{timestamp} [Event] {message}";

            WriteLogWithRotation(logLine, logFileName);
        }

        public void LogEvent(string message, bool isDebug)
        {
            if (isDebug) LogDebug(message);
            else LogEvent(message);
        }

        public void LogError(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logFileName = $"{DateTime.Now:yyyyMMdd}_error.log";
            string logLine = $"{timestamp} [Error] {message}";

            WriteLogWithRotation(logLine, logFileName);
        }

        public void LogDebug(string message)
        {
            if (!GlobalDebugEnabled) return;

            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string log = $"{ts} [Debug] {message}";
            string fileName = $"{DateTime.Now:yyyyMMdd}_debug.log";

            WriteLogWithRotation(log, fileName);
        }

        public void LogCustom(string message, string logType)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fileName = $"{DateTime.Now:yyyyMMdd}_{logType.ToLower()}.log";
            string logLine = $"{timestamp} [{logType.ToUpper()}] {message}";

            WriteLogWithRotation(logLine, fileName);
        }

        private string MaskIpAddress(string message)
        {
            return _ipMaskRegex.Replace(message, "*.*.*.$4");
        }

        private void WriteLogWithRotation(string message, string fileName)
        {
            string maskedMessage = MaskIpAddress(message);
            string filePath = Path.Combine(logFolderPath, fileName);

            try
            {
                RotateLogFileIfNeeded(filePath);

                const int MAX_RETRY = 3;
                for (int attempt = 1; attempt <= MAX_RETRY; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                        {
                            fs.Seek(0, SeekOrigin.End);
                            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                            {
                                sw.WriteLine(maskedMessage);
                            }
                        }
                        return;
                    }
                    catch (IOException) when (attempt < MAX_RETRY)
                    {
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write log: {ex.Message}");
            }
        }

        private void RotateLogFileIfNeeded(string filePath)
        {
            if (!File.Exists(filePath)) return;

            FileInfo fi = new FileInfo(filePath);
            if (fi.Length <= MAX_LOG_SIZE) return;

            string extension = fi.Extension;
            string withoutExt = Path.GetFileNameWithoutExtension(filePath);

            int index = 1;
            string rotatedPath;
            do
            {
                string rotatedName = $"{withoutExt}_{index}{extension}";
                rotatedPath = Path.Combine(logFolderPath, rotatedName);
                index++;
            }
            while (File.Exists(rotatedPath));

            try
            {
                File.Move(filePath, rotatedPath);
                // 회전된 파일은 다음번 Maintenance 주기(1시간 내)에 압축됨
            }
            catch { }
        }

        public static void BroadcastPluginDebug(bool enabled)
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in asms)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                    if (types == null) continue;

                    foreach (var t in types)
                    {
                        if (!t.IsClass) continue;
                        if (!string.Equals(t.Name, "SimpleLogger", StringComparison.Ordinal)) continue;

                        var m = t.GetMethod("SetDebugMode", BindingFlags.Public | BindingFlags.Static);
                        if (m == null) m = t.GetMethod("SetDebug", BindingFlags.Public | BindingFlags.Static);
                        if (m == null) continue;

                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                        {
                            try { m.Invoke(null, new object[] { enabled }); } catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }
}
