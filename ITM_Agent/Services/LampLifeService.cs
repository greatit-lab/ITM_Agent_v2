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
using System.ServiceProcess; // 윈도우 서비스 확인용

namespace ITM_Agent.Services
{
    public struct LampInfo
    {
        public string LampName;
        public int LampNo;
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

        private const int UPDATE_INTERVAL_MS = 60 * 60 * 1000; // 1시간

        public event Action<bool, DateTime> CollectionCompleted;

        public LampLifeService(SettingsManager settingsManager, LogManager logManager, MainForm mainForm)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
            _mainForm = mainForm;
            PROCESS_NAME = Environment.Is64BitOperatingSystem ? "Main64" : "Main";
        }

        // [수정] skipUiAutomation 파라미터 추가 (기본값 false)
        // MainForm의 'Run' 버튼 클릭 시: Start(false) -> UI 수행
        // MainForm의 '자동 복구' 시: Start(true) -> UI 건너뜀
        public void Start(bool skipUiAutomation = false)
        {
            lock (_lock)
            {
                if (_isRunning || !_settingsManager.IsLampLifeCollectorEnabled) return;

                _isRunning = true;
                _logManager.LogEvent($"[LampLifeService] Service Started. (SkipUI: {skipUiAutomation})");

                MigrateDatabaseSchema();

                Task.Run(async () =>
                {
                    // [Step 1] UI Automation (조건부 실행)
                    bool uiSuccess = false;
                    if (!skipUiAutomation)
                    {
                        _logManager.LogEvent("[LampLifeService] Executing Initial UI Automation...");
                        uiSuccess = await ExecuteUiCollectionAsync();
                    }
                    else
                    {
                        _logManager.LogEvent("[LampLifeService] Skipping UI Automation (Auto-Recovery Mode).");
                    }

                    // [Step 2] MSSQL 접속 및 매핑
                    // UI 수행 성공했거나, 스킵 모드일 때도 MSSQL 동기화는 시도 (기존 데이터 매핑 유지)
                    if (uiSuccess || skipUiAutomation)
                    {
                        // 스킵 모드일 때는 isInitialMapping=false로 하여 단순 업데이트만 수행하거나
                        // 필요에 따라 true로 유지. 여기서는 안전하게 false(주기적 모드)로 진입 권장
                        bool isInitial = uiSuccess; 
                        
                        _logManager.LogEvent("[LampLifeService] Connecting to MSSQL for Sync...");
                        await SyncWithEquipmentDatabaseAsync(isInitial);
                    }

                    // [Step 3] 주기적 MSSQL 폴링 시작
                    _schedular = new System.Threading.Timer(async _ =>
                    {
                        if (!_isRunning) return;
                        await SyncWithEquipmentDatabaseAsync(false);
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

        public async Task<bool> ExecuteUiCollectionAsync()
        {
            var collectedLamps = new List<LampInfo>();
            try
            {
                _mainForm.ShowTemporarilyForAutomation();
                await Task.Delay(500);

                // [수정] Application 객체도 IDisposable이므로 using으로 감싸 핸들 누수 방지
                using (var app = FlaUI.Core.Application.Attach(PROCESS_NAME))
                using (var automation = new UIA3Automation())
                {
                    var mainWindow = app.GetMainWindow(automation);
                    mainWindow.SetForeground();
                    await Task.Delay(500);

                    var processingButton = FindButton(mainWindow, "Processing", "25003");
                    if (processingButton != null)
                    {
                        processingButton.Click();
                        await Task.Delay(500);
                    }

                    var systemButton = FindButton(mainWindow, "System", "25004");
                    if (systemButton != null)
                    {
                        systemButton.Click();
                        await Task.Delay(500);
                    }

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
                                        LampName = cells[0].Name,
                                        Age = cells[1].Name,
                                        LifeSpan = cells[2].Name,
                                        LastChanged = cells[4].Name
                                    });
                                }
                            }
                        }
                    }
                    if (processingButton != null) processingButton.Click();
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
                UploadToPostgres(collectedLamps);
                CollectionCompleted?.Invoke(true, DateTime.Now);
                return true;
            }
            return false;
        }

        private async Task SyncWithEquipmentDatabaseAsync(bool isInitialMapping)
        {
            try
            {
                string connectionString = FindMssqlConnectionString();
                if (string.IsNullOrEmpty(connectionString))
                {
                    _logManager.LogError("[LampLifeService] CRITICAL: Could not determine MSSQL Connection String. Please check server status (osql -L).");
                    return;
                }

                _logManager.LogDebug($"[LampLifeService] Connecting to Equipment DB: {connectionString}");

                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync();

                    string query = "SELECT TOP 50 LogTime, LampID FROM tblLampChangeLog ORDER BY LogTime DESC";
                    var mssqlLogs = new List<(DateTime LogTime, int LampID)>();

                    using (var cmd = new SqlCommand(query, conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            try
                            {
                                // ▼▼▼ [수정] 안전한 형변환 (Convert 사용) ▼▼▼
                                // DB 컬럼이 tinyint, smallint, bigint 무엇이든 int로 변환
                                // DB 컬럼이 datetime, datetime2, smalldatetime 무엇이든 DateTime으로 변환
                                if (reader["LogTime"] != DBNull.Value && reader["LampID"] != DBNull.Value)
                                {
                                    DateTime logTime = Convert.ToDateTime(reader["LogTime"]);
                                    int lampId = Convert.ToInt32(reader["LampID"]);
                                    mssqlLogs.Add((logTime, lampId));
                                }
                            }
                            catch (Exception castEx)
                            {
                                // 데이터 1건 변환 실패는 로그 남기고 건너뜀 (전체 중단 방지)
                                _logManager.LogDebug($"[LampLifeService] Data conversion skipped for a row: {castEx.Message}");
                            }
                        }
                    }

                    if (mssqlLogs.Count > 0)
                    {
                        await UpdatePostgresWithMssqlData(mssqlLogs, isInitialMapping);
                    }
                    else
                    {
                        _logManager.LogEvent("[LampLifeService] No logs found in Equipment DB.");
                    }
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

                var currentData = new List<(string LampName, int? LampNo, DateTime LastChanged)>();
                using (var cmd = new NpgsqlCommand("SELECT lamp_name, lamp_no, last_changed FROM public.eqp_lamp_life WHERE eqpid = @eqpid", pgConn))
                {
                    cmd.Parameters.AddWithValue("@eqpid", eqpid);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            currentData.Add((reader.GetString(0), reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1), reader.GetDateTime(2)));
                        }
                    }
                }

