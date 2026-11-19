using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AntiStorage
{
    public partial class MainWindow
    {
        private readonly CleanupReview homeReview = new CleanupReview();

        private sealed class CleanupReview
        {
            internal CleanScan Scan;
            internal CleanOutcome Outcome;
            internal readonly HashSet<CleanFile> Selected = new HashSet<CleanFile>();

            internal void SetScan(CleanScan scan)
            {
                Scan = scan;
                Outcome = null;
                Selected.Clear();
                Selected.UnionWith(scan.Files.Where(file => !file.AuditOnly && !file.Recoverable));
            }

            internal void Apply(CleanOutcome outcome)
            {
                Outcome = outcome;
                Scan.Files.RemoveAll(outcome.Completed.Contains);
                Selected.Clear();
            }
        }

        private sealed class CleanupFileChoice
        {
            private readonly CleanupReview review;
            private readonly Action changed;
            internal readonly CleanFile File;

            internal CleanupFileChoice(CleanFile file, CleanupReview review, Action changed)
            {
                File = file;
                this.review = review;
                this.changed = changed;
            }

            public string PathName { get { return File.PathName; } }
            public string Size { get { return File.Size; } }
            public string LastChanged { get { return File.LastChanged; } }
            public bool CanChoose { get { return !File.AuditOnly; } }

            public bool Chosen
            {
                get { return review.Selected.Contains(File); }
                set
                {
                    if (!CanChoose) return;
                    var updated = value ? review.Selected.Add(File) : review.Selected.Remove(File);
                    if (updated) changed();
                }
            }
        }

        private void ShowCleaning(bool home)
        {
            var layout = ToolLayoutFor(home ? CleanupHome : "Choose what to scan", home
                ? "Scan for files you can remove to free up space."
                : "Pick the categories you want to look through.");
            var review = home ? homeReview : new CleanupReview();
            var rules = home ? null : Cleaner.Rules();

            Action showOptions = null;
            Action showResults = null;
            Action scan = async () =>
            {
                if (!home && !rules.Any(rule => rule.Enabled))
                {
                    Status.Text = "Choose at least one category to scan.";
                    return;
                }
                await RunWorkAsync("Looking for files to clean…", true, async token =>
                {
                    var exclusions = CurrentExclusions();
                    var found = await Task.Run(() =>
                    {
                        var selectedRules = home ? Cleaner.Rules().Where(rule => rule.Recommended) :
                            rules.Where(rule => rule.Enabled);
                        return Cleaner.Scan(selectedRules, exclusions, token);
                    });
                    token.ThrowIfCancellationRequested();
                    review.SetScan(found);
                    showResults();
                    if (home)
                    {
                        preferences.LastReviewUtc = DateTime.UtcNow;
                        SavePreferences();
                    }
                    Status.Text = "";
                });
            };
            showOptions = () =>
            {
                layout.Actions.Children.Clear();
                ShowCleanupOptions(layout, rules);
                var scanButton = ButtonFor(layout.Actions, "Scan selected categories", scan, true);
                Action update = () => scanButton.IsEnabled = rules.Any(rule => rule.Enabled);
                AttachRuleChanges(layout.Content.Child, update);
                update();
            };
            showResults = () => ShowCleanupResults(layout, review, scan,
                home ? (Action)(() => OpenPage("Custom clean")) : showOptions, showResults);

            if (home)
            {
                beginHealthScan = scan;
                if (review.Scan == null)
                    ShowCleanupCallout(layout, "Find junk files", "Look for temporary files and caches from Windows, your browsers and apps. You’ll see what was found before you clean.",
                        "Scan for junk", scan, "Choose what to scan", () => OpenPage("Custom clean"));
                else
                    showResults();
            }
            else
                showOptions();
        }

        private void ShowCleanupOptions(ToolLayout layout, List<CleanRule> rules)
        {
            var content = ScrollingContent(layout);
            var recommended = Section("Recommended", "Temporary files and caches that Windows and apps can rebuild.", content);
            foreach (var rule in rules.Where(rule => rule.Recommended))
                AddRuleOption(recommended, rule);
            var extra = new Expander { Header = new TextBlock { Text = "Show optional categories", FontWeight = FontWeights.SemiBold } };
            var options = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            options.Children.Add(Note("Includes downloads, crash reports, recent files and other categories you may want to keep."));
            foreach (var rule in rules.Where(rule => !rule.Recommended))
                AddRuleOption(options, rule);
            extra.Content = options;
            content.Children.Add(extra);
        }

        private void AddRuleOption(Panel panel, CleanRule rule)
        {
            var description = new StackPanel();
            description.Children.Add(new TextBlock { Text = rule.Label, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var hint = Note(rule.Hint);
            hint.Margin = new Thickness(0, 2, 0, 0);
            description.Children.Add(hint);
            var check = new CheckBox { Content = description, IsChecked = rule.Enabled, Margin = new Thickness(0, 4, 0, 8) };
            AutomationProperties.SetName(check, rule.Label);
            check.Checked += (sender, args) => rule.Enabled = true;
            check.Unchecked += (sender, args) => rule.Enabled = false;
            panel.Children.Add(check);
        }

        private static void AttachRuleChanges(UIElement element, Action update)
        {
            element.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler((sender, args) => update()));
            element.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler((sender, args) => update()));
        }

        private void ShowCleanupCallout(ToolLayout layout, string title, string detail, string primaryText, Action primary,
            string secondaryText, Action secondary)
        {
            layout.Actions.Children.Clear();
            var outer = new Grid();
            var panel = new StackPanel { Margin = new Thickness(26), MaxWidth = 570, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top };
            var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            outer.Children.Add(scroll);
            layout.Content.VerticalAlignment = VerticalAlignment.Top;
            layout.Content.Child = outer;
            panel.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var caption = Note(detail);
            caption.Margin = new Thickness(0, 8, 0, 18);
            panel.Children.Add(caption);
            var buttons = new WrapPanel();
            panel.Children.Add(buttons);
            ButtonFor(buttons, primaryText, primary, true);
            TextButtonFor(buttons, secondaryText, secondary);
        }

        private void ShowCleanupResults(ToolLayout layout, CleanupReview review, Action scan, Action options, Action render)
        {
            if (review.Scan.Files.Count == 0)
            {
                ShowCleanupCallout(layout, review.Outcome == null ? "No junk found" : "Cleanup finished",
                    review.Outcome == null ? EmptyScanDescription(review.Scan.Skipped) : review.Outcome.Summary,
                    "Scan again", scan, "Change scan options", options);
                return;
            }

            layout.Actions.Children.Clear();
            var content = ScrollingContent(layout);
            content.Children.Add(new TextBlock
            {
                Text = review.Outcome == null ? "Scan results" : "Files left after cleanup",
                FontSize = 20, FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(Note(review.Outcome == null
                ? "Choose the categories to clean. Use View files to pick individual files."
                : review.Outcome.Summary));
            if (review.Scan.Skipped != 0)
                content.Children.Add(Note(string.Format("{0:N0} locations or files were skipped during the scan.", review.Scan.Skipped)));
            var selected = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 10, 5) };
            var clean = ButtonFor(layout.Actions, "Clean selected", () => CleanFiles(review.Selected.ToList(), review.Scan.Files, outcome =>
            {
                review.Apply(outcome);
                render();
            }), true);
            AutomationProperties.SetLiveSetting(selected, AutomationLiveSetting.Polite);
            layout.Actions.Children.Add(selected);
            TextButtonFor(layout.Actions, "Scan again", scan);
            TextButtonFor(layout.Actions, "Change scan options", options);

            Action update = () =>
            {
                var count = review.Selected.Count;
                var toDelete = review.Selected.Where(file => !file.Recoverable).Sum(file => file.Bytes);
                var toMove = review.Selected.Count(file => file.Recoverable);
                selected.Text = count == 0 ? "No files selected" : string.Format("{0:N0} selected", count);
                if (count > toMove) selected.Text += "  ·  " + Cleaner.FormatBytes(toDelete) + " to remove";
                if (toMove > 0) selected.Text += string.Format("  ·  {0:N0} to Recovery", toMove);
                clean.IsEnabled = count > 0;
            };
            foreach (var group in review.Scan.Files.GroupBy(file => file.Category).OrderByDescending(group => group.Sum(file => file.Bytes)))
                AddCleanupCategory(content, layout, review, group.Key, group.ToList(), update, render);
            update();
        }

        private static string EmptyScanDescription(int skipped)
        {
            if (skipped == 0) return "The scan found no files in the selected categories.";
            return string.Format("The scan found no files in the locations it could read. {0:N0} locations or files were skipped.", skipped);
        }

        private void AddCleanupCategory(Panel panel, ToolLayout layout, CleanupReview review, string category,
            List<CleanFile> files, Action update, Action back)
        {
            var row = new DockPanel { Margin = new Thickness(0, 7, 0, 0), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(right, Dock.Right);
            right.Children.Add(new TextBlock { Text = Cleaner.FormatBytes(files.Sum(file => file.Bytes)), FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0) });
            var view = TextButtonFor(right, "View files", () => ShowCategoryFiles(layout, review, category, files, back));
            view.Margin = new Thickness(0);
            AutomationProperties.SetName(view, "View files for " + category);
            row.Children.Add(right);

            var labels = new StackPanel();
            labels.Children.Add(new TextBlock { Text = category, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var actionable = files.Where(file => !file.AuditOnly).ToList();
            var count = Note(actionable.Count == 0 ? string.Format("{0:N0} files · Review only", files.Count) :
                string.Format("{0:N0} {1}", files.Count, files.Count == 1 ? "file" : "files"));
            count.Margin = new Thickness(0, 2, 0, 0);
            labels.Children.Add(count);

            if (actionable.Count == 0)
                row.Children.Add(labels);
            else
            {
                var chosen = actionable.Count(review.Selected.Contains);
                var toggle = new CheckBox { Content = labels, IsChecked = chosen == 0 ? false : chosen == actionable.Count ? true : (bool?)null };
                AutomationProperties.SetName(toggle, "Select " + category);
                toggle.Click += (sender, args) =>
                {
                    if (toggle.IsChecked == true) review.Selected.UnionWith(actionable);
                    else review.Selected.ExceptWith(actionable);
                    update();
                };
                row.Children.Add(toggle);
            }

            panel.Children.Add(new Border { Child = row, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(0, 1, 0, 4) });
        }

        private void ShowCategoryFiles(ToolLayout layout, CleanupReview review, string category, List<CleanFile> files, Action back)
        {
            layout.Actions.Children.Clear();
            var table = Table("File", "PathName", "Size", "Size");
            table.Columns[1].Width = new DataGridLength(135);
            table.SelectionMode = DataGridSelectionMode.Single;
            if (files.Any(file => !file.AuditOnly))
                table.Columns.Insert(0, new DataGridTemplateColumn { Header = "Clean", Width = 105,
                    CellTemplate = (DataTemplate)FindResource("CleanupFileToggle") });
            TextBlock summary;
            ResultPanel(layout.Content, table, out summary);
            summary.FontWeight = FontWeights.SemiBold;
            Action update = () =>
            {
                var eligible = files.Count(file => !file.AuditOnly);
                summary.Text = eligible == 0 ? string.Format("{0} · {1:N0} files for review", category, files.Count) :
                    string.Format("{0} · {1:N0} of {2:N0} selected for cleanup", category, files.Count(review.Selected.Contains), eligible);
            };
            table.ItemsSource = files.OrderByDescending(file => file.Bytes).Select(file => new CleanupFileChoice(file, review, update)).ToList();
            ButtonFor(layout.Actions, "Back to scan results", back, true);
            var reveal = TextButtonFor(layout.Actions, "Show in File Explorer", () =>
            {
                var row = table.SelectedItem as CleanupFileChoice;
                RevealPath(row == null ? null : row.PathName);
            });
            reveal.IsEnabled = false;
            table.SelectionChanged += (sender, args) => reveal.IsEnabled = table.SelectedItem != null;
            update();
        }
    }
}
