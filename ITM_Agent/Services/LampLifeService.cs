// ITM_Agent/Services/LampLifeService.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient; // MSSQL 접속용
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConnectInfo;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Win32; // 레지스트리 접근용
using Npgsql;

namespace ITM_Agent.Services
{
    public struct LampInfo
    {
        public string LampName; // (구 LampId) 화면상 이름 (예: Lamp 1)
        public int LampNo;      // (신규) 장비 DB의 고유 ID (예: 4)
        public string Age;
        public string LifeSpan;
        public string LastChanged;
    }

    public class LampLifeService
    {
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private readonly MainForm _mainForm;
        private System.Threading.Timer _schedular;
        private bool _isRunning = false;
        private readonly object _lock = new object();
        private readonly string PROCESS_NAME;

        // 주기: 1시간
        private const int UPDATE_INTERVAL_MS = 60 * 60 * 1000;

        public event Action<bool, DateTime> CollectionCompleted;

        public LampLifeService(SettingsManager settingsManager, LogManager logManager, MainForm mainForm)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
            _mainForm = mainForm;
            PROCESS_NAME = Environment.Is64BitOperatingSystem ? "Main64" : "Main";
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isRunning || !_settingsManager.IsLampLifeCollectorEnabled) return;

                _isRunning = true;
                _logManager.LogEvent("[LampLifeService] Service Started.");

                // 1. DB 스키마 마이그레이션 (lamp_id -> lamp_name, lamp_no 추가)
                MigrateDatabaseSchema();

