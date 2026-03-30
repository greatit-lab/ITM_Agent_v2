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
using System.Runtime.InteropServices; // OS 메모리 Trim API 사용

namespace ITM_Agent.Services
{
    public sealed class ProcessMetric
    {
        public string ProcessName { get; set; }
        public long MemoryUsageMB { get; set; }       // Private Working Set (작업 관리자 수치)
        public long SharedMemoryUsageMB { get; set; } // 공유 메모리 사용량
        public long CommitMemoryMB { get; set; }      // (신규 추가) Commit Size
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

        // OS 레벨 워킹셋(Working Set) 메모리 반환 API
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, UIntPtr minimumWorkingSetSize, UIntPtr maximumWorkingSetSize);

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

            // 백그라운드 데이터 Flush 완료 후, OS에 잉여 메모리(Working Set) 강제 반환하여 메모리 점유율 최적화
            TrimMemory();
        }

        // 주기적인 메모리 정리 헬퍼 메서드
        private static void TrimMemory()
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (UIntPtr)uint.MaxValue, (UIntPtr)uint.MaxValue);
                }
            }
            catch { /* 권한 부족 등 무시 */ }
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
        
        // 성능 카운터가 고장난 OS인지 체크하는 플래그 (반복 예외 방지)
        private static bool _isPerfCounterBroken = false;

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
                try
                {
                    computer.Open();
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[HardwareSampler] Failed to reopen computer sensors: {ex.Message}");
                }

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
            if (computer == null || computer.Hardware == null) return; // 방어 코드 추가

            float cpuUsage = 0, memUsage = 0, cpuTemp = 0, gpuTemp = 0;
            int fanRpm = 0;

            try
            {
                if (LogManager.GlobalDebugEnabled && !_sensorInfoLogged)
                {
                    StringBuilder sensorInfo = new StringBuilder();
                    sensorInfo.AppendLine("[HardwareSampler] Detected Sensors (Logged Once):");
                    foreach (var hardware in computer.Hardware)
                    {
                        if (hardware == null) continue; // 방어 코드
                        hardware.Update();
                        sensorInfo.AppendLine($"  Hardware: {hardware.Name} ({hardware.HardwareType})");

                        if (hardware.Sensors != null)
                        {
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor != null)
                                    sensorInfo.AppendLine($"    Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                            }
                        }

                        if (hardware.SubHardware != null)
                        {
                            foreach (var subHardware in hardware.SubHardware)
                            {
                                if (subHardware == null) continue;
                                subHardware.Update();
                                sensorInfo.AppendLine($"    SubHardware: {subHardware.Name} ({subHardware.HardwareType})");

                                if (subHardware.Sensors != null)
                                {
                                    foreach (var sensor in subHardware.Sensors)
                                    {
                                        if (sensor != null)
                                            sensorInfo.AppendLine($"      Sensor: {sensor.Name} ({sensor.SensorType}) - Value: {sensor.Value}");
                                    }
                                }
                            }
                        }
                    }
                    logManager.LogDebug(sensorInfo.ToString());
                    _sensorInfoLogged = true;
                }
                else
                {
                    foreach (var hardware in computer.Hardware)
                    {
                        if (hardware == null) continue;
                        hardware.Update();

                        if (hardware.SubHardware != null)
                        {
                            foreach (var subHardware in hardware.SubHardware)
                            {
                                subHardware?.Update();
                            }
                        }
                    }
                }

                // --- 센서 값 읽기 ---
                var cpu = computer.Hardware.FirstOrDefault(h => h != null && h.HardwareType == HardwareType.Cpu);
                if (cpu != null && cpu.Sensors != null)
                {
                    cpuUsage = cpu.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Load && s.Name == "CPU Total")?.Value ?? 0;
                    cpuTemp = cpu.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Core (Tctl/Tdie)")))?.Value ??
                              cpu.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var memory = computer.Hardware.FirstOrDefault(h => h != null && h.HardwareType == HardwareType.Memory);
                if (memory != null && memory.Sensors != null)
                {
                    var memoryLoad = memory.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Load);
                    if (memoryLoad?.Value != null) memUsage = memoryLoad.Value.Value;
                    else
                    {
                        var used = memory.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Data && s.Name == "Memory Used")?.Value;
                        var total = memory.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Data && s.Name == "Memory Total")?.Value;
                        if (used.HasValue && total.HasValue && total > 0) memUsage = (used.Value / total.Value) * 100;
                    }
                }

                var gpu = computer.Hardware.FirstOrDefault(h => h != null && (h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuNvidia));
                if (gpu != null && gpu.Sensors != null)
                {
                    gpuTemp = gpu.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Temperature && (s.Name.Contains("Core") || s.Name.Contains("Temp")))?.Value ??
                              gpu.Sensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Temperature)?.Value ?? 0;
                }

                var allSensors = computer.Hardware
                    .Where(h => h != null)
                    .SelectMany(h =>
                    {
                        var hSensors = h.Sensors ?? Enumerable.Empty<ISensor>();
                        var shSensors = h.SubHardware != null
                            ? h.SubHardware.Where(sh => sh != null && sh.Sensors != null).SelectMany(sh => sh.Sensors)
                            : Enumerable.Empty<ISensor>();
                        return hSensors.Concat(shSensors);
                    });

                var cpuFanSensor = allSensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Fan && (s.Name.Contains("CPU") || s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("System Fan")));
                fanRpm = (int)(cpuFanSensor?.Value ?? allSensors.FirstOrDefault(s => s != null && s.SensorType == SensorType.Fan)?.Value ?? 0);

                // --- 실패 감지 및 자동 복구 로직 ---
                bool hasCpuError = cpuUsage == 0;
                bool hasMemError = memUsage == 0;

                if (hasCpuError && hasMemError)
                {
                    _consecutiveFailures++;
                    string failureDetails = $"Invalid sample detected (CPU: {cpuUsage:F2}, Mem: {memUsage:F2}). Failures: {_consecutiveFailures}";

                    if (LogManager.GlobalDebugEnabled)
                        logManager.LogDebug($"[HardwareSampler] {failureDetails}");

                    if (_consecutiveFailures >= FAILURE_THRESHOLD)
                    {
                        logManager.LogEvent("[HardwareSampler] Consecutive sensor failures reached threshold. Re-initializing...");
                        try
                        {
                            computer.Close();
                            computer.Open();
                            _consecutiveFailures = 0;
                            _sensorInfoLogged = false;
                            logManager.LogEvent("[HardwareSampler] Hardware monitor re-initialized.");
                        }
                        catch (Exception ex)
                        {
                            logManager.LogError($"[HardwareSampler] Re-init failed: {ex.Message}. Stopping.");
                            this.Stop();
                            _isInitialized = false;
                        }
                    }
                    return;
                }
                _consecutiveFailures = 0;

                // --- Top 5 프로세스 정보 수집 ---
                var topProcesses = new List<ProcessMetric>(5);
                try
                {
                    Process[] allProcesses = Process.GetProcesses();
                    try
                    {
                        var procInfos = new List<(string Name, long PrivateMem, long WorkingSet)>(allProcesses.Length);

                        foreach (var p in allProcesses)
                        {
                            try
                            {
                                procInfos.Add((p.ProcessName, p.PrivateMemorySize64, p.WorkingSet64));
                            }
                            catch { /* 권한이 없는 시스템 프로세스 무시 */ }
                        }

                        // 메모리 사용량 기준 내림차순 정렬
                        procInfos.Sort((a, b) => b.PrivateMem.CompareTo(a.PrivateMem));

                        int takeCount = Math.Min(5, procInfos.Count);
                        for (int i = 0; i < takeCount; i++)
                        {
                            long commitMB = procInfos[i].PrivateMem / (1024 * 1024);
                            long workingMB = procInfos[i].WorkingSet / (1024 * 1024);
                            long sharedMB = workingMB > commitMB ? workingMB - commitMB : 0;

                            long privateWorkingSetMB = workingMB; // 기본값 할당

                            // OS 성능 카운터가 정상이면 시도, 고장이면 기본값(workingMB) 유지
                            if (!_isPerfCounterBroken)
                            {
                                try
                                {
                                    using (PerformanceCounter pc = new PerformanceCounter("Process", "Working Set - Private", procInfos[i].Name, true))
                                    {
                                        privateWorkingSetMB = pc.RawValue / (1024 * 1024);
                                    }
                                }
                                catch
                                {
                                    _isPerfCounterBroken = true; // 최초 실패 시 이후부터는 시도 자체를 건너뜀
                                    logManager.LogDebug("[HardwareSampler] OS Performance Counter is broken. Falling back to default WorkingSet memory.");
                                }
                            }

                            topProcesses.Add(new ProcessMetric
                            {
                                ProcessName = procInfos[i].Name,
                                MemoryUsageMB = privateWorkingSetMB,    
                                SharedMemoryUsageMB = sharedMB,
                                CommitMemoryMB = commitMB               
                            });
                        }
                    }
                    finally
                    {
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

                // --- 이벤트 발생 ---
                OnSample?.Invoke(new Metric(cpuUsage, memUsage, cpuTemp, gpuTemp, fanRpm, topProcesses));

                bool isOver = (cpuUsage > 75f) || (memUsage > 80f);
                if (isOver && !overload) { overload = true; OnThresholdExceeded?.Invoke(); }
                else if (!isOver && overload) { overload = false; OnBackToNormal?.Invoke(); }
            }
            catch (NullReferenceException nre) when (!_isInitialized)
            {
                logManager.LogError($"[HardwareSampler] Not initialized. {nre.Message}");
                this.Stop();
            }
            catch (Exception ex)
            {
                if (_isInitialized)
                {
                    logManager.LogError($"[HardwareSampler] Failed to sample: {ex.Message}");
                }
            }
        }
    }
}
