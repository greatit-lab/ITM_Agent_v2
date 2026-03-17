// ITM_Agent/Services/ConfigUpdateService.cs
using ConnectInfo; // (DatabaseInfo.GetConnectionString, GetIniValue, WriteAllTextSafe)
using Npgsql;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography; // (AES 암호화)
using Newtonsoft.Json; // (FTP 정보 JSON 직렬화 - ITM_Agent 프로젝트에 NuGet 설치 필요)

namespace ITM_Agent.Services
{
    /// <summary>
    /// FTP 접속 정보 DTO (Data Transfer Object)
    /// (ConnectInfo.dll 및 EncryptTool의 것과 동일한 구조)
    /// </summary>
    internal class FtpConfig
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    /// <summary>
    /// 논의된 '안정성을 극대화한 시나리오'를 구현하는 핵심 서비스.
    /// (DB 테이블 폴링, Connection.ini 갱신, Stop/Run 트리거, 완료 보고)
    /// 내부망 Proxy 환경에서도 원래의 목적지 IP와 Proxy 정보를 정확히 분리하여 보고합니다.
    /// </summary>
    public class ConfigUpdateService : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private readonly MainForm _mainForm; // Stop/Run 사이클 트리거용
        private readonly string _eqpid;
        private readonly System.Threading.Timer _pollTimer;

        // 1분 (테스트용)
        private const int POLL_INTERVAL_MS = 60 * 1000;
        // 24시간 (운영용)
        // private const int POLL_INTERVAL_MS = 24 * 60 * 60 * 1000; 

        public ConfigUpdateService(SettingsManager settingsManager, LogManager logManager, MainForm mainForm, string eqpid)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
            _mainForm = mainForm;
            _eqpid = eqpid;

            // 10초 후 첫 폴링 시작, 이후 설정된 간격
            _pollTimer = new System.Threading.Timer(
                async (_) => await CheckForConfigurationUpdatesAsync(),
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(POLL_INTERVAL_MS)
            );

