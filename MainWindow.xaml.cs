using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace AntiStorage
{
    public partial class MainWindow : Window
    {
        private const int ResultLimit = 2000;
        private const int TemporaryAgeDays = 7;
        private const string EmptyValue = "\u2014";
        private readonly SnapshotStore store;
        private readonly string tempFolder;
        private FolderScope scope;
        private StorageResults storage;
        private CancellationTokenSource operation;
        private bool ready;

        public MainWindow() : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntiStorage"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"))
        {
        }

        internal MainWindow(string stateDirectory, string tempDirectory)
        {
            store = new SnapshotStore(stateDirectory);
            tempFolder = ScanPaths.NormalizeDirectory(tempDirectory);
            InitializeComponent();
            InitializeCleaner(stateDirectory);

            TempPath.Text = tempFolder;
            TemporarySummary.Text = "Files for review";
            TemporaryEmpty.Text = "Find temporary files to start the review.";
            ready = true;
            RenderStorage();
            Loaded += RestoreScope;
        }

        private async void RestoreScope(object sender, RoutedEventArgs args)
        {
            Loaded -= RestoreScope;
            await RunWorkAsync("Loading scan history\u2026", false, async cancellation =>
            {
                var saved = await Task.Run(() => store.ReadLastScope());
                if (saved != null)
                    await LoadScopeAsync(saved);
                Status.Text = string.Empty;
            });
            CheckScheduledReview();
        }

        private void ShowStorage(object sender, RoutedEventArgs args)
        {
            CleanerPage.Visibility = Visibility.Collapsed;
            StoragePage.Visibility = Visibility.Visible;
            TemporaryPage.Visibility = Visibility.Collapsed;
            if (operation == null)
                Status.Text = string.Empty;
        }

        private void ShowTemporary(object sender, RoutedEventArgs args)
        {
            CleanerPage.Visibility = Visibility.Collapsed;
            StoragePage.Visibility = Visibility.Collapsed;
            TemporaryPage.Visibility = Visibility.Visible;
            if (operation == null)
                Status.Text = string.Empty;
        }

        private static string Browse(string initialPath, string description)
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.ShowNewFolderButton = false;
                if (initialPath != null)
                    dialog.SelectedPath = initialPath;
                return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private async void ChooseFolder(object sender, RoutedEventArgs args)
        {
            var selected = Browse(scope == null ? null : scope.Root, "Choose a folder to track with AntiStorage.");
            if (selected != null)
                await SelectFolderAsync(selected);
        }

        private async Task SelectFolderAsync(string selected)
        {
            await RunWorkAsync("Loading scan history\u2026", false, async cancellation =>
            {
                var root = ScanPaths.NormalizeDirectory(selected);
                await Task.Run(() => ScanPaths.EnsureRegularDirectory(root));
                var next = scope;
                if (next == null || !string.Equals(next.Root, root, StringComparison.OrdinalIgnoreCase))
                {
                    var saved = await Task.Run(() => store.ReadLastScope());
                    next = saved != null && string.Equals(saved.Root, root, StringComparison.OrdinalIgnoreCase)
                        ? saved
                        : new FolderScope { Root = root, Exclusions = new List<string>() };
                }
                await LoadScopeAsync(next);
                Status.Text = string.Empty;
            });
        }

        private async Task LoadScopeAsync(FolderScope next)
        {
            var results = await Task.Run(() => StorageResults.FromHistory(store.ReadHistory(next)));
            scope = next;
            storage = results;
            ChangesTab.IsChecked = results.Comparison != null || results.Latest == null;
            LargestTab.IsChecked = ChangesTab.IsChecked != true;
            RenderStorage();
        }

        private async void AddExclusion(object sender, RoutedEventArgs args)
        {
            var selected = Browse(scope.Root, "Choose a subfolder to exclude from scans.");
            if (selected == null)
                return;

            var folder = ScanPaths.NormalizeDirectory(selected);
            if (!ScanPaths.IsChildOf(folder, scope.Root))
            {
                Status.Text = "Choose a subfolder inside the scan folder.";
                return;
            }
            if (scope.Exclusions.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                Status.Text = "That folder is already excluded.";
                return;
            }
            await ChangeExclusionsAsync(scope.Exclusions.Concat(new[] { folder }));
        }

        private async void RemoveExclusion(object sender, RoutedEventArgs args)
        {
            var selected = (string)ExcludedFolders.SelectedItem;
            await ChangeExclusionsAsync(scope.Exclusions.Where(path => path != selected));
        }

        private async Task ChangeExclusionsAsync(IEnumerable<string> exclusions)
        {
            await RunWorkAsync("Loading scan history\u2026", false, async cancellation =>
            {
                await LoadScopeAsync(new FolderScope { Root = scope.Root, Exclusions = exclusions.ToList() });
                Status.Text = "Exclusions updated. Comparisons use scans with the same exclusions.";
            });
        }

        private async void ScanFolder(object sender, RoutedEventArgs args)
        {
            if (scope == null)
            {
                var selected = Browse(null, "Choose a folder to scan.");
                if (selected == null)
                    return;
                await SelectFolderAsync(selected);
                if (scope == null)
                    return;
            }
            await CaptureStorageAsync();
        }

        private Task CaptureStorageAsync()
        {
            return RunWorkAsync("Scanning folder\u2026", true, async cancellation =>
            {
                var selected = scope;
                var excluded = selected.Exclusions.ToList();
                if (ScanPaths.IsChildOf(store.StateDirectory, selected.Root))
                    excluded.Add(store.StateDirectory);

                var progress = ScanProgress(cancellation);
                var snapshot = await Task.Run(() => StorageScan.Capture(selected.Root, excluded, null, progress, cancellation));
                cancellation.ThrowIfCancellationRequested();
                Status.Text = "Saving scan and preparing results\u2026";
                storage = await Task.Run(() => StorageResults.FromHistory(store.Record(selected, snapshot, cancellation)));
                ChangesTab.IsChecked = storage.Comparison != null;
                LargestTab.IsChecked = storage.Comparison == null;
                RenderStorage();
                Status.Text = "Scan saved.";
            });
        }

        private async void ScanTemporary(object sender, RoutedEventArgs args)
        {
            await CaptureTemporaryAsync();
        }

        private Task CaptureTemporaryAsync()
        {
            return RunWorkAsync("Looking for older temporary files\u2026", true, async cancellation =>
            {
                var progress = ScanProgress(cancellation);
                var cutoff = DateTime.UtcNow.AddDays(-TemporaryAgeDays);
                var output = await Task.Run(() =>
                {
                    var snapshot = StorageScan.Capture(tempFolder, new string[0], cutoff, progress, cancellation);
                    return new { Snapshot = snapshot, Rows = LargestFiles(snapshot) };
                });
                cancellation.ThrowIfCancellationRequested();
                TemporaryTable.ItemsSource = output.Rows;
                var count = output.Snapshot.Files.Count;
                TemporarySummary.Text = string.Format("{0} across {1:N0} {2}", Bytes(output.Snapshot.TotalBytes), count, count == 1 ? "file" : "files");
                TemporaryDetails.Text = ResultLimitNote(count) + SkippedNote(output.Snapshot.UnobservedPaths.Count);
                TemporaryEmpty.Text = "No temporary files met the seven-day rule.";
                TemporaryEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Status.Text = "Temporary-file review ready.";
            });
        }

        private IProgress<int> ScanProgress(CancellationToken cancellation)
        {
            return new Progress<int>(count =>
            {
                if (operation != null && operation.Token == cancellation && !cancellation.IsCancellationRequested)
                    Status.Text = string.Format("Scanning\u2026 {0:N0} files found", count);
            });
        }

        private async Task RunWorkAsync(string label, bool cancellable, Func<CancellationToken, Task> work)
        {
            if (operation != null)
                return;

            using (var source = new CancellationTokenSource())
            {
                operation = source;
                CleanerPage.IsEnabled = false;
                CleanerNavigation.IsEnabled = false;
                SettingsNavigation.IsEnabled = false;
                Status.Text = label;
                WorkIndicator.Visibility = Visibility.Visible;
                CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
                CancelButton.IsEnabled = true;
                UpdateControls();
                try
                {
                    await work(source.Token);
                }
                catch (OperationCanceledException)
                {
                    Status.Text = "Scan canceled. Saved scan results are still available.";
                }
                catch (Exception error)
                {
                    Status.Text = error.Message;
                }
                finally
                {
                    operation = null;
                    CleanerPage.IsEnabled = true;
                    CleanerNavigation.IsEnabled = true;
                    SettingsNavigation.IsEnabled = true;
                    WorkIndicator.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                    UpdateControls();
                }
            }
        }

        private void CancelScan(object sender, RoutedEventArgs args)
        {
            operation.Cancel();
            CancelButton.IsEnabled = false;
            Status.Text = "Canceling scan\u2026";
        }

        private void RenderStorage()
        {
            FolderPath.Text = scope == null ? "No folder selected" : scope.Root;
            ExcludedFolders.ItemsSource = scope == null ? null : scope.Exclusions;
            Exclusions.Header = scope == null || scope.Exclusions.Count == 0
                ? "Folder exclusions"
                : string.Format("Folder exclusions ({0})", scope.Exclusions.Count);
            var latest = storage == null ? null : storage.Latest;
            var comparison = storage == null ? null : storage.Comparison;
            ScannedSize.Text = latest == null ? EmptyValue : Bytes(latest.TotalBytes);
            ScannedCount.Text = latest == null ? EmptyValue : latest.Files.Count.ToString("N0");
            StorageDelta.Text = comparison == null ? EmptyValue : SignedBytes(comparison.ObservedDelta);
            RenderResults();
        }

        private void ChangeResults(object sender, RoutedEventArgs args)
        {
            RenderResults();
        }

        private void RenderResults()
        {
            var latest = storage == null ? null : storage.Latest;
            var comparison = storage == null ? null : storage.Comparison;
            var showChanges = ChangesTab.IsChecked == true;
            BeforeColumn.Visibility = AfterColumn.Visibility = DeltaColumn.Visibility = showChanges ? Visibility.Visible : Visibility.Collapsed;
            SizeColumn.Visibility = ModifiedColumn.Visibility = showChanges ? Visibility.Collapsed : Visibility.Visible;
            var rows = storage == null ? null : showChanges ? storage.ChangeRows : storage.LargestRows;
            StorageTable.ItemsSource = rows;
            StorageEmpty.Visibility = rows == null || rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (latest == null)
            {
                StorageDetails.Text = "The first scan establishes a baseline.";
                StorageEmpty.Text = scope == null ? "Choose a folder to get started." : "Scan this folder to see its files.";
            }
            else if (showChanges && comparison != null)
            {
                StorageDetails.Text = string.Format("{0} to {1}  \u00b7  {2:N0} files changed.",
                    Stamp(storage.Previous.CapturedUtc), Stamp(latest.CapturedUtc), comparison.Changes.Count)
                    + ResultLimitNote(comparison.Changes.Count)
                    + SkippedNote(storage.Previous.UnobservedPaths.Count + latest.UnobservedPaths.Count)
                    + (comparison.UnknownCount == 0 ? string.Empty : string.Format(" {0:N0} file changes could not be verified.", comparison.UnknownCount));
                StorageEmpty.Text = "No file-size changes were observed between these scans.";
            }
            else
            {
                StorageDetails.Text = (comparison == null ? "Baseline captured " : "Scanned ") + Stamp(latest.CapturedUtc) + "."
                    + (comparison == null ? " Scan again to compare changes." : string.Empty)
                    + (!showChanges ? ResultLimitNote(latest.Files.Count) : string.Empty)
                    + SkippedNote(latest.UnobservedPaths.Count);
                StorageEmpty.Text = showChanges ? "Take a second scan to see what changed." : "No files were found in the scanned folders.";
            }
            UpdateControls();
        }

        private void UpdateSelection(object sender, SelectionChangedEventArgs args)
        {
            UpdateControls();
        }

        private void UpdateControls()
        {
            if (!ready)
                return;

            var idle = operation == null;
            ChooseFolderButton.IsEnabled = ScanButton.IsEnabled = ReviewButton.IsEnabled = idle;
            Exclusions.IsEnabled = idle && scope != null;
            RemoveExclusionButton.IsEnabled = ExcludedFolders.SelectedItem != null;
            var selected = StorageTable.SelectedItem as FileRow;
            RevealStorageButton.IsEnabled = idle && selected != null && selected.FilePresent;
            RevealTemporaryButton.IsEnabled = idle && TemporaryTable.SelectedItem != null;
        }

        private void RevealStorageFile(object sender, RoutedEventArgs args)
        {
            Reveal(scope.Root, (FileRow)StorageTable.SelectedItem);
        }

        private void RevealTemporaryFile(object sender, RoutedEventArgs args)
        {
            Reveal(tempFolder, (FileRow)TemporaryTable.SelectedItem);
        }

        private void Reveal(string root, FileRow row)
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, row.RelativePath));
                if (!ScanPaths.IsChildOf(path, root) || !File.Exists(path))
                {
                    Status.Text = "The file is unavailable. Scan again to update the results.";
                    return;
                }
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(explorer, "/select,\"" + path + "\"");
            }
            catch (Exception error)
            {
                Status.Text = error.Message;
            }
        }

        private static string ResultLimitNote(int count)
        {
            return count <= ResultLimit ? string.Empty : string.Format(" Showing the top {0:N0} of {1:N0} files.", ResultLimit, count);
        }

        private static string SkippedNote(int count)
        {
            return count == 0 ? string.Empty : string.Format(" {0:N0} unreadable, linked or offline {1} skipped.", count, count == 1 ? "path" : "paths");
        }

        private static string Stamp(DateTime utc)
        {
            return utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }

        private static string SignedBytes(long value)
        {
            return (value < 0 ? "\u2212" : value > 0 ? "+" : string.Empty) + Bytes(Math.Abs(value));
        }

        private static string Bytes(long value)
        {
            var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB" };
            var size = (double)value;
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return size.ToString(unit == 0 ? "N0" : "N2") + " " + units[unit];
        }

        private static List<FileRow> LargestFiles(StorageSnapshot snapshot)
        {
            return snapshot.Files.OrderByDescending(file => file.Bytes).ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .Take(ResultLimit).Select(file => new FileRow
                {
                    RelativePath = file.RelativePath,
                    After = Bytes(file.Bytes),
                    Modified = Stamp(file.ModifiedUtc),
                    FilePresent = true
                }).ToList();
        }

        private sealed class StorageResults
        {
            internal StorageSnapshot Latest;
            internal StorageSnapshot Previous;
            internal SnapshotComparison Comparison;
            internal List<FileRow> LargestRows;
            internal List<FileRow> ChangeRows;

            internal static StorageResults FromHistory(SnapshotHistory history)
            {
                var results = new StorageResults { Latest = history.Latest, Previous = history.Previous };
                if (history.Latest != null)
                    results.LargestRows = LargestFiles(history.Latest);
                if (history.Previous != null)
                {
                    results.Comparison = SnapshotComparison.Create(history.Previous, history.Latest);
                    results.ChangeRows = results.Comparison.Changes.OrderByDescending(change => Math.Abs(change.Delta))
                        .ThenBy(change => change.RelativePath, StringComparer.Ordinal).Take(ResultLimit)
                        .Select(change => new FileRow
                        {
                            RelativePath = change.RelativePath,
                            Before = change.Before.HasValue ? Bytes(change.Before.Value) : EmptyValue,
                            After = change.After.HasValue ? Bytes(change.After.Value) : EmptyValue,
                            Delta = SignedBytes(change.Delta),
                            FilePresent = change.After.HasValue
                        }).ToList();
                }
                return results;
            }
        }

        private sealed class FileRow
        {
            public string RelativePath { get; set; }
            public string Before { get; set; }
            public string After { get; set; }
            public string Delta { get; set; }
            public string Modified { get; set; }
            internal bool FilePresent { get; set; }
        }
    }
}
