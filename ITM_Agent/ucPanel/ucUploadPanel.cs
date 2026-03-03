// ITM_Agent/ucPanel/ucUploadPanel.cs
using ITM_Agent.Plugins;
using ITM_Agent.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ITM_Agent.Properties;
using System.Diagnostics;

namespace ITM_Agent.ucPanel
{
    public partial class ucUploadPanel : UserControl
    {
        private readonly List<FileSystemWatcher> _tab1Watchers = new List<FileSystemWatcher>();
        private readonly List<FileSystemWatcher> _tab2Watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentDictionary<string, string> _tab1RuleMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _tab2RuleMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _lockTab1 = new object();
        private readonly object _lockTab2 = new object();

        private readonly ConcurrentDictionary<string, DateTime> _lastProcessEventTime =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const double DebounceSeconds = 2.0;
        private readonly object _cleanupLock = new object();

        private readonly ucConfigurationPanel _configPanel;
        private readonly ucPluginPanel _pluginPanel;
        private readonly SettingsManager _settingsManager;
        private readonly LogManager _logManager;
        private readonly ucOverrideNamesPanel _overridePanel;
        private readonly ucImageTransPanel _imageTransPanel;

        private readonly Dictionary<string, (string Task, string Filter, bool RequiresOverride)> _pluginMetadataCache =
            new Dictionary<string, (string Task, string Filter, bool RequiresOverride)>(StringComparer.OrdinalIgnoreCase);

        // 플러그인 런타임 캐시 추가 (메모리 누수 방지용)
        private readonly ConcurrentDictionary<string, PluginRuntimeInfo> _pluginRuntimeCache =
            new ConcurrentDictionary<string, PluginRuntimeInfo>(StringComparer.OrdinalIgnoreCase);

        private class PluginRuntimeInfo
        {
            public Type ClassType { get; set; }
            public MethodInfo Method { get; set; }
            public bool RequiresThreeArgs { get; set; }
        }

        private const string Tab1Section = "[UploadRulesTab1]";
        private const string Tab2Section = "[UploadRulesTab2]";

        private bool isRunning = false;
        private bool isPaused = false;

        public ucUploadPanel(ucConfigurationPanel configPanel, ucPluginPanel pluginPanel, SettingsManager settingsManager,
            ucOverrideNamesPanel ovPanel, ucImageTransPanel imageTransPanel)
        {
            InitializeComponent();

            _configPanel = configPanel ?? throw new ArgumentNullException(nameof(configPanel));
            _pluginPanel = pluginPanel ?? throw new ArgumentNullException(nameof(pluginPanel));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _overridePanel = ovPanel;
            _imageTransPanel = imageTransPanel ?? throw new ArgumentNullException(nameof(imageTransPanel));

            _logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);

            if (_overridePanel != null)
            {
                _overridePanel.FileRenamed += OnOverrideFileRenamed;
            }

            this.Load += UcUploadPanel_Load;
            _pluginPanel.PluginsChanged += OnPluginsChanged;

            btnCatAdd.Click += BtnCatAdd_Click;
            btnCatRemove.Click += BtnCatRemove_Click;
            btnCatSave.Click += BtnCatSave_Click;

            btnLiveAdd.Click += BtnLiveAdd_Click;
            btnLiveRemove.Click += BtnLiveRemove_Click;
            btnLiveSave.Click += BtnLiveSave_Click;

            InitializeDataGridViews();
            LoadSettings();

            dgvCategorized.CellValueChanged += Dgv_CellValueChanged;
            dgvLiveMonitoring.CellValueChanged += Dgv_CellValueChanged;
            dgvCategorized.CellFormatting += Dgv_CellFormatting;
            dgvLiveMonitoring.CellFormatting += Dgv_CellFormatting;
            dgvCategorized.DataError += Dgv_DataError;
            dgvLiveMonitoring.DataError += Dgv_DataError;
            dgvLiveMonitoring.CellClick += DgvLiveMonitoring_CellClick;
            dgvCategorized.CurrentCellDirtyStateChanged += Dgv_CurrentCellDirtyStateChanged;
            dgvLiveMonitoring.CurrentCellDirtyStateChanged += Dgv_CurrentCellDirtyStateChanged;

