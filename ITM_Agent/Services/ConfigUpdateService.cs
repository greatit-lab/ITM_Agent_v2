// ITM_Agent/Services/ConfigUpdateService.cs
using ConnectInfo; // (DatabaseInfo.GetConnectionString, GetIniValue, WriteAllTextSafe)
using Npgsql;
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography; // (AES 암호화)
using Newtonsoft.Json; // (FTP 정보 JSON 직렬화)
using System.Net.Sockets; // TCP 통신용

namespace ITM_Agent.Services
{
    internal class FtpConfig
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    public class ConfigUpdateService : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private readonly MainForm _mainForm;
        private readonly string _eqpid;
        private readonly System.Threading.Timer _pollTimer;

        private const int POLL_INTERVAL_MS = 60 * 1000;

        public ConfigUpdateService(SettingsManager settingsManager, LogManager logManager, MainForm mainForm, string eqpid)
        {
            _settingsManager = settingsManager;
            _logManager = logManager;
            _mainForm = mainForm;
            _eqpid = eqpid;

            _pollTimer = new System.Threading.Timer(
                async (_) => await CheckForConfigurationUpdatesAsync(),
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(POLL_INTERVAL_MS)
            );

            _logManager.LogEvent($"[ConfigUpdateService] Started. Polling interval: {POLL_INTERVAL_MS / 1000} sec.");
        }

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

        private async Task CheckForConfigurationUpdatesAsync()
        {
            _logManager.LogDebug("[ConfigUpdateService] Polling cfg_server for updates...");

            string updateFlag = null;
            string cs;

            try
            {
                try
                {
                    cs = DatabaseInfo.CreateDefault().GetConnectionString();
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[ConfigUpdateService] Cannot poll. Failed to read Connection.ini: {ex.Message}");
                    return;
                }

                string originalDbHost = GetOriginalDbHost();
                string originalFtpHost = GetOriginalFtpHost();

                string useProxy = _settingsManager.GetValueFromSection("Network", "UseProxy") == "1" ? "Y" : "N";
                string proxyIp = _settingsManager.GetValueFromSection("Network", "ProxyIP");
                if (useProxy == "N") proxyIp = null;

                using (var conn = new NpgsqlConnection(cs))
                {
                    await conn.OpenAsync();
                    await EnsureSchemaAsync(conn);

                    DateTime now = DateTime.Now;
                    DateTime cleanTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

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
                }

                if ("yes".Equals(updateFlag, StringComparison.OrdinalIgnoreCase))
                {
                    _logManager.LogEvent("[ConfigUpdateService] UpdateFlag 'yes' detected! Starting configuration update...");
                    await PerformUpdateProcessAsync(cs);
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

        // ⭐️ 신규: 프록시 서버에 목적지 IP 변경 명령 전송
        private async Task<bool> SendIpChangeCommandToProxyAsync(string proxyIp, string newIp)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    // Proxy Control Port 연결
                    await client.ConnectAsync(proxyIp, 19000);
                    using (var stream = client.GetStream())
                    {
                        byte[] data = Encoding.UTF8.GetBytes($"CHANGE_IP:{newIp}");
                        await stream.WriteAsync(data, 0, data.Length);

                        byte[] buffer = new byte[256];
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        if (response == "OK") return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ConfigUpdateService] Failed to send IP change to Proxy: {ex.Message}");
            }
            return false;
        }

        private async Task PerformUpdateProcessAsync(string oldConnectionString)
        {
            try
            {
                string newDbHost, newDbUser, newDbPw, newFtpHost, newFtpUser, newFtpPw;
                int newDbPort, newFtpPort;

                using (var conn = new NpgsqlConnection(oldConnectionString))
                {
                    await conn.OpenAsync();

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

                        newDbHost = reader.GetString(0);
                        newDbUser = reader.GetString(1);
                        newDbPw = reader.GetString(2);
                        newDbPort = reader.GetInt32(3);

                        newFtpHost = reader.GetString(4);
                        newFtpUser = reader.GetString(5);
                        newFtpPw = reader.GetString(6);
                        newFtpPort = reader.GetInt32(7);
                    }
                }
                _logManager.LogDebug("[ConfigUpdateService] Fetched new config from cfg_new_server.");

                // ⭐️ 신규: 프록시를 사용하는 경우, Agent 업데이트 전 프록시 IP 먼저 갱신
                string useProxy = _settingsManager.GetValueFromSection("Network", "UseProxy") == "1" ? "Y" : "N";
                string proxyIp = _settingsManager.GetValueFromSection("Network", "ProxyIP");

                if (useProxy == "Y" && !string.IsNullOrEmpty(proxyIp))
                {
                    _logManager.LogEvent($"[ConfigUpdateService] Sending dynamic IP change command to Proxy ({proxyIp})...");
                    bool proxyUpdated = await SendIpChangeCommandToProxyAsync(proxyIp, newDbHost);

                    if (!proxyUpdated)
                    {
                        _logManager.LogError("[ConfigUpdateService] CRITICAL: Proxy IP update failed. Aborting agent update to prevent network isolation.");
                        return; // 실패 시 에이전트 업데이트 및 재시작 중단 (방어 로직)
                    }
                    _logManager.LogEvent("[ConfigUpdateService] Proxy successfully updated its Target IP.");
                }

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

                string encryptedDbConfig = EncryptAES(dbConfigString, AgentCryptoConfig.AES_COMMON_KEY);
                string encryptedFtpConfig = EncryptAES(ftpConfigString, AgentCryptoConfig.AES_COMMON_KEY);

                _logManager.LogDebug("[ConfigUpdateService] New DB/FTP configs encrypted using AES Common Key.");

                StringBuilder iniBuilder = new StringBuilder();
                iniBuilder.AppendLine("; ITM Agent Connection Settings (Encrypted by ConfigUpdateService)");
                iniBuilder.AppendLine();
                iniBuilder.AppendLine("[Database]");
                iniBuilder.AppendLine($"Config = {encryptedDbConfig}");
                iniBuilder.AppendLine();
                iniBuilder.AppendLine("[Ftps]");
                iniBuilder.AppendLine($"Config = {encryptedFtpConfig}");

                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Connection.ini");
                DatabaseInfo.WriteAllTextSafe(configPath, iniBuilder.ToString());
                _logManager.LogEvent($"[ConfigUpdateService] Connection.ini updated securely with new host: {newDbHost}");

                _settingsManager.AutoRunOnStart = true;
                _logManager.LogEvent("[ConfigUpdateService] AutoRunOnStart set to 1.");

                _mainForm.TriggerRestartCycle();
            }
            catch (Exception ex)
            {
                _logManager.LogError($"[ConfigUpdateService] PerformUpdateProcess failed: {ex.Message}");
            }
        }