            _logManager.LogEvent($"[ConfigUpdateService] Started. Polling interval: {POLL_INTERVAL_MS / 1000} sec.");
        }

        /// <summary>
        /// cfg_server 테이블에 새로운 컬럼(use_proxy, proxy_ip)이 없으면 자동으로 추가합니다.
        /// </summary>
        private async Task EnsureSchemaAsync(NpgsqlConnection conn)
        {
            const string schemaSql = @"
                DO $$ 
                BEGIN 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='cfg_server' AND column_name='use_proxy') THEN
                        ALTER TABLE public.cfg_server ADD COLUMN use_proxy VARCHAR(5) DEFAULT 'N';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='cfg_server' AND column_name='proxy_ip') THEN
                        ALTER TABLE public.cfg_server ADD COLUMN proxy_ip VARCHAR(50);
                    END IF;
                END $$;";

            try
            {
                using (var cmd = new NpgsqlCommand(schemaSql, conn))
                {
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (Exception ex)
            {
                _logManager.LogDebug($"[ConfigUpdateService] EnsureSchemaAsync skipped or failed: {ex.Message}");
            }
        }

        /// <summary>
        /// (1~3단계) (구)DB의 cfg_server를 주기적으로 폴링하여 상태를 보고하고,
        /// update_flag='yes'를 감지합니다.
        /// </summary>
        private async Task CheckForConfigurationUpdatesAsync()
        {
            _logManager.LogDebug("[ConfigUpdateService] Polling cfg_server for updates...");

            string updateFlag = null;
            string cs; // DB 접속용 연결 문자열 (Proxy가 적용되었다면 Proxy 주소)

            try
            {
                try
                {
                    // (1) DatabaseInfo를 통해 현재 연결 문자열 확보
                    cs = DatabaseInfo.CreateDefault().GetConnectionString();
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[ConfigUpdateService] Cannot poll. Failed to read Connection.ini: {ex.Message}");
                    return;
                }

                // 2. Connection.ini를 직접 복호화하여 '순수 원본 IP(최종 목적지)' 추출
                string originalDbHost = GetOriginalDbHost();
                string originalFtpHost = GetOriginalFtpHost();

                // 3. Settings.ini를 읽어 Proxy(내부망) 사용 여부 및 IP 확인 (SettingsManager 활용 최적화)
                string useProxy = _settingsManager.GetValueFromSection("Network", "UseProxy") == "1" ? "Y" : "N";
                string proxyIp = _settingsManager.GetValueFromSection("Network", "ProxyIP");
                if (useProxy == "N") proxyIp = null;

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();
                    await EnsureSchemaAsync(conn); // 스키마 자동 점검

                    // 밀리초 제거를 위해 C#에서 정제된 시간을 생성하여 파라미터로 전달
                    DateTime now = DateTime.Now;
                    DateTime cleanTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

                    // (2) cfg_server 테이블에서 내 EQPID 정보 조회 (최초 자동 등록 및 하트비트)
                    const string sqlPoll = @"
                        INSERT INTO public.cfg_server (eqpid, agent_db_host, agent_ftp_host, update_flag, ""update"", use_proxy, proxy_ip)
                        VALUES (@eqpid, @db_host, @ftp_host, 'no', @update_time, @use_proxy, @proxy_ip)
                        ON CONFLICT (eqpid) DO UPDATE
                        SET 
                            agent_db_host = EXCLUDED.agent_db_host,
                            agent_ftp_host = EXCLUDED.agent_ftp_host,
                            use_proxy = EXCLUDED.use_proxy,
                            proxy_ip = EXCLUDED.proxy_ip,
                            ""update"" = @update_time
                        RETURNING update_flag;";

                    using (var cmd = new NpgsqlCommand(sqlPoll, conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", _eqpid);
                        cmd.Parameters.AddWithValue("@db_host", originalDbHost ?? "N/A");
                        cmd.Parameters.AddWithValue("@ftp_host", originalFtpHost ?? "N/A");
                        cmd.Parameters.AddWithValue("@update_time", cleanTime);
                        cmd.Parameters.AddWithValue("@use_proxy", useProxy);
                        cmd.Parameters.AddWithValue("@proxy_ip", proxyIp ?? (object)DBNull.Value);

                        updateFlag = (string)await cmd.ExecuteScalarAsync();
                    }
                } // conn.Close()

                // (3) 'yes' 플래그 감지 시 업데이트 프로세스 시작
                if ("yes".Equals(updateFlag, StringComparison.OrdinalIgnoreCase))
                {
                    _logManager.LogEvent("[ConfigUpdateService] UpdateFlag 'yes' detected! Starting configuration update...");
                    await PerformUpdateProcessAsync(cs); // (구)DB 연결 문자열 전달
                }
                else
                {
                    _logManager.LogDebug("[ConfigUpdateService] No update required (flag is not 'yes').");
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ConfigUpdateService] Failed to poll for updates: {ex.Message}");
            }
        }

        /// <summary>
        /// (4~7단계) update_flag='yes' 감지 시, (구)DB에서 (신)DB 정보를 가져와
        /// Connection.ini를 암호화하여 갱신하고, Agent의 Stop/Run 사이클을 트리거합니다.
        /// </summary>
        private async Task PerformUpdateProcessAsync(string oldConnectionString)
        {
            try
            {
                // 1. (구)DB의 cfg_new_server 테이블에서 새 정보 조회
                string newDbHost, newDbUser, newDbPw, newFtpHost, newFtpUser, newFtpPw;
                int newDbPort, newFtpPort;

                using (var conn = new NpgsqlConnection(oldConnectionString))
                {
                    await conn.OpenAsync();

                    // 요청하신 새 스키마(v2) 쿼리
                    const string sqlFetchNew = @"
                        SELECT new_db_host, new_db_user, new_db_pw, new_db_port,
                               new_ftp_host, new_ftp_user, new_ftp_pw, new_ftp_port
                        FROM public.cfg_new_server LIMIT 1";

                    using (var cmd = new NpgsqlCommand(sqlFetchNew, conn))
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (!await reader.ReadAsync())
                        {
                            _logManager.LogError("[ConfigUpdateService] UpdateFlag was 'yes', but no settings found in cfg_new_server. Aborting.");
                            return;
                        }

                        // 새 스키마(v2) 인덱스
                        newDbHost = reader.GetString(0);
                        newDbUser = reader.GetString(1);
                        newDbPw = reader.GetString(2); // (평문)
                        newDbPort = reader.GetInt32(3);

                        newFtpHost = reader.GetString(4);
                        newFtpUser = reader.GetString(5);
                        newFtpPw = reader.GetString(6); // (평문)
                        newFtpPort = reader.GetInt32(7);
                    }
                } // conn.Close()
                _logManager.LogDebug("[ConfigUpdateService] Fetched new config from cfg_new_server.");

                // 2. 새 평문 정보로 (신)DB 연결 문자열 및 (신)FTP JSON 생성
                string dbName = new NpgsqlConnectionStringBuilder(oldConnectionString).Database;

                var csBuilder = new NpgsqlConnectionStringBuilder
                {
                    Host = newDbHost,
                    Port = newDbPort,
                    Username = newDbUser,
                    Password = newDbPw,
                    Database = dbName,
                    Encoding = "UTF8",
                    SslMode = SslMode.Disable,
                    SearchPath = "public"
                };
                string dbConfigString = csBuilder.ConnectionString;

                var ftpConfig = new FtpConfig
                {
                    Host = newFtpHost,
                    Port = newFtpPort,
                    Username = newFtpUser,
                    Password = newFtpPw
                };
                string ftpConfigString = JsonConvert.SerializeObject(ftpConfig);

                // 3. 각각 공용 키(AES)로 암호화
                string encryptedDbConfig = EncryptAES(dbConfigString, AgentCryptoConfig.AES_COMMON_KEY);
                string encryptedFtpConfig = EncryptAES(ftpConfigString, AgentCryptoConfig.AES_COMMON_KEY);

                _logManager.LogDebug("[ConfigUpdateService] New DB/FTP configs encrypted using AES Common Key.");

                // 4. 새로운 Connection.ini 파일 내용 생성
                StringBuilder iniBuilder = new StringBuilder();
                iniBuilder.AppendLine("; ITM Agent Connection Settings (Encrypted by ConfigUpdateService)");
                iniBuilder.AppendLine();
                iniBuilder.AppendLine("[Database]");
                iniBuilder.AppendLine($"Config = {encryptedDbConfig}");
                iniBuilder.AppendLine();
                iniBuilder.AppendLine("[Ftps]");
                iniBuilder.AppendLine($"Config = {encryptedFtpConfig}");

                // 5. Connection.ini 파일 새로 쓰기
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Connection.ini");
                DatabaseInfo.WriteAllTextSafe(configPath, iniBuilder.ToString());
                _logManager.LogEvent($"[ConfigUpdateService] Connection.ini updated securely with new host: {newDbHost}");

                // 6. Settings.ini에 1회성 AutoRunOnStart=1 설정
                _settingsManager.AutoRunOnStart = true;
                _logManager.LogEvent("[ConfigUpdateService] AutoRunOnStart set to 1.");

                // 7. MainForm에 Stop/Run 사이클 트리거 요청
                _mainForm.TriggerRestartCycle();
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ConfigUpdateService] PerformUpdateProcess failed: {ex.Message}");
            }
        }

        /// <summary>
        /// (MainForm의 PerformRunLogic -> AutoRun 초기화 후 호출됨)
        /// 신(B) DB에 접속하여 업데이트 완료 보고
        /// </summary>
        public async Task ConfirmUpdateSuccessAsync()
        {
            try
            {
                // (8) (신)DB에 접속
                string newCs = DatabaseInfo.CreateDefault().GetConnectionString();

                // 업데이트 후, 바뀐 Connection.ini에서 원본 IP 다시 추출
                string newDbHost = GetOriginalDbHost();
                string newFtpHost = GetOriginalFtpHost();

                // SettingsManager 활용 최적화
                string useProxy = _settingsManager.GetValueFromSection("Network", "UseProxy") == "1" ? "Y" : "N";
                string proxyIp = _settingsManager.GetValueFromSection("Network", "ProxyIP");
                if (useProxy == "N") proxyIp = null;

                _logManager.LogEvent($"[ConfigUpdateService] Confirming update success to NEW DB (Real Host: {newDbHost})...");

                using (var conn = new NpgsqlConnection(newCs))
                {
                    await conn.OpenAsync();
                    await EnsureSchemaAsync(conn);

                    // 밀리초 제거된 cleanTime 사용
                    DateTime now = DateTime.Now;
                    DateTime cleanTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

                    // (9) cfg_server 테이블에 최종 상태 업데이트
                    const string sqlConfirm = @"
                        UPDATE public.cfg_server 
                        SET 
                            agent_db_host = @db_host,
                            agent_ftp_host = @ftp_host,
                            use_proxy = @use_proxy,
                            proxy_ip = @proxy_ip,
                            update_flag = 'no',
                            ""update"" = @update_time
                        WHERE eqpid = @eqpid;";

                    using (var cmd = new NpgsqlCommand(sqlConfirm, conn))
                    {
                        cmd.Parameters.AddWithValue("@db_host", newDbHost ?? "N/A");
                        cmd.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A"); // (null 방지)
                        cmd.Parameters.AddWithValue("@eqpid", _eqpid);
                        cmd.Parameters.AddWithValue("@update_time", cleanTime);
                        cmd.Parameters.AddWithValue("@use_proxy", useProxy);
                        cmd.Parameters.AddWithValue("@proxy_ip", proxyIp ?? (object)DBNull.Value);

                        int affected = await cmd.ExecuteNonQueryAsync();

                        if (affected > 0)
                        {
                            _logManager.LogEvent($"[ConfigUpdateService] Update confirmation sent. {affected} row(s) updated.");
                        }
                        else
                        {
                            // (신)DB에 cfg_server 레코드가 없는 경우 (구->신 DB 마이그레이션이 안된 경우)
                            _logManager.LogEvent("[ConfigUpdateService] Update confirmation: EQPID not found in new DB's cfg_server. Attempting INSERT.");
                            const string sqlInsertConfirm = @"
                                INSERT INTO public.cfg_server (eqpid, agent_db_host, agent_ftp_host, update_flag, ""update"", use_proxy, proxy_ip)
                                VALUES (@eqpid, @db_host, @ftp_host, 'no', @update_time, @use_proxy, @proxy_ip)
                                ON CONFLICT (eqpid) DO NOTHING;";

                            using (var cmdInsert = new NpgsqlCommand(sqlInsertConfirm, conn))
                            {
                                cmdInsert.Parameters.AddWithValue("@eqpid", _eqpid);
                                cmdInsert.Parameters.AddWithValue("@db_host", newDbHost ?? "N/A");
                                cmdInsert.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A"); // (null 방지)
                                cmdInsert.Parameters.AddWithValue("@update_time", cleanTime);
                                cmdInsert.Parameters.AddWithValue("@use_proxy", useProxy);
                                cmdInsert.Parameters.AddWithValue("@proxy_ip", proxyIp ?? (object)DBNull.Value);
                                await cmdInsert.ExecuteNonQueryAsync();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ConfigUpdateService] Failed to confirm update to new DB: {ex.Message}");
            }
        }

        // --- Connection.ini 직접 파싱/복호화 (Proxy 스와핑 방지) ---
        private string GetOriginalDbHost()
        {
            try
            {
                string encrypted = DatabaseInfo.GetIniValue("Database", "Config");
                if (string.IsNullOrEmpty(encrypted)) return null;
                string plain = DecryptAES(encrypted, AgentCryptoConfig.AES_COMMON_KEY);
                return new NpgsqlConnectionStringBuilder(plain).Host;
            }
            catch { return null; }
        }

        private string GetOriginalFtpHost()
        {
            try
            {
                string encrypted = DatabaseInfo.GetIniValue("Ftps", "Config");
                if (string.IsNullOrEmpty(encrypted)) return null;
                string plain = DecryptAES(encrypted, AgentCryptoConfig.AES_COMMON_KEY);
                var config = JsonConvert.DeserializeObject<FtpConfig>(plain);
                return config?.Host;
            }
            catch { return null; }
        }

        // --- 헬퍼 메서드 ---
        private string GetHostFromConnectionString(string cs)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(cs);
                return builder.Host;
            }
            catch { return "N/A"; }
        }

        /// <summary>
        /// (EncryptTool과 동일한 AES 암호화 헬퍼)
        /// </summary>
        private static string EncryptAES(string plainText, string keyString)
        {
            if (plainText == null) plainText = "";
            byte[] keyBytes;
            using (var sha = SHA256.Create())
            {
                keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(keyString));
            }
            using (var aes = new AesManaged())
            {
                aes.Key = keyBytes;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();
                byte[] iv = aes.IV;
                byte[] cipherBytes;
                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    cipherBytes = ms.ToArray();
                }
                byte[] fullCipher = new byte[iv.Length + cipherBytes.Length];
                Buffer.BlockCopy(iv, 0, fullCipher, 0, iv.Length);
                Buffer.BlockCopy(cipherBytes, 0, fullCipher, iv.Length, cipherBytes.Length);
                return Convert.ToBase64String(fullCipher);
            }
        }

        /// <summary>
        /// (ConnectInfo.dll과 동일한 AES 복호화 헬퍼)
        /// </summary>
        private static string DecryptAES(string cipherText, string keyString)
        {
            byte[] fullCipher = Convert.FromBase64String(cipherText);
            byte[] keyBytes;
            using (var sha = SHA256.Create())
            {
                keyBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(keyString));
            }
            byte[] iv = new byte[16];
            byte[] cipher = new byte[fullCipher.Length - 16];
            Buffer.BlockCopy(fullCipher, 0, iv, 0, 16);
            Buffer.BlockCopy(fullCipher, 16, cipher, 0, fullCipher.Length - 16);
            using (var aes = new AesManaged())
            {
                aes.Key = keyBytes;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                using (var ms = new MemoryStream(cipher))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
        }
    }
}
