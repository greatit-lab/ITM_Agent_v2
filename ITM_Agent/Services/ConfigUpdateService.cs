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

        // ▼▼▼ "공용 키" (ConnectInfo.dll/EncryptTool의 키와 100% 동일) ▼▼▼
        private const string AES_COMMON_KEY = "greatit-lab-itm-agent-v1-secret";

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
        /// (1~3단계) (구)DB의 cfg_server를 주기적으로 폴링하여 상태를 보고하고,
        /// update_flag='yes'를 감지합니다.
        /// </summary>
        private async Task CheckForConfigurationUpdatesAsync()
        {
            _logManager.LogDebug("[ConfigUpdateService] Polling cfg_server for updates...");

            string updateFlag = null;
            string cs; // (구)DB 연결 문자열

            try
            {
                // (1) Connection.ini의 현재 (구)DB 정보로 접속
                try
                {
                    cs = DatabaseInfo.CreateDefault().GetConnectionString();
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[ConfigUpdateService] Cannot poll. Failed to read Connection.ini: {ex.Message}");
                    return;
                }

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();

                    // (2) cfg_server 테이블에서 내 EQPID 정보 조회 (최초 자동 등록 및 하트비트)
                    // (요청사항 반영: 컬럼명 'update', NOW()::timestamp(0) 사용)
                    const string sqlPoll = @"
                        INSERT INTO public.cfg_server (eqpid, agent_db_host, agent_ftp_host, update_flag, ""update"")
                        VALUES (@eqpid, @db_host, @ftp_host, 'no', NOW()::timestamp(0))
                        ON CONFLICT (eqpid) DO UPDATE
                        SET 
                            agent_db_host = EXCLUDED.agent_db_host,
                            agent_ftp_host = EXCLUDED.agent_ftp_host,
                            ""update"" = NOW()::timestamp(0)
                        RETURNING update_flag;";

                    // Connection.ini에서 현재 설정 읽기
                    string iniDbHost = GetHostFromConnectionString(cs);

                    // FtpsInfo.CreateDefault()를 호출하여 FTP 호스트 가져오기
                    string iniFtpHost = "N/A"; // (기본값)
                    try
                    {
                        // FtpsInfo 인스턴스 생성을 시도하여 Host 속성을 바로 읽어옴
                        iniFtpHost = FtpsInfo.CreateDefault().Host;
                        if (string.IsNullOrEmpty(iniFtpHost))
                        {
                            iniFtpHost = "N/A";
                            _logManager.LogEvent("[ConfigUpdateService] FtpsInfo.Host is null or empty.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // (ConnectInfo.dll이 [Ftps] Config를 읽거나 복호화/파싱하다 실패한 경우)
                        _logManager.LogError($"[ConfigUpdateService] Failed to get FTP Host from FtpsInfo: {ex.Message}");
                        iniFtpHost = "N/A"; // (실패 시 N/A 유지)
                    }

                    using (var cmd = new NpgsqlCommand(sqlPoll, conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", _eqpid);
                        cmd.Parameters.AddWithValue("@db_host", iniDbHost);
                        cmd.Parameters.AddWithValue("@ftp_host", iniFtpHost);

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
        /// (요청하신 v2 스키마 및 v2 Config 형식 적용)
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
                // (DB 이름은 기존과 동일하다고 가정)
                string dbName = new NpgsqlConnectionStringBuilder(oldConnectionString).Database;

                var csBuilder = new NpgsqlConnectionStringBuilder
                {
                    Host = newDbHost,
                    Port = newDbPort,
                    Username = newDbUser,
                    Password = newDbPw, // (평문 암호 포함)
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
                    Password = newFtpPw // (평문 암호 포함)
                };
                string ftpConfigString = JsonConvert.SerializeObject(ftpConfig);

                // 3. 각각 공용 키(AES)로 암호화
                string encryptedDbConfig = EncryptAES(dbConfigString, AES_COMMON_KEY);
                string encryptedFtpConfig = EncryptAES(ftpConfigString, AES_COMMON_KEY);

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
                string newDbHost = GetHostFromConnectionString(newCs);
                string newFtpHost = "N/A";
                try
                {
                    // (Connection.ini가 방금 바뀌었으므로 FtpsInfo.CreateDefault()를 다시 호출)
                    newFtpHost = FtpsInfo.CreateDefault().Host;
                }
                catch { }

                _logManager.LogEvent($"[ConfigUpdateService] Confirming update success to NEW DB ({newDbHost})...");

                using (var conn = new NpgsqlConnection(newCs))
                {
                    await conn.OpenAsync();

                    // (9) cfg_server 테이블에 최종 상태 업데이트 (신규 DB에도 이 테이블이 있어야 함)
                    // (요청사항 반영: 컬럼명 'update', NOW()::timestamp(0) 사용)
                    const string sqlConfirm = @"
                        UPDATE public.cfg_server 
                        SET 
                            agent_db_host = @db_host,
                            agent_ftp_host = @ftp_host,
                            update_flag = 'no',
                            ""update"" = NOW()::timestamp(0)
                        WHERE eqpid = @eqpid;";

                    using (var cmd = new NpgsqlCommand(sqlConfirm, conn))
                    {
                        cmd.Parameters.AddWithValue("@db_host", newDbHost);
                        cmd.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A"); // (null 방지)
                        cmd.Parameters.AddWithValue("@eqpid", _eqpid);
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
                                INSERT INTO public.cfg_server (eqpid, agent_db_host, agent_ftp_host, update_flag, ""update"")
                                VALUES (@eqpid, @db_host, @ftp_host, 'no', NOW()::timestamp(0))
                                ON CONFLICT (eqpid) DO NOTHING;";

                            using (var cmdInsert = new NpgsqlCommand(sqlInsertConfirm, conn))
                            {
                                cmdInsert.Parameters.AddWithValue("@eqpid", _eqpid);
                                cmdInsert.Parameters.AddWithValue("@db_host", newDbHost);
                                cmdInsert.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A"); // (null 방지)
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
