// ITM_Agent/Services/FileWatcherManager.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ITM_Agent.Services
{
    public class FileWatcherManager
    {
        private SettingsManager settingsManager;
        private LogManager logManager;
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, DateTime> fileProcessTracker = new Dictionary<string, DateTime>();
        private readonly TimeSpan duplicateEventThreshold = TimeSpan.FromSeconds(5);

        private bool isRunning = false;
        private bool isPaused = false; 

        // [핵심 개선] 제외 폴더 캐싱 (성능 및 정확성 향상)
        private readonly List<string> _cachedExcludeFolders = new List<string>();

        private readonly Dictionary<string, FileTrackingInfo> trackedFiles = new Dictionary<string, FileTrackingInfo>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer stabilityCheckTimer;
        private readonly object trackingLock = new object();
        private const int StabilityCheckIntervalMs = 1000;
        private const double FileStableThresholdSeconds = 5.0;

        private class FileTrackingInfo
        {
            public DateTime LastEventTime { get; set; }
            public long LastSize { get; set; }
            public DateTime LastWriteTime { get; set; }
            public WatcherChangeTypes LastChangeType { get; set; }
        }

        private readonly object recoveryLock = new object();

        public FileWatcherManager(SettingsManager settingsManager, LogManager logManager, bool isDebugMode)
        {
            this.settingsManager = settingsManager;
            this.logManager = logManager;
            LogManager.GlobalDebugEnabled = isDebugMode;
        }

        public void UpdateDebugMode(bool isDebug)
        {
            LogManager.GlobalDebugEnabled = isDebug;
            logManager.LogEvent($"[FileWatcherManager] Debug mode updated to: {isDebug}");
        }

        public void PauseWatching()
        {
            if (!isRunning) return;
            isPaused = true;
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = false; } catch { }
            }
            logManager.LogEvent("[FileWatcherManager] Paused watching (Server Holding).");
        }

        public void ResumeWatching()
        {
            if (!isRunning) return;
            isPaused = false;
            foreach (var w in watchers)
            {
                try { w.EnableRaisingEvents = true; } catch { }
            }
            logManager.LogEvent("[FileWatcherManager] Resumed watching.");
        }

        public void InitializeWatchers()
        {
            StopWatchers();

            // [핵심 개선] 감시 시작 시 제외 폴더(ExcludeFolders) 목록을 캐싱 및 정규화
            _cachedExcludeFolders.Clear();
            var excludes = settingsManager.GetFoldersFromSection("[ExcludeFolders]");
            foreach (var ex in excludes)
            {
                try
                {
                    // 디렉토리 구분자를 확실히 추가하여 "C:\Target\Exclude"와 "C:\Target\ExcludeABC"가 오작동하지 않도록 방지
                    string norm = Path.GetFullPath(ex).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    _cachedExcludeFolders.Add(norm);
                }
                catch { }
            }

            var targetFolders = settingsManager.GetFoldersFromSection("[TargetFolders]");
            if (targetFolders.Count == 0)
            {
                logManager.LogEvent("[FileWatcherManager] No target folders configured for monitoring.");
                return;
            }

            foreach (var folder in targetFolders)
            {
                if (!Directory.Exists(folder))
                {
                    logManager.LogEvent($"[FileWatcherManager] Folder does not exist: {folder}", LogManager.GlobalDebugEnabled);
                    continue;
                }

                try
                {
                    var watcher = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 131072 
                    };

                    watcher.Created += OnFileChanged;
                    watcher.Changed += OnFileChanged;
                    watcher.Error += OnWatcherError;

                    watchers.Add(watcher);

                    if (LogManager.GlobalDebugEnabled)
                        logManager.LogDebug($"[FileWatcherManager] Initialized watcher for folder: {folder}");
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[FileWatcherManager] Failed to create watcher for {folder}. Error: {ex.Message}");
                }
            }
            logManager.LogEvent($"[FileWatcherManager] {watchers.Count} watcher(s) initialized. Exclude folders count: {_cachedExcludeFolders.Count}");
        }

        public void StartWatching()
        {
            if (isRunning) return;

            InitializeWatchers();
            if (watchers.Count == 0) return;

            foreach (var watcher in watchers)
            {
                try { watcher.EnableRaisingEvents = true; } catch { }
            }

            isRunning = true;
            isPaused = false;
            logManager.LogEvent("[FileWatcherManager] File monitoring started.");
        }

        public void StopWatchers()
        {
            foreach (var w in watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Created -= OnFileChanged;
                    w.Changed -= OnFileChanged;
                    w.Error -= OnWatcherError;
                    w.Dispose();
                }
                catch { }
            }
            watchers.Clear();

            lock (trackingLock)
            {
                stabilityCheckTimer?.Dispose();
                stabilityCheckTimer = null;
                trackedFiles.Clear();
            }

            isRunning = false;
            isPaused = false;
            logManager.LogEvent("[FileWatcherManager] File monitoring stopped.");
        }

        // [추가] 파일이 제외 폴더 하위에 있는지 검사하는 최적화 메서드
        private bool IsExcluded(string filePath)
        {
            if (_cachedExcludeFolders.Count == 0) return false;
            try
            {
                string currentFileDir = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(currentFileDir)) return false;

                string normalizedCurrentDir = Path.GetFullPath(currentFileDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                foreach (var exclude in _cachedExcludeFolders)
                {
                    // 디렉토리 경계가 정확히 일치하는 상위/하위 관계만 필터링
                    if (normalizedCurrentDir.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!isRunning || isPaused) return; 

            // [핵심 개선] 매 이벤트마다 INI를 읽지 않고 캐시된 리스트를 사용하여 경로 엄격 비교
            if (IsExcluded(e.FullPath)) return;

            if (IsDuplicateEvent(e.FullPath)) return;

            try
            {
                lock (trackingLock)
                {
                    DateTime now = DateTime.UtcNow;
                    long currentSize = GetFileSizeSafe(e.FullPath);
                    DateTime currentWriteTime = GetLastWriteTimeSafe(e.FullPath);

                    if (currentSize == 0 && e.ChangeType == WatcherChangeTypes.Changed) return;

                    if (!trackedFiles.TryGetValue(e.FullPath, out FileTrackingInfo info))
                    {
                        info = new FileTrackingInfo();
                        trackedFiles[e.FullPath] = info;
                    }

                    info.LastEventTime = now;
                    info.LastSize = currentSize;
                    info.LastWriteTime = currentWriteTime;
                    info.LastChangeType = e.ChangeType;

                    if (stabilityCheckTimer == null)
                    {
                        stabilityCheckTimer = new Timer(CheckFileStability, null, StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                    }
                    else
                    {
                        stabilityCheckTimer.Change(StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[FileWatcherManager] OnFileChanged Error: {ex.Message}");
            }
        }

        private void CheckFileStability(object state)
        {
            if (isPaused) return;

            try
            {
                DateTime now = DateTime.UtcNow;
                var stableFilesToProcess = new List<string>();

                lock (trackingLock)
                {
                    if (!isRunning || trackedFiles.Count == 0)
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }

                    var currentTrackedFiles = trackedFiles.ToList();

                    foreach (var kvp in currentTrackedFiles)
                    {
                        string filePath = kvp.Key;
                        FileTrackingInfo info = kvp.Value;

                        long currentSize = GetFileSizeSafe(filePath);
                        DateTime currentWriteTime = GetLastWriteTimeSafe(filePath);

                        if (currentSize == -1 || currentWriteTime == DateTime.MinValue)
                        {
                            trackedFiles.Remove(filePath);
                            continue;
                        }

                        if (currentSize != info.LastSize || currentWriteTime != info.LastWriteTime)
                        {
                            info.LastEventTime = now;
                            info.LastSize = currentSize;
                            info.LastWriteTime = currentWriteTime;
                            continue;
                        }

                        double elapsedSeconds = (now - info.LastEventTime).TotalSeconds;

                        if (elapsedSeconds >= FileStableThresholdSeconds)
                        {
                            if (IsFileReady(filePath))
                            {
                                stableFilesToProcess.Add(filePath);
                                trackedFiles.Remove(filePath);
                            }
                        }
                    }

                    if (trackedFiles.Count == 0)
                    {
                        stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    else
                    {
                        stabilityCheckTimer?.Change(StabilityCheckIntervalMs, StabilityCheckIntervalMs);
                    }
                }

                foreach (string stableFilePath in stableFilesToProcess)
                {
                    try { ProcessFile(stableFilePath); }
                    catch (Exception ex)
                    {
                        logManager.LogError($"[FileWatcherManager] Error processing stable file {stableFilePath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                logManager.LogError($"[FileWatcherManager] CheckFileStability Error: {ex.Message}");
                try { stabilityCheckTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
            }
        }

        public void StartRecoveryScan()
        {
            if (!Monitor.TryEnter(recoveryLock)) return; 

            Task.Run(() =>
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Lowest;

                    logManager.LogEvent("[Recovery] Starting Slow Recovery Scan...");

                    var targetFolders = settingsManager.GetFoldersFromSection("[TargetFolders]");
                    int totalProcessed = 0;

                    foreach (var folder in targetFolders)
                    {
                        if (!Directory.Exists(folder) || isPaused) continue;

                        foreach (string filePath in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                        {
                            if (isPaused) break; 

                            // [핵심 개선] 통합된 캐시 기반 제외 폴더 검사 사용
                            if (IsExcluded(filePath)) continue;

                            if (IsDuplicateEvent(filePath)) continue;

                            if (IsFileReady(filePath))
                            {
                                try
                                {
                                    string result = ProcessFile(filePath);
                                    if (result != null)
                                    {
                                        totalProcessed++;
                                        Thread.Sleep(200);

                                        lock (fileProcessTracker)
                                        {
                                            fileProcessTracker[filePath] = DateTime.UtcNow;
                                        }
                                    }
                                    else
                                    {
                                        Thread.Sleep(10);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logManager.LogError($"[Recovery] Error processing file '{filePath}': {ex.Message}");
                                }
                            }
                        }
                    }
                    logManager.LogEvent($"[Recovery] Scan completed. Processed: {totalProcessed} files.");
                }
                catch (Exception ex)
                {
                    logManager.LogError($"[Recovery] Scan failed: {ex.Message}");
                }
                finally
                {
                    Monitor.Exit(recoveryLock);
                }
            });
        }

        private string ProcessFile(string filePath)
        {
            string fileName;
            try { fileName = Path.GetFileName(filePath); if (string.IsNullOrEmpty(fileName)) return null; } catch { return null; }

            var regexList = settingsManager.GetRegexList();

            foreach (var kvp in regexList)
            {
                try
                {
                    if (Regex.IsMatch(fileName, kvp.Key))
                    {
                        string destinationFolder = kvp.Value;
                        string destinationFile = Path.Combine(destinationFolder, fileName);
                        try
                        {
                            Directory.CreateDirectory(destinationFolder);

                            if (!CopyFileWithSharedRead(filePath, destinationFile, true))
                            {
                                return null;
                            }

                            logManager.LogEvent($"[FileWatcherManager] File Copied: {fileName} -> {destinationFolder}");
                            return destinationFolder;
                        }
                        catch (Exception ex) { logManager.LogError($"[FileWatcherManager] Error copying file {fileName}: {ex.Message}"); }
                        return null;
                    }
                }
                catch { }
            }
            return null;
        }

        private bool CopyFileWithSharedRead(string sourcePath, string destPath, bool overwrite)
        {
            int maxRetries = 5;
            int delayMs = 300;

            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        using (var destStream = new FileStream(destPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                        {
                            sourceStream.CopyTo(destStream);
                        }
                    }
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (IOException) when (i < maxRetries - 1)
                {
                    Thread.Sleep(delayMs);
                }
                catch
                {
                    return false;
                }
            }
            logManager.LogError($"[FileWatcherManager] Copy failed after retries: {sourcePath}");
            return false;
        }

        private bool IsDuplicateEvent(string filePath)
        {
            DateTime now = DateTime.UtcNow;
            lock (fileProcessTracker)
            {
                if (fileProcessTracker.Count > 1000)
                {
                    var keysToRemove = fileProcessTracker
                        .Where(kvp => (now - kvp.Value).TotalMinutes > 5)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    foreach (var key in keysToRemove) fileProcessTracker.Remove(key);
                }
                if (fileProcessTracker.TryGetValue(filePath, out var lastProcessed))
                {
                    if ((now - lastProcessed) < duplicateEventThreshold) return true;
                }
                fileProcessTracker[filePath] = now;
                return false;
            }
        }

        private bool IsFileReady(string filePath)
        {
            if (!File.Exists(filePath)) return false;
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return true;
                }
            }
            catch { return false; }
        }

        private long GetFileSizeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? new FileInfo(filePath).Length : -1; }
            catch { return -1; }
        }

        private DateTime GetLastWriteTimeSafe(string filePath)
        {
            try { return File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            string errorMessage = ex?.Message ?? "Unknown watcher error";
            logManager.LogError($"[FileWatcherManager] Watcher error: {errorMessage}");

            if (ex is InternalBufferOverflowException)
            {
                FileSystemWatcher watcher = sender as FileSystemWatcher;
                if (watcher != null)
                {
                    logManager.LogEvent($"[FileWatcherManager] Buffer overflow on '{watcher.Path}'. Scheduling recovery scan.");
                    StartRecoveryScan();
                }
            }
        }
    }
}
