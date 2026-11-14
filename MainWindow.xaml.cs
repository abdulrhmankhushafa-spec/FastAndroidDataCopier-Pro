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
Interval = TimeSpan.FromSeconds(2.0)
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
await RunCommandCaptureAsync(adbPath, "reconnect offline", 2500);
await ScanAndSyncDevicesAsync(adbPath, silent: false);
BtnRefreshDevices.IsEnabled = true;
}
private async Task ScanAndSyncDevicesAsync(string adbPath, bool silent)
{
if (_isScanningDevices) return;
_isScanningDevices = true;
try
{
string output = await RunCommandCaptureAsync(adbPath, "devices -l", 4000);
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
if (state == "offline")
{
await RunCommandCaptureAsync(adbPath, $"-s {serial} reconnect", 2000);
}
string model = "";
var modelPart = parts.FirstOrDefault(p => p.StartsWith("model:", StringComparison.OrdinalIgnoreCase));
if (modelPart != null)
{
model = modelPart.Substring(6).Replace("_", " ").Trim();
}
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
foreach (var (serial, state, model) in parsedDevices)
{
var existing = ConnectedDevices.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
if (existing == null)
{
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
bool wasDisconnectedOrNotReady = existing.IsDisconnected || existing.DeviceState != "device";
existing.Serial = serial;
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
if (existing.NeedsResume && ChkAutoResumeOnReconnect.IsChecked == true && !existing.IsBusy)
{
existing.Status = "🔄 تمت إعادة التوصيل! جاري الاستئناف التلقائي...";
devicesToAutoResume.Add(existing);
}
else if (!existing.IsBusy)
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
string escapedFullRemote = fullRemoteFolderPath.Replace("\"", "\\\"");
string escapedBaseParent = baseDestParent.Replace("\"", "\\\"");
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{escapedBaseParent}\"", 5000);
if (conflictMode == 0)
{
device.Status = "🗑 جاري حذف النسخة القديمة للاستبدال...";
device.AlreadyCopiedBytes = 0;
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell rm -rf \"{escapedFullRemote}\"", 15000);
}
else if (conflictMode == 3)
{
device.Status = "✏️ جاري إعادة تسمية المجلد القديم...";
device.AlreadyCopiedBytes = 0;
string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
string renamedPath = $"{escapedFullRemote}_old_{timestamp}";
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mv \"{escapedFullRemote}\" \"{renamedPath}\"", 5000);
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
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{escapedFullRemote}\"", 5000);
device.Progress = 100;
device.Status = $"⚠️ المجلد المصدر فارغ! تم إنشاء {fullRemoteFolderPath}";
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
await AdbPushReliableAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, fallbackPlan, isResume: true, token);
}
}
else
{
bool isResume = (conflictMode == 1) && (resumePlan.AlreadyCopiedBytes > 0 || resumePlan.PartialFiles.Count > 0);
await AdbPushReliableAsync(device, adbPath, sourceFolder, baseDestParent, fullRemoteFolderPath, resumePlan, isResume, token);
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
private async Task<long> GetRemoteDirectoryBytesAsync(string adbPath, string serial, string fullRemoteFolderPath)
{
try
{
string escaped = fullRemoteFolderPath.Replace("\"", "\\\"");
string outStr = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell du -sk \"{escaped}\"", 3500);
if (!string.IsNullOrWhiteSpace(outStr))
{
var line = outStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
if (line != null)
{
var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
if (parts.Length >= 1 && long.TryParse(parts[0], out long kb) && kb >= 0)
{
return kb * 1024L;
}
}
}
}
catch { }
return 0;
}
private async Task<int> CountRemoteFilesAsync(string adbPath, string serial, string fullRemoteFolderPath)
{
try
{
string escaped = fullRemoteFolderPath.Replace("\"", "\\\"");
string output = await RunCommandCaptureAsync(adbPath, $"-s {serial} shell find \"{escaped}\" -type f", 8000);
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
string output = await RunCommandCaptureAsync(adbPath, cmd, 6000);
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
string rootFolderName = rootDir.Name;
const long PartialResumeThreshold = 8L * 1024 * 1024;
const long BlockSize = 1024L * 1024L;
var allFiles = EnumerateFilesSafe(rootDir);
foreach (FileInfo fi in allFiles)
{
plan.TotalLocalFilesCount++;
string innerRel = Path.GetRelativePath(rootDir.FullName, fi.FullName).Replace('\\', '/');
string relPath = $"{rootFolderName}/{innerRel}";
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
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{remoteDirPath.Replace("\"", "\\\"")}\"", 4000);
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
private async Task AdbPushReliableAsync(
DeviceItem device, string adbPath, string sourceFolder, string baseDestParent, string fullRemoteFolderPath,
TransferResumePlan resumePlan, bool isResume, CancellationToken token)
{
var rootDir = new DirectoryInfo(sourceFolder);
string rootFolderName = rootDir.Name;
var totalCopied = new RefLong { Value = resumePlan.AlreadyCopiedBytes };
var sessionBytes = new RefLong { Value = 0 };
var lastReportedBytes = new RefLong { Value = 0 };
var lastReportTime = new RefDouble { Value = 0 };
var sw = Stopwatch.StartNew();
if (!isResume && resumePlan.PartialFiles.Count == 0)
{
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{baseDestParent.Replace("\"", "\\\"")}\"", 5000);
Dispatcher.Invoke(() =>
{
device.Progress = 0.5;
device.Status = $"🚀 جاري النسخ السريع إلى {fullRemoteFolderPath}...";
device.SpeedInfo = "جاري بدء النقل...";
});
string cleanSource = sourceFolder.TrimEnd('\\', '/');
var pushPsi = new ProcessStartInfo
{
FileName = adbPath,
Arguments = $"-s {device.Serial} push \"{cleanSource}\" \"{baseDestParent}\"",
UseShellExecute = false,
RedirectStandardOutput = true,
RedirectStandardError = true,
StandardOutputEncoding = Encoding.UTF8,
StandardErrorEncoding = Encoding.UTF8,
CreateNoWindow = true
};
using var pushProc = Process.Start(pushPsi)!;
var stdOutTask = pushProc.StandardOutput.ReadToEndAsync();
var stdErrTask = pushProc.StandardError.ReadToEndAsync();
long lastBytes = 0;
double lastSec = 0;
while (!pushProc.HasExited)
{
if (token.IsCancellationRequested)
{
try { pushProc.Kill(entireProcessTree: true); } catch { }
token.ThrowIfCancellationRequested();
}
await Task.Delay(500, token);
long currentRemoteBytes = await GetRemoteDirectoryBytesAsync(adbPath, device.Serial, fullRemoteFolderPath);
double elapsed = sw.Elapsed.TotalSeconds;
if (currentRemoteBytes > 0)
{
device.AlreadyCopiedBytes = currentRemoteBytes;
long deltaBytes = Math.Max(0, currentRemoteBytes - lastBytes);
UpdateDeviceProgressUI(device, currentRemoteBytes, currentRemoteBytes, lastBytes, elapsed, lastSec);
lastBytes = currentRemoteBytes;
lastSec = elapsed;
}
}
string stdOut = await stdOutTask;
string stdErr = await stdErrTask;
int copiedFilesCount = await CountRemoteFilesAsync(adbPath, device.Serial, fullRemoteFolderPath);
if (pushProc.ExitCode == 0 && copiedFilesCount > 0)
{
device.Progress = 100;
device.NeedsResume = false;
device.Status = $"✅ تم نسخ {copiedFilesCount} ملف بنجاح إلى: {fullRemoteFolderPath}";
double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB ({copiedFilesCount} ملف مكتمل)";
return;
}
}
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{fullRemoteFolderPath.Replace("\"", "\\\"")}\"", 5000);
double initialPercent = _totalFolderBytes > 0 ? Math.Min(99.9, (totalCopied.Value * 100.0) / _totalFolderBytes) : 0;
Dispatcher.Invoke(() =>
{
device.Progress = Math.Max(0.5, initialPercent);
device.Status = totalCopied.Value > 0
? $"⏯ استئناف النقل المضمون من {initialPercent:F1}%..."
: $"📁 جاري نسخ الملفات إلى {fullRemoteFolderPath}...";
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
string innerRel = relPath.StartsWith(rootFolderName + "/", StringComparison.OrdinalIgnoreCase)
? relPath.Substring(rootFolderName.Length + 1)
: relPath;
string localFile = Path.Combine(rootDir.FullName, innerRel.Replace('/', Path.DirectorySeparatorChar));
var fi = new FileInfo(localFile);
if (!fi.Exists) continue;
string remoteFile = $"{baseDestParent}{relPath}";
int lastSlash = remoteFile.LastIndexOf('/');
if (lastSlash > 0)
{
string remoteDir = remoteFile.Substring(0, lastSlash);
if (createdDirs.Add(remoteDir))
{
await RunCommandCaptureAsync(adbPath, $"-s {device.Serial} shell mkdir -p \"{remoteDir.Replace("\"", "\\\"")}\"", 4000);
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
var stdOutTask = proc.StandardOutput.ReadToEndAsync();
var stdErrTask = proc.StandardError.ReadToEndAsync();
while (!proc.HasExited)
{
if (token.IsCancellationRequested)
{
try { proc.Kill(entireProcessTree: true); } catch { }
token.ThrowIfCancellationRequested();
}
await Task.Delay(350, token);
if (fi.Length >= 4 * 1024 * 1024)
{
long currentRemoteBytes = await GetRemoteDirectoryBytesAsync(adbPath, device.Serial, fullRemoteFolderPath);
if (currentRemoteBytes > totalCopied.Value)
{
double el = sw.Elapsed.TotalSeconds;
long sess = currentRemoteBytes - resumePlan.AlreadyCopiedBytes;
UpdateDeviceProgressUI(device, currentRemoteBytes, sess, lastReportedBytes.Value, el, lastReportTime.Value);
lastReportedBytes.Value = sess;
lastReportTime.Value = el;
}
}
}
string stdOut = await stdOutTask;
string stdErr = await stdErrTask;
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
device.Status = $"❌ ت)e+pfi FileShare.Read);
fsransfe.Kill(enti= totalCopiee <urreaorExitAsync(token);
}
}
private asysk \"{escaped}o�n��d-"rivate async Task<Dictionn6.}�t�sysk \"{escaped}o�n��);
lastReportedBytes.Valu0:.Valu0:.}c(token);
},lastReportedBy$TExitAsvsionBytesin Costedf (lastSlash6l�auble { public doubt(pushPsi)!;
var stdOutTask = pushProc.StandardOutput.ReadToEndAsync();
var stdErrTask = pushProc.StandardError.ReadToEndAsync();
long lastBytes = 0;
double ltReportedBytes9bPath,
ArgumurfntRe8� t;
wh�i$tReporlCopied.Value;
double elapsed = sw.Elapsed.TotalSeconds;
if (el
if (state != "device" && state !=�5oubt(pushP� = proc.Sl, fullRemoteFolderPath);
if (verifiedCount > 0)
{
device.ProgresionBytes.Val.ت٭Error.ReadToEndAsync();
long lastBytes = 0;
doublKdouble elapsed = sw.Elapsed.TotalSeconds;
if (elato uync();
lon Costezed")
opwatch.Sk.HasExited)ushPsi)!state !=�5oubt(pushn Costezed")
opwatch.HasExirreaorExitAsync(t;sh > 0 ? remoteFi
c(adbPath,�kp+ync();
loame}:BytRemoteBytes;
ic.St TotrError.ReadToEndAsync();
long lpushP� =ileName = ak \"{escap= 0;
doublKdouble elapsed = sw.Elapsed2ToEndAshng S"ndA=d2Az > 0)
{
map[relPath] = sz;
}
}
bemoteFo,
TransferRes
mapf
? $"⏯ ا = rPatd2}/ror.ReadToEndAsync();
long lastBytes = 0;
doublKdouble elapsed = sw.Elapsed.TotalSeconds;
if (elato uync();
lon Costezed")
opwatch.Sk.HasExited)ushPsi)!state !=�5oubt(pushn Costezed")
opwatch.HasExirreaorExitAsync(t;sh > 0 ? remoteFi
c(adbPath,�kp+ync();
loauresionBytes.Val.ت٭Error.ReadToEndAsync();
long lastBytes = 0;
doublKdouble elapsed = sw.Elapsed.TotalSeconds;
if (elato uync();
lon Costezed")
opwatch.Sk.HasExited)ushPsi)!state !=�5oubt(pushn Costezed")
opwatcsاء USB) �escapedFullRemooubt(pushn CostadTo $"⏯ ا = rPatd2}/ror.ReadToc.St To�سخ، أو }5);
loauresionBytep, in=emoteFcf\""ttotalGb = _esi();
long lastBytes =,d)ushPsi)!state !=�5oubt(put lastBytes=,d.Sele }5);
loauresionBytch.Sk.HasEdsN")EastBytes=,d.Se
lse
{
Fex�?escapedFullRe�9eCase)
? relPath.Su1o;
devicedoublies))
{
string t = line.etnAsync(bool forceResumeMdBytes.Value = sess"x stdO (very.IsEnabled = ent{
UpdateDeviceProgr"sPath, device.Serial, fullRemoteFolde)!staopiedsse)
? relPath.Su1o8ytes.Value = seqdoubsioe (entRemoteBytesad);F"-s {device.S([0 lastBytes=,d.Sele }5);
loaur}/aPlan.Pa0 lases, elapsed, lasTemoteFilesAs8ytes.Valu> 0 Nam}/ase)
? relPat�tReportTime.Value);
lastReportedBytes.Value = sessionBytes.Value;
lastReportTime.Value = elapsed;
}
}
await ddslastSec = elapsed;
}
}
sited)ush�محددuait ddsrt�i)!state t ddsrt�iit Task.Deplace('lse
{
4"✅art�iiadToEndA,porress = 100;
devsntRemts;
icoescap= 0;
doupopyDcap= 0;alue);
lasCyH-";
dev);
lonH }
privatteFisync(sAlreadyCopieBntRemotemoteBullRemoteFolderPath}...";
});
if (resumePlan.Partial(e}es;
if (elato an.Apsed2ToEndAshng S"
})g = await GetoEnasstate !=�=uble totalGb = _totalFolderBytes = 100;

