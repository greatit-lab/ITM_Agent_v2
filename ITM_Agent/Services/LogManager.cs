// ITM_Agent/Services/LogManager.cs
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;

namespace ITM_Agent.Services
{
    /// <summary>
    /// 이벤트 로그, 디버그 로그, 에러 로그 등을 기록하고,
    /// 각 로그 파일이 5MB를 초과할 경우 파일을 회전(로테이션)하는 클래스입니다.
    /// </summary>
    public class LogManager
    {
        private readonly string logFolderPath;
        private const long MAX_LOG_SIZE = 5 * 1024 * 1024; // 5MB

        /* ────────────────────────────────────────────────────────────── */
        /* ★ 추가 : 모든 인스턴스에서 공유하는 Debug 전역 플래그           */
        /* ────────────────────────────────────────────────────────────── */
        private static volatile bool _globalDebugEnabled = false;
        public static bool GlobalDebugEnabled          // MainForm 등에서 ON/OFF
        {
            get => _globalDebugEnabled;
            set => _globalDebugEnabled = value;
        }
        /* ────────────────────────────────────────────────────────────── */

        public LogManager(string baseDir)
        {
            logFolderPath = Path.Combine(baseDir, "Logs");
            Directory.CreateDirectory(logFolderPath);
        }

        /// <summary>
        /// (이벤트 로그) 간략하고 핵심적인 메시지만 기록.
        /// 5MB 초과 시 "yyyyMMdd_event_1.log"로 회전.
        /// </summary>
        public void LogEvent(string message)
        {
            // 이벤트 로그: 간략 표기 [EVT]
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logFileName = $"{DateTime.Now:yyyyMMdd}_event.log";
            string logLine = $"{timestamp} [Event] {message}";

            WriteLogWithRotation(logLine, logFileName);
        }

        /// <summary>
        /// 이벤트/디버그 로그 통합 메서드
        /// isDebug가 true이면 디버그로, 아니면 이벤트로 기록
        /// </summary>
        public void LogEvent(string message, bool isDebug)
        {
            if (isDebug)
            {
                LogDebug(message);
            }
            else
            {
                LogEvent(message);
            }
        }

        /// <summary>
        /// (에러 로그) 오류 상황에만 기록.
        /// 5MB 초과 시 "yyyyMMdd_error_1.log"로 회전.
        /// </summary>
        public void LogError(string message)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logFileName = $"{DateTime.Now:yyyyMMdd}_error.log";
            string logLine = $"{timestamp} [Error] {message}";

            WriteLogWithRotation(logLine, logFileName);
        }

        /* ★★ 수정 : 전역 플래그가 켜졌을 때만 기록 ★★ */
        public void LogDebug(string message)
        {
            if (!GlobalDebugEnabled) return;          // Debug OFF → 바로 반환

            string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string log = $"{ts} [Debug] {message}";
            string fileName = $"{DateTime.Now:yyyyMMdd}_debug.log";

            WriteLogWithRotation(log, fileName);
        }

        /// <summary>
        /// 필요시 사용자 정의 로그 유형 지정 가능
        /// ex) LogCustom("message", "info") => yyyyMMdd_info.log
        /// </summary>
        public void LogCustom(string message, string logType)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string fileName = $"{DateTime.Now:yyyyMMdd}_{logType.ToLower()}.log";
            string logLine = $"{timestamp} [{logType.ToUpper()}] {message}";

