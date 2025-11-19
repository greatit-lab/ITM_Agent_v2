// ITM_Agent/ucPanel/ucPluginPanel.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ITM_Agent.Plugins;
using ITM_Agent.Services;
using System.Text.RegularExpressions;

namespace ITM_Agent.ucPanel
{
    /// <summary>
    /// [추가] 한 번 로드된 플러그인의 런타임 정보를 캐시하는 클래스
    /// (메모리 누수 방지용)
    /// </summary>
    public class PluginRuntimeInfo
    {
        public Assembly LoadedAssembly { get; }
        public Type PluginType { get; }
        public MethodInfo ProcessMethod { get; }
        public int ProcessMethodArgCount { get; }

        public PluginRuntimeInfo(Assembly assembly, Type pluginType, MethodInfo method)
        {
            this.LoadedAssembly = assembly;
            this.PluginType = pluginType;
            this.ProcessMethod = method;
            this.ProcessMethodArgCount = method.GetParameters().Length;
        }
    }


    public partial class ucPluginPanel : UserControl
    {
        // 플러그인 정보를 보관하는 리스트 (PluginListItem은 플러그인명과 DLL 경로 정보를 저장)
        private List<PluginListItem> loadedPlugins = new List<PluginListItem>();
        private SettingsManager settingsManager;
        private LogManager logManager;

        // ▼▼▼ [추가] 런타임 플러그인 캐시 (메모리 누수 방지용) ▼▼▼
        private readonly Dictionary<string, PluginRuntimeInfo> _pluginCache =
            new Dictionary<string, PluginRuntimeInfo>(StringComparer.OrdinalIgnoreCase);

        // 플러그인 리스트가 변경될 때 통보용
        public event EventHandler PluginsChanged;

        /// <summary>
        /// [추가] ucUploadPanel에서 캐시된 플러그인 런타임 정보를 가져가기 위한 public 메서드
        /// </summary>
        public PluginRuntimeInfo GetPluginRuntime(string pluginName)
        {
            _pluginCache.TryGetValue(pluginName, out var info);
            return info;
        }

        public ucPluginPanel(SettingsManager settings)
        {
            InitializeComponent();
            settingsManager = settings;
            logManager = new LogManager(AppDomain.CurrentDomain.BaseDirectory);

            // settings.ini의 [RegPlugins] 섹션에서 기존에 등록된 플러그인 정보를 불러옴
            LoadPluginsFromSettings();
        }

        private void UpdatePluginListDisplay()
        {
            lb_PluginList.Items.Clear();
            for (int i = 0; i < loadedPlugins.Count; i++)
            {
                lb_PluginList.Items.Add($"{i + 1}. {loadedPlugins[i]}");
            }
        }

