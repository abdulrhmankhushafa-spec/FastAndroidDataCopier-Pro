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

        public MainWindow()
        {
            InitializeComponent();
            LvDevices.ItemsSource = ConnectedDevices;

            _autoDetectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _autoDetectTimer.Tick += AutoDetectTimer_Tick;
            _autoDetectTimer.Start();
        }

        private async void AutoDetectTimer_Tick(object? sender, EventArgs e)
        {
            if (ChkAutoDetectDevices.IsChecked != true || _isScanningDevices)
                return;

            string adbPath = TxtAdbPath.Text.Trim();
            if (!File.Exists(adbPath))
                return;

            await ScanAndSyncDevicesAsync(adbPath, silent: true);
        }

        private void BtnBrowseAdb_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ADB Executable (adb.exe)|adb.exe",
                Title = "اختر ملف adb.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                TxtAdbPath.Text = dlg.FileName;
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
            string adbPath = TxtAdbPath.Text.Trim();
            if (!File.Exists(adbPath))
            {
                MessageBox.Show("مسار ملف adb.exe غير صحيح! يرجى تحديده أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnRefreshDevices.IsEnabled = false;
            TxtGlobalStatus.Text = "جاري البحث عن الهواتف المتصلة وفحص المساحة المتوفرة...";
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
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Skip(1);

                var currentOnlineSerials = new HashSet<string>();
                var parsedDevices = new List<(string Serial, string Model)>();

                foreach (var line in lines)
                {
                    if (line.Contains(" device "))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        string serial = parts[0];
                        string model = "Android Phone";

                        var modelPart = parts.FirstOrDefault(p => p.StartsWith("model:"));
                        if (modelPart != null)
                            model = modelPart.Replace("model:", "").Replace("_", " ");

                        currentOnlineSerials.Add(serial);
                        parsedDevices.Add((serial, model));
                    }
                }

                foreach (var existing in ConnectedDevices.ToList())
                {
                    if (!currentOnlineSerials.Contains(existing.Serial))
                    {
                        if (!existing.IsDisconnected)
                        {
                            existing.IsDisconnected = true;
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

                foreach (var (serial, model) in parsedDevices)
                {
                    var existing = ConnectedDevices.FirstOrDefault(d => d.Serial == serial);
                    if (existing == null)
                    {
                        long freeBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, serial);
                        var deviceItem = new DeviceItem
                        {
                            Serial = serial,
                            ModelName = model,
                            IsSelected = ChkSelectAll.IsChecked == true,
                            FreeSpaceBytes = freeBytes,
                            Status = "جاهز للعمل ✅",
                            Progress = 0,
                            SpeedInfo = "0 MB/s"
                        };
                        deviceItem.EvaluateSpace(_totalFolderBytes, 0);
                        ConnectedDevices.Add(deviceItem);
                        TxtGlobalStatus.Text = $"🔌 تم التعرف تلقائياً على جهاز جديد: {model} ({serial})";
                    }
                    else if (existing.IsDisconnected)
                    {
                        existing.IsDisconnected = false;
                        existing.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, serial);
                        existing.EvaluateSpace(_totalFolderBytes, existing.AlreadyCopiedBytes);

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
                        }
                    }
                    else if (!silent && !existing.IsBusy)
                    {
                        existing.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, serial);
                        existing.EvaluateSpace(_totalFolderBytes, existing.AlreadyCopiedBytes);
                    }
                }

                int activeCount = ConnectedDevices.Count(d => !d.IsDisconnected);
                TxtDevicesCount.Text = $"{activeCount} جهاز متصل";

                if (!silent)
                {
                    TxtGlobalStatus.Text = $"تم تحديث القائمة: {activeCount} أجهزة متصلة حالياً.";
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
            string adbPath = TxtAdbPath.Text.Trim();
            string sourceFolder = TxtSourceFolder.Text.Trim();
            string targetDest = TxtTargetDest.Text.Trim();
            string apkPath = TxtApkPath.Text.Trim();
            bool enforceSpace = ChkEnforceSpaceCheck.IsChecked == true;
            bool useTarStream = CmbTransferMode.SelectedIndex == 0;

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
            try
            {
                string dfOut = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell df -k /sdcard");
                var lines = dfOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 2)
                {
                    string dataLine = lines.Last();
                    var cols = dataLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length >= 4 && long.TryParse(cols[3], out long availKb))
                    {
                        return availKb * 1024L;
                    }
                }
            }
            catch { }
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
            string adbPath = TxtAdbPath.Text.Trim();
            string apkPath = TxtApkPath.Text.Trim();

            if (!File.Exists(adbPath) || !File.Exists(apkPath))
            {
                MessageBox.Show("يرجى التأكد من صحة مسار adb.exe واختيار ملف APK صحيح.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedDevices = ConnectedDevices.Where(d => d.IsSelected && !d.IsDisconnected).ToList();
            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("يرجى تحديد جهاز متصل واحد على الأقل.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                string stdOut = await proc.StandardOutput.ReadToEndAsync();
                string stdErr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync(token);

                if (proc.ExitCode == 0 && stdOut.Contains("Success"))
                {
                    device.Status = "تم تثبيت الـ APK بنجاح ✅";
                    return true;
                }
                else
                {
                    device.Status = $"فشل تثبيت APK: {stdErr}";
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
            BtnCancelAll.IsEnabled = !enabled;
        }

        private async Task ExecuteBatchOperationAsync(bool forceResumeMode)
        {
            string adbPath = TxtAdbPath.Text.Trim();
            string sourceFolder = TxtSourceFolder.Text.Trim();
            string targetDest = TxtTargetDest.Text.Trim();
            string apkPath = TxtApkPath.Text.Trim();

            if (!File.Exists(adbPath))
            {
                MessageBox.Show("تأكد من وجود ملف adb.exe في المسار المحدد.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!Directory.Exists(sourceFolder))
            {
                MessageBox.Show("يرجى اختيار المجلد المصدر المراد نسخه أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedDevices = ConnectedDevices.Where(d => d.IsSelected && !d.IsDisconnected).ToList();
            if (selectedDevices.Count == 0)
            {
                MessageBox.Show("يرجى تحديد هاتف متصل واحد على الأقل من القائمة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_totalFolderBytes == 0)
            {
                TxtGlobalStatus.Text = "جاري حساب حجم المجلد...";
                _totalFolderBytes = await Task.Run(() => CalculateDirectorySize(new DirectoryInfo(sourceFolder)));
                double sizeGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                TxtFolderSizeInfo.Text = $"{sizeGb:F2} GB";
                UpdateAllDevicesSpaceVerdict();
            }

            bool installApkFirst = !forceResumeMode && (CmbOperationMode.SelectedIndex == 1) && File.Exists(apkPath);
            bool enforceSpace = ChkEnforceSpaceCheck.IsChecked == true;
            int conflictMode = forceResumeMode ? 1 : CmbConflictMode.SelectedIndex;
            bool useTarStream = CmbTransferMode.SelectedIndex == 0;

            _cts = new CancellationTokenSource();
            SetButtonsEnabled(false);

            TxtGlobalStatus.Text = forceResumeMode
                ? $"⏯ جاري فحص الملفات المنسوخة سابقاً واستئناف النسخ من نقطة التوقف لـ {selectedDevices.Count} أجهزة..."
                : $"🚀 جاري تنفيذ العمليات بالتوازي على {selectedDevices.Count} أجهزة...";

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
                string folderName = new DirectoryInfo(sourceFolder).Name;
                string normalizedTarget = targetDest.EndsWith("/") ? targetDest : targetDest + "/";
                string fullRemoteFolderPath = $"{normalizedTarget}{folderName}";

                if (conflictMode == 0)
                {
                    device.Status = "🗑 جاري حذف النسخة القديمة للاستبدال...";
                    device.AlreadyCopiedBytes = 0;
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell rm -rf \"{fullRemoteFolderPath}\"");
                }
                else if (conflictMode == 3)
                {
                    device.Status = "✏️ جاري إعادة تسمية المجلد القديم...";
                    device.AlreadyCopiedBytes = 0;
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string renamedPath = $"{fullRemoteFolderPath}_old_{timestamp}";
                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell \"if [ -d '{fullRemoteFolderPath}' ]; then mv '{fullRemoteFolderPath}' '{renamedPath}'; fi\"");
                }

                Dictionary<string, long> remoteExistingFiles = new Dictionary<string, long>(StringComparer.Ordinal);
                if (conflictMode == 1)
                {
                    device.Status = "🔍 جاري فحص نقطة التوقف والملفات المنسوخة مسبقاً...";
                    remoteExistingFiles = await GetRemoteExistingFilesAsync(adbPath, device.Serial, normalizedTarget, folderName);
                }

                var resumePlan = await Task.Run(() => BuildTransferPlan(sourceFolder, remoteExistingFiles, conflictMode == 1));
                device.AlreadyCopiedBytes = resumePlan.AlreadyCopiedBytes;

                device.Status = "🔍 جاري التحقق من المساحة المتوفرة...";
                device.FreeSpaceBytes = await GetDeviceFreeSpaceBytesAsync(adbPath, device.Serial);
                Dispatcher.Invoke(() => device.EvaluateSpace(_totalFolderBytes, device.AlreadyCopiedBytes));

                long remainingBytesNeeded = Math.Max(0, _totalFolderBytes - device.AlreadyCopiedBytes);
                if (enforceSpace && device.FreeSpaceBytes > 0 && device.FreeSpaceBytes < remainingBytesNeeded)
                {
                    device.Status = "❌ توقف: المساحة المتوفرة غير كافية للملفات المتبقية!";
                    return;
                }

                if (installApkFirst)
                {
                    bool apkOk = await InstallApkOnDeviceAsync(device, adbPath, apkPath, token);
                    if (!apkOk && token.IsCancellationRequested) return;
                }

                if (conflictMode == 1 && resumePlan.MissingRelativeFiles.Count == 0 && resumePlan.PartialFiles.Count == 0 && _totalFolderBytes > 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = "مكتمل مسبقاً 100% (لا توجد ملفات ناقصة) ✅";
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                    device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB (مكتمل)";
                    return;
                }

                if (useTarStream)
                {
                    await StreamTarWithSmartResumeAsync(device, adbPath, sourceFolder, normalizedTarget, resumePlan, conflictMode == 1, token);
                }
                else
                {
                    await AdbPushWithSmartResumeAsync(device, adbPath, sourceFolder, normalizedTarget, resumePlan, conflictMode == 1, token);
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

        private async Task<Dictionary<string, long>> GetRemoteExistingFilesAsync(string adbPath, string serial, string normalizedTarget, string folderName)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            try
            {
                string cmd = $"-s {serial} shell \"cd '{normalizedTarget}' 2>/dev/null && find '{folderName}' -type f -exec stat -c '%s|%n' {{}} + 2>/dev/null\"";
                string output = await RunCommandCaptureAsync(adbPath, cmd);
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    int sep = line.IndexOf('|');
                    if (sep > 0)
                    {
                        string sizeStr = line.Substring(0, sep).Trim();
                        string relPath = line.Substring(sep + 1).Trim().Replace('\\', '/');
                        if (long.TryParse(sizeStr, out long sz) && sz > 0)
                        {
                            map[relPath] = sz;
                        }
                    }
                }
            }
            catch { }
            return map;
        }

        private class TransferResumePlan
        {
            public long AlreadyCopiedBytes { get; set; }
            public List<string> MissingRelativeFiles { get; set; } = new List<string>();
            public List<(FileInfo LocalFile, string RelativePath, long RemoteBytes)> PartialFiles { get; set; } = new List<(FileInfo, string, long)>();
        }

        private TransferResumePlan BuildTransferPlan(string sourceFolder, Dictionary<string, long> remoteFiles, bool isResumeMode)
        {
            var plan = new TransferResumePlan();
            var rootDir = new DirectoryInfo(sourceFolder);
            string parentFull = rootDir.Parent!.FullName;

            if (!isResumeMode || remoteFiles.Count == 0)
            {
                return plan;
            }

            const long PartialResumeThreshold = 8L * 1024 * 1024;
            const long BlockSize = 1024L * 1024L;

            foreach (FileInfo fi in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string relPath = Path.GetRelativePath(parentFull, fi.FullName).Replace('\\', '/');
                if (remoteFiles.TryGetValue(relPath, out long remoteSize))
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

        private async Task StreamTarWithSmartResumeAsync(
            DeviceItem device, string adbPath, string sourceFolder, string targetDest,
            TransferResumePlan resumePlan, bool isResumeMode, CancellationToken token)
        {
            string parentDir = Directory.GetParent(sourceFolder)!.FullName;
            string folderName = new DirectoryInfo(sourceFolder).Name;

            await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{targetDest}\"");

            long totalCopied = resumePlan.AlreadyCopiedBytes;
            double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied * 100.0) / _totalFolderBytes) : 0;

            Dispatcher.Invoke(() =>
            {
                device.Progress = initialPercent;
                if (totalCopied > 0)
                {
                    double skippedGb = totalCopied / (1024.0 * 1024.0 * 1024.0);
                    device.Status = $"⏯ استئناف من {initialPercent:F1}% (تخطي {skippedGb:F2} GB منسوخة)...";
                }
                else
                {
                    device.Status = "⚡ جاري البث السريع (TAR Stream)...";
                }
            });

            var sw = Stopwatch.StartNew();
            long sessionBytes = 0;
            long lastReportedBytes = 0;
            double lastReportTime = 0;

            foreach (var (localFile, relPath, alignedOffset) in resumePlan.PartialFiles)
            {
                token.ThrowIfCancellationRequested();
                string remoteFilePath = $"{targetDest}{relPath}";
                string remoteDirPath = Path.GetDirectoryName(remoteFilePath)?.Replace('\\', '/') ?? targetDest;
                await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{remoteDirPath}\"");

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
                    totalCopied += read;
                    sessionBytes += read;
                    device.AlreadyCopiedBytes = totalCopied;

                    double elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed - lastReportTime >= 0.35)
                    {
                        UpdateDeviceProgressUI(device, totalCopied, sessionBytes, lastReportedBytes, elapsed, lastReportTime);
                        lastReportedBytes = sessionBytes;
                        lastReportTime = elapsed;
                    }
                }
                await ddIn.FlushAsync(token);
                ddIn.Close();
                await ddProc.WaitForExitAsync(token);
            }

            if (isResumeMode && resumePlan.AlreadyCopiedBytes > 0 && resumePlan.MissingRelativeFiles.Count == 0)
            {
                device.Progress = 100;
                device.NeedsResume = false;
                device.Status = "مكتمل بنجاح ✅";
                return;
            }

            string? tempListFile = null;
            string tarArgs;

            if (isResumeMode && resumePlan.AlreadyCopiedBytes > 0 && resumePlan.MissingRelativeFiles.Count > 0)
            {
                tempListFile = Path.Combine(Path.GetTempPath(), $"tar_resume_{device.Serial}_{Guid.NewGuid():N}.txt");
                await File.WriteAllLinesAsync(tempListFile, resumePlan.MissingRelativeFiles, new UTF8Encoding(false), token);
                tarArgs = $"-cf - -C \"{parentDir}\" -T \"{tempListFile}\"";
            }
            else
            {
                tarArgs = $"-cf - -C \"{parentDir}\" \"{folderName}\"";
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
                Arguments = $"-s {device.Serial} exec-in \"tar -xf - -C '{targetDest}'\"",
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
                    totalCopied += bytesRead;
                    sessionBytes += bytesRead;
                    device.AlreadyCopiedBytes = totalCopied;

                    double elapsedSec = sw.Elapsed.TotalSeconds;
                    if (elapsedSec - lastReportTime >= 0.35)
                    {
                        UpdateDeviceProgressUI(device, totalCopied, sessionBytes, lastReportedBytes, elapsedSec, lastReportTime);
                        lastReportedBytes = sessionBytes;
                        lastReportTime = elapsedSec;
                    }
                }

                await destStream.FlushAsync(token);
                destStream.Close();

                await adbProcess.WaitForExitAsync(token);

                if (adbProcess.ExitCode == 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = "مكتمل بنجاح ✅";
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
                    device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB (مكتمل)";
                }
                else
                {
                    string err = await adbProcess.StandardError.ReadToEndAsync();
                    device.NeedsResume = true;
                    device.Status = $"⚠️ انقطع النقل - جاهز للاستئناف ({err.Trim()})";
                }
            }
            catch (OperationCanceledException)
            {
                try { tarProcess.Kill(); } catch { }
                try { adbProcess.Kill(); } catch { }
                device.NeedsResume = true;
                device.Status = "⏸ تم الإيقاف - اضغط استئناف للإكمال من نفس النقطة";
            }
            catch (Exception ex)
            {
                try { tarProcess.Kill(); } catch { }
                try { adbProcess.Kill(); } catch { }
                device.NeedsResume = true;
                device.Status = $"⚠️ انقطع الاتصال - أعد التوصيل للاستئناف ({ex.Message})";
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

        private async Task AdbPushWithSmartResumeAsync(
            DeviceItem device, string adbPath, string sourceFolder, string targetDest,
            TransferResumePlan resumePlan, bool isResumeMode, CancellationToken token)
        {
            device.Status = isResumeMode && resumePlan.AlreadyCopiedBytes > 0
                ? "⏯ جاري استئناف النقل (ADB Push)..."
                : "📁 جاري النقل (ADB Push)...";

            if (isResumeMode && resumePlan.AlreadyCopiedBytes > 0 && resumePlan.MissingRelativeFiles.Count > 0)
            {
                string parentDir = Directory.GetParent(sourceFolder)!.FullName;
                long totalCopied = resumePlan.AlreadyCopiedBytes;
                int count = 0;
                int totalFiles = resumePlan.MissingRelativeFiles.Count;

                foreach (var relPath in resumePlan.MissingRelativeFiles)
                {
                    token.ThrowIfCancellationRequested();
                    string localFile = Path.Combine(parentDir, relPath.Replace('/', Path.DirectorySeparatorChar));
                    string remoteFile = $"{targetDest}{relPath}";
                    var fi = new FileInfo(localFile);

                    await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} push \"{localFile}\" \"{remoteFile}\"");
                    totalCopied += fi.Exists ? fi.Length : 0;
                    device.AlreadyCopiedBytes = totalCopied;
                    count++;

                    double percent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied * 100.0) / _totalFolderBytes) : (count * 100.0 / totalFiles);
                    double copiedGb = totalCopied / (1024.0 * 1024.0 * 1024.0);
                    double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);

                    Dispatcher.Invoke(() =>
                    {
                        device.Progress = percent;
                        device.SpeedInfo = $"{copiedGb:F2}/{totalGb:F2} GB ({count}/{totalFiles} ملف)";
                    });
                }

                device.Progress = 100;
                device.NeedsResume = false;
                device.Status = "مكتمل بنجاح ✅";
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {device.Serial} push \"{sourceFolder}\" \"{targetDest}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                while (!process.StandardOutput.EndOfStream)
                {
                    if (token.IsCancellationRequested)
                    {
                        process.Kill();
                        device.NeedsResume = true;
                        device.Status = "⏸ تم الإيقاف - جاهز للاستئناف";
                        return;
                    }
                    string? line = await process.StandardOutput.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line) && line.Contains("%"))
                    {
                        int idx = line.IndexOf('%');
                        if (idx > 0)
                        {
                            string numStr = line.Substring(Math.Max(0, idx - 3), Math.Min(3, idx)).Trim('[', ' ');
                            if (double.TryParse(numStr, out double p))
                            {
                                device.Progress = p;
                            }
                        }
                    }
                }

                await process.WaitForExitAsync(token);
                if (process.ExitCode == 0)
                {
                    device.Progress = 100;
                    device.NeedsResume = false;
                    device.Status = "مكتمل بنجاح ✅";
                }
                else
                {
                    device.NeedsResume = true;
                    device.Status = "⚠️ توقف قبل الاكتمال - اضغط استئناف";
                }
            }
            catch (Exception ex)
            {
                device.NeedsResume = true;
                device.Status = $"⚠️ انقطع الاتصال ({ex.Message}) - جاهز للاستئناف";
            }
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
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
    }

    public class DeviceItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _serial = "";
        private string _modelName = "";
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
        public long AlreadyCopiedBytes { get => _alreadyCopiedBytes; set { _alreadyCopiedBytes = value; OnPropertyChanged(); } }
        public bool IsDisconnected { get => _isDisconnected; set { _isDisconnected = value; OnPropertyChanged(); } }
        public bool NeedsResume { get => _needsResume; set { _needsResume = value; OnPropertyChanged(); } }
        public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }

        public long FreeSpaceBytes
        {
            get => _freeSpaceBytes;
            set
            {
                _freeSpaceBytes = value;
                double freeGb = _freeSpaceBytes / (1024.0 * 1024.0 * 1024.0);
                FreeSpaceText = $"المساحة الحرة: {freeGb:F2} GB";
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
            if (FreeSpaceBytes <= 0)
            {
                SpaceVerdict = "تعذر قراءة المساحة";
                SpaceStatusColor = "#F59E0B";
                return;
            }

            if (totalRequiredBytes <= 0)
            {
                SpaceVerdict = "جاهز للفحص ✅";
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