            WriteLogWithRotation(logLine, fileName);
        }

        // IP 마스킹을 위한 정규식 (클래스 멤버로 추가)
        /// <summary>
        /// IP 주소를 찾아서 마지막 옥텟을 마스킹하는 정규식입니다.
        /// 예: "10.0.0.1" -> "*.*.*.1"
        /// </summary>
        private static readonly Regex _ipMaskRegex = new Regex(
            @"\b(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 로그 메시지에서 IP 주소를 마스킹합니다.
        /// </summary>
        private string MaskIpAddress(string message)
        {
            // IP 주소 패턴(예: 10.0.0.1)을 찾아서 "*.*.*.1"로 변경합니다.
            return _ipMaskRegex.Replace(message, "*.*.*.$4");
        }

        private void WriteLogWithRotation(string message, string fileName)
        {
            // 로그 파일에 쓰기 전에 IP 마스킹 적용
            string maskedMessage = MaskIpAddress(message);
            string filePath = Path.Combine(logFolderPath, fileName);

            try
            {
                // (1) 5 MB 초과 시 회전
                RotateLogFileIfNeeded(filePath);

                // (2) FileShare.ReadWrite 로 열기  ➜  다른 쓰기 스트림과 공존
                const int MAX_RETRY = 3;
                for (int attempt = 1; attempt <= MAX_RETRY; attempt++)
                {
                    try
                    {
                        using (var fs = new FileStream(
                                   filePath,
                                   FileMode.OpenOrCreate,
                                   FileAccess.Write,
                                   FileShare.ReadWrite))          // 핵심 변경
                        {
                            fs.Seek(0, SeekOrigin.End);          // 항상 Append
                            using (var sw = new StreamWriter(fs, Encoding.UTF8))
                            {
                                sw.WriteLine(maskedMessage);
                            }
                        }
                        return;                                   // 성공 시 종료
                    }
                    catch (IOException) when (attempt < MAX_RETRY)
                    {
                        // 잠시 후 재시도
                        Thread.Sleep(250);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to write log after retries: {ex.Message}");
            }
        }

        // ================== 개선된 전체 메서드 ==================
        private void RotateLogFileIfNeeded(string filePath)
        {
            if (!File.Exists(filePath))
                return;

            FileInfo fi = new FileInfo(filePath);

            // (1) 5 MB 이하이면 그대로 사용
            if (fi.Length <= MAX_LOG_SIZE)
                return;

            // (2) 확장자 / 기본 이름 분리
            string extension = fi.Extension;                               // ".log"
            string withoutExt = Path.GetFileNameWithoutExtension(filePath); // "20250711_event"

            // (3) 다음 회전 인덱스 계산
            int index = 1;
            string rotatedPath;
            do
            {
                string rotatedName = $"{withoutExt}_{index}{extension}";     // ex) "20250711_event_3.log"
                rotatedPath = Path.Combine(logFolderPath, rotatedName);
                index++;
            }
            while (File.Exists(rotatedPath));  // 존재하는 파일이 없을 때까지 증가

            // (4) 원본 → 새 인덱스 파일로 이동
            File.Move(filePath, rotatedPath);

            // (5) 이후 WriteLogWithRotation() 가 호출되면서
            //     같은 이름의 새로운 원본 로그(0번) 파일이 자동 생성되어
            //     이어서 로그가 기록됨.
        }

        public static void BroadcastPluginDebug(bool enabled)
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in asms)
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray();
                    }
                    if (types == null) continue;

                    foreach (var t in types)
                    {
                        if (!t.IsClass) continue;
                        if (!string.Equals(t.Name, "SimpleLogger", StringComparison.Ordinal)) continue;

                        // 우선순위: SetDebugMode(bool) → SetDebug(bool)
                        var m = t.GetMethod("SetDebugMode", BindingFlags.Public | BindingFlags.Static);
                        if (m == null) m = t.GetMethod("SetDebug", BindingFlags.Public | BindingFlags.Static);
                        if (m == null) continue;

                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                        {
                            try { m.Invoke(null, new object[] { enabled }); }
                            catch { /* 플러그인 내부 예외는 무시(다른 플러그인 영향 방지) */ }
                        }
                    }
                }
            }
            catch { /* 전체 브로드캐스트 실패는 무시(기본 로깅 동작엔 영향 없음) */ }
        }
    }
}
