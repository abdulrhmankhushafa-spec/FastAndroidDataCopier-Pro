using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FastAndroidDataCopier
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DeviceItem> ConnectedDevices { get; set; } = new ObservableCollection<DeviceItem>();
        private CancellationTokenSource? _cts;
        private long _totalFolderBytes = 0;
        private readonly DispatcherTimer _autoDetectTimer;
        private bool _isScanningDevices = false;
        private int _activeTransferCount = 0;
        private string _resolvedAdbPath = "";
        private bool _adbServerStarted = false;

        public MainWindow()
        {
            InitializeComponent();
            LvDevices.ItemsSource = ConnectedDevices;

            _autoDetectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _autoDetectTimer.Tick += AutoDetectTimer_Tick;

            Loaded += async (_, __) =>
            {
                await InitializeAdbAndScanAsync();
                _autoDetectTimer.Start();
            };
        }

        private async Task InitializeAdbAndScanAsync()
        {
            string? adb = ResolveFixedAdbPath();
            if (adb == null)
            {
                TxtAdbStatusBadge.Text = "❌ أداة ADB غير موجودة";
                TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtGlobalStatus.Text = "⚠️ لم يتم العثور على adb.exe تلقائياً! يرجى وضع مجلد adb في C:\\adb أو بجوار البرنامج.";
                return;
            }

            _resolvedAdbPath = adb;
            TxtAdbStatusBadge.Text = $"✅ محرك ADB جاهز ({adb})";
            TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

            if (!_adbServerStarted)
            {
                _adbServerStarted = true;
                await RunCommandCaptureAsync(_resolvedAdbPath, "start-server", 5000);
            }

            await ScanAndSyncDevicesAsync(_resolvedAdbPath, silent: false);
        }

        private string? ResolveFixedAdbPath()
        {
            string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastAndroidDataCopier_adb.txt");
            if (File.Exists(configFile))
            {
                try
                {
                    string saved = File.ReadAllText(configFile).Trim();
                    if (File.Exists(saved)) return saved;
                }
                catch { }
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] candidates = new[]
            {
                Path.Combine(baseDir, "adb.exe"),
                Path.Combine(baseDir, "platform-tools", "adb.exe"),
                Path.Combine(baseDir, "adb", "adb.exe"),
                @"C:\adb\adb.exe",
                @"C:\platform-tools\adb.exe",
                @"C:\Android\platform-tools\adb.exe",
                @"C:\Program Files (x86)\Minimal ADB and Fastboot\adb.exe",
                @"C:\Program Files\Minimal ADB and Fastboot\adb.exe",
                @"D:\adb\adb.exe",
                @"D:\platform-tools\adb.exe",
                Path.Combine(localAppData, @"Android\Sdk\platform-tools\adb.exe"),
                Path.Combine(userProfile, @"AppData\Local\Android\Sdk\platform-tools\adb.exe"),
                Path.Combine(userProfile, @"Desktop\adb\adb.exe"),
                Path.Combine(userProfile, @"Desktop\platform-tools\adb.exe"),
                Path.Combine(userProfile, @"Downloads\platform-tools\adb.exe")
            };

            foreach (var path in candidates)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    return path;
            }

            try
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrEmpty(pathEnv))
                {
                    foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string candidate = Path.Combine(dir.Trim(), "adb.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch { }

            return null;
        }

        private string? EnsureAdbPath(bool promptIfMissing)
        {
            if (!string.IsNullOrWhiteSpace(_resolvedAdbPath) && File.Exists(_resolvedAdbPath))
                return _resolvedAdbPath;

            string? found = ResolveFixedAdbPath();
            if (found != null)
            {
                _resolvedAdbPath = found;
                TxtAdbStatusBadge.Text = $"✅ محرك ADB جاهز ({found})";
                TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                return found;
            }

            if (!promptIfMissing)
                return null;

            var res = MessageBox.Show(
                "لم يتم العثور على ملف adb.exe في المسار الثابت (C:\\adb\\adb.exe) أو بجوار البرنامج.\n\nهل تريد تحديد مكان ملف adb.exe الآن ليحفظه البرنامج تلقائياً بشكل دائم؟",
                "تحديد موقع أداة ADB لمرة واحدة",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Yes)
            {
                var dlg = new OpenFileDialog
                {
                    Filter = "ADB Executable (adb.exe)|adb.exe",
                    Title = "اختر ملف adb.exe (سيتم حفظه تلقائياً)"
                };
                if (dlg.ShowDialog() == true && File.Exists(dlg.FileName))
                {
                    _resolvedAdbPath = dlg.FileName;
                    try
                    {
                        string configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastAndroidDataCopier_adb.txt");
                        File.WriteAllText(configFile, _resolvedAdbPath);
                    }
                    catch { }

                    TxtAdbStatusBadge.Text = $"✅ محرك ADB جاهز ({_resolvedAdbPath})";
                    TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    return _resolvedAdbPath;
                }
            }

            return null;
        }

        private async void AutoDetectTimer_Tick(object? sender, EventArgs e)
        {
            if (ChkAutoDetectDevices.IsChecked != true || _isScanningDevices)
                return;

            string? adbPath = EnsureAdbPath(promptIfMissing: false);
            if (adbPath == null)
                return;

            await ScanAndSyncDevicesAsync(adbPath, silent: true);
        }

        private async void BtnRestartAdbServer_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTransferCount > 0)
            {
                MessageBox.Show("يرجى إيقاف عملية النسخ الجارية أولاً قبل إعادة تشغيل خادم ADB.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            BtnRestartAdbServer.IsEnabled = false;
            TxtGlobalStatus.Text = "🛠 جاري إعادة تشغيل خادم ADB وتنشيط منافذ USB...";
            try
            {
                await RunCommandCaptureAsync(adbPath, "kill-server", 4000);
                await Task.Delay(300);
                await RunCommandCaptureAsync(adbPath, "start-server", 5000);
                await RunCommandCaptureAsync(adbPath, "reconnect offline", 3000);
                await ScanAndSyncDevicesAsync(adbPath, silent: false);
            }
            finally
            {
                BtnRestartAdbServer.IsEnabled = true;
            }
        }

        private async void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "اختر المجلد المراد نسخه إلى الهواتف"
            };

            if (dlg.ShowDialog() == true)
            {
                TxtSourceFolder.Text = dlg.FolderName;
                TxtFolderSizeInfo.Text = "جاري الحساب...";

                string selectedPath = dlg.FolderName;
                _totalFolderBytes = await Task.Run(() => CalculateDirectorySize(new DirectoryInfo(selectedPath)));
                double sizeGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                TxtFolderSizeInfo.Text = $"{sizeGb:F2} GB";

                UpdateAllDevicesSpaceVerdict();
            }
        }

        private void BtnBrowseApk_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Android Package (*.apk)|*.apk",
                Title = "اختر ملف التطبيق أو اللعبة (APK)"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtApkPath.Text = dlg.FileName;
                CmbOperationMode.SelectedIndex = 1;
            }
        }

        private async void BtnBrowsePhoneDest_Click(object sender, RoutedEventArgs e)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            var activeDevice = ConnectedDevices.FirstOrDefault(d => d.IsSelected && d.IsOnlineReady)
                               ?? ConnectedDevices.FirstOrDefault(d => d.IsOnlineReady);

            if (activeDevice == null)
            {
                await ScanAndSyncDevicesAsync(adbPath, silent: false);
                activeDevice = ConnectedDevices.FirstOrDefault(d => d.IsOnlineReady);
            }

            if (activeDevice == null)
            {
                var unauth = ConnectedDevices.FirstOrDefault(d => d.DeviceState == "unauthorized");
                if (unauth != null)
                {
                    MessageBox.Show("الهاتف متصل ولكن لم يتم السماح لتصحيح USB بعد!\n\nيرجى فتح شاشة الهاتف والضغط على (سماح / Allow USB Debugging) ثم المحاولة مرة أخرى.", "مطلوب السماح من شاشة الهاتف", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("لا يوجد هاتف متصل وجاهز حالياً.\nتأكد من:\n1. توصيل كابل USB.\n2. تفعيل (تصحيح أخطاء USB) في خيارات المطور بالهاتف.", "لا يوجد هاتف متصل", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            var browser = new PhoneFolderBrowserDialog(adbPath, activeDevice.Serial, TxtTargetDest.Text)
            {
                Owner = this
            };

            if (browser.ShowDialog() == true)
            {
                TxtTargetDest.Text = browser.SelectedPath;
                TxtGlobalStatus.Text = $"🎯 تم اعتماد مسار النسخ داخل الهاتف: {browser.SelectedPath}";
            }
        }

        private static List<FileInfo> EnumerateFilesSafe(DirectoryInfo root)
        {
            var result = new List<FileInfo>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                try
                {
                    foreach (var fi in current.GetFiles())
                    {
                        result.Add(fi);
                    }
                }
                catch { }

                try
                {
                    foreach (var sub in current.GetDirectories())
                    {
                        stack.Push(sub);
                    }
                }
                catch { }
            }

            return result;
        }

        private static List<DirectoryInfo> EnumerateDirectoriesSafe(DirectoryInfo root)
        {
            var result = new List<DirectoryInfo>();
            var stack = new Stack<DirectoryInfo>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                result.Add(current);
                try
                {
                    foreach (var sub in current.GetDirectories())
                    {
                        stack.Push(sub);
                    }
                }
                catch { }
            }

            return result;
        }

        private long CalculateDirectorySize(DirectoryInfo dir)
        {
            long size = 0;
            foreach (var fi in EnumerateFilesSafe(dir))
            {
                try { size += fi.Length; } catch { }
            }
            return size;
        }

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            BtnRefreshDevices.IsEnabled = false;
            TxtGlobalStatus.Text = "🔍 جاري فحص منافذ USB والتعرف على جميع الهواتف المتصلة...";
            if (_activeTransferCount == 0)
            {
                await RunCommandCaptureAsync(adbPath, "reconnect offline", 2500);
            }
            await ScanAndSyncDevicesAsync(adbPath, silent: false);
            BtnRefreshDevices.IsEnabled = true;
        }

        private async Task ScanAndSyncDevicesAsync(string adbPath, bool silent)
        {
            if (_isScanningDevices) return;
            _isScanningDevices = true;

            try
            {
                // Host-only query: does NOT open any shell on busy devices so active USB transfers are NEVER interrupted!
                string output = await RunCommandCaptureAsync(adbPath, "devices -l", 3500);
                var rawLines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var currentSeenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parsedDevices = new List<(string Serial, string State, string Model)>();

                foreach (var rawLine in rawLines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) ||
                        line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("*") ||
                        line.StartsWith("adb", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string serial = parts[0].Trim();
                        string state = parts[1].Trim().ToLowerInvariant();

                        if (state != "device" && state != "unauthorized" && state != "offline" && state != "recovery")
                            continue;

                        if (state == "offline" && _activeTransferCount == 0)
                        {
                            await RunCommandCaptureAsync(adbPath, $"-s {serial} reconnect", 2000);
                        }

                        string model = "";
                        var modelPart = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.OrdinalIgnoreCase));
                        if (modelPart != null)
                        {
                            model = modelPart.Substring(6).Replace("_", " ").Trim();
                        }

                        currentSeenSerials.Add(serial);
                        parsedDevices.Add((serial, state, model));
                    }
                }

                foreach (var existing in ConnectedDevices.ToList())
                {
                    if (!currentSeenSerials.Contains(existing.Serial))
                    {
                        if (existing.IsBusy || existing.NeedsResume || (existing.Progress > 0 && existing.Progress < 100))
                        {
                            if (!existing.IsDisconnected)
                            {
                                existing.IsDisconnected = true;
                                existing.DeviceState = "disconnected";
                                existing.NeedsResume = true;
                                existing.Status = "⚠️ انفصل الكابل! أعد توصيله للاستئناف التلقائي";
                            }
                        }
                        else
                        {
                            ConnectedDevices.Remove(existing);
                        }
                    }
                }

                var devicesToAutoResume = new List<DeviceItem>();

                foreach (var (serial, state, parsedModel) in parsedDevices)
                {
                    var existing = ConnectedDevices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        string model = parsedModel;
                        if (string.IsNullOrWhiteSpace(model) && state == "device")
                        {
                            string propModel = (await RunCommandCaptureAsync(adbPath, $"-s {serial} shell getprop ro.product.model", 2500)).Trim();
                            if (!string.IsNullOrWhiteSpace(propModel) && !propModel.Contains("error", StringComparison.OrdinalIgnoreCase))
                            {
                                model = propModel;
                            }
                        }
                        if (string.IsNullOrWhiteSpace(model))
                        {
                            model = state == "unauthorized" ? "هاتف بانتظار السماح (Unauthorized)" : "Android Phone";
                        }

                        long freeBytes = state == "device" ? await GetDeviceFreeSpaceBytesAsync(adbPath, serial) : 0;
                        string initialStatus = state switch
                        {
                            "device" => "جاهز للعمل ✅",
                            "unauthorized" => "⚠️ افتح شاشة الهاتف واضغط (سماح تصحيح USB)",
                            "offline" => "⚠️ جاري تنشيط اتصال الهاتف...",
                            _ => $"حالة الاتصال: {state}"
                        };

                        var deviceItem = new DeviceItem
                        {
                            Serial = serial,
                            ModelName = model,
                            DeviceState = state,
                            IsDisconnected = false,
                            IsSelected = ChkSelectAll.IsChecked == true,
                            FreeSpaceBytes = freeBytes,
                            Status = initialStatus,
                            Progress = 0,
                            SpeedInfo = state == "device" ? "متصل وجاهز" : "بانتظار التفعيل"
                        };
                        deviceItem.EvaluateSpace(_totalFolderBytes, 0);
                        ConnectedDevices.Add(deviceItem);

                        TxtGlobalStatus.Text = state == "device"
                            ? $"🔌 تم التعرف على الهاتف بنجاح: {model} ({serial})"
                            : $"⚠️ تم اكتشاف الهاتف ({serial}) - يرجى الضغط على (سماح / Allow) في شاشة الهاتف!";
                    }
                    else
                    {
                        // CRITICAL: Do NOT touch or query a device while it is actively copying!
                        if (existing.IsBusy)
                        {
                            existing.IsDisconnected = false;
                            existing.DeviceState = state;
                            continue;
                        }

                        bool wasDisconnectedOrNotReady = existing.IsDisconnected || existing.DeviceState != "device";
                        existing.Serial = serial;
                        existing.IsDisconnected = false;
                        existing.DeviceState = state;
                        if (!string.IsNullOrWhiteSpace(parsedModel) && parsedModel != "Android Phone")
                            existing.ModelName = parsedModel;

                        if (state == "unauthorized")
                        {
                            existing.Status = "⚠️ افتح شاشة الهاتف واضغط (سماح تصحيح USB)";
                            existing.SpeedInfo = "بانتظار موافقة الهاتف";
                        }
                        else if (state == "offline")
                        {
                            existing.Status = "⚠️ جاري إعادة تنشيط منفذ الهاتف...";
                        }
                        else if (state == "device")
                        {
                            if (wasDisconnectedOrNotReady || !silent)
                            {
                                existing.IsSelected = true;
                                existing.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, serial);
                                existing.EvaluateSpace(_totalFolderBytes, existing.AlreadyCopiedBytes);
                            }

                            if (wasDisconnectedOrNotReady)
                            {
                                if (existing.NeedsResume && ChkAutoResumeOnReconnect.IsChecked == true)
                                {
                                    existing.Status = "🔄 تمت إعادة التوصيل! جاري الاستئناف التلقائي...";
                                    devicesToAutoResume.Add(existing);
                                }
                                else
                                {
                                    existing.Progress = 0;
                                    existing.Status = "جاهز للعمل ✅";
                                    existing.SpeedInfo = "تمت إعادة التوصيل بنجاح";
                                    TxtGlobalStatus.Text = $"🔌 تمت إعادة توصيل الهاتف والتعرف عليه: {existing.ModelName} ({serial})";
                                }
                            }
                        }
                    }
                }

                int readyCount = ConnectedDevices.Count(d => d.IsOnlineReady);
                int unauthCount = ConnectedDevices.Count(d => !d.IsDisconnected && d.DeviceState == "unauthorized");
                TxtDevicesCount.Text = unauthCount > 0
                    ? $"{readyCount} جاهز ({unauthCount} بانتظار السماح)"
                    : $"{readyCount} جهاز متصل";

                if (!silent)
                {
                    if (readyCount > 0)
                        TxtGlobalStatus.Text = $"✅ تم التعرف على {readyCount} هاتف متصل وجاهز للنسخ.";
                    else if (unauthCount > 0)
                        TxtGlobalStatus.Text = "⚠️ الهاتف متصل بالكمبيوتر، ولكن يجب فتح شاشة الهاتف والضغط على (سماح / Allow USB Debugging)!";
                    else
                        TxtGlobalStatus.Text = "⚠️ لم يتم العثور على أي هاتف متصل! تأكد من تفعيل (تصحيح أخطاء USB) في إعدادات المطور وتوصيل الكابل.";
                }

                if (devicesToAutoResume.Count > 0 && Directory.Exists(TxtSourceFolder.Text.Trim()))
                {
                    foreach (var dev in devicesToAutoResume)
                    {
                        _ = ResumeSingleDeviceAutomaticallyAsync(dev);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show($"خطأ أثناء قراءة الأجهزة: {ex.Message}", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                _isScanningDevices = false;
            }
        }

        private async Task ResumeSingleDeviceAutomaticallyAsync(DeviceItem device)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: false);
            if (adbPath == null) return;

            string sourceFolder = TxtSourceFolder.Text.Trim();
            string targetDest = TxtTargetDest.Text.Trim();
            string apkPath = TxtApkPath.Text.Trim();
            bool enforceSpace = ChkEnforceSpaceCheck.IsChecked == true;
            bool useTarStream = CmbTransferMode.SelectedIndex == 1;

            _cts ??= new CancellationTokenSource();
            BtnCancelAll.IsEnabled = true;
            TxtGlobalStatus.Text = $"🔄 جاري استئناف النسخ تلقائياً للجهاز {device.ModelName} من نقطة التوقف...";

            await ProcessSingleDeviceWorkflowAsync(
                device, adbPath, sourceFolder, targetDest,
                apkPath, installApkFirst: false, enforceSpace: enforceSpace,
                conflictMode: 1, useTarStream: useTarStream, _cts.Token);
        }

        private async Task<long> GetDeviceFreeSpaceBytesAsync(string adbPath, string serial)
        {
            string[] pathsToTry = new[] { "/sdcard", "/storage/emulated/0", "/data" };
            foreach (var p in pathsToTry)
            {
                try
                {
                    string dfOut = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell df -k {p}", 3000);
                    var lines = dfOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase)) continue;
                        var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (cols.Length >= 4 && long.TryParse(cols[3], out long availKb) && availKb > 0)
                        {
                            return availKb * 1024L;
                        }
                    }
                }
                catch { }
            }
            return 0;
        }

        private void UpdateAllDevicesSpaceVerdict()
        {
            foreach (var dev in ConnectedDevices)
            {
                dev.EvaluateSpace(_totalFolderBytes, dev.AlreadyCopiedBytes);
            }
        }

        private void ChkSelectAll_Changed(object sender, RoutedEventArgs e)
        {
            if (ConnectedDevices == null) return;
            bool select = ChkSelectAll.IsChecked == true;
            foreach (var dev in ConnectedDevices)
            {
                dev.IsSelected = select;
            }
        }

        private async void BtnInstallApkOnly_Click(object sender, RoutedEventArgs e)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            string apkPath = TxtApkPath.Text.Trim();
            if (!File.Exists(apkPath))
            {
                MessageBox.Show("يرجى اختيار ملف APK صحيح أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedDevices = ConnectedDevices.Where(d => d.IsSelected && d.IsOnlineReady).ToList();
            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("يرجى التأكد من توصيل الهاتف والموافقة على تصحيح USB وتحديده من القائمة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetButtonsEnabled(false);
            TxtGlobalStatus.Text = "📲 جاري تثبيت ملف الـ APK على جميع الأجهزة المحددة...";

            var tasks = selectedDevices.Select(dev => InstallApkOnDeviceAsync(dev, adbPath, apkPath, CancellationToken.None));
            await Task.WhenAll(tasks);

            SetButtonsEnabled(true);
            TxtGlobalStatus.Text = "اكتملت عملية تثبيت الـ APK على الأجهزة المحددة ✅";
        }

        private async Task<bool> InstallApkOnDeviceAsync(DeviceItem device, string adbPath, string apkPath, CancellationToken token)
        {
            device.Status = "📲 جاري تثبيت الـ APK...";
            device.SpeedInfo = Path.GetFileName(apkPath);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"-s {device.Serial} install -r \"{apkPath}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                var stdOutTask = proc.StandardOutput.ReadToEndAsync();
                var stdErrTask = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(token);
                string stdOut = await stdOutTask;
                string stdErr = await stdErrTask;

                if (proc.ExitCode == 0 && stdOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
                {
                    device.Status = "تم تثبيت الـ APK بنجاح ✅";
                    return true;
                }
                else
                {
                    string err = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                    device.Status = $"فشل تثبيت APK: {err.Trim()}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                device.Status = $"خطأ APK: {ex.Message}";
                return false;
            }
        }

        private async void BtnCopyDataOnly_Click(object sender, RoutedEventArgs e)
        {
            CmbOperationMode.SelectedIndex = 0;
            await ExecuteBatchOperationAsync(forceResumeMode: false);
        }

        private async void BtnInstallAndCopy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtApkPath.Text) || !File.Exists(TxtApkPath.Text.Trim()))
            {
                MessageBox.Show("يرجى اختيار ملف الـ APK أولاً لتثبيته قبل النسخ، أو اضغط على زر (📁 نسخ فقط).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CmbOperationMode.SelectedIndex = 1;
            await ExecuteBatchOperationAsync(forceResumeMode: false);
        }

        private async void BtnResumeCopy_Click(object sender, RoutedEventArgs e)
        {
            CmbConflictMode.SelectedIndex = 1;
            await ExecuteBatchOperationAsync(forceResumeMode: true);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            BtnCopyDataOnly.IsEnabled = enabled;
            BtnInstallAndCopy.IsEnabled = enabled;
            BtnResumeCopy.IsEnabled = enabled;
            BtnInstallApkOnly.IsEnabled = enabled;
            BtnBrowsePhoneDest.IsEnabled = enabled;
            BtnCancelAll.IsEnabled = !enabled;
        }

        private async Task ExecuteBatchOperationAsync(bool forceResumeMode)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            string sourceFolder = TxtSourceFolder.Text.Trim();
            string targetDest = PhoneFolderBrowserDialog.NormalizeRemotePath(TxtTargetDest.Text);
            TxtTargetDest.Text = targetDest;
            string apkPath = TxtApkPath.Text.Trim();

            if (!Directory.Exists(sourceFolder))
            {
                MessageBox.Show("يرجى اختيار المجلد المصدر من الكمبيوتر أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedDevices = ConnectedDevices.Where(d => d.IsSelected && d.IsOnlineReady).ToList();
            if (selectedDevices.Count == 0)
            {
                await ScanAndSyncDevicesAsync(adbPath, silent: false);
                selectedDevices = ConnectedDevices.Where(d => d.IsSelected && d.IsOnlineReady).ToList();
            }

            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("لا يوجد هاتف متصل وجاهز حالياً في القائمة!\nيرجى توصيل الهاتف والموافقة على رسالة (سماح تصحيح أخطاء USB) على شاشة الهاتف.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtGlobalStatus.Text = "⏳ جاري فحص وحساب حجم الملفات في المجلد المصدر...";
            _totalFolderBytes = await Task.Run(() => CalculateDirectorySize(new DirectoryInfo(sourceFolder)));
            double sizeGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
            TxtFolderSizeInfo.Text = $"{sizeGb:F2} GB";
            UpdateAllDevicesSpaceVerdict();

            bool installApkFirst = !forceResumeMode && (CmbOperationMode.SelectedIndex == 1) && File.Exists(apkPath);
            bool enforceSpace = ChkEnforceSpaceCheck.IsChecked == true;
            int conflictMode = forceResumeMode ? 1 : CmbConflictMode.SelectedIndex;
            bool useTarStream = CmbTransferMode.SelectedIndex == 1;

            _cts = new CancellationTokenSource();
            SetButtonsEnabled(false);

            TxtGlobalStatus.Text = forceResumeMode
                ? $"⏯ جاري فحص الملفات المنسوخة سابقاً واستئناف النسخ إلى ({targetDest}) لـ {selectedDevices.Count} أجهزة..."
                : $"🚀 جاري نسخ المجلد إلى ({targetDest}) على {selectedDevices.Count} أجهزة...";

            var tasks = selectedDevices.Select(device =>
                ProcessSingleDeviceWorkflowAsync(
                    device, adbPath, sourceFolder, targetDest,
                    apkPath, installApkFirst, enforceSpace,
                    conflictMode, useTarStream, _cts.Token)
            ).ToList();

            await Task.WhenAll(tasks);

            SetButtonsEnabled(true);
            TxtGlobalStatus.Text = "اكتملت العمليات للأجهزة المحددة!";
        }

        private async Task ProcessSingleDeviceWorkflowAsync(
            DeviceItem device, string adbPath, string sourceFolder, string targetDest,
            string apkPath, bool installApkFirst, bool enforceSpace,
            int conflictMode, bool useTarStream, CancellationToken token)
        {
            device.IsBusy = true;
            Interlocked.Increment(ref _activeTransferCount);
            try
            {
                var sourceDirInfo = new DirectoryInfo(sourceFolder);
                string folderName = sourceDirInfo.Name;
                string normalizedTarget = PhoneFolderBrowserDialog.NormalizeRemotePath(targetDest);

                string targetTrimmed = normalizedTarget.TrimEnd('/');
                string fullRemoteFolderPath;
                string baseDestParent;

                if (targetTrimmed.EndsWith("/" + folderName, StringComparison.OrdinalIgnoreCase))
                {
                    fullRemoteFolderPath = targetTrimmed;
                    int lastSlash = targetTrimmed.LastIndexOf('/');
                    baseDestParent = targetTrimmed.Substring(0, lastSlash + 1);
                }
                else
                {
                    baseDestParent = normalizedTarget;
                    fullRemoteFolderPath = $"{normalizedTarget}{folderName}";
                }

                string sqFullRemote = PhoneFolderBrowserDialog.EscapeSingleQuotes(fullRemoteFolderPath);
                string sqBaseParent = PhoneFolderBrowserDialog.EscapeSingleQuotes(baseDestParent);

                await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"mkdir -p '{sqBaseParent}'\"", 5000);

                if (conflictMode == 0)
                {
                    device.Status = "🗑 جاري حذف النسخة القديمة للاستبدال...";
                    device.AlreadyCopiedBytes = 0;
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"rm -rf '{sqFullRemote}'\"", 20000);
                }
                else if (conflictMode == 3)
                {
                    device.Status = "✏️ جاري إعادة تسمية المجلد القديم...";
                    device.AlreadyCopiedBytes = 0;
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string sqRenamed = PhoneFolderBrowserDialog.EscapeSingleQuotes($"{fullRemoteFolderPath}_old_{timestamp}");
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"if [ -d '{sqFullRemote}' ]; then mv '{sqFullRemote}' '{sqRenamed}'; fi\"", 5000);
                }

                Dictionary<string, long> remoteExistingFiles = new Dictionary<string, long>(StringComparer.Ordinal);
                if (conflictMode == 1)
                {
                    device.Status = "🔍 جاري فحص نقطة التوقف والملفات المنسوخة مسبقاً...";
                    remoteExistingFiles = await GetRemoteExistingFilesAsync(adbPath, device.Serial, baseDestParent, folderName);
                }

                device.Status = "📋 جاري تجهيز قائمة الملفات للنقل...";
                var resumePlan = await Task.Run(() => BuildTransferPlan(sourceFolder, remoteExistingFiles, conflictMode == 1));
                device.AlreadyCopiedBytes = resumePlan.AlreadyCopiedBytes;

                device.Status = "🔍 جاري التحقق من المساحة المتوفرة...";
                device.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, device.Serial);
                Dispatcher.Invoke(() => device.EvaluateSpace(_totalFolderBytes, device.AlreadyCopiedBytes));

                long remainingBytesNeeded = Math.Max(0, _totalFolderBytes - device.AlreadyCopiedBytes);
                if (enforceSpace && device.FreeSpaceBytes > 0 && device.FreeSpaceBytes < remainingBytesNeeded)
                {
                    device.Status = "❌ توقف: المساحة المتوفرة بالهاتف غير كافية!";
                    return;
                }

                if (installApkFirst)
                {
                    bool apkOk = await InstallApkOnDeviceAsync(device, adbPath, apkPath, token);
                    if (!apkOk && token.IsCancellationRequested) return;
                }

                if (resumePlan.TotalLocalFilesCount == 0)
                {
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"mkdir -p '{sqFullRemote}'\"", 5000);
                    device.Progress = 100;
                    device.Status = $"⚠️ المجلد المصدر فارغ! تم إنشاء {fullRemoteFolderPath}";
                    return;
                }

                if (conflictMode == 1 && resumePlan.MissingFiles.Count == 0 && _totalFolderBytes > 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = $"مكتمل مسبقاً 100% داخل {fullRemoteFolderPath} ✅";
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                    device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB (مكتمل)";
                    return;
                }

                if (useTarStream)
                {
                    bool tarSuccess = await StreamCustomTarArchiveAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, resumePlan, token);
                    if (!tarSuccess && !token.IsCancellationRequested)
                    {
                        device.Status = "🔄 جاري إكمال النقل تلقائياً عبر ADB Push المضمون...";
                        var fallbackExisting = await GetRemoteExistingFilesAsync(adbPath, device.Serial, baseDestParent, folderName);
                        var fallbackPlan = await Task.Run(() => BuildTransferPlan(sourceFolder, fallbackExisting, isResumeMode: true));
                        await AdbPushZeroShellContinuousAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, fallbackPlan, token);
                    }
                }
                else
                {
                    await AdbPushZeroShellContinuousAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, resumePlan, token);
                }

                device.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, device.Serial);
                Dispatcher.Invoke(() => device.EvaluateSpace(_totalFolderBytes, device.AlreadyCopiedBytes));
            }
            catch (OperationCanceledException)
            {
                device.NeedsResume = true;
                device.Status = "⏸ توقف مؤقت - جاهز للاستئناف من نفس النقطة";
            }
            catch (Exception ex)
            {
                device.NeedsResume = true;
                device.Status = $"⚠️ توقف ({ex.Message}) - جاهز للاستئناف";
            }
            finally
            {
                device.IsBusy = false;
                Interlocked.Decrement(ref _activeTransferCount);
            }
        }

        private async Task<int> CountRemoteFilesAsync(string adbPath, string serial, string fullRemoteFolderPath)
        {
            try
            {
                string sq = PhoneFolderBrowserDialog.EscapeSingleQuotes(fullRemoteFolderPath);
                string output = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell \"find '{sq}' -type f 2>/dev/null\"", 10000);
                if (string.IsNullOrWhiteSpace(output)) return 0;
                int count = 0;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("find:", StringComparison.OrdinalIgnoreCase) &&
                        !t.Contains("No such file", StringComparison.OrdinalIgnoreCase) &&
                        !t.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<Dictionary<string, long>> GetRemoteExistingFilesAsync(string adbPath, string serial, string baseDestParent, string folderName)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                string sqFolder = PhoneFolderBrowserDialog.EscapeSingleQuotes($"{baseDestParent}{folderName}");
                string cmd = $"-s {serial} shell \"find '{sqFolder}' -type f -exec stat -c '%s|%n' {{}} + 2>/dev/null\"";
                string output = await RunCommandCaptureAsync(adbPath, cmd, 10000);
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    int sep = line.IndexOf('|');
                    if (sep > 0)
                    {
                        string sizeStr = line.Substring(0, sep).Trim();
                        string fullPath = line.Substring(sep + 1).Trim().Replace('\\', '/');
                        if (fullPath.StartsWith(baseDestParent, StringComparison.Ordinal))
                        {
                            string relPath = fullPath.Substring(baseDestParent.Length).TrimStart('/');
                            if (long.TryParse(sizeStr, out long sz) && sz > 0)
                            {
                                map[relPath] = sz;
                            }
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private class TransferResumePlan
        {
            public int TotalLocalFilesCount { get; set; }
            public long AlreadyCopiedBytes { get; set; }
            public List<(FileInfo LocalFile, string RelativePath)> MissingFiles { get; set; } = new List<(FileInfo, string)>();
            public List<string> AllRelativeDirectories { get; set; } = new List<string>();
        }

        private TransferResumePlan BuildTransferPlan(string sourceFolder, Dictionary<string, long> remoteFiles, bool isResumeMode)
        {
            var plan = new TransferResumePlan();
            var rootDir = new DirectoryInfo(sourceFolder);
            string rootFolderName = rootDir.Name;

            foreach (var dir in EnumerateDirectoriesSafe(rootDir))
            {
                string innerDirRel = Path.GetRelativePath(rootDir.FullName, dir.FullName).Replace('\\', '/');
                string relDir = innerDirRel == "." ? rootFolderName : $"{rootFolderName}/{innerDirRel}";
                plan.AllRelativeDirectories.Add(relDir);
            }

            var allFiles = EnumerateFilesSafe(rootDir);
            foreach (FileInfo fi in allFiles)
            {
                plan.TotalLocalFilesCount++;
                string innerRel = Path.GetRelativePath(rootDir.FullName, fi.FullName).Replace('\\', '/');
                string relPath = $"{rootFolderName}/{innerRel}";

                if (isResumeMode && remoteFiles.TryGetValue(relPath, out long remoteSize) && remoteSize == fi.Length && fi.Length > 0)
                {
                    plan.AlreadyCopiedBytes += fi.Length;
                }
                else
                {
                    plan.MissingFiles.Add((fi, relPath));
                }
            }

            return plan;
        }

        private async Task CreateRemoteDirectoriesBatchAsync(string adbPath, string serial, string baseDestParent, List<string> relativeDirs, CancellationToken token)
        {
            if (relativeDirs.Count == 0) return;

            var batch = new List<string>();
            int currentCmdLen = 0;

            foreach (var relDir in relativeDirs)
            {
                token.ThrowIfCancellationRequested();
                string fullRemoteDir = $"{baseDestParent}{relDir}";
                string sq = $"'{PhoneFolderBrowserDialog.EscapeSingleQuotes(fullRemoteDir)}'";
                batch.Add(sq);
                currentCmdLen += sq.Length + 1;

                if (currentCmdLen > 2500)
                {
                    string joined = string.Join(" ", batch);
                    await RunCommandCaptureAsync(adbPath, $"-s {serial} shell \"mkdir -p {joined}\"", 8000);
                    batch.Clear();
                    currentCmdLen = 0;
                }
            }

            if (batch.Count > 0)
            {
                string joined = string.Join(" ", batch);
                await RunCommandCaptureAsync(adbPath, $"-s {serial} shell \"mkdir -p {joined}\"", 8000);
            }
        }

        // =========================================================================
        // METHOD 1: GUARANTEED CONTINUOUS ADB PUSH (Zero background shell interference!)
        // =========================================================================
        private async Task AdbPushZeroShellContinuousAsync(
            DeviceItem device, string adbPath, string sourceFolder, string baseDestParent, string fullRemoteFolderPath,
            TransferResumePlan resumePlan, CancellationToken token)
        {
            Dispatcher.Invoke(() =>
            {
                device.Status = "📂 جاري تهيئة هيكل المجلدات على الهاتف...";
            });

            await CreateRemoteDirectoriesBatchAsync(adbPath, device.Serial, baseDestParent, resumePlan.AllRelativeDirectories, token);

            long totalCopied = resumePlan.AlreadyCopiedBytes;
            long sessionBytes = 0;
            long lastReportedSessionBytes = 0;
            double lastReportTime = 0;
            var sw = Stopwatch.StartNew();

            double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied * 100.0) / _totalFolderBytes) : 0;
            Dispatcher.Invoke(() =>
            {
                device.Progress = Math.Max(0.5, initialPercent);
                device.Status = totalCopied > 0
                    ? $"⏯ استئناف النقل المضمون من {initialPercent:F1}%..."
                    : $"📁 جاري النسخ المباشر المضمون ({resumePlan.MissingFiles.Count} ملف)...";
            });

            int totalFilesToCopy = resumePlan.MissingFiles.Count;
            int currentIdx = 0;

            foreach (var (fi, relPath) in resumePlan.MissingFiles)
            {
                token.ThrowIfCancellationRequested();
                currentIdx++;

                if (!fi.Exists) continue;

                string localFile = fi.FullName;
                string remoteFile = $"{baseDestParent}{relPath}";

                Dispatcher.Invoke(() =>
                {
                    device.Status = $"📁 نسخ ({currentIdx}/{totalFilesToCopy}): {fi.Name}";
                });

                bool pushedOk = false;
                string lastError = "";

                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();

                    var psi = new ProcessStartInfo
                    {
                        FileName = adbPath,
                        Arguments = $"-s {device.Serial} push \"{localFile}\" \"{remoteFile}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi)!;
                    var stdOutTask = proc.StandardOutput.ReadToEndAsync();
                    var stdErrTask = proc.StandardError.ReadToEndAsync();

                    long fileLen = fi.Length;
                    long baseCopiedBeforeFile = totalCopied;
                    long baseSessionBeforeFile = sessionBytes;

                    // Pure time-interpolated UI update during large single-file push — NO concurrent adb shell commands!
                    while (!proc.HasExited)
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { proc.Kill(entireProcessTree: true); } catch { }
                            token.ThrowIfCancellationRequested();
                        }

                        await Task.Delay(250, token);

                        if (fileLen > 2 * 1024 * 1024 && !proc.HasExited)
                        {
                            double el = sw.Elapsed.TotalSeconds;
                            double avgSpeedBytesPerSec = el > 0.5 && sessionBytes > 0
                                ? sessionBytes / el
                                : 18.0 * 1024 * 1024; // Estimate 18 MB/s on first large file

                            double fileElapsed = Math.Max(0.1, el - lastReportTime);
                            long estimatedInFlight = (long)Math.Min(fileLen * 0.94, fileElapsed * avgSpeedBytesPerSec);
                            long estTotal = baseCopiedBeforeFile + estimatedInFlight;
                            double pct = _totalFolderBytes > 0 ? Math.Min(99.5, (estTotal * 100.0) / _totalFolderBytes) : 50;
                            double copiedGb = Math.Min(_totalFolderBytes, estTotal) / (1024.0 * 1024.0 * 1024.0);
                            double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                            double mbps = avgSpeedBytesPerSec / (1024.0 * 1024.0);

                            Dispatcher.Invoke(() =>
                            {
                                if (pct > device.Progress) device.Progress = pct;
                                device.SpeedInfo = $"{copiedGb:F2}/{totalGb:F2} GB (~{mbps:F1} MB/s)";
                            });
                        }
                    }

                    string stdOut = await stdOutTask;
                    string stdErr = await stdErrTask;

                    if (proc.ExitCode == 0)
                    {
                        pushedOk = true;
                        break;
                    }

                    lastError = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                    if (attempt == 1)
                    {
                        int lastSlash = remoteFile.LastIndexOf('/');
                        if (lastSlash > 0)
                        {
                            string sqParent = PhoneFolderBrowserDialog.EscapeSingleQuotes(remoteFile.Substring(0, lastSlash));
                            await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"mkdir -p '{sqParent}'\"", 4000);
                        }
                        await Task.Delay(300, token);
                    }
                }

                if (!pushedOk)
                {
                    device.NeedsResume = true;
                    device.Status = $"⚠️ توقف عند {fi.Name}: {lastError.Trim()} (اضغط استئناف)";
                    return;
                }

                totalCopied += fi.Length;
                sessionBytes += fi.Length;
                device.AlreadyCopiedBytes = totalCopied;

                double elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed - lastReportTime >= 0.15 || currentIdx == totalFilesToCopy)
                {
                    UpdateDeviceProgressUI(device, totalCopied, sessionBytes, lastReportedSessionBytes, elapsed, lastReportTime);
                    lastReportedSessionBytes = sessionBytes;
                    lastReportTime = elapsed;
                }
            }

            int verifiedCount = await CountRemoteFilesAsync(adbPath, device.Serial, fullRemoteFolderPath);
            if (verifiedCount > 0)
            {
                device.Progress = 100;
                device.NeedsResume = false;
                device.Status = $"✅ تم نسخ {verifiedCount} ملف بنجاح إلى: {fullRemoteFolderPath}";
                double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB ({verifiedCount} ملف مكتمل)";
            }
            else
            {
                device.NeedsResume = true;
                device.Status = $"❌ تعذر التحقق من الملفات في {fullRemoteFolderPath}! جرب النسخ إلى /sdcard/Download/";
            }
        }

        // =========================================================================
        // METHOD 2: BUILT-IN PURE C# USTAR STREAMER (No Windows tar.exe dependency!)
        // =========================================================================
        private async Task<bool> StreamCustomTarArchiveAsync(
            DeviceItem device, string adbPath, string sourceFolder, string baseDestParent, string fullRemoteFolderPath,
            TransferResumePlan resumePlan, CancellationToken token)
        {
            await CreateRemoteDirectoriesBatchAsync(adbPath, device.Serial, baseDestParent, resumePlan.AllRelativeDirectories, token);

            long totalCopied = resumePlan.AlreadyCopiedBytes;
            long sessionBytes = 0;
            long lastReportedBytes = 0;
            double lastReportTime = 0;
            var sw = Stopwatch.StartNew();

            double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied * 100.0) / _totalFolderBytes) : 0;
            Dispatcher.Invoke(() =>
            {
                device.Progress = Math.Max(0.5, initialPercent);
                device.Status = totalCopied > 0
                    ? $"⏯ استئناف بث TAR من {initialPercent:F1}%..."
                    : "⚡ جاري البث المباشر فائق السرعة (TAR Stream)...";
            });

            string sqBaseParent = PhoneFolderBrowserDialog.EscapeSingleQuotes(baseDestParent);
            var adbPsi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {device.Serial} exec-in \"tar -xf - -C '{sqBaseParent}'\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var adbProcess = new Process { StartInfo = adbPsi };
            try
            {
                adbProcess.Start();
                var errTask = adbProcess.StandardError.ReadToEndAsync();
                var outTask = adbProcess.StandardOutput.ReadToEndAsync();

                using Stream destStream = adbProcess.StandardInput.BaseStream;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                byte[] zeroPadding = new byte[512];

                try
                {
                    int totalFiles = resumePlan.MissingFiles.Count;
                    int idx = 0;

                    foreach (var (fi, relPath) in resumePlan.MissingFiles)
                    {
                        token.ThrowIfCancellationRequested();
                        idx++;
                        if (!fi.Exists) continue;

                        Dispatcher.Invoke(() =>
                        {
                            device.Status = $"⚡ بث TAR ({idx}/{totalFiles}): {fi.Name}";
                        });

                        byte[] header = CreatePosixTarHeader(relPath, fi.Length, fi.LastWriteTimeUtc);
                        await destStream.WriteAsync(header, 0, 512, token);

                        long remaining = fi.Length;
                        using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, useAsync: true))
                        {
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(buffer.Length, remaining);
                                int read = await fs.ReadAsync(buffer, 0, toRead, token);
                                if (read <= 0) break;

                                await destStream.WriteAsync(buffer, 0, read, token);
                                remaining -= read;
                                totalCopied += read;
                                sessionBytes += read;
                                device.AlreadyCopiedBytes = totalCopied;

                                double elapsedSec = sw.Elapsed.TotalSeconds;
                                if (elapsedSec - lastReportTime >= 0.2)
                                {
                                    UpdateDeviceProgressUI(device, totalCopied, sessionBytes, lastReportedBytes, elapsedSec, lastReportTime);
                                    lastReportedBytes = sessionBytes;
                                    lastReportTime = elapsedSec;
                                }
                            }
                        }

                        // If file shrank while reading, pad remaining bytes with zeros so TAR stream stays aligned
                        while (remaining > 0)
                        {
                            int pad = (int)Math.Min(512, remaining);
                            await destStream.WriteAsync(zeroPadding, 0, pad, token);
                            remaining -= pad;
                        }

                        int remainder512 = (int)(fi.Length % 512);
                        if (remainder512 > 0)
                        {
                            int pad512 = 512 - remainder512;
                            await destStream.WriteAsync(zeroPadding, 0, pad512, token);
                        }
                    }

                    // Write two 512-byte zero blocks to mark EOF of TAR archive
                    await destStream.WriteAsync(zeroPadding, 0, 512, token);
                    await destStream.WriteAsync(zeroPadding, 0, 512, token);
                    await destStream.FlushAsync(token);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                destStream.Close();
                await adbProcess.WaitForExitAsync(token);
                string _ = await errTask;
                string __ = await outTask;

                int verifiedCount = await CountRemoteFilesAsync(adbPath, device.Serial, fullRemoteFolderPath);
                if (adbProcess.ExitCode == 0 && verifiedCount >= resumePlan.MissingFiles.Count)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = $"✅ تم بث ونسخ {verifiedCount} ملف بنجاح إلى: {fullRemoteFolderPath}";
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                    device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB ({verifiedCount} ملف مكتمل)";
                    return true;
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                try { adbProcess.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch
            {
                try { adbProcess.Kill(entireProcessTree: true); } catch { }
                return false;
            }
        }

        private static byte[] CreatePosixTarHeader(string relativePath, long fileSize, DateTime lastModifiedUtc)
        {
            byte[] header = new byte[512];
            string cleanPath = relativePath.Replace('\\', '/').TrimStart('/');

            byte[] pathBytes = Encoding.UTF8.GetBytes(cleanPath);
            if (pathBytes.Length <= 100)
            {
                Array.Copy(pathBytes, 0, header, 0, pathBytes.Length);
            }
            else
            {
                // Split into prefix (up to 155 bytes at offset 345) and name (up to 100 bytes at offset 0)
                int splitSlash = cleanPath.LastIndexOf('/');
                while (splitSlash > 0 && Encoding.UTF8.GetByteCount(cleanPath.Substring(splitSlash + 1)) > 100)
                {
                    splitSlash = cleanPath.LastIndexOf('/', splitSlash - 1);
                }

                if (splitSlash > 0)
                {
                    string prefix = cleanPath.Substring(0, splitSlash);
                    string name = cleanPath.Substring(splitSlash + 1);
                    byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                    byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
                    Array.Copy(nameBytes, 0, header, 0, Math.Min(100, nameBytes.Length));
                    Array.Copy(prefixBytes, 0, header, 345, Math.Min(155, prefixBytes.Length));
                }
                else
                {
                    Array.Copy(pathBytes, 0, header, 0, 100);
                }
            }

            WriteOctalString(header, 100, 8, 420); // 0644 permissions (decimal 420)
            WriteOctalString(header, 108, 8, 0);   // uid
            WriteOctalString(header, 116, 8, 0);   // gid

            if (fileSize < 8589934591L) // < 8 GB fits in standard octal
            {
                WriteOctalString(header, 124, 12, fileSize);
            }
            else
            {
                // Base-256 extension for files >= 8 GB
                header[124] = 0x80;
                long val = fileSize;
                for (int i = 135; i >= 125; i--)
                {
                    header[i] = (byte)(val & 0xFF);
                    val >>= 8;
                }
            }

            long unixTime = Math.Max(0, (long)(lastModifiedUtc - DateTime.UnixEpoch).TotalSeconds);
            WriteOctalString(header, 136, 12, unixTime);

            // Fill checksum field with spaces initially
            for (int i = 148; i < 156; i++) header[i] = 0x20;

            header[156] = (byte)'0'; // Regular file

            // ustar magic + version
            byte[] magic = Encoding.ASCII.GetBytes("ustar\00000");
            Array.Copy(magic, 0, header, 257, magic.Length);

            long checksum = 0;
            for (int i = 0; i < 512; i++) checksum += header[i];

            string chkOctal = Convert.ToString(checksum, 8).PadLeft(6, '0');
            byte[] chkBytes = Encoding.ASCII.GetBytes(chkOctal);
            Array.Copy(chkBytes, 0, header, 148, 6);
            header[154] = 0;
            header[155] = 0x20;

            return header;
        }

        private static void WriteOctalString(byte[] buffer, int offset, int length, long value)
        {
            string octal = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            byte[] bytes = Encoding.ASCII.GetBytes(octal);
            int copyLen = Math.Min(length - 1, bytes.Length);
            Array.Copy(bytes, bytes.Length - copyLen, buffer, offset, copyLen);
            buffer[offset + length - 1] = 0;
        }

        private void UpdateDeviceProgressUI(DeviceItem device, long totalCopied, long sessionBytes, long lastReportedBytes, double elapsedSec, double lastReportTime)
        {
            double speedMbps = ((sessionBytes - lastReportedBytes) / (1024.0 * 1024.0)) / Math.Max(0.05, elapsedSec - lastReportTime);
            double copiedGb = Math.Min(_totalFolderBytes, totalCopied) / (1024.0 * 1024.0 * 1024.0);
            double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
            double percent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied * 100.0) / _totalFolderBytes) : 50;

            Dispatcher.Invoke(() =>
            {
                device.Progress = percent;
                device.SpeedInfo = $"{copiedGb:F2}/{totalGb:F2} GB ({Math.Max(0.1, speedMbps):F1} MB/s)";
            });
        }

        private void BtnCancelAll_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancelAll.IsEnabled = false;
            TxtGlobalStatus.Text = "⏸ تم إيقاف العمليات، يمكنك الضغط على (⏯ استئناف النسخ من نقطة التوقف) في أي وقت للإكمال.";
        }

        private async Task<string> RunCommandCaptureAsync(string fileName, string args, int timeoutMs = 8000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "";
            }

            string stdout = await outTask;
            string stderr = await errTask;
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
    }

    public class DeviceItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _serial = "";
        private string _modelName = "";
        private string _deviceState = "device";
        private long _freeSpaceBytes;
        private long _alreadyCopiedBytes;
        private bool _isDisconnected;
        private bool _needsResume;
        private bool _isBusy;
        private string _freeSpaceText = "غير معروف";
        private string _spaceVerdict = "بانتظار تحديد المجلد";
        private string _spaceStatusColor = "#38BDF8";
        private double _progress;
        private string _status = "";
        private string _speedInfo = "";

        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
        public string Serial { get => _serial; set { _serial = value; OnPropertyChanged(); } }
        public string ModelName { get => _modelName; set { _modelName = value; OnPropertyChanged(); } }
        public string DeviceState { get => _deviceState; set { _deviceState = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsOnlineReady)); } }
        public bool IsOnlineReady => !IsDisconnected && DeviceState == "device";
        public long AlreadyCopiedBytes { get => _alreadyCopiedBytes; set { _alreadyCopiedBytes = value; OnPropertyChanged(); } }
        public bool IsDisconnected { get => _isDisconnected; set { _isDisconnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsOnlineReady)); } }
        public bool NeedsResume { get => _needsResume; set { _needsResume = value; OnPropertyChanged(); } }
        public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }

        public long FreeSpaceBytes
        {
            get => _freeSpaceBytes;
            set
            {
                _freeSpaceBytes = value;
                if (_freeSpaceBytes > 0)
                {
                    double freeGb = _freeSpaceBytes / (1024.0 * 1024.0 * 1024.0);
                    FreeSpaceText = $"المساحة الحرة: {freeGb:F2} GB";
                }
                else
                {
                    FreeSpaceText = "المساحة: متاحة";
                }
                OnPropertyChanged();
            }
        }

        public string FreeSpaceText { get => _freeSpaceText; set { _freeSpaceText = value; OnPropertyChanged(); } }
        public string SpaceVerdict { get => _spaceVerdict; set { _spaceVerdict = value; OnPropertyChanged(); } }
        public string SpaceStatusColor { get => _spaceStatusColor; set { _spaceStatusColor = value; OnPropertyChanged(); } }

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }
        public string ProgressText => $"{Progress:F1}%";
        public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }
        public string SpeedInfo { get => _speedInfo; set { _speedInfo = value; OnPropertyChanged(); } }

        public void EvaluateSpace(long totalRequiredBytes, long alreadyCopiedBytes = 0)
        {
            if (DeviceState == "unauthorized")
            {
                SpaceVerdict = "بانتظار السماح بالهاتف ⚠️";
                SpaceStatusColor = "#F59E0B";
                return;
            }

            if (FreeSpaceBytes <= 0)
            {
                SpaceVerdict = "متصل وجاهز ✅";
                SpaceStatusColor = "#38BDF8";
                return;
            }

            if (totalRequiredBytes <= 0)
            {
                SpaceVerdict = "جاهز للنسخ ✅";
                SpaceStatusColor = "#10B981";
                return;
            }

            long netNeededBytes = Math.Max(0, totalRequiredBytes - alreadyCopiedBytes);

            if (FreeSpaceBytes >= netNeededBytes)
            {
                double diffGb = (FreeSpaceBytes - netNeededBytes) / (1024.0 * 1024.0 * 1024.0);
                SpaceVerdict = $"كافية (يتبقى {diffGb:F1} GB) ✅";
                SpaceStatusColor = "#10B981";
            }
            else
            {
                double shortageGb = (netNeededBytes - FreeSpaceBytes) / (1024.0 * 1024.0 * 1024.0);
                SpaceVerdict = $"غير كافية (ينقص {shortageGb:F1} GB) ❌";
                SpaceStatusColor = "#EF4444";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
