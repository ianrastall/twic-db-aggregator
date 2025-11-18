using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace TWICDBAggregator
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int FirstKnownPgn = 920;
        private static readonly DateTime FirstKnownDate = new(2012, 9, 4);
        private const int MaxLogEntries = 200;
        private static readonly Uri TwicBaseUri = new("http://www.theweekinchess.com/zips/");
        private const int DownloadTimeoutSeconds = 120;
        private const int BufferSize = 131072; // 128KB - more optimal for modern systems

        private readonly ObservableCollection<string> logEntries = new();
        private readonly HttpClient httpClient;
        private CancellationTokenSource? buildCts;
        private readonly object buildLock = new();
        private int totalIssues;
        private int addedIssues;
        private int skippedIssues;
        private bool isInitializing = true;

        private bool IsBuilding 
        { 
            get 
            { 
                lock (buildLock) 
                { 
                    return buildCts != null; 
                } 
            } 
        }

        public MainWindow()
        {
            InitializeComponent();

            // Create HttpClient with proper timeout and keep-alive settings
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(30)
            };
            
            httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds)
            };
            
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TWICDBAggregator/1.0 (+https://theweekinchess.com)");

            calendarStart.DisplayDateStart = FirstKnownDate;
            calendarStart.DisplayDateEnd = DateTime.Now;
            calendarStart.SelectedDate = FirstKnownDate;
            calendarStart.DisplayDate = FirstKnownDate;

            calendarEnd.DisplayDateStart = FirstKnownDate;
            calendarEnd.DisplayDateEnd = DateTime.Now;
            calendarEnd.SelectedDate = DateTime.Today;
            calendarEnd.DisplayDate = DateTime.Today;

            status.ItemsSource = logEntries;
            AppendLog("Ready. (Originally by Ross Hytnen)");
            
            // Initialization complete - now log date range changes
            isInitializing = false;
            
            // Log the initial date range once
            AppendLog($"Date Range: {FirstKnownDate.ToShortDateString()} to {DateTime.Today.ToShortDateString()}");
            
            ValidateData();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            TryEnableDarkTitleBar();
        }

        /// <summary>
        /// Given a selected date, calculates the number of weeks since the start of the TWIC records.
        /// 1 week = 1 pgn and so a simple addition is sufficient to find us the name.
        /// </summary>
        private static int GetPgnFromDate(DateTime date)
        {
            if (date < FirstKnownDate)
            {
                date = FirstKnownDate;
            }

            double week = Math.Floor((date - FirstKnownDate).TotalDays / 7);
            return (int)(FirstKnownPgn + week);
        }

        /// <summary>
        /// Don't allow them to try and build a database until all necessary info is available.
        /// </summary>
        private bool ValidateData()
        {
            bool valid = !string.IsNullOrWhiteSpace(textBoxFileName.Text)
                         && calendarEnd.SelectedDate != null
                         && calendarStart.SelectedDate != null;

            if (!IsBuilding)
                buttonBuild.IsEnabled = valid;
            return valid;
        }

        private void AppendLog(string log)
        {
            // Use BeginInvoke to avoid potential deadlocks from async contexts
            _ = Dispatcher.BeginInvoke(() =>
            {
                logEntries.Add($"{DateTime.Now:HH:mm:ss}  {log}");
                if (logEntries.Count > MaxLogEntries)
                    logEntries.RemoveAt(0);

                if (logEntries.Count > 0 && !isInitializing)
                {
                    status.ScrollIntoView(logEntries[^1]);
                }
            });
        }

        private void SetUiState(bool isBuilding)
        {
            buttonBuild.Content = isBuilding ? "Cancel" : "Build DB";
            textBoxFileName.IsEnabled = !isBuilding;
            buttonFileChooser.IsEnabled = !isBuilding;
            rbAppend.IsEnabled = !isBuilding;
            rbCreateNew.IsEnabled = !isBuilding;
            calendarEnd.IsEnabled = !isBuilding;
            calendarStart.IsEnabled = !isBuilding;
            buttonBuild.IsEnabled = isBuilding || ValidateData();
        }

        private void UpdateProgressCounters()
        {
            statusAdded.Text = addedIssues.ToString();
            statusTotal.Text = totalIssues.ToString();
            statusSkipped.Text = skippedIssues.ToString();
        }

        /// <summary>
        /// Cleans out any leftover files.
        /// </summary>
        private void PurgeAppData()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TWICDBAggregator");
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(path))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; safe to ignore.
                }
            }
        }

        /// <summary>
        /// Downloads, unzips, and merges the database.
        /// </summary>
        private async Task BuildDatabaseAsync(CancellationToken cancellationToken)
        {
            AppendLog("Starting database build...");

            DateTime startDate = calendarStart.SelectedDate ?? FirstKnownDate;
            DateTime endDate = calendarEnd.SelectedDate ?? DateTime.Today;

            // Ensure we never fetch TWIC issues that predate the first known archive number.
            int first = Math.Max(FirstKnownPgn, GetPgnFromDate(startDate));
            int second = Math.Max(FirstKnownPgn, GetPgnFromDate(endDate));

            if (second < first)
            {
                (first, second) = (second, first);
            }

            addedIssues = 0;
            skippedIssues = 0;
            totalIssues = Math.Max(0, (second - first) + 1);
            UpdateProgressCounters();

            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TWICDBAggregator");
            Directory.CreateDirectory(path);

            FileMode fileMode = rbCreateNew.IsChecked == true ? FileMode.Create : FileMode.Append;

            await using FileStream? output = await OpenOutputStreamAsync(textBoxFileName.Text, fileMode, cancellationToken);
            if (output == null)
            {
                return;
            }

            bool wroteData = false;
            for (int pgnNumber = first; pgnNumber <= second; pgnNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string baseName = $"twic{pgnNumber}";
                string zipFilename = $"{baseName}g.zip";
                string pgnFilename = $"{baseName}.pgn";
                Uri url = new(TwicBaseUri, zipFilename);

                string zipPath = Path.Combine(path, zipFilename);
                string pgnPath = Path.Combine(path, pgnFilename);

                if (!await DownloadZipAsync(url, zipPath, cancellationToken))
                {
                    skippedIssues++;
                    UpdateProgressCounters();
                    if (stopOnSkip.IsChecked == true)
                    {
                        AppendLog("Stopping because a TWIC issue was skipped.");
                        break;
                    }
                    continue;
                }

                if (!await ExtractFirstEntryAsync(zipPath, pgnPath, pgnFilename, cancellationToken))
                {
                    skippedIssues++;
                    UpdateProgressCounters();
                    if (stopOnSkip.IsChecked == true)
                    {
                        AppendLog("Stopping because a TWIC issue was skipped.");
                        break;
                    }
                    continue;
                }

                if (!await AppendFileAsync(pgnPath, output, cancellationToken))
                {
                    skippedIssues++;
                    UpdateProgressCounters();
                    if (stopOnSkip.IsChecked == true)
                    {
                        AppendLog("Stopping because a TWIC issue was skipped.");
                        break;
                    }
                    continue;
                }

                wroteData = true;
                addedIssues++;
                UpdateProgressCounters();
                TryCleanup(zipPath, pgnPath);
            }

            // Ensure all data is flushed to disk
            await output.FlushAsync(cancellationToken);

            AppendLog(wroteData ? "Database build complete." : "No TWIC issues downloaded for the selected range.");
        }

        private async Task<FileStream?> OpenOutputStreamAsync(string outputPath, FileMode mode, CancellationToken cancellationToken)
        {
            try
            {
                var stream = new FileStream(outputPath, mode, FileAccess.Write, FileShare.Read, bufferSize: BufferSize, useAsync: true);
                if (mode == FileMode.Append)
                {
                    stream.Seek(0, SeekOrigin.End);
                }

                await stream.FlushAsync(cancellationToken);
                return stream;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"Can't open database for writing: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> DownloadZipAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
        {
            try
            {
                AppendLog($"Downloading {url}");
                
                using HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    AppendLog($"Download failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {url}");
                    return false;
                }

                await using FileStream destination = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: BufferSize, useAsync: true);
                await response.Content.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                return true;
            }
            catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
            {
                // This is a timeout (not user cancellation)
                AppendLog($"Download timed out for {url}");
                TryCleanup(destinationPath);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                AppendLog($"Failed to download {url}: {ex.Message}");
                TryCleanup(destinationPath);
                return false;
            }
        }

        private async Task<bool> ExtractFirstEntryAsync(string zipPath, string destinationPath, string expectedPgnName, CancellationToken cancellationToken)
        {
            try
            {
                AppendLog($"Unzipping {Path.GetFileName(zipPath)}");
                using ZipArchive zip = ZipFile.OpenRead(zipPath);
                
                // Find the specific PGN file by name, ignoring case
                ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e => 
                    e.Name.Equals(expectedPgnName, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    AppendLog($"Archive {Path.GetFileName(zipPath)} does not contain expected file {expectedPgnName}.");
                    return false;
                }

                await using Stream entryStream = entry.Open();
                await using FileStream stream = new(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: BufferSize, useAsync: true);
                await entryStream.CopyToAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                AppendLog($"Failed to extract {Path.GetFileName(zipPath)}: {ex.Message}");
                TryCleanup(destinationPath);
                return false;
            }
        }

        private async Task<bool> AppendFileAsync(string sourcePath, FileStream destination, CancellationToken cancellationToken)
        {
            try
            {
                AppendLog($"Merging {Path.GetFileName(sourcePath)}");
                await using FileStream stream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: BufferSize, useAsync: true);
                await stream.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog($"Failed to write to database: {ex.Message}");
                return false;
            }
        }

        private void TryCleanup(params string[] filePaths)
        {
            foreach (string file in filePaths)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Cleanup failure is not fatal for subsequent files.
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cancel any ongoing build operation
            lock (buildLock)
            {
                buildCts?.Cancel();
            }
            
            // Don't dispose HttpClient immediately - give pending operations a chance to cancel gracefully
            // The finalizer will clean it up
            base.OnClosed(e);
        }

        private void TryEnableDarkTitleBar()
        {
            IntPtr hWnd = new WindowInteropHelper(this).EnsureHandle();
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            int trueValue = 1;
            _ = DwmSetWindowAttribute(hWnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (e.ClickCount == 2)
            {
                // Disable maximize on double-click to keep layout consistent.
                e.Handled = true;
                return;
            }

            DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CopyLogEntry_Click(object sender, RoutedEventArgs e)
        {
            if (status.SelectedItem is string selectedEntry)
            {
                Clipboard.SetText(selectedEntry);
                AppendLog("Log entry copied to clipboard.");
            }
        }

        private void CopyAllLog_Click(object sender, RoutedEventArgs e)
        {
            if (logEntries.Count > 0)
            {
                string allLogs = string.Join(Environment.NewLine, logEntries);
                Clipboard.SetText(allLogs);
                AppendLog("All log entries copied to clipboard.");
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            logEntries.Clear();
            AppendLog("Log cleared.");
        }
    }
}
