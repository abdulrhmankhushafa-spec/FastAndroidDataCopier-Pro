using System;
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
                TxtAdbStatusBadge.Text = "❌ أداة ADB غير موجودة (اضغط لتحديدها مرة واحدة)";
                TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtGlobalStatus.Text = "⚠️ لم يتم العثور على adb.exe تلقائياً! يرجى وضع مجلد adb في C:\\adb أو بجوار البرنامج.";
                return;
            }

            _resolvedAdbPath = adb;
            TxtAdbStatusBadge.Text = $"✅ محرك ADB نشط ({ adb })";
            TxtAdbStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));

            if (!_adbServerStarted)
            {
                _adbServerStarted = true;
                await RunCommandCaptureAsync(_resolvedAdbPath, "start-server");
            }

            await ScanAndSyncDevicesAsync(_resolvedAdbPath, silent: false);
        }

        private string? ResolveFixedAdbPath()
        {
            // 1. Check saved path in AppData if previously located
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

            // 2. Check all standard fixed locations
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

            // 3. Check system PATH environment variable
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
                TxtAdbStatusBadge.Text = $"✅ محرك ADB نشط ({found})";
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

                    TxtAdbStatusBadge.Text = $"✅ محرك ADB نشط ({_resolvedAdbPath})";
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
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            BtnRestartAdbServer.IsEnabled = false;
            TxtGlobalStatus.Text = "🛠 جاري إعادة تشغيل خادم ADB وإعادة فحص منافذ USB...";
            try
            {
                await RunCommandCaptureAsync(adbPath, "kill-server");
                await Task.Delay(400);
                await RunCommandCaptureAsync(adbPath, "start-server");
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

        private long CalculateDirectorySize(DirectoryInfo dir)
        {
            long size = 0;
            try
            {
                foreach (FileInfo fi in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    size += fi.Length;
                }
            }
            catch { }
            return size;
        }

        private async void BtnRefreshDevices_Click(object sender, RoutedEventArgs e)
        {
            string? adbPath = EnsureAdbPath(promptIfMissing: true);
            if (adbPath == null) return;

            BtnRefreshDevices.IsEnabled = false;
            TxtGlobalStatus.Text = "🔍 جاري فحص منافذ USB والتعرف على الهواتف المتصلة...";
            await ScanAndSyncDevicesAsync(adbPath, silent: false);
            BtnRefreshDevices.IsEnabled = true;
        }

        private async Task ScanAndSyncDevicesAsync(string adbPath, bool silent)
        {
            if (_isScanningDevices) return;
            _isScanningDevices = true;

            try
            {
                string output = await RunCommandCaptureAsync(adbPath, "devices -l");
                var rawLines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var currentSeenSerials = new HashSet<string>(StringComparer.Ordinal);
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
                        string serial = parts[0];
                        string state = parts[1].ToLowerInvariant(); // device, unauthorized, offline, recovery, sideload

                        if (state != "device" && state != "unauthorized" && state != "offline" && state != "recovery")
                            continue;

                        string model = "";
                        var modelPart = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.OrdinalIgnoreCase));
                        if (modelPart != null)
                        {
                            model = modelPart.Substring(6).Replace("_", " ").Trim();
                        }

                        if (string.IsNullOrWhiteSpace(model) && state == "device")
                        {
                            string propModel = (await RunCommandCaptureAsync(adbPath, $"-s {serial} shell getprop ro.product.model")).Trim();
                            if (!string.IsNullOrWhiteSpace(propModel) && !propModel.Contains("error", StringComparison.OrdinalIgnoreCase))
                            {
                                model = propModel;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(model))
                        {
                            model = state == "unauthorized" ? "هاتف بانتظار السماح (Unauthorized)" : "Android Phone";
                        }

                        currentSeenSerials.Add(serial);
                        parsedDevices.Add((serial, state, model));
                    }
                }

                foreach (var existing in ConnectedDevices.ToList())
                {
                    if (!currentSeenSerials.Contains(existing.Serial))
                    {
                        if (!existing.IsDisconnected)
                        {
                            existing.IsDisconnected = true;
                            existing.DeviceState = "disconnected";
                            if (existing.IsBusy || (existing.Progress > 0 && existing.Progress < 100))
                            {
                                existing.NeedsResume = true;
                                existing.Status = "⚠️ انفصل الكابل! أعد توصيله للاستئناف التلقائي";
                            }
                            else
                            {
                                existing.Status = "🔌 غير متصل حالياً";
                            }
                        }
                    }
                }

                var devicesToAutoResume = new List<DeviceItem>();

                foreach (var (serial, state, model) in parsedDevices)
                {
                    var existing = ConnectedDevices.FirstOrDefault(d => d.Serial == serial);
                    if (existing == null)
                    {
                        long freeBytes = state == "device" ? await GetDeviceFreeSpaceBytesAsync(adbPath, serial) : 0;
                        string initialStatus = state switch
                        {
                            "device" => "جاهز للعمل ✅",
                            "unauthorized" => "⚠️ افتح شاشة الهاتف واضغط (سماح تصحيح USB)",
                            "offline" => "⚠️ الهاتف في وضع Offline (افصل الكابل وأعد توصيله)",
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
                            : $"⚠️ تم اكتشاف الهاتف ({serial}) ولكن يجب الضغط على (سماح / Allow) في شاشة الهاتف!";
                    }
                    else
                    {
                        bool wasDisconnectedOrNotReady = existing.IsDisconnected || existing.DeviceState != "device";
                        existing.IsDisconnected = false;
                        existing.DeviceState = state;
                        if (!string.IsNullOrWhiteSpace(model) && model != "Android Phone")
                            existing.ModelName = model;

                        if (state == "unauthorized")
                        {
                            existing.Status = "⚠️ افتح شاشة الهاتف واضغط (سماح تصحيح USB)";
                            existing.SpeedInfo = "بانتظار موافقة الهاتف";
                        }
                        else if (state == "offline")
                        {
                            existing.Status = "⚠️ الهاتف Offline - أعد توصيل الكابل";
                        }
                        else if (state == "device")
                        {
                            if (wasDisconnectedOrNotReady || !silent)
                            {
                                existing.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, serial);
                                existing.EvaluateSpace(_totalFolderBytes, existing.AlreadyCopiedBytes);
                            }

                            if (wasDisconnectedOrNotReady)
                            {
                                if (existing.NeedsResume && ChkAutoResumeOnReconnect.IsChecked == true && !existing.IsBusy)
                                {
                                    existing.Status = "🔄 تمت إعادة التوصيل! جاري الاستئناف التلقائي...";
                                    devicesToAutoResume.Add(existing);
                                }
                                else if (!existing.IsBusy)
                                {
                                    existing.Status = existing.NeedsResume
                                        ? "⏸ متوقف - اضغط استئناف النسخ للإكمال"
                                        : "جاهز للعمل ✅";
                                    existing.SpeedInfo = "متصل وجاهز";
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
                    string dfOut = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell df -k {p}");
                    var lines = dfOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Reverse())
                    {
                        if (line.StartsWith("Filesystem", StringComparison.OrdinalIgnoreCase)) continue;
                        var cols = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        // Available KB is typically column index 3 (or 2 if wrapped)
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
                string stdOut = await proc.StandardOutput.ReadToEndAsync();
                string stdErr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(token);

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

            TxtGlobalStatus.Text = "جاري حساب حجم الملفات في المجلد المصدر...";
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
            try
            {
                var sourceDirInfo = new DirectoryInfo(sourceFolder);
                string folderName = sourceDirInfo.Name;
                string normalizedTarget = PhoneFolderBrowserDialog.NormalizeRemotePath(targetDest);

                // If user already browsed inside a folder with the exact same name, don't duplicate folderName/folderName
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

                string escapedFullRemote = fullRemoteFolderPath.Replace("\"", "\\\"");
                string escapedBaseParent = baseDestParent.Replace("\"", "\\\"");

                await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{escapedBaseParent}\"");

                if (conflictMode == 0)
                {
                    device.Status = "🗑 جاري حذف النسخة القديمة للاستبدال...";
                    device.AlreadyCopiedBytes = 0;
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell rm -rf \"{escapedFullRemote}\"");
                }
                else if (conflictMode == 3)
                {
                    device.Status = "✏️ جاري إعادة تسمية المجلد القديم...";
                    device.AlreadyCopiedBytes = 0;
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string renamedPath = $"{escapedFullRemote}_old_{timestamp}";
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mv \"{escapedFullRemote}\" \"{renamedPath}\"");
                }

                Dictionary<string, long> remoteExistingFiles = new Dictionary<string, long>(StringComparer.Ordinal);
                if (conflictMode == 1)
                {
                    device.Status = "🔍 جاري فحص نقطة التوقف والملفات المنسوخة مسبقاً...";
                    remoteExistingFiles = await GetRemoteExistingFilesAsync(adbPath, device.Serial, baseDestParent, folderName);
                }

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

                int totalSourceFilesCount = resumePlan.TotalLocalFilesCount;
                if (totalSourceFilesCount == 0)
                {
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{escapedFullRemote}\"");
                    device.Progress = 100;
                    device.Status = $"تم إنشاء المجلد الفارغ في {fullRemoteFolderPath} ✅";
                    return;
                }

                if (conflictMode == 1 && resumePlan.MissingRelativeFiles.Count == 0 && resumePlan.PartialFiles.Count == 0 && _totalFolderBytes > 0)
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
                    bool tarSuccess = await StreamTarWithSmartResumeAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, resumePlan, conflictMode == 1, token);
                    if (!tarSuccess && !token.IsCancellationRequested)
                    {
                        device.Status = "🔄 جاري التحويل التلقائي إلى ADB Push المضمون...";
                        var fallbackExisting = await GetRemoteExistingFilesAsync(adbPath, device.Serial, baseDestParent, folderName);
                        var fallbackPlan = await Task.Run(() => BuildTransferPlan(sourceFolder, fallbackExisting, isResumeMode: true));
                        await AdbPushPerFileReliableAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, fallbackPlan, token);
                    }
                }
                else
                {
                    await AdbPushPerFileReliableAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, resumePlan, token);
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
            }
        }

        private async Task<int> CountRemoteFilesAsync(string adbPath, string serial, string fullRemoteFolderPath)
        {
            try
            {
                string escaped = fullRemoteFolderPath.Replace("\"", "\\\"");
                string output = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell find \"{escaped}\" -type f");
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
                string fullFolder = $"{baseDestParent}{folderName}".Replace("\"", "\\\"");
                string cmd = $"-s {serial} shell find \"{fullFolder}\" -type f -exec stat -c \"%s|%n\" {{}} +";
                string output = await RunCommandCaptureAsync(adbPath, cmd);
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
            public List<string> MissingRelativeFiles { get; set; } = new List<string>();
            public List<(FileInfo LocalFile, string RelativePath, long RemoteBytes)> PartialFiles { get; set; } = new List<(FileInfo, string, long)>();
        }

        private TransferResumePlan BuildTransferPlan(string sourceFolder, Dictionary<string, long> remoteFiles, bool isResumeMode)
        {
            var plan = new TransferResumePlan();
            var rootDir = new DirectoryInfo(sourceFolder);
            string parentFull = rootDir.Parent!.FullName;

            const long PartialResumeThreshold = 8L * 1024 * 1024;
            const long BlockSize = 1024L * 1024L;

            foreach (FileInfo fi in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                plan.TotalLocalFilesCount++;
                string relPath = Path.GetRelativePath(parentFull, fi.FullName).Replace('\\', '/');
                if (isResumeMode && remoteFiles.TryGetValue(relPath, out long remoteSize))
                {
                    if (remoteSize == fi.Length)
                    {
                        plan.AlreadyCopiedBytes += fi.Length;
                    }
                    else if (remoteSize > BlockSize && remoteSize < fi.Length && fi.Length >= PartialResumeThreshold)
                    {
                        long alignedBytes = (remoteSize / BlockSize) * BlockSize;
                        plan.AlreadyCopiedBytes += alignedBytes;
                        plan.PartialFiles.Add((fi, relPath, alignedBytes));
                    }
                    else
                    {
                        plan.MissingRelativeFiles.Add(relPath);
                    }
                }
                else
                {
                    plan.MissingRelativeFiles.Add(relPath);
                }
            }

            return plan;
        }

        private class RefLong { public long Value; }
        private class RefDouble { public double Value; }

        private async Task CopyPartialFilesWithDdAsync(
            DeviceItem device, string adbPath, string baseDestParent,
            TransferResumePlan resumePlan, Stopwatch sw,
            RefLong totalCopied, RefLong sessionBytes, RefLong lastReportedBytes, RefDouble lastReportTime,
            CancellationToken token)
        {
            foreach (var (localFile, relPath, alignedOffset) in resumePlan.PartialFiles)
            {
                token.ThrowIfCancellationRequested();
                string remoteFilePath = $"{baseDestParent}{relPath}";
                int lastSlash = remoteFilePath.LastIndexOf('/');
                string remoteDirPath = lastSlash > 0 ? remoteFilePath.Substring(0, lastSlash) : baseDestParent;
                await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{remoteDirPath.Replace("\"", "\\\"")}\"");

                long seekBlocks = alignedOffset / (1024L * 1024L);
                var ddPsi = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"-s {device.Serial} exec-in \"dd of='{remoteFilePath}' bs=1048576 seek={seekBlocks} conv=notrunc\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var ddProc = Process.Start(ddPsi)!;
                using var fs = new FileStream(localFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(alignedOffset, SeekOrigin.Begin);

                byte[] buf = new byte[1024 * 1024];
                int read;
                using Stream ddIn = ddProc.StandardInput.BaseStream;
                while ((read = await fs.ReadAsync(buf, 0, buf.Length, token)) > 0)
                {
                    await ddIn.WriteAsync(buf, 0, read, token);
                    totalCopied.Value += read;
                    sessionBytes.Value += read;
                    device.AlreadyCopiedBytes = totalCopied.Value;

                    double elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed - lastReportTime.Value >= 0.35)
                    {
                        UpdateDeviceProgressUI(device, totalCopied.Value, sessionBytes.Value, lastReportedBytes.Value, elapsed, lastReportTime.Value);
                        lastReportedBytes.Value = sessionBytes.Value;
                        lastReportTime.Value = elapsed;
                    }
                }
                await ddIn.FlushAsync(token);
                ddIn.Close();
                await ddProc.WaitForExitAsync(token);
            }
        }

        private async Task AdbPushPerFileReliableAsync(
            DeviceItem device, string adbPath, string sourceFolder, string baseDestParent, string fullRemoteFolderPath,
            TransferResumePlan resumePlan, CancellationToken token)
        {
            string parentDir = Directory.GetParent(sourceFolder)!.FullName;
            await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{fullRemoteFolderPath.Replace("\"", "\\\"")}\"");

            var totalCopied = new RefLong { Value = resumePlan.AlreadyCopiedBytes };
            var sessionBytes = new RefLong { Value = 0 };
            var lastReportedBytes = new RefLong { Value = 0 };
            var lastReportTime = new RefDouble { Value = 0 };
            var sw = Stopwatch.StartNew();

            double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied.Value * 100.0) / _totalFolderBytes) : 0;
            Dispatcher.Invoke(() =>
            {
                device.Progress = initialPercent;
                device.Status = totalCopied.Value > 0
                    ? $"⏯ استئناف النقل المضمون من {initialPercent:F1}%..."
                    : $"📁 جاري النسخ إلى {fullRemoteFolderPath}...";
            });

            if (resumePlan.PartialFiles.Count > 0)
            {
                await CopyPartialFilesWithDdAsync(device, adbPath, baseDestParent, resumePlan, sw, totalCopied, sessionBytes, lastReportedBytes, lastReportTime, token);
            }

            var createdDirs = new HashSet<string>(StringComparer.Ordinal);
            int totalFiles = resumePlan.MissingRelativeFiles.Count;
            int currentFileIndex = 0;

            foreach (var relPath in resumePlan.MissingRelativeFiles)
            {
                token.ThrowIfCancellationRequested();
                currentFileIndex++;

                string localFile = Path.Combine(parentDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                var fi = new FileInfo(localFile);
                if (!fi.Exists) continue;

                string remoteFile = $"{baseDestParent}{relPath}";
                int lastSlash = remoteFile.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    string remoteDir = remoteFile.Substring(0, lastSlash);
                    if (createdDirs.Add(remoteDir))
                    {
                        await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{remoteDir.Replace("\"", "\\\"")}\"");
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    device.Status = $"📁 جاري نسخ ({currentFileIndex}/{totalFiles}): {fi.Name}";
                });

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
                string stdOut = await proc.StandardOutput.ReadToEndAsync();
                string stdErr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(token);

                if (proc.ExitCode != 0)
                {
                    device.NeedsResume = true;
                    string errMsg = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                    device.Status = $"⚠️ فشل نسخ {fi.Name}: {errMsg.Trim()}";
                    return;
                }

                totalCopied.Value += fi.Length;
                sessionBytes.Value += fi.Length;
                device.AlreadyCopiedBytes = totalCopied.Value;

                double elapsed = sw.Elapsed.TotalSeconds;
                if (elapsed - lastReportTime.Value >= 0.2 || currentFileIndex == totalFiles)
                {
                    UpdateDeviceProgressUI(device, totalCopied.Value, sessionBytes.Value, lastReportedBytes.Value, elapsed, lastReportTime.Value);
                    lastReportedBytes.Value = sessionBytes.Value;
                    lastReportTime.Value = elapsed;
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

        private async Task<bool> StreamTarWithSmartResumeAsync(
            DeviceItem device, string adbPath, string sourceFolder, string baseDestParent, string fullRemoteFolderPath,
            TransferResumePlan resumePlan, bool isResumeMode, CancellationToken token)
        {
            string parentDir = Directory.GetParent(sourceFolder)!.FullName;
            string folderName = new DirectoryInfo(sourceFolder).Name;

            var totalCopied = new RefLong { Value = resumePlan.AlreadyCopiedBytes };
            var sessionBytes = new RefLong { Value = 0 };
            var lastReportedBytes = new RefLong { Value = 0 };
            var lastReportTime = new RefDouble { Value = 0 };
            var sw = Stopwatch.StartNew();

            double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied.Value * 100.0) / _totalFolderBytes) : 0;

            Dispatcher.Invoke(() =>
            {
                device.Progress = initialPercent;
                device.Status = totalCopied.Value > 0
                    ? $"⏯ استئناف من {initialPercent:F1}%..."
                    : "⚡ جاري البث السريع (TAR Stream)...";
            });

            if (resumePlan.PartialFiles.Count > 0)
            {
                await CopyPartialFilesWithDdAsync(device, adbPath, baseDestParent, resumePlan, sw, totalCopied, sessionBytes, lastReportedBytes, lastReportTime, token);
            }

            if (isResumeMode && resumePlan.AlreadyCopiedBytes > 0 && resumePlan.MissingRelativeFiles.Count == 0)
            {
                int count = await CountRemoteFilesAsync(adbPath, device.Serial, fullRemoteFolderPath);
                if (count > 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = $"✅ مكتمل ({count} ملف) في {fullRemoteFolderPath}";
                    return true;
                }
                return false;
            }

            string? tempListFile = null;
            string tarArgs;

            if (isResumeMode && resumePlan.AlreadyCopiedBytes > 0 && resumePlan.MissingRelativeFiles.Count > 0)
            {
                tempListFile = Path.Combine(Path.GetTempPath(), $"tar_resume_{device.Serial}_{Guid.NewGuid():N}.txt");
                await File.WriteAllLinesAsync(tempListFile, resumePlan.MissingRelativeFiles, new UTF8Encoding(false), token);
                tarArgs = $"--format=ustar -cf - -C \"{parentDir}\" -T \"{tempListFile}\"";
            }
            else
            {
                tarArgs = $"--format=ustar -cf - -C \"{parentDir}\" \"{folderName}\"";
            }

            var tarPsi = new ProcessStartInfo
            {
                FileName = "tar.exe",
                Arguments = tarArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var adbPsi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {device.Serial} exec-in \"mkdir -p '{baseDestParent}' && tar -xf - -C '{baseDestParent}'\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var tarProcess = new Process { StartInfo = tarPsi };
            using var adbProcess = new Process { StartInfo = adbPsi };

            try
            {
                tarProcess.Start();
                adbProcess.Start();

                using Stream sourceStream = tarProcess.StandardOutput.BaseStream;
                using Stream destStream = adbProcess.StandardInput.BaseStream;

                byte[] buffer = new byte[1024 * 1024];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await destStream.WriteAsync(buffer, 0, bytesRead, token);
                    totalCopied.Value += bytesRead;
                    sessionBytes.Value += bytesRead;
                    device.AlreadyCopiedBytes = totalCopied.Value;

                    double elapsedSec = sw.Elapsed.TotalSeconds;
                    if (elapsedSec - lastReportTime.Value >= 0.35)
                    {
                        UpdateDeviceProgressUI(device, totalCopied.Value, sessionBytes.Value, lastReportedBytes.Value, elapsedSec, lastReportTime.Value);
                        lastReportedBytes.Value = sessionBytes.Value;
                        lastReportTime.Value = elapsedSec;
                    }
                }

                await destStream.FlushAsync(token);
                destStream.Close();

                await adbProcess.WaitForExitAsync(token);

                int verifiedCount = await CountRemoteFilesAsync(adbPath, device.Serial, fullRemoteFolderPath);
                if (adbProcess.ExitCode == 0 && verifiedCount > 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = $"✅ تم نسخ {verifiedCount} ملف بنجاح إلى: {fullRemoteFolderPath}";
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                    device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB ({verifiedCount} ملف مكتمل)";
                    return true;
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                try { tarProcess.Kill(); } catch { }
                try { adbProcess.Kill(); } catch { }
                throw;
            }
            catch
            {
                try { tarProcess.Kill(); } catch { }
                try { adbProcess.Kill(); } catch { }
                return false;
            }
            finally
            {
                if (tempListFile != null && File.Exists(tempListFile))
                {
                    try { File.Delete(tempListFile); } catch { }
                }
            }
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
                device.SpeedInfo = $"{copiedGb:F2}/{totalGb:F2} GB ({speedMbps:F1} MB/s)";
            });
        }

        private void BtnCancelAll_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            BtnCancelAll.IsEnabled = false;
            TxtGlobalStatus.Text = "⏸ تم إيقاف العمليات، يمكنك الضغط على (⏯ استئناف النسخ من نقطة التوقف) في أي وقت للإكمال.";
        }

        private async Task<string> RunCommandCaptureAsync(string fileName, string args)
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
            await p.WaitForExitAsync();
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
                    FreeSpaceText = "المساحة: قيد الفحص";
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