        private void btn_PlugAdd_Click(object sender, EventArgs e)
        {
            /* 1) 파일 선택 대화상자 (전통적 using 블록) */
            using (OpenFileDialog open = new OpenFileDialog
            {
                Filter = "DLL Files (*.dll)|*.dll|All Files (*.*)|*.*",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory,
                Multiselect = true
            })
            {
                if (open.ShowDialog() != DialogResult.OK) return;

                int addedCount = 0;
                int skippedCount = 0;
                List<string> errorMessages = new List<string>();

                foreach (string selectedDllPath in open.FileNames)
                {
                    // ▼▼▼ [수정] 2~7단계 로직 변경 (캐시 사용) ▼▼▼
                    string tempPluginName = null; // 임시 이름 (오류 로깅용)
                    try
                    {
                        /* 2) Assembly 임시 로드 (이름 확인 및 중복 체크용) */
                        // (ReflectionOnlyLoadContext는 .NET Core 7.3에서는 복잡하므로,
                        // 우선은 GetName()으로 이름만 확인합니다.)
                        // GetName()은 전체 어셈블리를 로드하지 않습니다.
                        tempPluginName = AssemblyName.GetAssemblyName(selectedDllPath).Name;

                        if (loadedPlugins.Any(p => p.PluginName.Equals(tempPluginName, StringComparison.OrdinalIgnoreCase)))
                        {
                            logManager.LogEvent($"Plugin add skipped (already registered): {tempPluginName}");
                            skippedCount++;
                            continue;
                        }

                        /* 3) Library 폴더 준비 */
                        string libraryFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library");
                        if (!Directory.Exists(libraryFolder)) Directory.CreateDirectory(libraryFolder);

                        /* 4) 플러그인 DLL 복사 */
                        string destDllPath = Path.Combine(libraryFolder, Path.GetFileName(selectedDllPath));
                        if (File.Exists(destDllPath))
                        {
                            logManager.LogEvent($"Plugin add skipped (file already exists in Library): {Path.GetFileName(selectedDllPath)}");
                            skippedCount++;
                            continue;
                        }
                        File.Copy(selectedDllPath, destDllPath);

                        /* 5) 참조 DLL 자동 복사 (임시 로드된 AssemblyName 사용) */
                        // (주의: 이 방식은 간접 참조 DLL은 복사하지 못할 수 있습니다)
                        Assembly tempAsm = Assembly.Load(File.ReadAllBytes(selectedDllPath));
                        foreach (AssemblyName refAsm in tempAsm.GetReferencedAssemblies())
                        {
                            string refFile = refAsm.Name + ".dll";
                            string srcRef = Path.Combine(Path.GetDirectoryName(selectedDllPath), refFile);
                            string dstRef = Path.Combine(libraryFolder, refFile);

                            if (File.Exists(srcRef) && !File.Exists(dstRef))
                                File.Copy(srcRef, dstRef);
                        }
                        // tempAsm는 여기서 해제(되진 않지만, Add 시에는 어차피 LoadAndCachePlugin에서 다시 로드)

                        /* 6) 필수 NuGet DLL 강제 복사 (기존과 동일) */
                        string[] mustHave = { "System.Text.Encoding.CodePages.dll" };
                        foreach (var f in mustHave)
                        {
                            string src = Path.Combine(Path.GetDirectoryName(selectedDllPath), f);
                            string dst = Path.Combine(libraryFolder, f);
                            if (File.Exists(src) && !File.Exists(dst))
                                File.Copy(src, dst);
                        }

                        /* 7) 목록 설정 등록 및 캐시 */
                        // (복사된 DLL 경로(destDllPath)로 캐시에 로드)
                        PluginRuntimeInfo runtimeInfo = LoadAndCachePlugin(destDllPath);
                        if (runtimeInfo == null)
                        {
                            throw new Exception("Failed to load assembly into runtime cache after copying.");
                        }

                        var item = new PluginListItem
                        {
                            PluginName = runtimeInfo.LoadedAssembly.GetName().Name,
                            PluginVersion = runtimeInfo.LoadedAssembly.GetName().Version.ToString(),
                            AssemblyPath = destDllPath
                        };

                        loadedPlugins.Add(item);
                        SavePluginInfoToSettings(item); // INI 파일에 저장
                        logManager.LogEvent($"Plugin registered: {item.PluginName}");
                        addedCount++;
                    }
                    catch (Exception ex)
                    {
                        errorMessages.Add($"Error loading {Path.GetFileName(selectedDllPath)} (Plugin: {tempPluginName ?? "Unknown"}): {ex.Message}");
                        logManager.LogError($"Plugin load error: {ex}");
                    }
                    // ▲▲▲ [수정] 완료 ▲▲▲
                } 

                if (addedCount > 0)
                {
                    UpdatePluginListDisplay();
                    PluginsChanged?.Invoke(this, EventArgs.Empty);
                }

                if (errorMessages.Count > 0)
                {
                    MessageBox.Show(string.Join("\n", errorMessages),
                                    Properties.Resources.CAPTION_ERROR, 
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (addedCount > 0 || skippedCount > 0)
                {
                    string msg = string.Format(
                        Properties.Resources.MSG_PLUGIN_ADD_RESULT,
                        addedCount,
                        skippedCount);
                    MessageBox.Show(msg,
                                    Properties.Resources.CAPTION_INFO, 
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void btn_PlugRemove_Click(object sender, EventArgs e)
        {
            if (lb_PluginList.SelectedItems.Count == 0)
            {
                MessageBox.Show(Properties.Resources.MSG_PLUGIN_SELECT_DELETE, 
                                Properties.Resources.CAPTION_INFO,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedDisplayItems = lb_PluginList.SelectedItems.Cast<string>().ToList();

            string confirmMsg = string.Format(
                Properties.Resources.MSG_PLUGIN_CONFIRM_DELETE, 
                selectedDisplayItems.Count);
            DialogResult result = MessageBox.Show(
                confirmMsg,
                Properties.Resources.CAPTION_WARNING, 
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            int removedCount = 0;
            List<string> errorMessages = new List<string>();

            foreach (string display in selectedDisplayItems)
            {
                string pluginName = Regex.Replace(display, @"^\d+\.\s*", "");
                pluginName = Regex.Replace(pluginName, @"\s*\(v.*\)$", "");
                pluginName = pluginName.Trim();

                var pluginItem = loadedPlugins
                                 .FirstOrDefault(p => p.PluginName.Equals(pluginName,
                                                         StringComparison.OrdinalIgnoreCase));
                if (pluginItem == null)
                {
                    errorMessages.Add($"Internal list error: '{pluginName}' not found.");
                    logManager.LogError($"Remove failed (not in list): {pluginName}");
                    continue;
                }

                if (File.Exists(pluginItem.AssemblyPath))
                {
                    try
                    {
                        File.Delete(pluginItem.AssemblyPath);
                        logManager.LogEvent($"DLL deleted: {pluginItem.AssemblyPath}");
                    }
                    catch (Exception ex)
                    {
                        errorMessages.Add($"DLL delete error '{pluginName}': {ex.Message}");
                        logManager.LogError("DLL 삭제 오류: " + ex.Message);
                        continue;
                    }
                }

                loadedPlugins.Remove(pluginItem);
                settingsManager.RemoveKeyFromSection("RegPlugins", pluginName);
                
                // ▼▼▼ [추가] 런타임 캐시에서도 제거 ▼▼▼
                _pluginCache.Remove(pluginName);
                // ▲▲▲ [추가] 완료 ▲▲▲

                logManager.LogEvent($"Plugin removed: {pluginName}");
                removedCount++;
            }

            if (removedCount > 0)
            {
                UpdatePluginListDisplay();
                PluginsChanged?.Invoke(this, EventArgs.Empty);
            }

            if (errorMessages.Count > 0)
            {
                MessageBox.Show($"Errors occurred:\n{string.Join("\n", errorMessages)}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SavePluginInfoToSettings(PluginListItem pluginItem)
        {
            string relativePath = Path.Combine("Library", Path.GetFileName(pluginItem.AssemblyPath));
            settingsManager.SetValueToSection("RegPlugins",
                pluginItem.PluginName,
                relativePath);
        }

        /// <summary>
        /// [추가] 어셈블리를 로드하고 런타임 캐시에 저장하는 중앙 헬퍼
        /// </summary>
        private PluginRuntimeInfo LoadAndCachePlugin(string assemblyPath)
        {
            if (!File.Exists(assemblyPath))
            {
                logManager.LogError($"[CachePlugin] File not found: {assemblyPath}");
                return null;
            }

            try
            {
                // 1. 바이트로 로드 (메모리 누수의 핵심 원인 해결)
                // Assembly.Load(bytes)는 동일 경로라도 다른 어셈블리로 취급되지만,
                // 여기서는 시작 시 1회, Add 시 1회만 호출되므로 누수 문제는 아님.
                // (더 좋은 방법은 AppDomain이지만, C# 7.3이므로 이 방식을 유지)
                byte[] dllBytes = File.ReadAllBytes(assemblyPath);
                Assembly asm = Assembly.Load(dllBytes);
                string pluginName = asm.GetName().Name;

                // 2. 이미 캐시되어 있으면 반환 (중복 로드 방지)
                if (_pluginCache.ContainsKey(pluginName))
                {
                    return _pluginCache[pluginName];
                }

                // 3. ProcessAndUpload 메서드 및 타입 검색
                Type targetType = asm.GetTypes().FirstOrDefault(t => t.IsClass && !t.IsAbstract && t.GetMethods().Any(m => m.Name == "ProcessAndUpload"));
                if (targetType == null)
                {
                    logManager.LogError($"[CachePlugin] No class with ProcessAndUpload() method found in: {pluginName}");
                    return null;
                }

                // 4. 오버로드 순서대로 메서드 검색 (ucUploadPanel의 로직을 중앙화)
                MethodInfo mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(object), typeof(object) });
                if (mi == null) mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string), typeof(string) });
                if (mi == null) mi = targetType.GetMethod("ProcessAndUpload", new[] { typeof(string) });

                if (mi == null)
                {
                    logManager.LogError($"[CachePlugin] No compatible ProcessAndUpload() overload found in: {pluginName}");
                    return null;
                }

                // 5. 캐시 저장
                var runtimeInfo = new PluginRuntimeInfo(asm, targetType, mi);
                _pluginCache[pluginName] = runtimeInfo;

                logManager.LogDebug($"[CachePlugin] Plugin cached successfully: {pluginName}");
                return runtimeInfo;
            }
            catch (Exception ex)
            {
                logManager.LogError($"[CachePlugin] Failed to load assembly {assemblyPath}: {ex.Message}");
                return null;
            }
        }


        private void LoadPluginsFromSettings()
        {
            var pluginEntries = settingsManager.GetFoldersFromSection("[RegPlugins]");
            foreach (string entry in pluginEntries)
            {
                string[] parts = entry.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;

                string iniKeyName = parts[0].Trim();
                string assemblyPath = parts[1].Trim();

                if (!Path.IsPathRooted(assemblyPath))
                    assemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyPath);

                // ▼▼▼ [수정] LoadAndCachePlugin 헬퍼 사용 ▼▼▼
                PluginRuntimeInfo runtimeInfo = LoadAndCachePlugin(assemblyPath);

                if (runtimeInfo == null)
                {
                    logManager.LogError($"플러그인 로드 실패 (Cache/Load): {assemblyPath}");
                    continue;
                }

                try
                {
                    string asmName = runtimeInfo.LoadedAssembly.GetName().Name;
                    string asmVersion = runtimeInfo.LoadedAssembly.GetName().Version.ToString();

                    var item = new PluginListItem
                    {
                        PluginName = asmName,
                        PluginVersion = asmVersion,
                        AssemblyPath = assemblyPath
                    };

                    loadedPlugins.Add(item);
                    logManager.LogEvent($"Plugin auto-loaded: {item}");
                }
                catch (Exception ex)
                {
                    logManager.LogError($"플러그인 메타데이터 생성 실패: {ex.Message}");
                }
                // ▲▲▲ [수정] 완료 ▲▲▲
            }
            UpdatePluginListDisplay();
        }

        /// <summary>
        /// 외부에서 로드된 플러그인 목록을 반환합니다.
        /// </summary>
        public List<PluginListItem> GetLoadedPlugins()
        {
            return loadedPlugins;
        }

        #region ====== Run 상태 동기화 ====== 

        private void SetControlsEnabled(bool enabled)
        {
            btn_PlugAdd.Enabled = enabled;
            btn_PlugRemove.Enabled = enabled;
            lb_PluginList.Enabled = enabled;
        }

        public void UpdateStatusOnRun(bool isRunning)
        {
            SetControlsEnabled(!isRunning);
        }

        public void InitializePanel(bool isRunning)
        {
            SetControlsEnabled(!isRunning);
        }

        #endregion
    }
}