        public async Task ConfirmUpdateSuccessAsync()
        {
            try
            {
                string newCs = DatabaseInfo.CreateDefault().GetConnectionString();
                string newDbHost = GetOriginalDbHost();
                string newFtpHost = GetOriginalFtpHost();

                string useProxy = _settingsManager.GetValueFromSection("Network", "UseProxy") == "1" ? "Y" : "N";
                string proxyIp = _settingsManager.GetValueFromSection("Network", "ProxyIP");
                if (useProxy == "N") proxyIp = null;

                _logManager.LogEvent($"[ConfigUpdateService] Confirming update success to NEW DB (Real Host: {newDbHost})...");

                using (var conn = new NpgsqlConnection(newCs))
                {
                    await conn.OpenAsync();
                    await EnsureSchemaAsync(conn);

                    DateTime now = DateTime.Now;
                    DateTime cleanTime = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

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
                        cmd.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A");
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
                            _logManager.LogEvent("[ConfigUpdateService] Update confirmation: EQPID not found in new DB's cfg_server. Attempting INSERT.");
                            const string sqlInsertConfirm = @"
                                INSERT INTO public.cfg_server (eqpid, agent_db_host, agent_ftp_host, update_flag, ""update"", use_proxy, proxy_ip)
                                VALUES (@eqpid, @db_host, @ftp_host, 'no', @update_time, @use_proxy, @proxy_ip)
                                ON CONFLICT (eqpid) DO NOTHING;";

                            using (var cmdInsert = new NpgsqlCommand(sqlInsertConfirm, conn))
                            {
                                cmdInsert.Parameters.AddWithValue("@eqpid", _eqpid);
                                cmdInsert.Parameters.AddWithValue("@db_host", newDbHost ?? "N/A");
                                cmdInsert.Parameters.AddWithValue("@ftp_host", newFtpHost ?? "N/A");
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

        private string GetHostFromConnectionString(string cs)
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(cs);
                return builder.Host;
            }
            catch { return "N/A"; }
        }

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
