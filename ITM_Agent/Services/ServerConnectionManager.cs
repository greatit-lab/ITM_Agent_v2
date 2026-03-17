// ITM_Agent/Services/ServerConnectionManager.cs
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ConnectInfo; // DatabaseInfo, FtpsInfo
using Npgsql;

namespace ITM_Agent.Services
{
    /// <summary>
    /// 서버(DB, Object Storage API) 연결 상태를 10초 주기로 정밀 감시합니다.
    /// DB는 실제 쿼리, Object Storage는 HTTP Health Check를 통해 생존 여부를 확인합니다.
    /// </summary>
    public class ServerConnectionManager : IDisposable
    {
        // 상태 변경 시 알림 이벤트 (전체성공여부, DB성공여부, API성공여부, 메시지)
        public event Action<bool, bool, bool, string> ConnectionStatusChanged;

        private readonly LogManager _logManager;
        private readonly System.Threading.Timer _checkTimer;
        private readonly object _lock = new object();
        private readonly Random _random = new Random();

        // HTTP 통신을 위한 클라이언트 (재사용 권장)
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // 현재 상태
        private bool _isServerConnected = true;
        private bool _isRunning = false;

        // 설정: 체크 주기 (10초)
        private const int CHECK_INTERVAL_MS = 10 * 1000;

        // 설정: DB 타임아웃 (3초)
        private const int DB_TIMEOUT = 3;

        // 설정: API 포트 (ITM.UploadApi 기본 포트)
        private const int API_PORT = 8082;

        public bool IsConnected => _isServerConnected;

        public ServerConnectionManager(LogManager logManager)
        {
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            // 타이머 초기화 (시작은 Start() 호출 시)
            _checkTimer = new System.Threading.Timer(async _ => await CheckConnectionsAsync(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning) return;
                _isRunning = true;

                // 즉시 실행하지 않고, 랜덤 지연 후 시작 (Thundering Herd 방지)
                int startDelay = _random.Next(100, 2000);
                _checkTimer.Change(startDelay, CHECK_INTERVAL_MS);
                _logManager.LogEvent($"[ServerConnectionManager] Monitoring started. Interval: {CHECK_INTERVAL_MS / 1000}s");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _isRunning = false;
                _checkTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _logManager.LogEvent("[ServerConnectionManager] Monitoring stopped.");
            }
        }

        private async Task CheckConnectionsAsync()
        {
            if (!_isRunning) return;

            try
            {
                // 두 서버 상태 확인 (DB & Object Storage API)
                bool dbOk = await CheckDatabaseAsync();
                bool apiOk = await CheckObjectStorageApiAsync();

                // 둘 다 정상이어야 "연결됨"으로 판정
                bool currentStatus = dbOk && apiOk;

                if (_isServerConnected != currentStatus)
                {
                    _isServerConnected = currentStatus;
                    string msg = currentStatus ? "Server connection restored." : "Server connection lost.";

                    _logManager.LogEvent($"[ServerConnectionManager] Status Changed: {msg} (DB:{dbOk}, API:{apiOk})");

                    // 상세 상태 전달
                    ConnectionStatusChanged?.Invoke(currentStatus, dbOk, apiOk, msg);
                }
                else if (!currentStatus)
                {
                    // 끊긴 상태 지속 시 UI 갱신용 이벤트 발생 (로그 생략)
                    ConnectionStatusChanged?.Invoke(currentStatus, dbOk, apiOk, "Connection unstable...");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ServerConnectionManager] Error during check: {ex.Message}");
            }
        }

        private async Task<bool> CheckDatabaseAsync()
        {
            // ⭐️ [핵심 추가] 프록시 모드(내부망)일 경우, 연결 끊김 검사를 무시하고 무조건 초록불(true)로 강제 통과
            if (DatabaseInfo.GetSettingsIniValue("Network", "UseProxy") == "1")
            {
                return true;
            }

            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();

                // Pooling=false 추가 (실제 핸드셰이크 강제)
                if (!cs.Contains("Pooling=")) cs += ";Pooling=false";

                // 타임아웃 설정
                if (!cs.Contains("Timeout=")) cs += $";Timeout={DB_TIMEOUT}";

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();

                    // 실제 쿼리 실행으로 엔진 생존 확인
                    using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                    {
                        await cmd.ExecuteScalarAsync();
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> CheckObjectStorageApiAsync()
        {
            // ⭐️ [핵심 추가] 프록시 모드(내부망)일 경우, 연결 끊김 검사를 무시하고 무조건 초록불(true)로 강제 통과
            if (DatabaseInfo.GetSettingsIniValue("Network", "UseProxy") == "1")
            {
                return true;
            }

            try
            {
                // Connection.ini의 [Ftps] 섹션에서 IP만 가져옴 (포트는 API_PORT 사용)
                var ftpInfo = FtpsInfo.CreateDefault();
                string host = ftpInfo.Host;

                if (string.IsNullOrEmpty(host)) return false;

                // Health Check URL 구성
                string url = $"http://{host}:{API_PORT}/api/FileUpload/health";

                // HTTP GET 요청
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3))) // 3초 타임아웃
                {
                    var response = await _httpClient.GetAsync(url, cts.Token);
                    return response.IsSuccessStatusCode; // 200 OK면 true
                }
            }
            catch
            {
                // 연결 실패, 타임아웃, 404 등 모든 오류 시 false
                return false;
            }
        }

        public void Dispose()
        {
            _checkTimer?.Dispose();
        }
    }
}
