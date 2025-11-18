// ITM_Agent/Services/EqpidManager.cs
using ConnectInfo;
using Npgsql;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ITM_Agent.Services
{
    public class EqpidManager
    {
        private readonly SettingsManager settingsManager;
        private readonly LogManager logManager;
        private readonly string appVersion;

        private static readonly ConcurrentDictionary<string, TimeZoneInfo> timezoneCache = new ConcurrentDictionary<string, TimeZoneInfo>();

        public EqpidManager(SettingsManager settings, LogManager logManager, string appVersion)
        {
            this.settingsManager = settings ?? throw new ArgumentNullException(nameof(settings));
            this.logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            this.appVersion = appVersion ?? throw new ArgumentNullException(nameof(appVersion));
        }

        public TimeZoneInfo GetTimezoneForEqpid(string eqpid)
        {
            if (timezoneCache.TryGetValue(eqpid, out TimeZoneInfo cachedZone))
            {
                return cachedZone;
            }

            TimeZoneInfo fetchedZone = null;
            string timezoneId = null;
            try
            {
                string cs = DatabaseInfo.CreateDefault().GetConnectionString();
                using (var conn = new NpgsqlConnection(cs))
                {
                    conn.Open();
                    using (var cmd = new NpgsqlCommand("SELECT timezone FROM public.agent_info WHERE eqpid = @eqpid LIMIT 1", conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", eqpid);
                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                        {
                            timezoneId = result.ToString();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(timezoneId))
                {
                    fetchedZone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
                    timezoneCache.TryAdd(eqpid, fetchedZone);
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[EqpidManager] Failed to fetch timezone for {eqpid}: {ex.Message}");
                return TimeZoneInfo.Local;
            }

            return fetchedZone ?? TimeZoneInfo.Local;
        }

        public void InitializeEqpid()
        {
            logManager.LogEvent("[EqpidManager] Initializing Eqpid.");
            string eqpid = settingsManager.GetEqpid();
            if (string.IsNullOrEmpty(eqpid))
            {
                logManager.LogEvent("[EqpidManager] Eqpid is empty. Prompting for input.");
                PromptForEqpid();
            }
            else
            {
                logManager.LogEvent($"[EqpidManager] Eqpid found: {eqpid}");
                // 기존 UploadAgentInfoToDatabase -> 신규 RegisterOrUpdateAgentInfo로 변경
                RegisterOrUpdateAgentInfo(eqpid, settingsManager.GetEqpType());
            }
        }

        private void PromptForEqpid()
        {
            bool isValidInput = false;
            while (!isValidInput)
            {
                using (var form = new EqpidInputForm())
                {
                    var result = form.ShowDialog();
                    if (result == DialogResult.OK)
                    {
                        logManager.LogEvent($"[EqpidManager] Eqpid input accepted: {form.Eqpid}");
                        settingsManager.SetEqpid(form.Eqpid.ToUpper());
                        settingsManager.SetType(form.Type);
                        logManager.LogEvent($"[EqpidManager] Type set to: {form.Type}");

                        UploadAgentInfoToDatabase(form.Eqpid.ToUpper(), form.Type);
                        isValidInput = true;
                    }
                    else if (result == DialogResult.Cancel)
                    {
                        logManager.LogEvent("[EqpidManager] Eqpid input canceled. Application will exit.");
                        MessageBox.Show("Eqpid 입력이 취소되었습니다. 애플리케이션을 종료합니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Environment.Exit(0);
                    }
                }
            }
        }

        // 메서드명 변경 및 로직 분리
        private void RegisterOrUpdateAgentInfo(string eqpid, string type)
        {
            // (1) agent_info 테이블 업데이트 (기존 UploadAgentInfoToDatabase 로직)
            UploadAgentInfoToDatabase(eqpid, type);
        }

        private void UploadAgentInfoToDatabase(string eqpid, string type)
        {
            // Connection.ini를 읽도록 수정된 DatabaseInfo.cs를 호출
            string connString;
            try
            {
                connString = DatabaseInfo.CreateDefault().GetConnectionString();
            }
            catch (Exception ex)
            {
                logManager.LogError($"[EqpidManager] Failed to get ConnectionString from Connection.ini: {ex.Message}");
                MessageBox.Show($"Connection.ini 파일에서 DB 접속 정보를 읽는 데 실패했습니다. 프로그램을 종료합니다.\n\n{ex.Message}", "치명적 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
                return;
            }

            var currentInfo = new
            {
                OsVersion = SystemInfoCollector.GetOSVersion(),
                Architecture = SystemInfoCollector.GetOsArchitecture(),
                MachineName = SystemInfoCollector.GetMachineName(),
                Locale = SystemInfoCollector.GetLocale(),
                TimeZone = SystemInfoCollector.GetTimeZoneId(),
                IpAddress = SystemInfoCollector.GetIpAddress(),
                MacAddress = SystemInfoCollector.GetMacAddress(),
                Cpu = SystemInfoCollector.GetFormattedCpuInfo(),
                Memory = SystemInfoCollector.GetFormattedMemoryInfo(),
                Disk = SystemInfoCollector.GetFormattedDiskInfo(),
                Vga = SystemInfoCollector.GetFormattedVgaInfo()
            };

            try
            {
                using (var conn = new NpgsqlConnection(connString))
                {
                    conn.Open();

                    Dictionary<string, object> existingInfo = null;
                    const string selectSql = "SELECT * FROM public.agent_info WHERE eqpid = @eqpid LIMIT 1";
                    using (var cmd = new NpgsqlCommand(selectSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", eqpid);
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                existingInfo = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    existingInfo[reader.GetName(i)] = reader.GetValue(i);
                                }
                            }
                        }
                    }

                    bool hasChanges = CheckForChanges(existingInfo, currentInfo, type);

                    if (!hasChanges)
                    {
                        logManager.LogEvent("[EqpidManager] No changes detected in agent info. Skipping update.");
                        return;
                    }

                    const string agentSql = @"
                        INSERT INTO public.agent_info (
                            eqpid, type, os, system_type, pc_name, locale, timezone, app_ver, 
                            reg_date, servtime, ip_address, mac_address, cpu, memory, 
                            disk, vga
                        ) VALUES (
                            @eqpid, @type, @os, @arch, @pc_name, @loc, @tz, @app_ver, 
                            @pc_now::timestamp(0), NOW()::timestamp(0), @ip_address, @mac_address, @cpu, @memory,
                            @disk, @vga
                        )
                        ON CONFLICT (eqpid, pc_name) DO UPDATE SET
                            type = EXCLUDED.type, os = EXCLUDED.os, system_type = EXCLUDED.system_type,
                            pc_name = EXCLUDED.pc_name, locale = EXCLUDED.locale, timezone = EXCLUDED.timezone,
                            app_ver = EXCLUDED.app_ver, reg_date = EXCLUDED.reg_date, servtime = NOW()::timestamp(0),
                            ip_address = EXCLUDED.ip_address, mac_address = EXCLUDED.mac_address,
                            cpu = EXCLUDED.cpu, memory = EXCLUDED.memory, disk = EXCLUDED.disk,
                            vga = EXCLUDED.vga;";

                    using (var cmd = new NpgsqlCommand(agentSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@eqpid", eqpid);
                        cmd.Parameters.AddWithValue("@type", type ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@os", currentInfo.OsVersion);
                        cmd.Parameters.AddWithValue("@arch", currentInfo.Architecture);
                        cmd.Parameters.AddWithValue("@pc_name", currentInfo.MachineName);
                        cmd.Parameters.AddWithValue("@loc", currentInfo.Locale);
                        cmd.Parameters.AddWithValue("@tz", currentInfo.TimeZone);
                        cmd.Parameters.AddWithValue("@app_ver", appVersion);
                        cmd.Parameters.AddWithValue("@pc_now", DateTime.Now);
                        cmd.Parameters.AddWithValue("@ip_address", currentInfo.IpAddress);
                        cmd.Parameters.AddWithValue("@mac_address", currentInfo.MacAddress);
                        cmd.Parameters.AddWithValue("@cpu", currentInfo.Cpu);
                        cmd.Parameters.AddWithValue("@memory", currentInfo.Memory);
                        cmd.Parameters.AddWithValue("@disk", currentInfo.Disk);
                        cmd.Parameters.AddWithValue("@vga", currentInfo.Vga);
                        cmd.ExecuteNonQuery();
                    }
                    logManager.LogEvent($"[EqpidManager] agent_info table updated successfully.");
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[EqpidManager] DB upload failed: {ex.Message}");
            }
        }

        private bool CheckForChanges(Dictionary<string, object> dbData, dynamic currentData, string currentType)
        {
            if (dbData == null)
            {
                logManager.LogEvent("[EqpidManager] No existing record found. Inserting new record.");
                return true;
            }

            var changes = new List<string>();
            string GetDbString(string key) => (dbData.TryGetValue(key, out var value) && value != DBNull.Value) ? value.ToString() : "";
            string GetCurrentString(string value) => value ?? "";

            void Compare(string fieldName, string dbValue, string currentValue)
            {
                if (!string.Equals(dbValue, currentValue, StringComparison.Ordinal))
                {
                    changes.Add($"'{fieldName}' changed from '{dbValue}' to '{currentValue}'");
                }
            }

            Compare("type", GetDbString("type"), GetCurrentString(currentType));
            Compare("os", GetDbString("os"), GetCurrentString(currentData.OsVersion));
            Compare("system_type", GetDbString("system_type"), GetCurrentString(currentData.Architecture));
            Compare("pc_name", GetDbString("pc_name"), GetCurrentString(currentData.MachineName));
            Compare("locale", GetDbString("locale"), GetCurrentString(currentData.Locale));
            Compare("timezone", GetDbString("timezone"), GetCurrentString(currentData.TimeZone));
            Compare("app_ver", GetDbString("app_ver"), GetCurrentString(appVersion));
            Compare("ip_address", GetDbString("ip_address"), GetCurrentString(currentData.IpAddress));
            Compare("mac_address", GetDbString("mac_address"), GetCurrentString(currentData.MacAddress));
            Compare("cpu", GetDbString("cpu"), GetCurrentString(currentData.Cpu));
            Compare("memory", GetDbString("memory"), GetCurrentString(currentData.Memory));
            Compare("disk", GetDbString("disk"), GetCurrentString(currentData.Disk));
            Compare("vga", GetDbString("vga"), GetCurrentString(currentData.Vga));

            if (changes.Any())
            {
                logManager.LogEvent($"[EqpidManager] Changes detected: {string.Join("; ", changes)}. Updating record.");
                return true;
            }
            return false;
        }
    }

    public static class SystemInfoCollector
    {
        private static ManagementObjectCollection GetWmiQueryResult(string wmiClass, string property)
        {
            try
            {
                var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
                return searcher.Get();
            }
            catch { return null; }
        }

        public static string GetOsArchitecture() => Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";

        public static string GetFormattedCpuInfo()
        {
            try
            {
                var cpu = GetWmiQueryResult("Win32_Processor", "Name")?.Cast<ManagementObject>().FirstOrDefault();
                if (cpu == null) return "N/A";

                string cpuName = cpu["Name"]?.ToString().Trim() ?? "";
                cpuName = Regex.Replace(cpuName, @"\(R\)|\(TM\)|CPU|Processor|Gen", "").Trim();
                cpuName = Regex.Replace(cpuName, @"\s+", " ");
                var match = Regex.Match(cpuName, @"(Intel Core i\d-\d+\w*|AMD Ryzen \d \d+\w*)\s*@\s*(\d\.\d+\wHz)");
                if (match.Success)
                {
                    return $"{match.Groups[1].Value.Replace("Core ", "")} {match.Groups[2].Value}";
                }
                return cpuName;
            }
            catch { return "N/A"; }
        }

        // ▼▼▼ [핵심 수정] 메모리 정보 조회 로직 안정성 강화 ▼▼▼
        public static string GetFormattedMemoryInfo()
        {
            try
            {
                double totalMemoryGB = 0;
                // 1순위: Win32_OperatingSystem (KB 단위, 더 안정적)
                var os = GetWmiQueryResult("Win32_OperatingSystem", "TotalVisibleMemorySize")?.Cast<ManagementObject>().FirstOrDefault();
                if (os != null && os["TotalVisibleMemorySize"] != null)
                {
                    totalMemoryGB = Math.Round(Convert.ToDouble(os["TotalVisibleMemorySize"]) / (1024 * 1024));
                }
                else // 2순위: Win32_ComputerSystem (Byte 단위)
                {
                    var cs = GetWmiQueryResult("Win32_ComputerSystem", "TotalPhysicalMemory")?.Cast<ManagementObject>().FirstOrDefault();
                    if (cs != null && cs["TotalPhysicalMemory"] != null)
                    {
                        totalMemoryGB = Math.Round(Convert.ToDouble(cs["TotalPhysicalMemory"]) / (1024 * 1024 * 1024));
                    }
                }

                if (totalMemoryGB <= 0) return "N/A";

                string memoryType = "Unknown";
                var memoryDevices = GetWmiQueryResult("Win32_PhysicalMemory", "SMBIOSMemoryType, MemoryType")?.Cast<ManagementObject>();
                if (memoryDevices != null)
                {
                    foreach (var device in memoryDevices)
                    {
                        if (device["SMBIOSMemoryType"] != null)
                        {
                            string type = GetMemoryTypeFromSmbios(device["SMBIOSMemoryType"].ToString());
                            if (type != "Unknown") { memoryType = type; break; }
                        }
                        if (memoryType == "Unknown" && device["MemoryType"] != null)
                        {
                            string type = GetMemoryTypeFromGeneral(device["MemoryType"].ToString());
                            if (type != "Unknown") { memoryType = type; break; }
                        }
                    }
                }

                return (memoryType != "Unknown") ? $"{memoryType} {totalMemoryGB}GB" : $"{totalMemoryGB}GB";
            }
            catch { return "N/A"; }
        }

        private static string GetMemoryTypeFromSmbios(string typeCode)
        {
            switch (Convert.ToInt32(typeCode))
            {
                case 20: return "DDR";
                case 21: return "DDR2";
                case 22: return "FBDIMM";
                case 24: return "DDR3";
                case 26: case 30: return "DDR4";
                case 34: return "DDR5";
                default: return "Unknown";
            }
        }

        private static string GetMemoryTypeFromGeneral(string typeCode)
        {
            switch (Convert.ToInt32(typeCode))
            {
                case 17: return "DDR";
                case 18: return "DDR2";
                case 24: return "DDR3";
                case 26: return "DDR4";
                case 0: default: return "Unknown";
            }
        }

        public static string GetFormattedDiskInfo()
        {
            try
            {
                var partitionInfos = new List<string>();
                var logicalDisks = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDisk WHERE DriveType = 3").Get().Cast<ManagementObject>();

                foreach (var logicalDisk in logicalDisks)
                {
                    var deviceID = logicalDisk["DeviceID"]?.ToString();
                    if (deviceID == null) continue;

                    var totalSizeBytes = Convert.ToUInt64(logicalDisk["Size"]);
                    var totalSizeGB = Math.Round((double)totalSizeBytes / (1024 * 1024 * 1024), 2);

                    partitionInfos.Add($"{deviceID} {totalSizeGB:F2}GB");
                }
                return string.Join(" / ", partitionInfos);
            }
            catch { return "N/A"; }
        }

        public static string GetFormattedVgaInfo()
        {
            try
            {
                var controllers = GetWmiQueryResult("Win32_VideoController", "Name, AdapterRAM");
                if (controllers == null) return "N/A";

                var vgaList = new List<Tuple<string, double>>();
                foreach (var controller in controllers.Cast<ManagementObject>())
                {
                    string name = controller["Name"]?.ToString();
                    object ramObj = controller["AdapterRAM"];

                    if (string.IsNullOrEmpty(name) || name.Contains("Microsoft Basic") || name.Contains("Mirage"))
                        continue;

                    double ramMB = 0;
                    if (ramObj != null && double.TryParse(ramObj.ToString(), out double ramBytes))
                    {
                        ramMB = Math.Round(ramBytes / (1024 * 1024), 0);
                    }
                    vgaList.Add(Tuple.Create(name, ramMB));
                }

                if (!vgaList.Any()) return "N/A";

                var discreteGpu = vgaList.FirstOrDefault(vga =>
                    vga.Item1.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    vga.Item1.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0);

                if (discreteGpu != null)
                {
                    return $"{discreteGpu.Item1} ({discreteGpu.Item2} MB)";
                }

                var firstGpu = vgaList.First();
                return $"{firstGpu.Item1} ({firstGpu.Item2} MB)";
            }
            catch { return "N/A"; }
        }

        public static string GetOSVersion() => GetWmiQueryResult("Win32_OperatingSystem", "Caption")?.Cast<ManagementObject>().FirstOrDefault()?["Caption"]?.ToString().Trim() ?? "N/A";
        public static string GetMachineName() => Environment.MachineName;
        public static string GetLocale() => CultureInfo.CurrentUICulture.Name;
        public static string GetTimeZoneId() => TimeZoneInfo.Local.Id;

        private static (string IpAddress, string MacAddress) GetPrimaryNetworkInfo()
        {
            try
            {
                var primaryInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.GetIPProperties().GatewayAddresses.Any())
                    .OrderByDescending(n => n.Speed)
                    .FirstOrDefault();

                if (primaryInterface != null)
                {
                    var ipProps = primaryInterface.GetIPProperties();
                    var ipv4Address = ipProps.UnicastAddresses
                        .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)?
                        .Address.ToString();

                    var macAddress = primaryInterface.GetPhysicalAddress()?.ToString();
                    return (ipv4Address ?? "Not found", macAddress ?? "N/A");
                }
            }
            catch { /* Fallback */ }

            var fallbackIp = Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "Not found";

            var fallbackMac = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet && ni.OperationalStatus == OperationalStatus.Up)
                ?.GetPhysicalAddress().ToString() ?? "N/A";

            return (fallbackIp, fallbackMac);
        }

        public static string GetIpAddress()
        {
            return GetPrimaryNetworkInfo().IpAddress;
        }

        public static string GetMacAddress()
        {
            return GetPrimaryNetworkInfo().MacAddress;
        }
    }
}