ial} she e);
lasCyH-";fsranye3ue >= 0.35)
{
Updartringond�g5U!e !=�=uble totalGb = _totalim();
if (!t.StartsWith("find:", StringComparison.OrdinalIgnoreCase) &&
!t.Contains("NinalIse)lue;
lastRepor lastSlash) : baseDestParr1umePlan.AlreadyC6plashFHas await RFoldB"vEncodi;ath}...";
});
if (resumePlanar totalGb = _totalim)>krlanar totalGb = _totalim)>krlanar totalGb = _tob00;"NinalIse)lue;
lastRepo30es, elaps�onBytes.Value;
ladle totalGb = _4= ent{
Updatuf =.";
vaceResتمت إعادة ال
Updatuf =.";
vaceResت�)New()she e);
lasCyH-";fsranye3ue >= 0.35)
{
Updartringond�g5U!e !=�=uble totalGb g lastBytes = 0;
douo8.0 4];
int read;
using SttalFolderByemoteFont)tes = 0;
douo8.0 N�parts = tring f
});
if (resn=emotesrt�astp  lastSlash);
if (creue;
ladlsanai30es, elapDmePlanar t,sk =ed")
oplderByemoteFonubleis ddInoed = sw.Elapse 1024rdCount}nErro9: == "UapedFul�
var line = outStr.Split(new[] . اف
"u sw.Elapse 10n resumePlReadToE] . ا�5);�rn;
}
vart�astp  lastSlash);
if (cName = adbPathcoescap/aPlan.Pa0 lases, el�ap/spsed, lasTt"3ile);
long lpushP� =ileName = ak \"tedBytes.Val
}
sitedhount}nErrosanai _totalieFonubleppppppppppppppppppppa4h,
pushn Costezed")
opwatch.HasExirreaorExitAsync(t;sh > 0 ? remoteFi
c(adbPath,n�n4,tes =�dToc.St To�سخbUlace("\" = _totalFoldeHasExirreaorExxxxxxx)dInoed = sxtesDe !=�5oub,lyn�n4,tes =�dTocllName, innpied.Value)
{
double kc�o
{
FileName = adbPath,
Arguments = $"-s {device.Serial} push \"{localFile}\" \"{remoteFile}\"",
UseShellExecute = false,
RedirectStandardOutput = true,
RedirectStandardError = true,
StandardOutputEncoding = Encoding.UTF8,
StandardErrorEncoding = Encoding.UT�tF8,
St;
});
if (reSt;
});
vate asyWWWWWWWWWtalFolderBytes, device.Alrrguments = $"-s {device.Serial} push \"{localFile}\" \"{remoteFile}\"",
UseShellEle}\"",
UseShr.ReadToEnda�otalieFonublepppppppppppi1"caت٭Error.ReadToEndAuDictionn6.}�t�sysk \Nh2bt(pushn bDd0=Wcsاء Bytes, device.A  Na).ReplaclastSlash7a
UseShr.Re,
UseSheltputle.Serial} push \"{localFile2bt(pushn bDd0=Wcs�ectStandardErrpushn bFonublepppppppppppi1""xe kc�o
push \"{localFile2bt(pushn bDd0=Wcr));
de9f (sep > 0)Dlse
{
5
if (reST�g5U!eoo = Path.GetFi:"�ى {ready bDd0=Wcr));
de9f (sep > 0))she e);
Dir =th.GetFi:"�ى {ready bDd0=Wcr));
de9f (sep > 0))she t+�e9f (sep > 0long lpusCVtes =�dTocllName, iner>�{4r.Re,
Use5useTarStream = CmbTransferMode.SelectedIndex == 1;
_cts ??= newcapedFc = elap تم نسخ {verifiedCount} ملف بنجاح إلى: {fullRemoteFolderPath}";
double totalGb = _totalFolderBytes / (1024.0 * 1024.0 * 1024.0);
device.SpeedInfo = $"{totalGb:F2}/{totalGb:F2} GB ({verifiedCount} ملف مكتمل)";
}
else
{
device.NeedsResuStream edCount� �llF�t�� إلى: {fullRemofullRemotOoubtunt� �llclueValue);
la888888883firreaorExitAsync(t;sh > 0 ? remoteFi
c(adbPath,n�n4,tes =�dToc.St To�سخbUlace("\" = _d (sep > 0))sh6c-{
FileNoteFi
c(adbPath,n�n4,tes =�dToc.St To�سخbUlac _totalFolderBytes / (1024.0 *�o(adbPat(adbda _totalFold�افnabled;
BtnResumeCopy.h)kDd0=Wcr))bPat(adbda _tt(adbda _totalFoldl-�> 0))sh6c-{
FileNoteFi
c(adbr?alFile2btr.�خbUlac _to(adbr?
device.SpeedInfo = $"{totalGb24.0 * 1024devaFonublep(reSed.Value;
double elapsed = sw.Elapsed.T Costezed" _totalFold�اe;
la.IsArguments = $"-s {del0.0) / _totalFolderBytes) : 0;
Dispatcher.Invoke(()tEncoding = Encoding.UTF8,
StandardErrorEncoding = Encoding.UT�tF8,
St;
});
if (reSt;
});
vate asyWWWWWWWWWtalFolderByte;
spput = true,
RebPat(adbda _totalFo 0;
douo8.0 4];





O�O�e/e !=�=uble totalGb = _totalFolderBytes = 100;

ial} she /}/ase)
?
ArgumurfntRe8� t;
wal(e}� t;
walringoe /}/ase)
?9E,.;� t;
exOf('/');
baseDestParent = targetTrimmealse,
RediDasCyH-";fsraning.UTFdE riDasCyH-ytes);
llEle}\"",
UseShr.ReadToEnda�otalieF
DestPa/2ayte{localifiedCount&&
!t.Contain4�b8se,
RediDasCyH-";fsraning.UTFdE riDasCyH-ytes);
llEle}\"",
UseShr.ReadToEnda�otalieF
DestPa/2ayte{localifiedCount&&
!t.Contain4�b8se,
RediDasCyH-";fsraning.UTFdE riDasCyH-ytes);
llEle}\"",
UseShr.ReadToEnda�otalieF
DestPa/2ayte{localifiedCount&&
!t.Contain4�{
device.NeedsReth}...";
});
if (r1Bm.UTFdE riDce.NeedsRethdCount&&var p*De !=�5o:
{
fullRemH-";fsraning.UTFdE riDasCy[] { '\r', '\alGb = _totalim();
if (!t.St.ReadToEnda�o?
Argumurfe.Ser�t(pushn Costezu+syWWW024d�bMode == 1)
{
de{ '\r',quested();
}
await Ta8se,dE  = _totalim();
if (!t.St.ReadToEnda�o?
Argumurfe.Ser�t(pushn Costezu+syWWW024d�bMoReadToEndaeFile.LastInde0rFc
awaiemts;
icoescap=iFc
a; �Name
?
AndaeFile.LastInde0rFuble elapsed = sw.�SRemH-";fsraning.UTFdE riDasCy[] { '\c(sAle elapsed = sw.rtes);
llEle}\"",
UseShr.ReadToEnda�otalieF
DestPa/2ayte{localifiedCount&&
!t.Contain4�b8se,
�ld�اe;
3aain4m1eFont)tes = 0;
dountFileIndex == CounPathCyH-ld�اt = awaissed =ifiedCount} ملف � USBBm.UdevintFilo:
{
fce.Status.Elap0)
r,
UseAdE riDasCy[] { '\c(sAle elapsed = sw.rtes);
llRemoteFoldering.U,waiemts;
icpc(sA{le elapsesaseDestParent}{fc�ل�;
s�eAdE ntaiPld��s
m=.";
vaceRe;
deviceullR
walring
UseShr.ReadToEnda�otalieF
DestPa/2ayte{localifeFcf\""ttotalGb = _esi();
long lastBF
De8�Trim()}";
return;
}
totalCopied.Value += fi.Length;
sessionBytes.Value += fi.Length;
device.AlreadyCopiedBytes = totalCopied.Value;
double elapsed = sw.Elapsed.TotalSeconds;
if (elapsed - lastReportTime.Value >= 0.2 || currentFileIndex == totalFiles)
{
Updat�(() a(:rn03ceRe;
deviceullR
walring
UseShr.ReadToEileIndex intFilo:
{
fce.StatusIfCancellationRequestee.Sto:
{
fce.StatusIflri4];Wa�اt = awaissed =ifiedCount} ur" oEn) : 0;ocaliiDasCy[] 
});
if (sferPlan( T_r() a $"-s B8y[] 
});
if (sferPlan( T_r() a $"-s B8y[] 
});
if (sferPlan( T_r() a $"-s B8y[] 
});
if (f 0rng, leeer() �ToEp-"S:
{
fce.StatusIflri4];Wa�اt = awaissed =ifiedCount} ur" oEn) : 0;ocaliiDasCy[] 
});
if (sferPlan( T_rdCount� �ckPlanarent.Lenlri4];Wa�اt = awa,rdinaesu)))))))))[F} ur" oEn) : ce.AlreadyCopiedByteAnareAlreadyCoustring, loroc.f#p = true,
StandardOutputEncoding = oc.f#p = true,
StandardOutputEncodng, lorng = oc.f#pue7cB�سخbUlacNlC?oroc.e();
await ddProc.WaitForExit"nforceSpactEn�;
s�<st(pushnnpied.Value)
{
double kc�o
{
.WaitForE stda2rushnnpied.Value)
{
doub�evi{roc
wtch stSlash7RfLong { Value = 0seReqO.f#p = true,
 atorChar));
var fiC<emo.e.LastInde0rFuble elapsed = ds;
if l�لdme);
2
c(aalring
UseShr.ReadToEnda�oTsh7RfLong { Value = 0seReqO.f#p = true,
 atorChar));
var fiC<emo.e.LastInde0rFuble elapsed = ds;
if l�لdme);
2
c(addc
wtch stSlash7RfLonit .0);@astIndr
= ds;
if }AlreaReadToEnda5.al{del0.0) cp}�p
llE: ce.AllreaRead)rS l�لdmetApkPathxEnda5.al{del0.0) cp}�p
lvrrorEncod5.a(tIndr"f5.altRelativePath(-� == 1/qO.f#p = true,
 atorChar));
var fiC<emo.e.LastInde0rFuble elapsed = ds;etes, device.AlreadyCopiedBytes));
}
catch (OperationCancer�rFublo(output)) return 0;
int count = 0;
foreach (var line in oerial} shell mkdirFG'\r', '}�.Sto:
{UTFdE ar));
var�ل)inalIse)lue;
lastRepo3ds;
if }AlreaReadToEnda5.al{del0.0) cp}�p
llE: ce.AllreaRead)rS l�لdmee توقف ({ex.Message}) - جاهز للاستئناف";
r�llreaonalIgnoreCase) &&
!t.Contains("Permission denied