                // 2. 비동기로 초기화 작업 및 주기적 작업 시작
                Task.Run(async () =>
                {
                    // [Step 1] UI Automation 1회 실행 (초기 데이터 적재)
                    _logManager.LogEvent("[LampLifeService] Executing Initial UI Automation...");
                    bool uiSuccess = await ExecuteUiCollectionAsync();

                    // [Step 2] MSSQL 접속 및 매핑 (Time Matching)
                    if (uiSuccess)
                    {
                        _logManager.LogEvent("[LampLifeService] UI Collection done. Starting MSSQL Mapping...");
                        await SyncWithEquipmentDatabaseAsync(true); // true = Initial Mapping Mode
                    }
                    else
                    {
                        _logManager.LogError("[LampLifeService] Initial UI Collection failed. Skipping MSSQL mapping.");
                    }

                    // [Step 3] 주기적 MSSQL 폴링 스케줄러 시작 (1시간 간격)
                    _schedular = new System.Threading.Timer(async _ =>
                    {
                        if (!_isRunning) return;
                        await SyncWithEquipmentDatabaseAsync(false); // false = Update Mode
                    }, null, UPDATE_INTERVAL_MS, UPDATE_INTERVAL_MS);
                });
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isRunning) return;
                _isRunning = false;
                _schedular?.Dispose();
                _schedular = null;
                _logManager.LogEvent("[LampLifeService] Service Stopped.");
            }
        }

        /// <summary>
        /// [Step 1] 기존 UI 자동화 로직 (1회성 실행용)
        /// </summary>
        public async Task<bool> ExecuteUiCollectionAsync()
        {
            // (수동 실행 시 외부 호출 가능하도록 public 유지)
            var collectedLamps = new List<LampInfo>();

            try
            {
                _mainForm.ShowTemporarilyForAutomation();
                await Task.Delay(500);

                var app = FlaUI.Core.Application.Attach(PROCESS_NAME);
                using (var automation = new UIA3Automation())
                {
                    var mainWindow = app.GetMainWindow(automation);
                    mainWindow.SetForeground();
                    await Task.Delay(500);

                    // UI 조작 (Processing -> System -> Lamps 탭)
                    var processingButton = FindButton(mainWindow, "Processing", "25003");
                    if (processingButton == null) throw new Exception("'Processing' button not found.");
                    processingButton.Click();
                    await Task.Delay(500);

                    var systemButton = FindButton(mainWindow, "System", "25004");
                    if (systemButton == null) throw new Exception("'System' button not found.");
                    systemButton.Click();
                    await Task.Delay(500);

                    var tabControl = FindElementWithRetry(mainWindow, cf => cf.ByControlType(ControlType.Tab));
                    var lampsTab = FindElementWithRetry(tabControl, cf => cf.ByName("Lamps").And(cf.ByControlType(ControlType.TabItem)))?.AsTabItem();
                    if (lampsTab != null)
                    {
                        lampsTab.Click();
                        await Task.Delay(1000);

                        var lampList = mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("10819").And(cf.ByControlType(ControlType.List)))?.AsListBox();
                        if (lampList != null)
                        {
                            foreach (var item in lampList.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)))
                            {
                                var cells = item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
                                if (cells.Length > 4)
                                {
                                    collectedLamps.Add(new LampInfo
                                    {
                                        LampName = cells[0].Name, // UI상의 이름
                                        Age = cells[1].Name,
                                        LifeSpan = cells[2].Name,
                                        LastChanged = cells[4].Name
                                    });
                                }
                            }
                        }
                    }
                    // 복귀
                    processingButton.Click();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] UI Automation Error: {ex.Message}");
                return false;
            }
            finally
            {
                _mainForm.HideToTrayAfterAutomation();
            }

            if (collectedLamps.Count > 0)
            {
                UploadToPostgres(collectedLamps); // 1차 저장 (lamp_no는 null 상태)
                CollectionCompleted?.Invoke(true, DateTime.Now);
                return true;
            }
            return false;
        }

        /// <summary>
        /// [Step 2 & 3] 장비 MSSQL 접속 및 데이터 동기화
        /// </summary>
        /// <param name="isInitialMapping">true: 이름-시간 매칭하여 ID 찾기 / false: ID로 최신 시간 업데이트</param>
        private async Task SyncWithEquipmentDatabaseAsync(bool isInitialMapping)
        {
            try
            {
                string connectionString = FindMssqlConnectionString();
                if (string.IsNullOrEmpty(connectionString))
                {
                    _logManager.LogError("[LampLifeService] Could not find a valid Local MSSQL instance.");
                    return;
                }

                _logManager.LogDebug($"[LampLifeService] Connecting to Equipment DB: {connectionString}");

                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync();

                    // 장비 DB에서 최근 50건 로그 조회
                    // (테이블명은 예시인 tblLampChangeLog 사용, 실제 환경에 맞춰 수정 필요)
                    string query = @"
                        SELECT TOP 50 LogTime, LampID 
                        FROM tblLampChangeLog 
                        ORDER BY LogTime DESC";

                    var mssqlLogs = new List<(DateTime LogTime, int LampID)>();
                    using (var cmd = new SqlCommand(query, conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                            {
                                mssqlLogs.Add((reader.GetDateTime(0), reader.GetInt32(1)));
                            }
                        }
                    }

                    if (mssqlLogs.Count == 0)
                    {
                        _logManager.LogEvent("[LampLifeService] No logs found in Equipment DB.");
                        return;
                    }

                    // PostgreSQL 데이터와 비교 및 업데이트
                    await UpdatePostgresWithMssqlData(mssqlLogs, isInitialMapping);
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] MSSQL Sync Failed: {ex.Message}");
            }
        }

        private async Task UpdatePostgresWithMssqlData(List<(DateTime LogTime, int LampID)> mssqlLogs, bool isMappingMode)
        {
            string pgCs = DatabaseInfo.CreateDefault().GetConnectionString();
            using (var pgConn = new NpgsqlConnection(pgCs))
            {
                await pgConn.OpenAsync();
                string eqpid = _settingsManager.GetEqpid();

                // 현재 저장된 데이터 조회
                var currentData = new List<(string LampName, int? LampNo, DateTime LastChanged)>();
                using (var cmd = new NpgsqlCommand("SELECT lamp_name, lamp_no, last_changed FROM public.eqp_lamp_life WHERE eqpid = @eqpid", pgConn))
                {
                    cmd.Parameters.AddWithValue("@eqpid", eqpid);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            string name = reader.GetString(0);
                            int? no = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                            DateTime changed = reader.GetDateTime(2);
                            currentData.Add((name, no, changed));
                        }
                    }
                }

                foreach (var pgRow in currentData)
                {
                    if (isMappingMode)
                    {
                        // [매핑 모드] 시간(초 단위까지)이 일치하는 항목을 찾아 LampID(lamp_no) 업데이트
                        // MSSQL의 LogTime과 PostgreSQL의 last_changed 비교
                        var match = mssqlLogs.FirstOrDefault(m => Math.Abs((m.LogTime - pgRow.LastChanged).TotalSeconds) < 2);

                        if (match.LampID > 0)
                        {
                            // 매핑 발견! lamp_no 업데이트
                            using (var updateCmd = new NpgsqlCommand("UPDATE public.eqp_lamp_life SET lamp_no = @no WHERE eqpid = @eqpid AND lamp_name = @name", pgConn))
                            {
                                updateCmd.Parameters.AddWithValue("@no", match.LampID);
                                updateCmd.Parameters.AddWithValue("@eqpid", eqpid);
                                updateCmd.Parameters.AddWithValue("@name", pgRow.LampName);
                                await updateCmd.ExecuteNonQueryAsync();
                                _logManager.LogEvent($"[LampLifeService] Mapped '{pgRow.LampName}' -> LampID {match.LampID}");
                            }
                        }
                    }
                    else
                    {
                        // [업데이트 모드] lamp_no가 있는 항목에 대해, MSSQL의 더 최신 로그가 있는지 확인
                        if (pgRow.LampNo.HasValue)
                        {
                            var latestLog = mssqlLogs
                                .Where(m => m.LampID == pgRow.LampNo.Value)
                                .OrderByDescending(m => m.LogTime)
                                .FirstOrDefault();

                            if (latestLog.LampID > 0 && latestLog.LogTime > pgRow.LastChanged)
                            {
                                // 새로운 교체 이력 발견 -> 업데이트
                                // Age는 (현재시간 - 교체시간)으로 자동 계산
                                int newAge = (int)(DateTime.Now - latestLog.LogTime).TotalHours;

                                string updateSql = @"
                                    UPDATE public.eqp_lamp_life 
                                    SET last_changed = @ts, age_hour = @age, serv_ts = NOW()::timestamp(0)
                                    WHERE eqpid = @eqpid AND lamp_no = @no";

                                using (var updateCmd = new NpgsqlCommand(updateSql, pgConn))
                                {
                                    updateCmd.Parameters.AddWithValue("@ts", latestLog.LogTime);
                                    updateCmd.Parameters.AddWithValue("@age", newAge);
                                    updateCmd.Parameters.AddWithValue("@eqpid", eqpid);
                                    updateCmd.Parameters.AddWithValue("@no", pgRow.LampNo.Value);
                                    await updateCmd.ExecuteNonQueryAsync();
                                    _logManager.LogEvent($"[LampLifeService] Updated '{pgRow.LampName}' (ID:{pgRow.LampNo}) : New Change Time {latestLog.LogTime}");
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 로컬 레지스트리를 검색하여 SQL Server 인스턴스 이름을 찾고 연결 문자열 생성
        /// </summary>
        private string FindMssqlConnectionString()
        {
            string instanceName = null;
            string hostName = Environment.MachineName;

            try
            {
                // 레지스트리에서 설치된 SQL 인스턴스 검색
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                {
                    if (key != null)
                    {
                        foreach (var name in key.GetValueNames())
                        {
                            // "SQLSERVER"가 포함된 인스턴스 우선 검색 (대소문자 무시)
                            if (name.IndexOf("SQLSERVER", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                instanceName = name;
                                break;
                            }
                        }
                        // 없으면 첫 번째꺼라도 사용
                        if (instanceName == null && key.GetValueNames().Length > 0)
                        {
                            instanceName = key.GetValueNames()[0];
                        }
                    }
                }
            }
            catch { /* 권한 문제 등으로 실패 시 무시 */ }

            if (string.IsNullOrEmpty(instanceName))
            {
                // 발견 못했으면 기본값 시도
                instanceName = "SQLEXPRESS"; 
            }

            string dataSource = $"{hostName}\\{instanceName}";
            
            // Windows 통합 인증 사용
            return $"Data Source={dataSource};Initial Catalog=N2000_MEASURE;Integrated Security=True;TrustServerCertificate=True;";
        }

        /// <summary>
        /// PostgreSQL 테이블 스키마 변경 (최초 1회 실행)
        /// lamp_id -> lamp_name 변경 및 lamp_no 컬럼 추가
        /// </summary>
        private void MigrateDatabaseSchema()
        {
            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();
                using (var conn = new NpgsqlConnection(cs))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            DO $$ 
                            BEGIN 
                                -- 1. lamp_id 컬럼이 있으면 lamp_name으로 변경
                                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='eqp_lamp_life' AND column_name='lamp_id') THEN
                                    ALTER TABLE public.eqp_lamp_life RENAME COLUMN lamp_id TO lamp_name;
                                END IF;

                                -- 2. lamp_no 컬럼이 없으면 추가
                                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='eqp_lamp_life' AND column_name='lamp_no') THEN
                                    ALTER TABLE public.eqp_lamp_life ADD COLUMN lamp_no INTEGER NULL;
                                END IF;
                            END $$;";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[LampLifeService] Schema Migration Failed: {ex.Message}");
            }
        }

        private void UploadToPostgres(List<LampInfo> lamps)
        {
            var dbInfo = DatabaseInfo.CreateDefault();
            string eqpid = _settingsManager.GetEqpid();

            using (var conn = new NpgsqlConnection(dbInfo.GetConnectionString()))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    // lamp_name 기준 Upsert (lamp_no는 건드리지 않음)
                    const string sql = @"
                        INSERT INTO public.eqp_lamp_life 
                            (eqpid, lamp_name, ts, age_hour, lifespan_hour, last_changed, serv_ts)
                        VALUES 
                            (@eqpid, @name, @ts, @age, @life, @changed, NOW()::timestamp(0))
                        ON CONFLICT (eqpid, lamp_name) DO UPDATE SET
                            ts = EXCLUDED.ts,
                            age_hour = EXCLUDED.age_hour,
                            lifespan_hour = EXCLUDED.lifespan_hour,
                            last_changed = EXCLUDED.last_changed,
                            serv_ts = NOW()::timestamp(0);";

                    using (var cmd = new NpgsqlCommand(sql, conn, tx))
                    {
                        cmd.Parameters.Add("@eqpid", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@name", NpgsqlTypes.NpgsqlDbType.Varchar);
                        cmd.Parameters.Add("@ts", NpgsqlTypes.NpgsqlDbType.Timestamp);
                        cmd.Parameters.Add("@age", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@life", NpgsqlTypes.NpgsqlDbType.Integer);
                        cmd.Parameters.Add("@changed", NpgsqlTypes.NpgsqlDbType.Timestamp);

                        foreach (var lamp in lamps)
                        {
                            cmd.Parameters["@eqpid"].Value = eqpid;
                            cmd.Parameters["@name"].Value = lamp.LampName;
                            cmd.Parameters["@ts"].Value = DateTime.Now;

                            if (int.TryParse(lamp.Age, out int age)) cmd.Parameters["@age"].Value = age;
                            else cmd.Parameters["@age"].Value = 0;

                            if (int.TryParse(lamp.LifeSpan, out int life)) cmd.Parameters["@life"].Value = life;
                            else cmd.Parameters["@life"].Value = 0;

                            if (DateTime.TryParse(lamp.LastChanged, out DateTime changed)) cmd.Parameters["@changed"].Value = changed;
                            else cmd.Parameters["@changed"].Value = DBNull.Value;

                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        // --- UI Helper Methods ---
        private FlaUI.Core.AutomationElements.Button FindButton(Window window, string name, string autoId)
        {
            var btn = window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button)))?.AsButton();
            if (btn == null && Environment.Is64BitOperatingSystem)
            {
                btn = window.FindFirstDescendant(cf => cf.ByAutomationId(autoId))?.AsButton();
            }
            return btn;
        }

        private AutomationElement FindElementWithRetry(AutomationElement parent, Func<ConditionFactory, ConditionBase> conditionFunc, int timeoutMs = 5000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                var element = parent.FindFirstDescendant(conditionFunc);
                if (element != null) return element;
                Thread.Sleep(200);
            }
            return null;
        }
    }
}
