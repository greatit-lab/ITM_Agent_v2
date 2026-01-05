// ITM_Agent/Services/PerformanceMonitor.cs
using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ITM_Agent.Services
{
    public sealed class ProcessMetric
    {
        public string ProcessName { get; set; }
        public long MemoryUsageMB { get; set; } // Private Working Set (Private Bytes)
        public long SharedMemoryUsageMB { get; set; } // 추가: 공유 메모리 사용량
    }

    public sealed class PerformanceMonitor
    {
        private static readonly Lazy<PerformanceMonitor> _inst = new Lazy<PerformanceMonitor>(() => new PerformanceMonitor());
        public static PerformanceMonitor Instance => _inst.Value;

        private const long MAX_LOG_SIZE = 5 * 1024 * 1024;
        private readonly HardwareSampler sampler;
        private readonly CircularBuffer<Metric> buffer = new CircularBuffer<Metric>(capacity: 1000);
        private readonly Timer flushTimer;
        private readonly object sync = new object();
        private const int FLUSH_INTERVAL_MS = 30_000;
        private const int BULK_COUNT = 60;
        private bool isEnabled;
        private bool sampling;
        private bool fileLoggingEnabled;

        internal void StartSampling()
        {
            if (sampling) return;
            sampling = true;
            sampler.Start();
        }

        internal void StopSampling()
        {
            if (!sampling) return;
            sampler.Stop();
            DisableFileLogging();
            sampling = false;
        }

        internal void SetFileLogging(bool enable) => (enable ? (Action)EnableFileLogging : DisableFileLogging)();
        private void EnableFileLogging()
        {
            if (fileLoggingEnabled) return;
            Directory.CreateDirectory(GetLogDir());
            flushTimer.Change(FLUSH_INTERVAL_MS, FLUSH_INTERVAL_MS);
            fileLoggingEnabled = true;
        }

        private void DisableFileLogging()
        {
            if (!fileLoggingEnabled) return;
            flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            FlushToFile();
            fileLoggingEnabled = false;
        }

        internal void RegisterConsumer(Action<Metric> consumer)
        {
            sampler.OnSample += consumer;
        }

        internal void UnregisterConsumer(Action<Metric> consumer)
        {
            sampler.OnSample -= consumer;
        }

        private PerformanceMonitor()
        {
            sampler = new HardwareSampler(intervalMs: 5_000);
            sampler.OnSample += OnSampleReceived;
            sampler.OnThresholdExceeded += () => sampler.IntervalMs = 1_000;
            sampler.OnBackToNormal += () => sampler.IntervalMs = 5_000;
            flushTimer = new Timer(_ => FlushToFile(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (sync)
            {
                if (isEnabled) return;
                isEnabled = true;
                Directory.CreateDirectory(GetLogDir());
                sampler.Start();
                flushTimer.Change(FLUSH_INTERVAL_MS, FLUSH_INTERVAL_MS);
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                if (!isEnabled) return;
                isEnabled = false;
                sampler.Stop();
                flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                FlushToFile();
            }
        }

        private void OnSampleReceived(Metric m)
        {
            lock (sync)
            {
                buffer.Push(m);
                if (fileLoggingEnabled && buffer.Count >= BULK_COUNT)
                    FlushToFile();
            }
        }

        private void FlushToFile()
        {
            if (!fileLoggingEnabled || buffer.Count == 0) return;
            string fileName = $"{DateTime.Now:yyyyMMdd}_performance.log";
            string filePath = Path.Combine(GetLogDir(), fileName);
            RotatePerfLogIfNeeded(filePath);
            using (var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Seek(0, SeekOrigin.End);
                using (var sw = new StreamWriter(fs))
                {
                    foreach (Metric m in buffer.ToArray())
                    {
                        var topProcessesLog = string.Join(", ", m.TopProcesses.Select(p => $"{p.ProcessName}={p.MemoryUsageMB}MB"));
                        sw.WriteLine($"{m.Timestamp:yyyy-MM-dd HH:mm:ss.fff} C:{m.Cpu:F2} M:{m.Mem:F2} CT:{m.CpuTemp:F1} GT:{m.GpuTemp:F1} FAN:{m.FanRpm} | Top5: [{topProcessesLog}]");
                    }
                }
            }
            buffer.Clear();
        }

        private void RotatePerfLogIfNeeded(string filePath)
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length <= MAX_LOG_SIZE) return;
            string extension = fi.Extension;
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            string dir = fi.DirectoryName;
            int index = 1;
            string rotatedPath;
            do
            {
                string rotatedName = $"{baseName}_{index}{extension}";
                rotatedPath = Path.Combine(dir, rotatedName);
                index++;
            }
            while (File.Exists(rotatedPath));
            File.Move(filePath, rotatedPath);
        }

        private static string GetLogDir() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
    }

    internal readonly struct Metric
    {
        public DateTime Timestamp { get; }
        public float Cpu { get; }
        public float Mem { get; }
        public float CpuTemp { get; }
        public float GpuTemp { get; }
        public int FanRpm { get; }
        public List<ProcessMetric> TopProcesses { get; }

        public Metric(float cpu, float mem, float cpuTemp, float gpuTemp, int fanRpm, List<ProcessMetric> topProcesses)
        {
            Timestamp = DateTime.Now;
            Cpu = cpu;
            Mem = mem;
            CpuTemp = cpuTemp;
            GpuTemp = gpuTemp;
            FanRpm = fanRpm;
            TopProcesses = topProcesses;
        }
    }

    internal sealed class CircularBuffer<T>
    {
        private readonly T[] buf;
        private int head, count;
        public int Capacity { get; }
        public int Count => count;
        public CircularBuffer(int capacity) { Capacity = capacity; buf = new T[capacity]; }
        public void Push(T item)
        {
            buf[(head + count) % Capacity] = item;
            if (count == Capacity) head = (head + 1) % Capacity;
            else count++;
        }
        public IEnumerable<T> ToArray() => Enumerable.Range(0, count).Select(i => buf[(head + i) % Capacity]);
        public void Clear() => head = count = 0;
    }

    internal sealed class HardwareSampler
    {
        public event Action<Metric> OnSample;
        public event Action OnThresholdExceeded;
        public event Action OnBackToNormal;

        private readonly Computer computer;
        private Timer timer;
        private int interval;
        private bool overload;
        private readonly LogManager logManager;

        private int _consecutiveFailures = 0;
        private const int FAILURE_THRESHOLD = 3;
        private bool _isInitialized = false;

        private static bool _sensorInfoLogged = false;

        public int IntervalMs
        {
            get => interval;
            set { interval = Math.Max(500, value); timer?.Change(0, interval); }
        }

        public HardwareSampler(int intervalMs)
        {
            interval = intervalMs;
            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true
            };

            try
            {
                if (!IsRunningAsAdmin())
                {
                    logManager.LogError("[HardwareSampler] Not running with Administrator privileges. Hardware sensor data may be unavailable. Please run the agent as an administrator.");
                }
                computer.Open();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                logManager.LogError($"[HardwareSampler] CRITICAL: Failed to open LibreHardwareMonitor on initial load: {ex.Message}. Performance monitoring will be disabled.");
                _isInitialized = false;
            }
        }

        private bool IsRunningAsAdmin()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        public void Start()
        {
            if (_isInitialized)
            {
                // _sensorInfoLogged = false; // 필요 시 주석 해제
                timer = new Timer(_ => Sample(), null, 0, interval);
            }
            else
            {
                logManager.LogEvent("[HardwareSampler] Skipping Start() because initial hardware monitor load failed.");
            }
        }

        public void Stop()
        {
            timer?.Dispose();
            timer = null;
            try
            {
                if (_isInitialized)
                {
                    computer.Close();
                }
            }
            catch { /* Ignore */ }
        }


        private void Sample()
        {
            if (!_isInitialized) return;

            float cpuUsage = 0, memUsage = 0, cpuTemp = 0, gpuTemp = 0;
            int fanRpm = 0;

            try
            {
                // Debug 모드가 켜져 있고, 아직 센서 정보가 로그되지 않았을 때만 상세 정보 기록
                if (LogManager.GlobalDebugEnabled && !_sensorInfoLogged)
                {
                    StringBuilder sensorInfo = new StringBuilder();
                    sensorInfo.AppendLine("[HardwareSampler] Detected Sensors (Logged Once):");
                    foreach (var hardware in computer.Hardware)
                    {
                        hardware.Update();
                        sensorInfo.AppendLine($"  Hardware: {hardware.Name} ({hardware.HardwareType})");
                        foreach (var sensor in hardware.Sensors)
                        {
                            sensorInfo.AppendLine($"    Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                        }
                        foreach (var subHardware in hardware.SubHardware)
                        {
                            subHardware.Update();
                            sensorInfo.AppendLine($"    SubHardware: {subHardware.Name} ({subHardware.HardwareType})");
                            foreach (var sensor in subHardware.Sensors)
                            {
                                sensorInfo.AppendLine($"      Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                            }
                        }
                    }
                    logManager.LogDebug(sensorInfo.ToString());
                    _sensorInfoLogged = true;
                }
                else // 일반 샘플링 시 업데이트만 수행
                {
                    foreach (var hardware in computer.Hardware)
                    {
                        hardware.Update();
                        foreach (var subHardware in hardware.SubHardware)
                        {
                            subHardware.Update();
                        }
                    }
                }

                // --- 나머지 센서 값 읽기 로직 (이전과 동일) ---
                var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    cpuUsage = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
                    cpuTemp = cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Core (Tctl/Tdie)")))?.Value ??
                              cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                if (memory != null)
                {
                    var memoryLoad = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                    if (memoryLoad?.Value != null) memUsage = memoryLoad.Value.Value;
                    else
                    {
                        var used = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Used")?.Value;
                        var total = memory.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Data && s.Name == "Memory Total")?.Value;
                        if (used.HasValue && total.HasValue && total > 0) memUsage = (used.Value / total.Value) * 100;
                    }
                }

                var gpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia);
                if (gpu != null)
                {
                    gpuTemp = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("Temp")))?.Value ??
                              gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var allSensors = computer.Hardware.SelectMany(h => h.Sensors.Concat(h.SubHardware.SelectMany(sh => sh.Sensors)));
                var cpuFanSensor = allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && (s.Name.Contains("CPU") || s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("System Fan")));
                fanRpm = (int)(cpuFanSensor?.Value ?? allSensors.FirstOrDefault(s => s.SensorType == SensorType.Fan)?.Value ?? 0);

                // --- 실패 감지 및 자동 복구 로직 (이전과 동일) ---
                bool hasCpuError = cpuUsage == 0;
                bool hasMemError = memUsage == 0;
                bool hasTempError = cpuTemp == 0;

                if ((hasCpuError && hasMemError) || hasTempError)
                {
                    _consecutiveFailures++;
                    string failureDetails = $"Invalid sample detected (CPU Usage: {cpuUsage:F2}, Mem Usage: {memUsage:F2}, CPU Temp: {cpuTemp:F1}). Consecutive failures: {_consecutiveFailures}";
                    logManager.LogDebug($"[HardwareSampler] {failureDetails}");

                    if (_consecutiveFailures >= FAILURE_THRESHOLD)
                    {
                        logManager.LogEvent("[HardwareSampler] Consecutive sensor failures reached threshold. Attempting to re-initialize hardware monitor.");
                        try
                        {
                            computer.Close();
                            computer.Open();
                            _consecutiveFailures = 0;
                            _sensorInfoLogged = false; // 재초기화 성공 시 플래그 리셋
                            logManager.LogEvent("[HardwareSampler] Hardware monitor re-initialized successfully.");
                        }
                        catch (Exception ex)
                        {
                            logManager.LogError($"[HardwareSampler] CRITICAL: Failed to re-initialize hardware monitor: {ex.Message}. Stopping performance sampling.");
                            this.Stop();
                            _isInitialized = false; // 재초기화 실패 시 상태 변경
                        }
                    }
                    return;
                }
                _consecutiveFailures = 0;

                // --- Top 5 프로세스 정보 수집 (메모리/핸들 누수 방지 수정) ---
                var topProcesses = new List<ProcessMetric>();
                try
                {
                    // [수정] Process.GetProcesses() 반환 객체는 반드시 Dispose 해야 함
                    Process[] allProcesses = Process.GetProcesses();
                    try
                    {
                        topProcesses = allProcesses
                            .OrderByDescending(p => p.PrivateMemorySize64)
                            .Take(5)
                            .Select(p => {
                                long privateMemoryMB = p.PrivateMemorySize64 / (1024 * 1024);
                                long workingSetMB = p.WorkingSet64 / (1024 * 1024);
                                long sharedMemoryMB = workingSetMB > privateMemoryMB ? workingSetMB - privateMemoryMB : 0;
                                return new ProcessMetric { ProcessName = p.ProcessName, MemoryUsageMB = privateMemoryMB, SharedMemoryUsageMB = sharedMemoryMB };
                            })
                            .ToList();
                    }
                    finally
                    {
                        // [수정] 사용한 모든 프로세스 객체 명시적 해제
                        foreach (var p in allProcesses)
                        {
                            try { p.Dispose(); } catch { }
                        }
                    }
                }
                catch (Exception procEx)
                {
                    logManager.LogDebug($"[HardwareSampler] Failed to get process list: {procEx.Message}");
                }

                // --- 이벤트 발생 (이전과 동일) ---
                OnSample?.Invoke(new Metric(cpuUsage, memUsage, cpuTemp, gpuTemp, fanRpm, topProcesses));

                bool isOver = (cpuUsage > 75f) || (memUsage > 80f);
                if (isOver && !overload) { overload = true; OnThresholdExceeded?.Invoke(); }
                else if (!isOver && overload) { overload = false; OnBackToNormal?.Invoke(); }
            }
            catch (NullReferenceException nre) when (!_isInitialized)
            {
                logManager.LogError($"[HardwareSampler] Attempted to sample but hardware monitor is not initialized. {nre.Message}");
                this.Stop();
            }
            catch (Exception ex)
            {
                if (_isInitialized)
                {
                    logManager.LogError($"[HardwareSampler] Failed to sample hardware info: {ex.Message}");
                    if (LogManager.GlobalDebugEnabled) logManager.LogDebug($"[HardwareSampler] Sampling Exception details: {ex.StackTrace}");
                }
            }
        } // Sample() 메서드 끝

    } // HardwareSampler 클래스 끝

} // 네임스페이스 끝
