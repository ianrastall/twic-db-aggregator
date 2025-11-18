using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TWICDBAggregator
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private void calendarStart_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (calendarStart.SelectedDate == null)
            {
                return;
            }

            if (calendarEnd.SelectedDate == null)
            {
                calendarEnd.SelectedDate = calendarStart.SelectedDate;
            }

            DateTime startDate = calendarStart.SelectedDate.Value;
            DateTime endDate = calendarEnd.SelectedDate ?? startDate;

            if ((endDate - startDate).TotalDays < 0)
            {
                calendarEnd.SelectedDate = startDate;
                calendarEnd.DisplayDate = calendarStart.DisplayDate;
                endDate = startDate;
            }

            if (!isInitializing)
            {
                AppendLog($"Date Range: {startDate.ToShortDateString()} to {endDate.ToShortDateString()}");
            }
            
            ValidateData();
            SaveSettings();
        }

        private void calendarEnd_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (calendarEnd.SelectedDate == null && calendarStart.SelectedDate == null)
            {
                return;
            }

            if (calendarStart.SelectedDate == null)
            {
                calendarStart.SelectedDate = calendarEnd.SelectedDate;
            }

            DateTime startDate = calendarStart.SelectedDate ?? FirstKnownDate;
            DateTime endDate = calendarEnd.SelectedDate ?? startDate;

            if ((endDate - startDate).TotalDays < 0)
            {
                calendarStart.SelectedDate = endDate;
                calendarStart.DisplayDate = calendarEnd.DisplayDate;
                startDate = endDate;
            }

            if (!isInitializing)
            {
                AppendLog($"Date Range: {startDate.ToShortDateString()} to {endDate.ToShortDateString()}");
            }
            
            ValidateData();
            SaveSettings();
        }

        /// <summary>
        /// Pick a file for the big database.
        /// </summary>
        private void buttonFileChooser_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                FileName = "PublicTWIC",
                DefaultExt = ".pgn",
                Filter = "PGN files (.pgn)|*.pgn"
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
            {
                textBoxFileName.Text = dialog.FileName;
            }

            ValidateData();
            SaveSettings();
        }

        /// <summary>
        /// Validate data if they enter the filename by hand
        /// </summary>
        private void textBoxFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Validate path for invalid characters
            if (!string.IsNullOrWhiteSpace(textBoxFileName.Text))
            {
                string path = textBoxFileName.Text.Trim();
                char[] invalidChars = Path.GetInvalidPathChars();
                
                if (path.Any(c => invalidChars.Contains(c)))
                {
                    AppendLog("File path contains invalid characters.");
                    buttonBuild.IsEnabled = false;
                    return;
                }
            }
            
            ValidateData();
            SaveSettings();
        }

        /// <summary>
        /// Start or cancel building the database.
        /// </summary>
        private async void buttonBuild_Click(object sender, RoutedEventArgs e)
        {
            if (IsBuilding)
            {
                AppendLog("Cancelling build ...");
                buttonBuild.IsEnabled = false;
                
                lock (buildLock)
                {
                    buildCts?.Cancel();
                }
                return;
            }

            if (!ValidateData())
            {
                AppendLog("Select a valid date range and output file before building.");
                return;
            }

            CancellationTokenSource? localCts = null;
            try
            {
                localCts = new CancellationTokenSource();
                
                lock (buildLock)
                {
                    buildCts = localCts;
                }
                
                SetUiState(true);

                await BuildDatabaseAsync(localCts.Token);
            }
            catch (OperationCanceledException)
            {
                AppendLog("Build canceled.");
            }
            catch (Exception ex)
            {
                AppendLog($"Build failed: {ex.Message}");
            }
            finally
            {
                lock (buildLock)
                {
                    buildCts = null;
                }
                
                localCts?.Dispose();
                PurgeAppData();
                SetUiState(false);
            }
        }
    }
}