                foreach (var pgRow in currentData)
                {
                    if (isMappingMode)
                    {
                        var match = mssqlLogs.FirstOrDefault(m => Math.Abs((m.LogTime - pgRow.LastChanged).TotalSeconds) < 2);
                        if (match.LampID > 0)
                        {
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
                        if (pgRow.LampNo.HasValue)
                        {
                            var latestLog = mssqlLogs.Where(m => m.LampID == pgRow.LampNo.Value).OrderByDescending(m => m.LogTime).FirstOrDefault();
                            if (latestLog.LampID > 0 && latestLog.LogTime > pgRow.LastChanged)
                            {
                                int newAge = (int)(DateTime.Now - latestLog.LogTime).TotalHours;
                                DateTime cleanLastChanged = TruncateToSeconds(latestLog.LogTime);
                                DateTime cleanAgentTime = TruncateToSeconds(DateTime.Now);

                                string updateSql = @"UPDATE public.eqp_lamp_life SET last_changed = @last_changed, age_hour = @age, ts = @ts, serv_ts = NOW()::timestamp(0) WHERE eqpid = @eqpid AND lamp_no = @no";
                                using (var updateCmd = new NpgsqlCommand(updateSql, pgConn))
                                {
                                    updateCmd.Parameters.AddWithValue("@last_changed", cleanLastChanged);
                                    updateCmd.Parameters.AddWithValue("@age", newAge);
                                    updateCmd.Parameters.AddWithValue("@ts", cleanAgentTime);
                                    updateCmd.Parameters.AddWithValue("@eqpid", eqpid);
                                    updateCmd.Parameters.AddWithValue("@no", pgRow.LampNo.Value);
                                    await updateCmd.ExecuteNonQueryAsync();
                                    _logManager.LogEvent($"[LampLifeService] Updated '{pgRow.LampName}' (ID:{pgRow.LampNo}) : Changed {cleanLastChanged}");
                                }
                            }
                        }
                    }
                }
            }
        }

        private string FindMssqlConnectionString()
        {
            string foundInstance = null;
            string machineName = Environment.MachineName;

            // 1. Registry 검색
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                {
                    if (key != null)
                    {
                        foreach (var name in key.GetValueNames())
                        {
                            if (name.IndexOf("SQLSERVER", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foundInstance = name;
                                _logManager.LogEvent($"[LampLifeService] Found MSSQL Instance (Registry): {foundInstance}");
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Windows Service 검색
            if (string.IsNullOrEmpty(foundInstance))
            {
                try
                {
                    var services = ServiceController.GetServices();
                    foreach (var service in services)
                    {
                        if (service.ServiceName.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase))
                        {
                            string instancePart = service.ServiceName.Substring(6);
                            if (instancePart.IndexOf("SQLSERVER", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foundInstance = instancePart;
                                _logManager.LogEvent($"[LampLifeService] Found MSSQL Instance (Service): {foundInstance}");
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Command Line (osql -L / sqlcmd -L) 실행 및 파싱
            if (string.IsNullOrEmpty(foundInstance))
            {
                _logManager.LogEvent("[LampLifeService] Registry/Service search failed. Trying 'sqlcmd -L'...");
                foundInstance = GetInstanceFromCommandLine(machineName);
            }

            if (string.IsNullOrEmpty(foundInstance))
            {
                _logManager.LogError($"[LampLifeService] Failed to detect any SQL Instance containing 'SQLSERVER' on host {machineName}.");
                return null;
            }

            // [수정] 호스트 이름 '.' (로컬) 사용
            string dataSource = $".\\{foundInstance}";
            return $"Data Source={dataSource};Initial Catalog=N2000_MEASURE;Integrated Security=True;TrustServerCertificate=True;";
        }

        private string GetInstanceFromCommandLine(string targetMachineName)
        {
            string found = null;
            string[] commands = { "sqlcmd", "osql" };

            foreach (var cmd in commands)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = "-L",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (Process p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            string output = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(3000);

                            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in lines)
                            {
                                string trimmed = line.Trim();
                                if (trimmed.IndexOf(targetMachineName, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                    trimmed.IndexOf("SQLSERVER", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    int slashIdx = trimmed.IndexOf('\\');
                                    if (slashIdx >= 0)
                                    {
                                        found = trimmed.Substring(slashIdx + 1).Trim();
                                        _logManager.LogEvent($"[LampLifeService] Found Instance via {cmd}: {found}");
                                        return found;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogDebug($"[LampLifeService] '{cmd} -L' failed: {ex.Message}");
                }
            }
            return null;
        }

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
                                IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='eqp_lamp_life' AND column_name='lamp_id') THEN
                                    ALTER TABLE public.eqp_lamp_life RENAME COLUMN lamp_id TO lamp_name;
                                END IF;
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

                        DateTime cleanNow = TruncateToSeconds(DateTime.Now);

                        foreach (var lamp in lamps)
                        {
                            cmd.Parameters["@eqpid"].Value = eqpid;
                            cmd.Parameters["@name"].Value = lamp.LampName;
                            cmd.Parameters["@ts"].Value = cleanNow;

                            if (int.TryParse(lamp.Age, out int age)) cmd.Parameters["@age"].Value = age;
                            else cmd.Parameters["@age"].Value = 0;

                            if (int.TryParse(lamp.LifeSpan, out int life)) cmd.Parameters["@life"].Value = life;
                            else cmd.Parameters["@life"].Value = 0;

                            if (DateTime.TryParse(lamp.LastChanged, out DateTime changed))
                                cmd.Parameters["@changed"].Value = TruncateToSeconds(changed);
                            else cmd.Parameters["@changed"].Value = DBNull.Value;

                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        private DateTime TruncateToSeconds(DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);
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