            RefreshPluginMetadataCache();
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            }
            catch { return path.ToUpperInvariant(); }
        }

        private void OnOverrideFileRenamed(string newFilePath)
        {
            if (isPaused) return;

            Task.Run(() =>
            {
                try
                {
                    if (_settingsManager.IsDebugMode)
                        _logManager.LogDebug($"[ucUploadPanel] Direct Signal Received from OverridePanel: {newFilePath}");

                    string dir = Path.GetDirectoryName(newFilePath);
                    string name = Path.GetFileName(newFilePath);
                    var args = new FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, name);

                    OnTab1FileEvent(this, args);
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[ucUploadPanel] Direct Signal Processing Failed: {ex.Message}");
                }
            });
        }

        #region --- UI 초기화 및 설정 로드 ---

        private void InitializeDataGridViews()
        {
            dgvCategorized.Columns.Clear();
            dgvCategorized.AutoGenerateColumns = false;
            dgvCategorized.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvCategorized.Columns.Add(new DataGridViewTextBoxColumn { Name = "TaskName", HeaderText = Properties.Resources.UPLOAD_COL_TASKNAME, DataPropertyName = "TaskName", FillWeight = 15 });
            dgvCategorized.Columns.Add(new DataGridViewComboBoxColumn { Name = "WatchFolder", HeaderText = Properties.Resources.UPLOAD_COL_CAT_FOLDER, DataPropertyName = "WatchFolder", FillWeight = 58 });
            dgvCategorized.Columns.Add(new DataGridViewComboBoxColumn { Name = "PluginName", HeaderText = Properties.Resources.UPLOAD_COL_PLUGIN, DataPropertyName = "PluginName", FillWeight = 27 });

            dgvLiveMonitoring.Columns.Clear();
            dgvLiveMonitoring.AutoGenerateColumns = false;
            dgvLiveMonitoring.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn { Name = "TaskName", HeaderText = Properties.Resources.UPLOAD_COL_TASKNAME, DataPropertyName = "TaskName", FillWeight = 16 });
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn { Name = "WatchFolder", HeaderText = Properties.Resources.UPLOAD_COL_LIVE_FOLDER, DataPropertyName = "WatchFolder", FillWeight = 35 });
            dgvLiveMonitoring.Columns.Add(new DataGridViewButtonColumn { Name = "btnSelectFolder", HeaderText = Properties.Resources.UPLOAD_COL_SELECT, Text = "...", UseColumnTextForButtonValue = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, FillWeight = 5, Width = 40 });
            dgvLiveMonitoring.Columns.Add(new DataGridViewTextBoxColumn { Name = "FileFilter", HeaderText = Properties.Resources.UPLOAD_COL_FILTER, DataPropertyName = "FileFilter", FillWeight = 20 });
            dgvLiveMonitoring.Columns.Add(new DataGridViewComboBoxColumn { Name = "PluginName", HeaderText = Properties.Resources.UPLOAD_COL_PLUGIN, DataPropertyName = "PluginName", FillWeight = 28 });
        }

        private void InitializeComboBoxColumns()
        {
            var pluginNames = _pluginPanel.GetLoadedPlugins().Select(p => p.PluginName).ToArray();

            var dgvCatPluginCol = dgvCategorized.Columns["PluginName"] as DataGridViewComboBoxColumn;
            if (dgvCatPluginCol != null) { dgvCatPluginCol.Items.Clear(); if (pluginNames.Length > 0) dgvCatPluginCol.Items.AddRange(pluginNames); }

            var regexFolders = _configPanel.GetRegexList().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string imageSaveFolder = _imageTransPanel.GetImageSaveFolder();
            if (!string.IsNullOrEmpty(imageSaveFolder) && !regexFolders.Contains(imageSaveFolder, StringComparer.OrdinalIgnoreCase))
            {
                regexFolders.Add(imageSaveFolder);
            }

            var dgvCatFolderCol = dgvCategorized.Columns["WatchFolder"] as DataGridViewComboBoxColumn;
            if (dgvCatFolderCol != null) { dgvCatFolderCol.Items.Clear(); if (regexFolders.Count > 0) dgvCatFolderCol.Items.AddRange(regexFolders.ToArray()); }

            var dgvLivePluginCol = dgvLiveMonitoring.Columns["PluginName"] as DataGridViewComboBoxColumn;
            if (dgvLivePluginCol != null) { dgvLivePluginCol.Items.Clear(); if (pluginNames.Length > 0) dgvLivePluginCol.Items.AddRange(pluginNames); }
        }

        private void UcUploadPanel_Load(object sender, EventArgs e)
        {
            tpCategorized.Text = Properties.Resources.UPLOAD_TAB1_HEADER;
            tpLiveMonitoring.Text = Properties.Resources.UPLOAD_TAB2_HEADER;
            InitializeComboBoxColumns();
        }

        private void LoadSettings()
        {
            dgvCategorized.Rows.Clear();
            foreach (string line in _settingsManager.GetFoldersFromSection(Tab1Section))
            {
                string[] parts = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 3)
                {
                    int idx = dgvCategorized.Rows.Add();
                    dgvCategorized.Rows[idx].Cells["TaskName"].Value = parts[0];
                    dgvCategorized.Rows[idx].Cells["WatchFolder"].Value = parts[1];
                    dgvCategorized.Rows[idx].Cells["PluginName"].Value = parts[2];
                }
            }
            dgvLiveMonitoring.Rows.Clear();
            foreach (string line in _settingsManager.GetFoldersFromSection(Tab2Section))
            {
                string[] parts = line.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 4)
                {
                    int idx = dgvLiveMonitoring.Rows.Add();
                    dgvLiveMonitoring.Rows[idx].Cells["TaskName"].Value = parts[0];
                    dgvLiveMonitoring.Rows[idx].Cells["WatchFolder"].Value = parts[1];
                    dgvLiveMonitoring.Rows[idx].Cells["FileFilter"].Value = parts[2];
                    dgvLiveMonitoring.Rows[idx].Cells["PluginName"].Value = parts[3];
                }
            }
        }

        private bool ValidateRules(DataGridView dgv, string tabName, out string errorMessage)
        {
            errorMessage = string.Empty;
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;
                string taskName = row.Cells["TaskName"].Value?.ToString();
                string pluginName = row.Cells["PluginName"].Value?.ToString();
                if (!string.IsNullOrEmpty(taskName) && taskName != "New Task")
                {
                    if (string.IsNullOrEmpty(pluginName) || pluginName == "(플러그인 선택)")
                    {
                        errorMessage = string.Format(Properties.Resources.MSG_RUN_PLUGIN_REQUIRED, tabName, taskName);
                        return false;
                    }
                }
            }
            return true;
        }

        private void PerformSave(string section, DataGridView dgv)
        {
            var lines = new List<string>();
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;
                string taskName = row.Cells["TaskName"].Value?.ToString();
                string pluginName = row.Cells["PluginName"].Value?.ToString();
                bool isValid = !string.IsNullOrEmpty(taskName) && taskName != "New Task" && !string.IsNullOrEmpty(pluginName) && pluginName != "(플러그인 선택)";

                if (dgv == dgvCategorized && isValid)
                {
                    string watchFolder = row.Cells["WatchFolder"].Value?.ToString();
                    if (!string.IsNullOrEmpty(watchFolder) && watchFolder != "(폴더 선택)")
                        lines.Add(string.Join("||", taskName, watchFolder, pluginName));
                }
                else if (dgv == dgvLiveMonitoring && isValid)
                {
                    string watchFolder = row.Cells["WatchFolder"].Value?.ToString();
                    string fileFilter = row.Cells["FileFilter"].Value?.ToString();
                    if (!string.IsNullOrEmpty(watchFolder) && !string.IsNullOrEmpty(fileFilter))
                        lines.Add(string.Join("||", taskName, watchFolder, fileFilter, pluginName));
                }
            }
            _settingsManager.SetFoldersToSection(section, lines);
            _logManager.LogEvent($"[ucUploadPanel] Settings saved for section: {section}");
        }

        private void BtnCatAdd_Click(object sender, EventArgs e) { int idx = dgvCategorized.Rows.Add(); dgvCategorized.Rows[idx].Cells["TaskName"].Value = "New Task"; }
        private void BtnCatRemove_Click(object sender, EventArgs e) { foreach (DataGridViewRow row in dgvCategorized.SelectedRows) if (!row.IsNewRow) dgvCategorized.Rows.Remove(row); }
        private void BtnCatSave_Click(object sender, EventArgs e)
        {
            if (!ValidateRules(dgvCategorized, Properties.Resources.UPLOAD_TAB1_HEADER, out string err)) { MessageBox.Show(err, Properties.Resources.CAPTION_ERROR, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            PerformSave(Tab1Section, dgvCategorized);
            MessageBox.Show(Properties.Resources.MSG_SAVE_CAT_SUCCESS, Properties.Resources.CAPTION_SAVE_COMPLETE, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void BtnLiveAdd_Click(object sender, EventArgs e) { int idx = dgvLiveMonitoring.Rows.Add(); dgvLiveMonitoring.Rows[idx].Cells["TaskName"].Value = "New Task"; dgvLiveMonitoring.Rows[idx].Cells["FileFilter"].Value = "*.*"; }
        private void BtnLiveRemove_Click(object sender, EventArgs e) { foreach (DataGridViewRow row in dgvLiveMonitoring.SelectedRows) if (!row.IsNewRow) dgvLiveMonitoring.Rows.Remove(row); }
        private void BtnLiveSave_Click(object sender, EventArgs e)
        {
            if (!ValidateRules(dgvLiveMonitoring, Properties.Resources.UPLOAD_TAB2_HEADER, out string err)) { MessageBox.Show(err, Properties.Resources.CAPTION_ERROR, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            PerformSave(Tab2Section, dgvLiveMonitoring);
            MessageBox.Show(Properties.Resources.MSG_SAVE_LIVE_SUCCESS, Properties.Resources.CAPTION_SAVE_COMPLETE, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion

        #region --- Watcher 및 상태 제어 ---

        public void InitializePanel(bool isRunning)
        {
            UpdateStatusOnRun(isRunning);
        }

        public void PauseWatching()
        {
            if (!isRunning) return;

            isPaused = true;
            StopWatchers();

            _logManager.LogEvent("[ucUploadPanel] Watchers Paused (Server Holding).");
        }

        public void ResumeWatching()
        {
            if (!isRunning) return;

            isPaused = false;
            StopWatchers();
            InitializeWatchers();

            _logManager.LogEvent("[ucUploadPanel] Watchers Resumed.");
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            this.isRunning = isRunning;
            SetControlsEnabled(!isRunning);

            if (isRunning && !isPaused)
            {
                StopWatchers();
                InitializeWatchers();
            }
            else
            {
                StopWatchers();
            }
        }

        private void InitializeWatchers()
        {
            _logManager.LogEvent("[ucUploadPanel] Initializing watchers...");
            InitializeComboBoxColumns();

            lock (_lockTab1)
            {
                _tab1RuleMap.Clear();
                var foldersToWatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (DataGridViewRow row in dgvCategorized.Rows)
                {
                    if (row.IsNewRow) continue;
                    string folder = row.Cells["WatchFolder"].Value?.ToString();
                    string plugin = row.Cells["PluginName"].Value?.ToString();

                    if (string.IsNullOrEmpty(folder) || folder == "(폴더 선택)" ||
                        string.IsNullOrEmpty(plugin) || plugin == "(플러그인 선택)") continue;

                    foldersToWatch.Add(folder);
                    _tab1RuleMap[NormalizePath(folder)] = plugin;
                }

                foreach (string folder in foldersToWatch)
                {
                    try
                    {
                        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                        var watcher = new FileSystemWatcher(folder)
                        {
                            Filter = "*.*",
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                            EnableRaisingEvents = true
                        };
                        watcher.Created += OnTab1FileEvent;
                        watcher.Renamed += OnTab1FileEvent;
                        watcher.Changed += OnTab1FileEvent;

                        _tab1Watchers.Add(watcher);
                        _logManager.LogEvent($"[ucUploadPanel] Tab1 Watcher started: {folder}");
                    }
                    catch (Exception ex) { _logManager.LogError($"[ucUploadPanel] Tab1 Watcher Failed: {ex.Message}"); }
                }
            }

            lock (_lockTab2)
            {
                _tab2RuleMap.Clear();
                foreach (DataGridViewRow row in dgvLiveMonitoring.Rows)
                {
                    if (row.IsNewRow) continue;
                    string folder = row.Cells["WatchFolder"].Value?.ToString();
                    string filter = row.Cells["FileFilter"].Value?.ToString();
                    string plugin = row.Cells["PluginName"].Value?.ToString();

                    if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(filter) ||
                        string.IsNullOrEmpty(plugin) || plugin == "(플러그인 선택)") continue;

                    try
                    {
                        if (!Directory.Exists(folder)) { _logManager.LogEvent($"[ucUploadPanel] Tab2 Watcher skip (not found): {folder}"); continue; }
                        var watcher = new FileSystemWatcher(folder)
                        {
                            Filter = filter,
                            IncludeSubdirectories = false,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                            EnableRaisingEvents = true
                        };
                        watcher.Created += OnTab2FileChanged;
                        watcher.Renamed += OnTab2FileChanged;
                        watcher.Changed += OnTab2FileChanged;
                        _tab2Watchers.Add(watcher);

                        string mapKey = $"{NormalizePath(folder)}|{filter.ToUpperInvariant()}";
                        _tab2RuleMap[mapKey] = plugin;
                        _logManager.LogEvent($"[ucUploadPanel] Tab2 Watcher started: {folder}");
                    }
                    catch (Exception ex) { _logManager.LogError($"[ucUploadPanel] Tab2 Watcher Error: {ex.Message}"); }
                }
            }
        }

        private void StopWatchers()
        {
            lock (_lockTab1)
            {
                foreach (var w in _tab1Watchers) { w.EnableRaisingEvents = false; w.Created -= OnTab1FileEvent; w.Renamed -= OnTab1FileEvent; w.Changed -= OnTab1FileEvent; w.Dispose(); }
                _tab1Watchers.Clear(); _tab1RuleMap.Clear();
            }
            lock (_lockTab2)
            {
                foreach (var w in _tab2Watchers) { w.EnableRaisingEvents = false; w.Created -= OnTab2FileChanged; w.Renamed -= OnTab2FileChanged; w.Changed -= OnTab2FileChanged; w.Dispose(); }
                _tab2Watchers.Clear(); _tab2RuleMap.Clear();
            }
            _logManager.LogEvent("[ucUploadPanel] All watchers stopped.");
        }

        #endregion

        #region --- 파일 이벤트 핸들러 ---

        private bool WaitForFileReady(string filePath, int timeoutSeconds = 60)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                try
                {
                    if (!File.Exists(filePath)) return false;

                    // [핵심 개선] FileShare.None(독점 잠금) -> FileShare.ReadWrite | FileShare.Delete
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length >= 0) return true;
                    }
                }
                catch (IOException) { }
                catch (Exception ex)
                {
                    _logManager.LogError($"[WaitForFileReady] Error checking file {filePath}: {ex.Message}");
                    return false;
                }
                Thread.Sleep(500);
            }
            _logManager.LogError($"[WaitForFileReady] Timeout waiting for file: {filePath}");
            return false;
        }

        private void OnTab1FileEvent(object sender, FileSystemEventArgs e)
        {
            if (e.Name.Contains("_#1_") || isPaused) return;

            Task.Run(() =>
            {
                try
                {
                    if (!WaitForFileReady(e.FullPath, 60))
                    {
                        _logManager.LogEvent($"[Tab1] File locked or copy failed (timeout), skipping: {e.Name}");
                        return;
                    }

                    string fileKey = e.FullPath.ToUpperInvariant();
                    DateTime now = DateTime.Now;

                    if (_lastProcessEventTime.Count > 2000)
                    {
                        lock (_cleanupLock)
                        {
                            if (_lastProcessEventTime.Count > 2000)
                            {
                                var oldKeys = _lastProcessEventTime.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToList();
                                foreach (var key in oldKeys) _lastProcessEventTime.TryRemove(key, out _);
                            }
                        }
                    }

                    if (_lastProcessEventTime.TryGetValue(fileKey, out var lastTime) && (now - lastTime).TotalSeconds < DebounceSeconds) return;
                    _lastProcessEventTime[fileKey] = now;

                    string folder = NormalizePath(Path.GetDirectoryName(e.FullPath));
                    if (!_tab1RuleMap.TryGetValue(folder, out string pluginName))
                    {
                        var parent = NormalizePath(Directory.GetParent(e.FullPath)?.FullName);
                        if (string.IsNullOrEmpty(parent) || !_tab1RuleMap.TryGetValue(parent, out pluginName))
                        {
                            if (_settingsManager.IsDebugMode) _logManager.LogDebug($"[Tab1] No rule for '{folder}' (File: {e.Name})");
                            return;
                        }
                    }

                    _logManager.LogEvent($"[Tab1] Triggered: {e.Name} ({e.ChangeType}) -> Plugin: {pluginName}");
                    RunPlugin(pluginName, e.FullPath);
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[Tab1] Error processing file {e.Name}: {ex.Message}");
                }
            });
        }

        private void OnTab2FileChanged(object sender, FileSystemEventArgs e)
        {
            if (e.Name.Contains("_#1_") || isPaused) return;

            Task.Run(() =>
            {
                try
                {
                    if (!WaitForFileReady(e.FullPath, 60)) return;

                    string fileKey = e.FullPath.ToUpperInvariant();
                    DateTime now = DateTime.Now;

                    if (_lastProcessEventTime.Count > 2000)
                    {
                        lock (_cleanupLock)
                        {
                            if (_lastProcessEventTime.Count > 2000)
                            {
                                var oldKeys = _lastProcessEventTime.Where(kv => (now - kv.Value).TotalMinutes > 5).Select(kv => kv.Key).ToList();
                                foreach (var key in oldKeys) _lastProcessEventTime.TryRemove(key, out _);
                            }
                        }
                    }

                    if (_lastProcessEventTime.TryGetValue(fileKey, out var lastTime) && (now - lastTime).TotalSeconds < DebounceSeconds) return;
                    _lastProcessEventTime[fileKey] = now;

                    string folder = NormalizePath(Path.GetDirectoryName(e.FullPath));
                    string filter = (sender as FileSystemWatcher)?.Filter.ToUpperInvariant();
                    string mapKey = $"{folder}|{filter}";

                    if (_tab2RuleMap.TryGetValue(mapKey, out string pluginName))
                    {
                        _logManager.LogEvent($"[Tab2] Triggered: {e.Name} -> Plugin: {pluginName}");
                        RunPlugin(pluginName, e.FullPath);
                    }
                    else
                    {
                        if (_settingsManager.IsDebugMode) _logManager.LogDebug($"[Tab2] No rule for '{mapKey}'");
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogError($"[Tab2] Error processing file {e.Name}: {ex.Message}");
                }
            });
        }

        private void RunPlugin(string pluginName, string filePath)
        {
            try
            {
                if (!_pluginRuntimeCache.TryGetValue(pluginName, out PluginRuntimeInfo runtimeInfo))
                {
                    var pluginItem = _pluginPanel.GetLoadedPlugins().FirstOrDefault(p => p.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
                    if (pluginItem == null || !File.Exists(pluginItem.AssemblyPath))
                    {
                        _logManager.LogError($"[ucUploadPanel] Plugin DLL not found: {pluginName}");
                        return;
                    }

                    try
                    {
                        byte[] dllBytes = File.ReadAllBytes(pluginItem.AssemblyPath);
                        Assembly asm = Assembly.Load(dllBytes);

                        Type targetType = asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.Name == "ProcessAndUpload"));
                        if (targetType == null)
                        {
                            _logManager.LogError($"[ucUploadPanel] Invalid plugin class: {pluginName}");
                            return;
                        }

                        MethodInfo mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(object), typeof(object) });
                        bool requiresThreeArgs = true;

                        if (mi == null)
                        {
                            mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(string) });
                            requiresThreeArgs = false;

                            if (mi == null)
                            {
                                mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string) });
                            }
                        }

                        if (mi == null)
                        {
                            _logManager.LogError($"[ucUploadPanel] ProcessAndUpload method not found in {pluginName}");
                            return;
                        }

                        runtimeInfo = new PluginRuntimeInfo
                        {
                            ClassType = targetType,
                            Method = mi,
                            RequiresThreeArgs = requiresThreeArgs
                        };

                        _pluginRuntimeCache.TryAdd(pluginName, runtimeInfo);
                        _logManager.LogEvent($"[ucUploadPanel] Plugin loaded and cached: {pluginName}");
                    }
                    catch (Exception ex)
                    {
                        _logManager.LogError($"[ucUploadPanel] Error loading plugin assembly {pluginName}: {ex.Message}");
                        return;
                    }
                }

                if (runtimeInfo != null && runtimeInfo.Method != null)
                {
                    object pluginObj = Activator.CreateInstance(runtimeInfo.ClassType);

                    object[] args;
                    var parameters = runtimeInfo.Method.GetParameters();

                    if (parameters.Length == 3)
                    {
                        args = new object[] { filePath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini"), null };
                    }
                    else if (parameters.Length == 2)
                    {
                        args = new object[] { filePath, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Settings.ini") };
                    }
                    else
                    {
                        args = new object[] { filePath };
                    }

                    Task.Run(() =>
                    {
                        try
                        {
                            runtimeInfo.Method.Invoke(pluginObj, args);
                            _logManager.LogEvent($"[ucUploadPanel] Plugin execution completed: {pluginName}");
                        }
                        catch (Exception ex)
                        {
                            string realError = ex.GetBaseException().Message;
                            _logManager.LogError($"[ucUploadPanel] Plugin execution failed: {pluginName}. Error: {realError}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                string realError = ex.GetBaseException().Message;
                _logManager.LogError($"[ucUploadPanel] Failed to run plugin {pluginName}: {realError}");
            }
        }

        #endregion

        #region --- 기타 헬퍼 메서드 ---

        private void Dgv_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.Value != null && e.Value != DBNull.Value) return;
            var dgv = sender as DataGridView;
            if (dgv == null) return;
            if (dgv.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)
            {
                string colName = dgv.Columns[e.ColumnIndex].Name;
                if (colName == "WatchFolder") { e.Value = "(폴더 선택)"; e.FormattingApplied = true; }
                else if (colName == "PluginName") { e.Value = "(플러그인 선택)"; e.FormattingApplied = true; }
            }
            else if (dgv == dgvLiveMonitoring && dgv.Columns[e.ColumnIndex].Name == "WatchFolder")
            {
                e.Value = "(경로 입력/선택)";
                e.FormattingApplied = true;
            }
        }

        private void Dgv_DataError(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; }

        private void DgvLiveMonitoring_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || dgvLiveMonitoring.ReadOnly) return;
            if (dgvLiveMonitoring.Columns[e.ColumnIndex].Name == "btnSelectFolder")
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = _configPanel?.BaseFolderPath ?? AppDomain.CurrentDomain.BaseDirectory;
                    if (dlg.ShowDialog() == DialogResult.OK) dgvLiveMonitoring.Rows[e.RowIndex].Cells["WatchFolder"].Value = dlg.SelectedPath;
                }
            }
        }

        private void Dgv_CurrentCellDirtyStateChanged(object sender, EventArgs e) { if (sender is DataGridView dgv && dgv.CurrentCell is DataGridViewComboBoxCell) dgv.CommitEdit(DataGridViewDataErrorContexts.Commit); }

        private void Dgv_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var dgv = sender as DataGridView;
            if (dgv.Columns[e.ColumnIndex].Name != "PluginName") return;
            string pluginName = dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
            if (string.IsNullOrEmpty(pluginName) || pluginName == "(플러그인 선택)") return;

            if (_pluginMetadataCache.TryGetValue(pluginName, out var meta))
            {
                var taskCell = dgv.Rows[e.RowIndex].Cells["TaskName"];
                if (string.IsNullOrEmpty(taskCell.Value?.ToString()) || taskCell.Value.ToString() == "New Task") taskCell.Value = meta.Task;
                if (dgv == dgvLiveMonitoring && meta.Filter != null) dgv.Rows[e.RowIndex].Cells["FileFilter"].Value = meta.Filter;
            }
        }

        private void RefreshPluginMetadataCache()
        {
            _pluginMetadataCache.Clear();
            foreach (var item in _pluginPanel.GetLoadedPlugins())
            {
                var meta = LoadPluginMetadata(item.AssemblyPath);
                if (meta.Task != null) _pluginMetadataCache[item.PluginName] = meta;
            }
        }

        private void OnPluginsChanged(object sender, EventArgs e)
        {
            InitializeComboBoxColumns();
            RefreshPluginMetadataCache();
            _pluginRuntimeCache.Clear();
            _logManager.LogEvent("[ucUploadPanel] Plugins changed - Cache cleared.");
        }

        private (string Task, string Filter, bool RequiresOverride) LoadPluginMetadata(string dllPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(dllPath);
                Assembly asm = Assembly.Load(bytes);
                var type = asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.Name == "ProcessAndUpload"));
                if (type != null)
                {
                    object obj = Activator.CreateInstance(type);
                    string t = type.GetProperty("DefaultTaskName")?.GetValue(obj) as string;
                    string f = type.GetProperty("DefaultFileFilter")?.GetValue(obj) as string;
                    bool r = (bool)(type.GetProperty("RequiresOverrideNames")?.GetValue(obj) ?? false);
                    return (t, f, r);
                }
            }
            catch { }
            return (null, null, false);
        }

        public void LoadImageSaveFolder_PathChanged() { InitializeComboBoxColumns(); LoadSettings(); }

        private void SetControlsEnabled(bool enabled)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetControlsEnabled(enabled)));
                return;
            }

            dgvCategorized.ReadOnly = !enabled;
            btnCatAdd.Enabled = enabled;
            btnCatRemove.Enabled = enabled;
            btnCatSave.Enabled = enabled;
            dgvLiveMonitoring.ReadOnly = !enabled;
            btnLiveAdd.Enabled = enabled;
            btnLiveRemove.Enabled = enabled;
            btnLiveSave.Enabled = enabled;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopWatchers();
                if (components != null) components.Dispose();
            }
            base.Dispose(disposing);
        }

        public bool HasInvalidRules(out string msg)
        {
            return ValidateRules(dgvCategorized, "Tab1", out msg) ? false : ValidateRules(dgvLiveMonitoring, "Tab2", out msg) ? false : true;
        }

        #endregion
    }
}
