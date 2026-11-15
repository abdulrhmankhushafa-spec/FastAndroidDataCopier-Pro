using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FastAndroidDataCopier
{
    public partial class PhoneFolderBrowserDialog : Window
    {
        private readonly string _adbPath;
        private readonly string _serial;
        public string SelectedPath { get; private set; } = "/sdcard/Download/";

        public PhoneFolderBrowserDialog(string adbPath, string serial, string initialPath)
        {
            InitializeComponent();
            _adbPath = adbPath;
            _serial = serial;
            SelectedPath = NormalizeRemotePath(string.IsNullOrWhiteSpace(initialPath) ? "/sdcard/Download/" : initialPath);
            TxtCurrentRemotePath.Text = SelectedPath;
            Loaded += async (_, __) => await LoadDirectoriesAsync(SelectedPath);
        }

        public static string NormalizeRemotePath(string path)
        {
            string p = (path ?? "/sdcard/Download/").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(p)) p = "/sdcard/Download/";
            if (!p.StartsWith("/")) p = "/" + p;
            while (p.Contains("//")) p = p.Replace("//", "/");
            if (!p.EndsWith("/")) p += "/";
            return p;
        }

        public static string EscapeSingleQuotes(string s)
        {
            return (s ?? "").Replace("'", "'\\''");
        }

        private async Task LoadDirectoriesAsync(string remotePath)
        {
            string norm = NormalizeRemotePath(remotePath);
            TxtCurrentRemotePath.Text = norm;
            SelectedPath = norm;
            TxtBrowserStatus.Text = "⏳ جاري قراءة المجلدات من الهاتف...";
            LstFolders.Items.Clear();

            try
            {
                string sqNorm = EscapeSingleQuotes(norm);
                await RunAdbAsync($"-s {_serial} shell \"mkdir -p '{sqNorm}' 2>/dev/null\"");

                var (_, stdout1, _) = await RunAdbAsync($"-s {_serial} shell \"ls -1 -p '{sqNorm}' 2>/dev/null\"");

                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!string.IsNullOrWhiteSpace(stdout1))
                {
                    foreach (var raw in stdout1.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (line.StartsWith("ls:", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (line.EndsWith("/"))
                        {
                            string folder = line.TrimEnd('/');
                            if (!string.IsNullOrWhiteSpace(folder) && folder != "." && folder != "..")
                            {
                                dirs.Add(folder);
                            }
                        }
                    }
                }

                if (dirs.Count == 0)
                {
                    var (_, stdout2, _) = await RunAdbAsync($"-s {_serial} shell \"find '{sqNorm}' -maxdepth 1 -mindepth 1 -type d 2>/dev/null\"");
                    if (!string.IsNullOrWhiteSpace(stdout2))
                    {
                        foreach (var raw in stdout2.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string line = raw.Trim();
                            if (line.StartsWith("find:", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string cleaned = line.TrimEnd('/');
                            int lastSlash = cleaned.LastIndexOf('/');
                            string name = lastSlash >= 0 ? cleaned.Substring(lastSlash + 1) : cleaned;
                            if (!string.IsNullOrWhiteSpace(name) && name != "." && name != "..")
                            {
                                dirs.Add(name);
                            }
                        }
                    }
                }

                foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    LstFolders.Items.Add($"📁 {d}");
                }

                TxtBrowserStatus.Text = dirs.Count > 0
                    ? $"✅ تم العثور على {dirs.Count} مجلد داخل {norm} (انقر مرتين للدخول أو اضغط اعتماد المسار)."
                    : $"📂 المسار {norm} جاهز للنسخ (لا توجد مجلدات فرعية ظاهرة بداخله).";
            }
            catch (Exception ex)
            {
                TxtBrowserStatus.Text = $"⚠️ تعذر قراءة المسار: {ex.Message}";
            }
        }

        private async void LstFolders_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstFolders.SelectedItem is string item)
            {
                string folderName = item.Replace("📁 ", "").Trim();
                if (!string.IsNullOrEmpty(folderName))
                {
                    string nextPath = NormalizeRemotePath(TxtCurrentRemotePath.Text) + folderName + "/";
                    await LoadDirectoriesAsync(nextPath);
                }
            }
        }

        private async void BtnRefreshDir_Click(object sender, RoutedEventArgs e)
        {
            await LoadDirectoriesAsync(TxtCurrentRemotePath.Text);
        }

        private async void BtnUp_Click(object sender, RoutedEventArgs e)
        {
            string current = NormalizeRemotePath(TxtCurrentRemotePath.Text).TrimEnd('/');
            if (string.IsNullOrEmpty(current) || current == "/" || current.Equals("/sdcard", StringComparison.OrdinalIgnoreCase))
            {
                await LoadDirectoriesAsync("/sdcard/");
                return;
            }

            int lastSlash = current.LastIndexOf('/');
            string parent = lastSlash <= 0 ? "/sdcard/" : current.Substring(0, lastSlash + 1);
            await LoadDirectoriesAsync(parent);
        }

        private async void BtnGoPath_Click(object sender, RoutedEventArgs e)
        {
            await LoadDirectoriesAsync(TxtCurrentRemotePath.Text);
        }

        private async void BtnQuickPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                await LoadDirectoriesAsync(path);
            }
        }

        private async void BtnCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            string newName = TxtNewFolderName.Text.Trim().Replace("/", "").Replace("\\", "");
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("يرجى كتابة اسم المجلد الجديد أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string target = NormalizeRemotePath(TxtCurrentRemotePath.Text) + newName + "/";
            string sqTarget = EscapeSingleQuotes(target);
            await RunAdbAsync($"-s {_serial} shell \"mkdir -p '{sqTarget}'\"");
            TxtNewFolderName.Text = "";
            await LoadDirectoriesAsync(target);
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (LstFolders.SelectedItem is string item)
            {
                string sub = item.Replace("📁 ", "").Trim();
                string openPath = NormalizeRemotePath(TxtCurrentRemotePath.Text);
                string subPath = openPath + sub + "/";

                var res = MessageBox.Show(
                    $"لديك مجلد محدد في القائمة ({sub}).\n\n• اضغط (نعم / Yes) للنسخ داخل المجلد المحدد:\n{subPath}\n\n• اضغط (لا / No) للنسخ داخل المسار المفتوح حالياً:\n{openPath}",
                    "تأكيد مسار الوجهة",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Cancel) return;
                SelectedPath = (res == MessageBoxResult.Yes) ? subPath : openPath;
            }
            else
            {
                SelectedPath = NormalizeRemotePath(TxtCurrentRemotePath.Text);
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async Task<(int ExitCode, string StdOut, string StdErr)> RunAdbAsync(string args, int timeoutMs = 8000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
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
                return (-1, "", "Timeout");
            }

            return (p.ExitCode, await outTask, await errTask);
        }
    }
}
