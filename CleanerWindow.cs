using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AntiStorage
{
    public partial class MainWindow
    {
        private string cleanerState;
        private RecoveryStore recovery;
        private CleanerPreferences preferences;
        private readonly Dictionary<string, RadioButton> cleanerPages = new Dictionary<string, RadioButton>();
        private const string CleanupHome = "Clean up";
        private const string StorageTools = "Storage";
        private const string MoreTools = "More tools";
        private const string SettingsPage = "Settings & help";
        private string activePage;
        private Action beginHealthScan;
        private DispatcherTimer reminder;
        private string settingsError;

        private void InitializeCleaner(string state)
        {
            cleanerState = state;
            recovery = new RecoveryStore(state);
            try { preferences = CleanerPreferences.Load(state); }
            catch (Exception error)
            {
                preferences = new CleanerPreferences();
                settingsError = "Saved settings could not be read. " + error.Message;
            }
            AddNavigation(CleanerNavigation, CleanupHome, StorageTools, "Recovery", MoreTools);
            AddNavigation(SettingsNavigation, SettingsPage);
            Navigate(CleanupHome);
            reminder = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            reminder.Tick += (sender, args) => CheckScheduledReview();
            reminder.Start();
            ContentRendered += (sender, args) => Dispatcher.BeginInvoke(new Action(CheckScheduledReview), DispatcherPriority.ApplicationIdle);
            Closing += (sender, args) =>
            {
                if (operation != null)
                {
                    args.Cancel = true;
                    Status.Text = "Wait for the current operation to finish or cancel the scan.";
                }
                else reminder.Stop();
            };
        }

        private void AddNavigation(Panel panel, params string[] pages)
        {
            foreach (var name in pages)
            {
                var button = new RadioButton { GroupName = "Pages", Content = name, Style = (Style)FindResource("Navigation"),
                    Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 4) };
                button.Click += (sender, args) => OpenPage(name);
                cleanerPages.Add(name, button);
                panel.Children.Add(button);
            }
        }

        private static string SectionFor(string name)
        {
            switch (name)
            {
                case CleanupHome:
                case "Custom clean": return CleanupHome;
                case StorageTools:
                case "Storage activity":
                case "Disk analyzer":
                case "Duplicate finder":
                case "Cloud storage": return StorageTools;
                case "Recovery":
                case SettingsPage: return name;
                default: return MoreTools;
            }
        }

        private void Navigate(string name)
        {
            activePage = name;
            cleanerPages[SectionFor(name)].IsChecked = true;
            if (name == "Storage activity")
            {
                ShowStorage(this, new RoutedEventArgs());
                return;
            }
            StoragePage.Visibility = Visibility.Collapsed;
            TemporaryPage.Visibility = Visibility.Collapsed;
            CleanerPage.Visibility = Visibility.Visible;
            if (operation == null) Status.Text = "";
            switch (name)
            {
                case CleanupHome:
                    ShowCleaning(true);
                    Dispatcher.BeginInvoke(new Action(CheckScheduledReview), DispatcherPriority.ApplicationIdle);
                    break;
                case "Custom clean": ShowCleaning(false); break;
                case "Recovery": ShowRecovery(); break;
                case StorageTools: ShowStorageDirectory(); break;
                case MoreTools: ShowToolDirectory(); break;
                case "Disk analyzer": ShowAnalyzer("disk"); break;
                case "Duplicate finder": ShowAnalyzer("duplicates"); break;
                case "Cloud storage": ShowAnalyzer("cloud"); break;
                case "Browser privacy": ShowBrowserPrivacy(); break;
                case "Recycle Bin": ShowRecycleOptions(); break;
                case SettingsPage: ShowSettings(); break;
                default: ShowWindowsTool(name); break;
            }
        }

        private void OpenPage(string page)
        {
            if (operation != null) return;
            Navigate(page);
        }

        private void OpenStorageTools(object sender, RoutedEventArgs args)
        {
            OpenPage(StorageTools);
        }

        private void ShowStorageDirectory()
        {
            var layout = ToolLayoutFor(StorageTools, "Find what is taking up space or what changed since your last scan.");
            var panel = ScrollingContent(layout);
            AddTool(panel, "Find large files", "See the largest files on a drive or in a folder.", "Disk analyzer");
            AddTool(panel, "Find duplicates", "Compare file contents and review extra copies.", "Duplicate finder");
            AddTool(panel, "Track folder changes", "Compare scans to see which files made a folder grow.", "Storage activity");
            AddTool(panel, "Synced storage", "Review local copies in a cloud folder.", "Cloud storage");
        }

        private void ShowToolDirectory()
        {
            var panel = ScrollingContent(ToolLayoutFor(MoreTools, "Choose a tool for a specific task."));
            var apps = Section("Apps and updates", "", panel);
            AddTool(apps, "Uninstall apps", "Review installed desktop apps and open their uninstall options.", WindowsTools.Apps);
            AddTool(apps, "Manage startup apps", "Choose what opens when you sign in to Windows.", WindowsTools.Startup);
            AddTool(apps, "Check for app updates", "Find updates available through WinGet.", WindowsTools.Software);
            var privacy = Section("Privacy and cleanup", "", panel);
            AddTool(privacy, "Browser privacy", "Open controls for browsing history, cookies and extensions.", "Browser privacy");
            AddTool(privacy, "Recycle Bin", "Review discarded files or empty the bin.", "Recycle Bin");
            var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(new Expander { Header = new TextBlock { Text = "Advanced Windows tools",
                FontWeight = FontWeights.SemiBold }, Content = advanced });
            AddTool(advanced, "Running apps", "See memory use and close an app from the list.", WindowsTools.Performance);
            AddTool(advanced, "Driver updates", "See installed drivers and check for updates from Windows.", WindowsTools.Drivers);
            AddTool(advanced, "Registry review", "Find specific broken app references and back up changes.", WindowsTools.RegistryAudit);
            AddTool(advanced, "System information", "See PC hardware and open Windows maintenance tools.", WindowsTools.SystemInfo);
        }

        private void AddTool(Panel panel, string title, string detail, string page)
        {
            var button = ButtonFor(panel, title, () => OpenPage(page));
            button.Style = (Style)FindResource("DirectoryItem");
            button.Margin = new Thickness(0, 0, 0, 7);
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            var row = new DockPanel();
            var arrow = new TextBlock { Text = "\u203a", FontSize = 21, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("Green"), Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(arrow, Dock.Right);
            row.Children.Add(arrow);
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            content.Children.Add(Note(detail));
            row.Children.Add(content);
            button.Content = row;
        }

        private sealed class ToolLayout
        {
            internal StackPanel Header;
            internal WrapPanel Actions;
            internal Border Content;
        }

        private ToolLayout ToolLayoutFor(string title, string description)
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
            var parent = SectionFor(activePage);
            if (activePage != parent)
            {
                var back = TextButtonFor(top, parent == CleanupHome ? "Back to Clean up" :
                    parent == StorageTools ? "All storage tools" : "All tools", () => OpenPage(parent));
                back.HorizontalAlignment = HorizontalAlignment.Left;
                back.Padding = new Thickness(0, 1, 6, 1);
                back.MinHeight = 24;
            }
            top.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Heading") });
            if (!string.IsNullOrWhiteSpace(description)) top.Children.Add(Note(description));
            var middle = new Border { Style = (Style)FindResource("Panel"), ClipToBounds = true };
            var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var footer = new StackPanel();
            footer.Children.Add(actions);
            Grid.SetRow(middle, 1);
            Grid.SetRow(footer, 2);
            grid.Children.Add(top);
            grid.Children.Add(middle);
            grid.Children.Add(footer);
            CleanerPage.Content = grid;
            return new ToolLayout { Header = top, Content = middle, Actions = actions };
        }

        private TextBlock Note(string text)
        {
            return new TextBlock { Text = text, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 4) };
        }

        private Button ButtonFor(Panel panel, string text, Action action, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 5) };
            if (primary) button.Style = (Style)FindResource("Primary");
            button.Click += (sender, args) =>
            {
                if (operation != null) return;
                Attempt(action);
            };
            panel.Children.Add(button);
            return button;
        }

        private Button TextButtonFor(Panel panel, string text, Action action)
        {
            var button = ButtonFor(panel, text, action);
            button.Style = (Style)FindResource("TextButton");
            return button;
        }

        private void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { Status.Text = error.Message; }
        }

        private void AsyncButtonFor(Panel panel, string text, Func<Task> action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 5) };
            button.Click += async (sender, args) =>
            {
                if (operation != null) return;
                try { await action(); }
                catch (Exception error) { Status.Text = error.Message; }
            };
            panel.Children.Add(button);
        }

        private static StackPanel Section(string title, string description, Panel parent)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            section.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(description))
                section.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            parent.Children.Add(section);
            return section;
        }

        private StackPanel ScrollingContent(ToolLayout layout)
        {
            var panel = new StackPanel { Margin = new Thickness(19) };
            layout.Content.VerticalAlignment = VerticalAlignment.Stretch;
            layout.Content.Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            return panel;
        }

        private DataGrid Table(params string[] columns)
        {
            var grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended, CanUserSortColumns = true };
            for (var i = 0; i < columns.Length; i += 2)
            {
                var last = i + 2 == columns.Length;
                var cell = new Style(typeof(TextBlock));
                cell.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                cell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(columns[i + 1])));
                grid.Columns.Add(new DataGridTextColumn { Header = columns[i], Binding = new Binding(columns[i + 1]),
                    MinWidth = i == 0 ? 185 : last ? 128 : 100,
                    Width = i == 0 ? new DataGridLength(1.8, DataGridLengthUnitType.Star) :
                        last ? new DataGridLength(1.2, DataGridLengthUnitType.Star) : new DataGridLength(135),
                    ElementStyle = cell });
            }
            return grid;
        }

        private static void ResultPanel(Border border, DataGrid table, out TextBlock summary)
        {
            var layout = new DockPanel();
            summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Padding = new Thickness(12, 9, 12, 9) };
            DockPanel.SetDock(summary, Dock.Top);
            layout.Children.Add(summary);
            layout.Children.Add(table);
            ((INotifyCollectionChanged)table.Items).CollectionChanged += (sender, args) =>
                table.Visibility = table.Items.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            table.Visibility = Visibility.Collapsed;
            border.VerticalAlignment = VerticalAlignment.Stretch;
            border.Child = layout;
        }

        private static void SelectFiles(DataGrid table, Func<CleanFile, bool> filter)
        {
            table.UnselectAll();
            var files = table.ItemsSource as IEnumerable<CleanFile>;
            if (files == null) return;
            foreach (var file in files.Where(filter)) table.SelectedItems.Add(file);
        }

        private void CleanSelected(DataGrid table, TextBlock summary)
        {
            var chosen = table.SelectedItems.OfType<CleanFile>().Where(file => !file.AuditOnly).ToList();
            var all = table.ItemsSource as IEnumerable<CleanFile>;
            CleanFiles(chosen, all, outcome =>
            {
                table.ItemsSource = all.Where(file => !outcome.Completed.Contains(file)).ToList();
                summary.Text = outcome.Summary;
            });
        }

        private async void CleanFiles(List<CleanFile> chosen, IEnumerable<CleanFile> all, Action<CleanOutcome> complete)
        {
            if (chosen.Count == 0 || all == null) { Status.Text = "Select files that can be cleaned."; return; }
            var selected = new HashSet<CleanFile>(chosen);
            var keepers = all.Where(file => file.Group != 0 && !selected.Contains(file)).ToLookup(file => file.Group);
            if (chosen.Any(file => file.Group != 0 && !keepers[file.Group].Any()))
            {
                Status.Text = "Leave at least one copy unselected in every duplicate group.";
                return;
            }
            var permanent = chosen.Count(file => !file.Recoverable);
            var movable = chosen.Count - permanent;
            var message = (permanent == 0 ? "" : permanent.ToString("N0") + " selected files will be permanently deleted (" +
                Cleaner.FormatBytes(chosen.Where(file => !file.Recoverable).Sum(file => file.Bytes)) + ").\n\n") +
                (movable == 0 ? "" : movable.ToString("N0") + " files will move to Recovery on the Windows profile drive. " +
                    "Space is freed only when you delete them from Recovery.\n\n") + "Continue with the selected files?";
            if (!Confirm("Clean selected files", message)) return;
            await RunWorkAsync("Cleaning selected files…", false, async token =>
            {
                var exclusions = CurrentExclusions();
                var outcome = await Task.Run(() => ApplyCleanup(chosen, keepers, exclusions, token));
                complete(outcome);
                Status.Text = outcome.FirstError ?? "Cleanup complete.";
                AppendHistory(outcome.Summary);
            });
        }

        private sealed class CleanOutcome
        {
            internal readonly HashSet<CleanFile> Completed = new HashSet<CleanFile>();
            internal int Deleted, Moved, Failed;
            internal long Bytes;
            internal string FirstError;
            internal string Summary
            {
                get { return string.Format("{0:N0} deleted ({1} logical size) · {2:N0} moved to Recovery · {3:N0} skipped",
                    Deleted, Cleaner.FormatBytes(Bytes), Moved, Failed); }
            }
        }

        private CleanOutcome ApplyCleanup(List<CleanFile> chosen, ILookup<int, CleanFile> keepers, string[] exclusions, CancellationToken token)
        {
            var result = new CleanOutcome();
            foreach (var group in chosen.GroupBy(file => file.Group))
            {
                CheckedFile keeper = null;
                try
                {
                    if (group.Key != 0)
                    {
                        foreach (var alternative in keepers[group.Key])
                        {
                            try
                            {
                                keeper = CheckedFile.Open(alternative, false, true);
                                if (keeper.Hash(token) == alternative.Hash) break;
                                keeper.Dispose();
                                keeper = null;
                            }
                            catch (IOException) { if (keeper != null) keeper.Dispose(); keeper = null; }
                            catch (UnauthorizedAccessException) { if (keeper != null) keeper.Dispose(); keeper = null; }
                        }
                    }
                    foreach (var file in group)
                    {
                        try
                        {
                            if (Cleaner.Excluded(file.PathName, exclusions)) throw new IOException("This file is excluded.");
                            if (Cleaner.BrowserRunning(file.Browser)) throw new IOException("Close " + file.Browser + " and scan again to clean its cache.");
                            if (file.Group != 0 && keeper == null) throw new IOException("The copy you kept changed or is unavailable.");
                            using (var handle = CheckedFile.Open(file, true, file.Group != 0))
                            {
                                if (file.Group != 0 && handle.Hash(token) != file.Hash)
                                    throw new IOException("The duplicate changed. Scan again.");
                                if (file.Recoverable) recovery.Save(file, handle);
                                else handle.Delete();
                            }
                            if (file.Recoverable) result.Moved++;
                            else { result.Deleted++; result.Bytes += file.Bytes; }
                            result.Completed.Add(file);
                        }
                        catch (Exception error)
                        {
                            if (!ScanPaths.IsFileAccessError(error)) throw;
                            result.Failed++;
                            if (result.FirstError == null) result.FirstError = "Some files were skipped. " + error.Message;
                        }
                    }
                }
                finally { if (keeper != null) keeper.Dispose(); }
            }
            return result;
        }

        private void ShowAnalyzer(string mode)
        {
            var cloud = mode == "cloud";
            var duplicates = mode == "duplicates";
            var layout = ToolLayoutFor(cloud ? "Synced storage" : duplicates ? "Find duplicates" : "Find large files",
                cloud ? "Review files downloaded to a synced folder." :
                duplicates ? "Find files with identical contents and review the extra copies." :
                "See what takes up the most space on a drive or in a folder.");
            var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            layout.Header.Children.Add(options);
            var root = "";
            var folder = Note("No location selected");
            Action locationChanged = () => { };
            var drives = new ComboBox { MinWidth = 135, Margin = new Thickness(0, 0, 8, 5) };
            drives.ItemsSource = DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed).Select(drive => drive.Name).ToList();
            System.Windows.Automation.AutomationProperties.SetName(drives, "Drive");
            drives.SelectionChanged += (sender, args) => { root = (string)drives.SelectedItem ?? ""; locationChanged(); };
            if (cloud)
            {
                var providerLinks = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                TextButtonFor(providerLinks, "OneDrive", () => WindowsTools.Launch("https://onedrive.live.com/"));
                TextButtonFor(providerLinks, "Google Drive", () => WindowsTools.Launch("https://drive.google.com/drive/quota"));
                TextButtonFor(providerLinks, "Dropbox", () => WindowsTools.Launch("https://www.dropbox.com/home"));
                TextButtonFor(providerLinks, "Gmail", () => WindowsTools.Launch("https://mail.google.com/"));
                layout.Header.Children.Add(new Expander { Header = "Manage files online", Content = providerLinks });
            }
            if (!duplicates && !cloud) options.Children.Add(drives);
            ButtonFor(options, "Choose folder", () =>
            {
                var path = Browse(root, "Choose a folder to review");
                if (path == null) return;
                drives.SelectedItem = null;
                root = ScanPaths.NormalizeDirectory(path);
                locationChanged();
            });
            TextBox minimumPlaceholder, agePlaceholder;
            var filters = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            LabeledInput(filters, "Minimum size (MiB)", duplicates ? "1" : "25", out minimumPlaceholder);
            LabeledInput(filters, "Minimum age (days)", cloud ? "365" : "0", out agePlaceholder);
            layout.Header.Children.Add(new Expander { Header = "Size and age filters", Content = filters, Margin = new Thickness(0, 3, 0, 0) });
            layout.Header.Children.Add(folder);
            var table = Table("File", "PathName", "Size", "Size", duplicates ? "Match" : "Modified", duplicates ? "Category" : "LastChanged");
            TextBlock summary;
            ResultPanel(layout.Content, table, out summary);
            summary.Text = "Choose a location and scan.";
            var scanButton = ButtonFor(layout.Actions, duplicates ? "Find duplicates" : "Scan location", async () =>
            {
                int megabytes, days;
                if (!int.TryParse(minimumPlaceholder.Text, out megabytes) || megabytes < 0 ||
                    !int.TryParse(agePlaceholder.Text, out days) || days < 0 || days > 36500)
                {
                    Status.Text = "Enter a nonnegative size and an age from 0 to 36,500 days.";
                    return;
                }
                if (!Cleaner.IsFixedLocalPath(root)) { Status.Text = "Choose a folder on a fixed local drive."; return; }
                var chosenRoot = root;
                var excluded = CurrentExclusions();
                await RunWorkAsync(duplicates ? "Comparing file contents…" : "Scanning storage…", true, async token =>
                {
                    var found = await Task.Run(() => Cleaner.Analyze(chosenRoot, mode, megabytes * 1048576L, days, excluded, token));
                    token.ThrowIfCancellationRequested();
                    table.ItemsSource = found.Files.OrderByDescending(file => file.Bytes).ThenBy(file => file.Group).ToList();
                    var amount = duplicates ? found.Files.GroupBy(file => file.Group).Sum(group => (group.Count() - 1L) * group.First().Bytes)
                        : found.Files.Sum(file => file.Bytes);
                    summary.Text = string.Format("{0:N0} files · {1} {2} · {3:N0} paths skipped",
                        found.Files.Count, Cleaner.FormatBytes(amount), duplicates ? "in extra copies" : "in results", found.Skipped);
                    Status.Text = string.Empty;
                });
            }, true);
            var resultActions = new WrapPanel();
            layout.Actions.Children.Add(resultActions);
            if (duplicates)
                ButtonFor(resultActions, "Select extra copies", () =>
                {
                    var files = table.ItemsSource as IEnumerable<CleanFile>;
                    if (files == null) return;
                    var extras = new HashSet<CleanFile>(files.GroupBy(file => file.Group)
                        .SelectMany(group => group.OrderByDescending(file => file.Modified).ThenBy(file => file.PathName).Skip(1)));
                    SelectFiles(table, extras.Contains);
                });
            var move = cloud ? null : ButtonFor(resultActions, "Move to Recovery", () => CleanSelected(table, summary));
            var reveal = TextButtonFor(resultActions, "Show in File Explorer", () => RevealPath(SelectedPath(table)));
            TextButtonFor(resultActions, "Export CSV", () => Export(table));
            Action updateSelection = () =>
            {
                if (move != null) move.IsEnabled = table.SelectedItems.OfType<CleanFile>().Any(file => !file.AuditOnly);
                reveal.IsEnabled = table.SelectedItem != null;
            };
            table.SelectionChanged += (sender, args) => updateSelection();
            ((INotifyCollectionChanged)table.Items).CollectionChanged += (sender, args) =>
            {
                resultActions.Visibility = table.Items.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
                updateSelection();
            };
            resultActions.Visibility = Visibility.Collapsed;
            locationChanged = () =>
            {
                table.ItemsSource = null;
                folder.Text = string.IsNullOrEmpty(root) ? "No location selected" : root;
                summary.Text = string.IsNullOrEmpty(root) ? "Choose a location to get started." : "Ready to scan the selected location.";
                scanButton.IsEnabled = !string.IsNullOrEmpty(root);
            };
            locationChanged();
        }

        private static void LabeledInput(Panel panel, string caption, string value, out TextBox input)
        {
            var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 5) };
            input = new TextBox { Text = value, Width = 55, VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 5) };
            System.Windows.Automation.AutomationProperties.SetName(input, caption);
            panel.Children.Add(label);
            panel.Children.Add(input);
        }

        private void ShowRecovery()
        {
            var layout = ToolLayoutFor("Recovery", "Restore files you moved here, or delete them permanently to free up space.");
            var table = Table("Original path", "Original", "Size", "Size", "Moved on", "Moved");
            TextBlock summary;
            ResultPanel(layout.Content, table, out summary);
            Action refresh = () =>
            {
                var entries = recovery.List();
                table.ItemsSource = entries;
                summary.Text = entries.Count == 0 ? "No files in Recovery. Files you choose to move here will appear here." :
                    string.Format("{0:N0} files · {1} in Recovery", entries.Count, Cleaner.FormatBytes(entries.Sum(item => item.Bytes)));
            };
            Func<bool, Task> resolve = async restore =>
            {
                var items = table.SelectedItems.OfType<RecoveryItem>().ToList();
                if (items.Count == 0) { Status.Text = "Select one or more files."; return; }
                if (!Confirm(restore ? "Restore files" : "Permanently delete files", restore ?
                    "Return the selected files to their original folders? Existing files are kept." :
                    "Permanently delete " + items.Count + " selected files from Recovery?")) return;
                await RunWorkAsync(restore ? "Restoring files…" : "Deleting files from Recovery…", false, async token =>
                {
                    var errors = await Task.Run(() =>
                    {
                        var failed = new List<string>();
                        foreach (var item in items)
                        {
                            try { recovery.Resolve(item, restore); }
                            catch (Exception error)
                            {
                                if (!ScanPaths.IsFileAccessError(error)) throw;
                                failed.Add(error.Message);
                            }
                        }
                        return failed;
                    });
                    refresh();
                    Status.Text = errors.Count == 0 ? (restore ? "Files restored." : "Files deleted.") :
                        string.Format("{0} of {1} files completed. {2}", items.Count - errors.Count, items.Count, errors[0]);
                    AppendHistory(Status.Text);
                });
            };
            var restoreButton = ButtonFor(layout.Actions, "Restore selected", () => resolve(true), true);
            var deleteButton = ButtonFor(layout.Actions, "Delete permanently", () => resolve(false));
            TextButtonFor(layout.Actions, "Refresh", refresh);
            Action update = () =>
            {
                restoreButton.IsEnabled = deleteButton.IsEnabled = table.SelectedItems.Count > 0;
                restoreButton.Visibility = deleteButton.Visibility = table.Items.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            };
            table.SelectionChanged += (sender, args) => update();
            ((INotifyCollectionChanged)table.Items).CollectionChanged += (sender, args) => update();
            Attempt(refresh);
            update();
        }

        private void ShowBrowserPrivacy()
        {
            var screen = ToolLayoutFor("Browser privacy", "Open your browser’s privacy controls for history, cookies, saved data, exceptions and extensions.");
            var panel = ScrollingContent(screen);
            foreach (var browser in Cleaner.Browsers().Where(item => item.Value.Length > 0))
            {
                var name = browser.Key;
                var row = Section(name, "", panel);
                var buttons = new WrapPanel();
                row.Children.Add(buttons);
                ButtonFor(buttons, "Privacy settings", () => OpenBrowserPage(name, false));
                ButtonFor(buttons, "Extensions", () => OpenBrowserPage(name, true));
            }
        }

        private static void OpenBrowserPage(string name, bool extensions)
        {
            var relative = name == "Chrome" ? @"Google\Chrome\Application\chrome.exe" :
                name == "Edge" ? @"Microsoft\Edge\Application\msedge.exe" :
                name == "Brave" ? @"BraveSoftware\Brave-Browser\Application\brave.exe" : @"Mozilla Firefox\firefox.exe";
            var program = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), relative)).FirstOrDefault(File.Exists);
            if (program == null) throw new IOException("This browser’s executable could not be found.");
            var uri = name == "Firefox" ? (extensions ? "about:addons" : "about:preferences#privacy") :
                name.ToLowerInvariant() + (extensions ? "://extensions" : "://settings/clearBrowserData");
            WindowsTools.Launch(program, uri);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RecycleInfo
        {
            internal uint Size;
            internal long Bytes, Count;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string root, ref RecycleInfo info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr owner, string root, uint flags);

        private void ShowRecycleOptions()
        {
            var info = new RecycleInfo { Size = (uint)Marshal.SizeOf(typeof(RecycleInfo)) };
            var status = SHQueryRecycleBin(null, ref info);
            ButtonDialog("Recycle Bin", status >= 0 ? string.Format("{0:N0} items · {1}", info.Count, Cleaner.FormatBytes(info.Bytes)) : "Review items in Recycle Bin.",
                new KeyValuePair<string, Action>("Open Recycle Bin", () => WindowsTools.Launch(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:RecycleBinFolder")),
                new KeyValuePair<string, Action>("Empty Recycle Bin", () =>
                {
                    if (!Confirm("Empty Recycle Bin", "Permanently delete the contents of the Windows Recycle Bin?")) return;
                    Marshal.ThrowExceptionForHR(SHEmptyRecycleBin(new System.Windows.Interop.WindowInteropHelper(this).Handle, null, 1));
                    Status.Text = "Recycle Bin emptied.";
                    AppendHistory("Recycle Bin emptied.");
                }));
        }

        private void ButtonDialog(string title, string description, params KeyValuePair<string, Action>[] actions)
        {
            var layout = ToolLayoutFor(title, description);
            var container = ScrollingContent(layout);
            foreach (var action in actions) ButtonFor(container, action.Key, action.Value);
        }

        private void ShowWindowsTool(string page)
        {
            var description = page == WindowsTools.Apps ? "Review installed desktop apps. Launch Windows Installer for MSI packages or open Installed Apps." :
                page == WindowsTools.Startup ? "Review what starts with Windows and disable supported current user startup entries with a backup." :
                page == WindowsTools.Performance ? "See memory use for processes in this session and ask selected visible apps to close." :
                page == WindowsTools.Software ? "Check the WinGet catalog for newer versions. Select an update to run its interactive installer." :
                page == WindowsTools.Drivers ? "List installed hardware drivers or check for compatible offers from Windows Update." :
                page == WindowsTools.RegistryAudit ? "Find broken per user app paths. AntiStorage backs up a selected value before removing it." :
                "View basic hardware and drive information and open built-in Windows maintenance tools.";
            var title = page == WindowsTools.Apps ? "Uninstall apps" : page == WindowsTools.Startup ? "Manage startup apps" :
                page == WindowsTools.Performance ? "Running apps" : page == WindowsTools.SystemInfo ? "System information" : page;
            var layout = ToolLayoutFor(title, description);
            var table = Table("Name", "Name", page == WindowsTools.Performance ? "Memory" : "Details", "Detail", "Source / status", "Source");
            TextBlock summary;
            ResultPanel(layout.Content, table, out summary);
            summary.Text = page == WindowsTools.Software ? "Check for updates to see which apps have a new version." :
                "Load the list to get started.";
            var options = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var shortcuts = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            var filter = new ComboBox { MinWidth = 165, Margin = new Thickness(0, 0, 8, 5) };
            if (page == WindowsTools.Startup) filter.ItemsSource = new[] { "Startup entries", "Disabled by AntiStorage", "Scheduled tasks", "Services", "Context menus" };
            else if (page == WindowsTools.Drivers) filter.ItemsSource = new[] { "Installed drivers", "Available drivers" };
            else if (page == WindowsTools.RegistryAudit) filter.ItemsSource = new[] { "Broken app paths", "Backups" };
            else if (page == WindowsTools.Software) filter.ItemsSource = new[] { "Available updates", "Ignored updates" };
            if (filter.ItemsSource != null)
            {
                options.Children.Add(filter);
                layout.Header.Children.Add(options);
                filter.SelectedIndex = 0;
                System.Windows.Automation.AutomationProperties.SetName(filter, "View");
            }
            Action refresh = async () =>
            {
                var selection = filter.SelectedItem as string;
                var ignored = new HashSet<string>(preferences.IgnoredUpdates, StringComparer.OrdinalIgnoreCase);
                await RunWorkAsync("Loading " + page.ToLowerInvariant() + "…", true, async token =>
                {
                    var rows = page == WindowsTools.Software && selection == "Ignored updates"
                        ? preferences.IgnoredUpdates.Select(id => new ToolRow { Name = id, Id = id, Source = "Ignored" }).ToList()
                        : await Task.Run(() => WindowsTools.Read(page, selection, cleanerState, token));
                    token.ThrowIfCancellationRequested();
                    if (page == WindowsTools.Software && selection != "Ignored updates")
                        rows = rows.Where(row => !ignored.Contains(row.Id) && !ignored.Contains(row.Id + "@" + row.Revision)).ToList();
                    table.ItemsSource = rows;
                    summary.Text = rows.Count == 0 ? "No results for this view." : string.Format("{0:N0} results", rows.Count);
                    Status.Text = "";
                });
            };
            filter.SelectionChanged += (sender, args) => { table.ItemsSource = null; refresh(); };
            ButtonFor(layout.Actions, page == WindowsTools.Software ? "Check for updates" : "Load list", refresh, true);
            var resultActions = new WrapPanel();
            layout.Actions.Children.Add(resultActions);
            Func<ToolRow> selected = () =>
            {
                var row = table.SelectedItem as ToolRow;
                if (row == null) throw new IOException("Select a row first.");
                return row;
            };
            if (page == WindowsTools.Apps)
            {
                ButtonFor(resultActions, "Uninstall selected", () =>
                {
                    var row = selected();
                    if (Confirm("Uninstall app", "Open the uninstall workflow for " + row.Name + "?")) WindowsTools.Uninstall(row);
                });
                TextButtonFor(shortcuts, "Installed Apps", () => WindowsTools.Launch("ms-settings:appsfeatures"));
            }
            if (page == WindowsTools.Startup || page == WindowsTools.RegistryAudit)
            {
                var startup = page == WindowsTools.Startup;
                var backupView = startup ? "Disabled by AntiStorage" : "Backups";
                var liveView = startup ? "Startup entries" : "Broken app paths";
                var remove = ButtonFor(resultActions, startup ? "Disable selected" : "Remove selected value", () =>
                {
                    if (!Equals(filter.SelectedItem, liveView)) throw new IOException("Choose the " + liveView + " view.");
                    var row = selected();
                    if (Confirm(startup ? "Disable startup entry" : "Remove broken app path", "Back up and remove the selected value for " + row.Name + "?"))
                    {
                        WindowsTools.RemoveEntry(row, cleanerState, startup);
                        AppendHistory(startup ? "Disabled one startup entry." : "Removed one broken app path.");
                        refresh();
                    }
                });
                var restore = ButtonFor(resultActions, "Restore backup", () =>
                {
                    if (!Equals(filter.SelectedItem, backupView)) throw new IOException("Choose the " + backupView + " view.");
                    var row = selected();
                    if (Confirm("Restore value", "Restore the backed up value for " + row.Name + "?"))
                    {
                        WindowsTools.RestoreEntry(row, cleanerState, startup);
                        AppendHistory("Restored one registry backup.");
                        refresh();
                    }
                });
                Action updateButtons = () =>
                {
                    remove.Visibility = Equals(filter.SelectedItem, liveView) ? Visibility.Visible : Visibility.Collapsed;
                    restore.Visibility = Equals(filter.SelectedItem, backupView) ? Visibility.Visible : Visibility.Collapsed;
                };
                filter.SelectionChanged += (sender, args) => updateButtons();
                updateButtons();
                if (startup)
                {
                    TextButtonFor(shortcuts, "Windows Startup Apps", () => WindowsTools.Launch("ms-settings:startupapps"));
                    TextButtonFor(shortcuts, "Task Scheduler", () => WindowsTools.Launch(WindowsTools.SystemExecutable("taskschd.msc")));
                    TextButtonFor(shortcuts, "Services", () => WindowsTools.Launch(WindowsTools.SystemExecutable("services.msc")));
                }
            }
            if (page == WindowsTools.Performance)
            {
                ButtonFor(resultActions, "Ask app to close", () =>
                {
                    var row = selected();
                    if (Confirm("Close app", "Ask " + row.Name + " to close? Save your work in that app first."))
                    {
                        WindowsTools.CloseApplication(row);
                        Status.Text = "Close requested. The app may ask you to save your work.";
                    }
                });
                TextButtonFor(shortcuts, "Task Manager", () => WindowsTools.Launch(WindowsTools.SystemExecutable("taskmgr.exe")));
                TextButtonFor(shortcuts, "Manage startup", () => OpenPage(WindowsTools.Startup));
            }
            if (page == WindowsTools.Software)
            {
                var availableActions = new WrapPanel();
                resultActions.Children.Add(availableActions);
                AsyncButtonFor(availableActions, "Install selected update", async () =>
                {
                    var row = selected();
                    if (!Equals(filter.SelectedItem, "Available updates")) throw new IOException("Select an available update.");
                    if (!Confirm("Update " + row.Name, "Launch the WinGet installer for " + row.Source + " version " + row.Revision + "?")) return;
                    await RunWorkAsync("Waiting for the installer…", false, async token =>
                    {
                        var code = await Task.Run(() => WindowsTools.Upgrade(row));
                        Status.Text = code == 0 ? "WinGet reported success. Scan again to refresh the list." :
                            "WinGet returned 0x" + code.ToString("X8") + ". Review the installer’s result.";
                        AppendHistory(code == 0 ? "One app update completed." : "An app update returned an error.");
                    });
                });
                TextButtonFor(availableActions, "Skip version", () =>
                {
                    var row = selected();
                    if (row.Revision == null) return;
                    preferences.IgnoredUpdates.Add(row.Id + "@" + row.Revision);
                    SavePreferences();
                    refresh();
                });
                TextButtonFor(availableActions, "Ignore app", () =>
                {
                    var row = selected();
                    if (row.Revision == null) return;
                    preferences.IgnoredUpdates.Add(row.Id);
                    SavePreferences();
                    refresh();
                });
                var clear = ButtonFor(resultActions, "Stop ignoring", () =>
                {
                    var row = selected();
                    if (!Equals(filter.SelectedItem, "Ignored updates")) throw new IOException("Choose the Ignored updates view.");
                    preferences.IgnoredUpdates.RemoveAll(item => item == row.Id);
                    SavePreferences();
                    refresh();
                });
                Action updateButtons = () =>
                {
                    var ignoredView = Equals(filter.SelectedItem, "Ignored updates");
                    availableActions.Visibility = ignoredView ? Visibility.Collapsed : Visibility.Visible;
                    clear.Visibility = ignoredView ? Visibility.Visible : Visibility.Collapsed;
                };
                filter.SelectionChanged += (sender, args) => updateButtons();
                updateButtons();
            }
            if (page == WindowsTools.Drivers)
            {
                TextButtonFor(shortcuts, "Windows Update", () => WindowsTools.Launch("ms-settings:windowsupdate-optionalupdates"));
                TextButtonFor(shortcuts, "Device Manager", () => WindowsTools.Launch(WindowsTools.SystemExecutable("devmgmt.msc")));
            }
            if (page == WindowsTools.SystemInfo)
            {
                TextButtonFor(shortcuts, "Storage Sense", () => WindowsTools.Launch("ms-settings:storagesense"));
                TextButtonFor(shortcuts, "Optimize Drives", () => WindowsTools.Launch(WindowsTools.SystemExecutable("dfrgui.exe")));
                TextButtonFor(shortcuts, "System Restore", () => WindowsTools.Launch(WindowsTools.SystemExecutable("rstrui.exe")));
                TextButtonFor(shortcuts, "Windows Disk Cleanup", () => WindowsTools.Launch(WindowsTools.SystemExecutable("cleanmgr.exe")));
            }
            if (shortcuts.Children.Count > 0)
                layout.Header.Children.Add(new Expander { Header = "Related tools", Content = shortcuts, Margin = new Thickness(0, 7, 0, 0) });
            var export = TextButtonFor(layout.Actions, "Export CSV", () => Export(table));
            Action updateActions = () =>
            {
                resultActions.Visibility = table.SelectedItem == null ? Visibility.Collapsed : Visibility.Visible;
                export.Visibility = table.Items.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            };
            table.SelectionChanged += (sender, args) => updateActions();
            ((INotifyCollectionChanged)table.Items).CollectionChanged += (sender, args) => updateActions();
            updateActions();
        }

        private void ShowSettings()
        {
            var layout = ToolLayoutFor("Settings & help", "Control scans, exclusions and local activity records.");
            var container = ScrollingContent(layout);
            if (settingsError != null) container.Children.Add(Note(settingsError));
            var schedule = Section("Scheduled scans", "AntiStorage can scan while Clean up is open. Review the results before cleaning.", container);
            var options = new WrapPanel();
            schedule.Children.Add(options);
            var interval = new ComboBox { MinWidth = 165, Margin = new Thickness(0, 0, 12, 4) };
            interval.ItemsSource = new[] { "Off", "Daily", "Weekly", "Every two weeks" };
            var days = new[] { 0, 1, 7, 14 };
            interval.SelectedIndex = Math.Max(0, Array.IndexOf(days, preferences.IntervalDays));
            options.Children.Add(interval);
            var onOpen = new CheckBox { Content = "Scan when opening the app", IsChecked = preferences.ScanOnOpen,
                VerticalAlignment = VerticalAlignment.Center };
            options.Children.Add(onOpen);
            ButtonFor(schedule, "Save review schedule", () =>
            {
                preferences.IntervalDays = days[interval.SelectedIndex];
                preferences.ScanOnOpen = onOpen.IsChecked == true;
                SavePreferences();
                Status.Text = "Review schedule saved.";
            }, true);
            var exclude = Section("Cleanup exclusions", "Excluded folders and their contents are skipped by cleaner and disk analysis scans.", container);
            var list = new ListBox { Height = 95, Margin = new Thickness(0, 0, 0, 8) };
            Action reload = () => list.ItemsSource = preferences.Exclusions.OrderBy(path => path).ToList();
            reload();
            exclude.Children.Add(list);
            var buttons = new WrapPanel();
            exclude.Children.Add(buttons);
            ButtonFor(buttons, "Add folder", () =>
            {
                var path = Browse("", "Choose a folder to exclude from cleaner and analyzer scans");
                if (path == null) return;
                path = ScanPaths.NormalizeDirectory(path);
                if (!preferences.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
                    preferences.Exclusions.Add(path);
                SavePreferences();
                reload();
            });
            ButtonFor(buttons, "Remove exclusion", () =>
            {
                var path = (string)list.SelectedItem;
                if (path == null) return;
                preferences.Exclusions.Remove(path);
                SavePreferences();
                reload();
            });
            var help = Section("Help and activity", "Version 0.2.1. Scan data and activity stay on this PC. Windows and provider tools handle their own connections.", container);
            ButtonFor(help, "GitHub releases", () => WindowsTools.Launch("https://github.com/dogoragahelpme/AntiStorage/releases"));
            ButtonFor(help, "Open activity log", () =>
            {
                var log = Path.Combine(cleanerState, "activity.log");
                if (!File.Exists(log)) throw new IOException("There is no cleaning or maintenance activity to show yet.");
                WindowsTools.Launch(log);
            });
            ButtonFor(help, "Project help", () => WindowsTools.Launch("https://github.com/dogoragahelpme/AntiStorage#readme"));
        }

        private bool initialReview = true;

        private bool ShouldReview()
        {
            return (initialReview && preferences.ScanOnOpen) ||
                (preferences.IntervalDays > 0 && (DateTime.UtcNow - preferences.LastReviewUtc).TotalDays >= preferences.IntervalDays);
        }

        private void CheckScheduledReview()
        {
            if (operation != null || !ShouldReview() || activePage != CleanupHome || !IsVisible || beginHealthScan == null) return;
            initialReview = false;
            beginHealthScan();
        }

        private void SavePreferences()
        {
            try { preferences.Save(cleanerState); }
            catch (Exception error) { throw new IOException("Could not save the AntiStorage settings. " + error.Message, error); }
        }

        private string[] CurrentExclusions()
        {
            return preferences.Exclusions.Concat(new[] { cleanerState }).ToArray();
        }

        private bool Confirm(string title, string message)
        {
            return MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        private static string SelectedPath(DataGrid table)
        {
            var file = table.SelectedItem as CleanFile;
            return file == null ? null : file.PathName;
        }

        private void RevealPath(string path)
        {
            if (path == null || !File.Exists(path)) throw new IOException("Select a file that is still available.");
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            WindowsTools.Launch(explorer, "/select,\"" + path + "\"");
        }

        private void AppendHistory(string message)
        {
            try
            {
                Directory.CreateDirectory(cleanerState);
                ScanPaths.EnsureRegularDirectory(cleanerState);
                var path = Path.Combine(cleanerState, "activity.log");
                if (File.Exists(path) && ScanPaths.IsUnobserved(File.GetAttributes(path))) return;
                File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch (Exception error) { if (!ScanPaths.IsFileAccessError(error)) throw; }
        }

        private void Export(DataGrid table)
        {
            var items = table.Items.OfType<object>().ToList();
            if (items.Count == 0) throw new IOException("Run a scan before exporting.");
            using (var dialog = new Forms.SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = "AntiStorage-results.csv" })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                var columns = table.Columns.OfType<DataGridTextColumn>().ToList();
                using (var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine(string.Join(",", columns.Select(column => Csv(Convert.ToString(column.Header)))));
                    foreach (var item in items)
                        writer.WriteLine(string.Join(",", columns.Select(column => Csv(Convert.ToString(item.GetType()
                            .GetProperty(((Binding)column.Binding).Path.Path).GetValue(item, null))))));
                }
                Status.Text = "Results exported.";
            }
        }

        private static string Csv(string input)
        {
            var value = input ?? "";
            if (value.TrimStart().Length > 0 && "=+-@".Contains(value.TrimStart()[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
