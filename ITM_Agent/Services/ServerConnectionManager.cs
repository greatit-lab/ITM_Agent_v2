// ITM_Agent/Services/ServerConnectionManager.cs
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ConnectInfo; // DatabaseInfo, FtpsInfo
using Npgsql;

namespace ITM_Agent.Services
{
    /// <summary>
    /// [수정] 서버(DB, FTP) 연결 상태를 10초 주기로 정밀 감시합니다.
    /// Connection Pool을 우회하여 실제 연결 여부를 확인하며, 상태 변경 시 이벤트를 발생시킵니다.
    /// </summary>
    public class ServerConnectionManager : IDisposable
    {
        // 상태 변경 시 알림 이벤트 (전체성공여부, DB성공여부, FTP성공여부, 메시지)
        public event Action<bool, bool, bool, string> ConnectionStatusChanged;

        private readonly LogManager _logManager;
        private readonly System.Threading.Timer _checkTimer;
        private readonly object _lock = new object();
        private readonly Random _random = new Random();

        // 현재 상태
        private bool _isServerConnected = true;
        private bool _isRunning = false;

        // [변경] 설정: 체크 주기 (60초 -> 10초로 단축하여 즉각 반응)
        private const int CHECK_INTERVAL_MS = 10 * 1000;

        // 설정: DB 타임아웃 (3초)
        private const int DB_TIMEOUT = 3;

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
                // 두 서버 상태 확인
                bool dbOk = await CheckDatabaseAsync();
                bool ftpOk = await CheckFtpAsync();

                // 둘 다 정상이어야 "연결됨"으로 판정
                bool currentStatus = dbOk && ftpOk;

                // [중요] 상태가 변했거나, '연결 끊김' 상태가 지속될 때도 UI 갱신을 위해 이벤트를 발생시킬 수 있음
                // 여기서는 "상태 변화" 시점에만 발생시키되, DB/FTP 각각의 상태 변화도 감지
                // (기존에는 전체 상태만 봤으나, 이제는 부분 상태 변화도 중요함)

                // 다만 너무 잦은 로그를 막기 위해, 내부적으로 상태가 완전히 동일하면 스킵하고
                // DB나 FTP 중 하나라도 상태가 바뀌면 알림을 보냅니다.
                // 편의상 _isServerConnected(전체)만 비교하던 것을 확장할 수도 있으나,
                // 일단은 전체 상태 변화 또는 장애 상황 지속 시 재확인을 위해 매번 로그를 찍지 않는 선에서 처리합니다.

                if (_isServerConnected != currentStatus)
                {
                    _isServerConnected = currentStatus;
                    string msg = currentStatus ? "Server connection restored." : "Server connection lost.";

                    _logManager.LogEvent($"[ServerConnectionManager] Status Changed: {msg} (DB:{dbOk}, FTP:{ftpOk})");

                    // 상세 상태 전달
                    ConnectionStatusChanged?.Invoke(currentStatus, dbOk, ftpOk, msg);
                }
                else if (!currentStatus)
                {
                    // [추가] 이미 끊긴 상태라도, DB/FTP 상태가 서로 다를 수 있으므로 
                    // 확실한 UI 동기화를 위해 끊김 상태에서는 계속 이벤트를 전달해주는 것이 안전할 수 있음.
                    // 단, 로그 폭주를 막기 위해 로그는 남기지 않고 UI 업데이트용 델리게이트만 호출
                    ConnectionStatusChanged?.Invoke(currentStatus, dbOk, ftpOk, "Connection unstable...");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ServerConnectionManager] Error during check: {ex.Message}");
            }
        }

        private async Task<bool> CheckDatabaseAsync()
        {
            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();

                // [핵심 변경 1] Pooling=false 추가: 
                // 끊긴 연결을 재사용하는 것을 방지하고 매번 실제 핸드셰이크를 수행합니다.
                if (!cs.Contains("Pooling=")) cs += ";Pooling=false";

                // 타임아웃 설정
                if (!cs.Contains("Timeout=")) cs += $";Timeout={DB_TIMEOUT}";

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();

                    // [핵심 변경 2] 실제 쿼리 실행:
                    // 연결 객체가 생성되어도 실제 통신이 되는지 확인하기 위해 가벼운 쿼리 수행
                    using (var cmd = new NpgsqlCommand("SELECT 1", conn))
                    {
                        await cmd.ExecuteScalarAsync();
                    }

                    return true;
                }
            }
            catch
            {
                // 연결 실패, 타임아웃, 쿼리 실패 등 모든 오류를 '연결 끊김'으로 간주
                return false;
            }
        }

        private async Task<bool> CheckFtpAsync()
        {
            try
            {
                var ftpInfo = FtpsInfo.CreateDefault();
                string host = ftpInfo.Host;
                int port = ftpInfo.Port;

                if (string.IsNullOrEmpty(host)) return false;

                // TCP 포트 연결 확인 (가볍고 빠름)
                using (var tcp = new TcpClient())
                {
                    var task = tcp.ConnectAsync(host, port);
                    // 3초 내에 연결 안 되면 실패 처리
                    if (await Task.WhenAny(task, Task.Delay(3000)) == task)
                    {
                        return tcp.Connected;
                    }
                    else
                    {
                        return false; // Timeout
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _checkTimer?.Dispose();
        }
    }
}
