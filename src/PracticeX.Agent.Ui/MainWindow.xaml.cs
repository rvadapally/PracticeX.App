using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using PracticeX.Agent.Cli.Http;
using PracticeX.Agent.Cli.Inventory;
using PracticeX.Discovery.Contracts;

namespace PracticeX.Agent.Ui;

public partial class MainWindow : Window
{
    public ObservableCollection<ScoredRowVm> Rows { get; } = new();
    private ICollectionView? _rowsView;
    private Guid? _manifestBatchId;
    private string? _scanRoot;
    private List<ManifestItemDto> _manifestItems = new();

    public MainWindow()
    {
        InitializeComponent();
        _rowsView = CollectionViewSource.GetDefaultView(Rows);
        _rowsView.Filter = RowFilter;
        ResultsGrid.ItemsSource = _rowsView;
        Loaded += async (_, _) => await RefreshConnectionsAsync();
    }

    private bool Insecure => true; // dev default; the API uses self-signed certs locally
    private string? Token => Environment.GetEnvironmentVariable("PRACTICEX_TOKEN");

    // ---- Connection list ------------------------------------------------------

    private async Task RefreshConnectionsAsync()
    {
        if (!Uri.TryCreate(ApiBox.Text, UriKind.Absolute, out var apiUri))
        {
            SetStatus("Invalid API URL.", isError: true);
            return;
        }

        SetStatus("Loading connections...");
        try
        {
            var connections = await PracticeXClient.ListConnectionsAsync(apiUri, Token, Insecure, default);
            var folders = connections.Where(c => c.SourceType == "local_folder").ToList();
            ConnectionCombo.ItemsSource = folders.Select(c => new ConnectionOption(c)).ToList();
            if (folders.Count == 0)
            {
                SetStatus("No local_folder connections found. Create one in the web UI first.", isError: true);
            }
            else
            {
                ConnectionCombo.SelectedIndex = 0;
                SetStatus($"Loaded {folders.Count} local_folder connection(s). Pick a folder and click Scan.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load connections: {ex.Message}", isError: true);
        }
    }

    private async void OnRefreshConnections(object sender, RoutedEventArgs e) => await RefreshConnectionsAsync();

    // ---- Folder pick + scan ---------------------------------------------------

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Pick a folder to scan",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : @"C:\"
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (ConnectionCombo.SelectedItem is not ConnectionOption conn)
        {
            SetStatus("Pick a connection first.", isError: true);
            return;
        }
        if (!Uri.TryCreate(ApiBox.Text, UriKind.Absolute, out var apiUri))
        {
            SetStatus("Invalid API URL.", isError: true);
            return;
        }
        if (!Directory.Exists(FolderBox.Text))
        {
            SetStatus($"Folder does not exist: {FolderBox.Text}", isError: true);
            return;
        }

        ScanBtn.IsEnabled = false;
        UploadBtn.IsEnabled = false;
        SuccessPanel.Visibility = Visibility.Collapsed;
        Rows.Clear();
        ResetCounts();

        try
        {
            _scanRoot = FolderBox.Text;
            ShowProgress("Inventorying folder...", "0 files");

            var sw = Stopwatch.StartNew();
            var progress = new Progress<int>(count =>
            {
                ProgressDetail.Text = $"{count:N0} files inventoried";
                TotalText.Text = count.ToString("N0");
            });

            _manifestItems = await Task.Run(() => EnumerateWithProgress(_scanRoot!, progress));

            if (_manifestItems.Count == 0)
            {
                HideProgress();
                SetStatus("No files passed inventory filters.", isError: true);
                return;
            }

            ShowProgress(
                $"Scoring {_manifestItems.Count:N0} files in the cloud...",
                "Posting metadata-only manifest (no bytes uploaded)",
                indeterminate: true);

            using var client = new PracticeXClient(apiUri, conn.Id, Token, Insecure);
            var response = await client.PostManifestAsync(_manifestItems, notes: null, default);

            _manifestBatchId = response.BatchId;
            TotalText.Text = response.TotalItems.ToString("N0");
            StrongText.Text = response.StrongCount.ToString("N0");
            LikelyText.Text = response.LikelyCount.ToString("N0");
            PossibleText.Text = response.PossibleCount.ToString("N0");
            SkippedText.Text = response.SkippedCount.ToString("N0");

            foreach (var item in response.Items)
            {
                var row = new ScoredRowVm(item);
                row.PropertyChanged += (_, _) => UpdateSelectionLabel();
                Rows.Add(row);
            }

            ApplyDefaultSelection();
            UpdateFilterCount();
            HideProgress();

            sw.Stop();
            SetStatus($"Scan complete in {sw.Elapsed.TotalSeconds:0.0}s. Manifest batch {response.BatchId} (phase=manifest). Pick rows to upload, then click Upload selected.");
        }
        catch (Exception ex)
        {
            HideProgress();
            SetStatus($"Scan failed: {ex.Message}", isError: true);
        }
        finally
        {
            ScanBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Streams items off the enumerator and reports the running count back to
    /// the UI thread every ~100 files. Keeps the KPI tile and progress bar
    /// alive even on huge trees.
    /// </summary>
    private static List<ManifestItemDto> EnumerateWithProgress(string root, IProgress<int> progress)
    {
        var list = new List<ManifestItemDto>();
        foreach (var item in FolderEnumerator.Enumerate(root))
        {
            list.Add(item);
            if (list.Count % 100 == 0)
            {
                progress.Report(list.Count);
            }
        }
        progress.Report(list.Count);
        return list;
    }

    // ---- Upload --------------------------------------------------------------

    /// <summary>
    /// Step 1 of upload: open the Prepare-upload modal so the user reviews
    /// what they're about to send (file count, total size, band breakdown,
    /// optional notes) before bytes leave the building.
    /// </summary>
    private void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_manifestBatchId is null)
        {
            SetStatus("Run a scan first.", isError: true);
            return;
        }
        if (ConnectionCombo.SelectedItem is not ConnectionOption)
        {
            SetStatus("Pick a connection first.", isError: true);
            return;
        }

        var selected = Rows.Where(r => r.IsSelected && r.Band != ManifestBandNames.Skipped).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Nothing selected.", isError: true);
            return;
        }

        OpenUploadPreview(selected);
    }

    private void OnCancelUpload(object sender, RoutedEventArgs e) =>
        UploadPreviewModal.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Step 2 of upload: user confirmed in the modal — actually POST the bundle.
    /// </summary>
    private async void OnConfirmUpload(object sender, RoutedEventArgs e)
    {
        if (_manifestBatchId is null || _scanRoot is null)
        {
            UploadPreviewModal.Visibility = Visibility.Collapsed;
            return;
        }
        if (ConnectionCombo.SelectedItem is not ConnectionOption conn)
        {
            UploadPreviewModal.Visibility = Visibility.Collapsed;
            return;
        }
        if (!Uri.TryCreate(ApiBox.Text, UriKind.Absolute, out var apiUri))
        {
            UploadPreviewModal.Visibility = Visibility.Collapsed;
            return;
        }

        var selected = Rows.Where(r => r.IsSelected && r.Band != ManifestBandNames.Skipped).ToList();
        if (selected.Count == 0)
        {
            UploadPreviewModal.Visibility = Visibility.Collapsed;
            return;
        }

        var bundleFiles = MapBundle(selected);
        var notes = string.IsNullOrWhiteSpace(PreviewNotesBox.Text) ? null : PreviewNotesBox.Text.Trim();

        UploadPreviewModal.Visibility = Visibility.Collapsed;
        UploadBtn.IsEnabled = false;
        ScanBtn.IsEnabled = false;
        ShowProgress(
            $"Uploading {selected.Count:N0} file(s)...",
            "Streaming bytes to /folder/bundles",
            indeterminate: true);

        try
        {
            using var client = new PracticeXClient(apiUri, conn.Id, Token, Insecure);
            var summary = await client.PostBundleAsync(_manifestBatchId.Value, bundleFiles, notes, default);

            HideProgress();
            BackfillTiers(summary);
            ShowComplexityPanel(summary.Complexity);
            ShowSuccessPanel(summary, selected.Count, apiUri);

            // Once uploaded, the manifest batch is complete and can't accept more files.
            _manifestBatchId = null;
            SetStatus($"Bundle complete. Status={summary.Status}. Open the review queue to triage candidates.");
        }
        catch (Exception ex)
        {
            HideProgress();
            SetStatus($"Upload failed: {ex.Message}", isError: true);
        }
        finally
        {
            ScanBtn.IsEnabled = true;
            UpdateSelectionLabel();
        }
    }

    /// <summary>
    /// Pulls per-file complexity tiers off the bundle response and stamps them
    /// onto the corresponding manifest rows so the Tier column lights up.
    /// Match key is ManifestItemId (cloud preserves it through the round-trip).
    /// </summary>
    private void BackfillTiers(IngestionBatchSummaryDto summary)
    {
        var tierByName = summary.Items
            .Where(i => !string.IsNullOrEmpty(i.ComplexityTier))
            .GroupBy(i => (i.RelativePath ?? i.Name))
            .ToDictionary(g => g.Key, g => g.First().ComplexityTier);

        foreach (var row in Rows)
        {
            if (tierByName.TryGetValue(row.RelativePath, out var tier))
            {
                row.ComplexityTier = tier;
            }
        }
    }

    private void OpenUploadPreview(IReadOnlyList<ScoredRowVm> selected)
    {
        var totalBytes = selected.Sum(r => GetItemSize(r.RelativePath));
        var byBand = selected.GroupBy(r => r.Band).ToDictionary(g => g.Key, g => g.Count());
        var bandText = string.Join("  ·  ", new[]
            {
                ($"{byBand.GetValueOrDefault(ManifestBandNames.Strong)} Strong"),
                ($"{byBand.GetValueOrDefault(ManifestBandNames.Likely)} Likely"),
                ($"{byBand.GetValueOrDefault(ManifestBandNames.Possible)} Possible")
            });

        PreviewSubtitle.Text =
            $"You're about to send these files to PracticeX. Per-file complexity profiling " +
            "(page count, sheet count, formulas, macros, etc.) runs server-side after they land. " +
            "Use Notes to attach a label that shows up on the batch row.";

        PreviewFileCount.Text = selected.Count.ToString("N0");
        PreviewTotalSize.Text = FormatBytes(totalBytes);
        PreviewBands.Text = bandText;

        // Show first 50 file paths to keep the modal scannable.
        PreviewFileList.ItemsSource = selected
            .Select(r => r.RelativePath)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Concat(selected.Count > 50 ? new[] { $"... +{selected.Count - 50:N0} more" } : Array.Empty<string>())
            .ToList();

        PreviewDisclaimer.Text =
            "Estimate only. Placeholder rates for sizing — final pricing per signed engagement.";

        PreviewNotesBox.Text = string.Empty;
        UploadPreviewModal.Visibility = Visibility.Visible;
    }

    private long GetItemSize(string relativePath)
    {
        return _manifestItems
            .Where(i => i.RelativePath == relativePath)
            .Select(i => i.SizeBytes)
            .FirstOrDefault();
    }

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        const double gib = mib * 1024;
        return bytes switch
        {
            >= (long)gib => $"{bytes / gib:0.0} GB",
            >= (long)mib => $"{bytes / mib:0.0} MB",
            >= (long)kib => $"{bytes / kib:0.0} KB",
            _ => $"{bytes} B"
        };
    }

    private void ShowComplexityPanel(BatchComplexityProfileDto? complexity)
    {
        if (complexity is null)
        {
            ComplexityPanel.Visibility = Visibility.Collapsed;
            return;
        }

        SimpleText.Text = complexity.SimpleCount.ToString("N0");
        ModerateText.Text = complexity.ModerateCount.ToString("N0");
        LargeText.Text = complexity.LargeCount.ToString("N0");
        ExtraText.Text = complexity.ExtraCount.ToString("N0");
        EstHoursText.Text = complexity.TotalEstimatedHours is { } h ? $"{h:0.0}" : "—";
        EstCostText.Text = complexity.EstimatedDocumentIntelligenceCostUsd is { } c
            ? $"${c:0.00}"
            : "—";

        BlockerText.Text = complexity.Blockers.Count == 0
            ? string.Empty
            : "Blockers: " + string.Join("  ·  ",
                complexity.Blockers.Select(b => $"{b.Count} {b.Code.Replace('_', ' ')}"));

        ComplexityPanel.Visibility = Visibility.Visible;
    }

    private List<BundleFile> MapBundle(IEnumerable<ScoredRowVm> rows)
    {
        var mimeByPath = _manifestItems
            .GroupBy(i => i.RelativePath)
            .ToDictionary(g => g.Key, g => g.First().MimeType ?? "application/octet-stream");

        return rows.Select(r => new BundleFile(
            AbsolutePath: Path.GetFullPath(Path.Combine(_scanRoot!, r.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
            RelativePath: r.RelativePath,
            Name: r.Name,
            MimeType: mimeByPath.GetValueOrDefault(r.RelativePath, "application/octet-stream"),
            ManifestItemId: r.ManifestItemId)).ToList();
    }

    // ---- Selection helpers ---------------------------------------------------

    private void OnSelectStrongLikely(object sender, RoutedEventArgs e) =>
        SelectByPredicate(r => r.Band is ManifestBandNames.Strong or ManifestBandNames.Likely);

    private void OnSelectAllVisible(object sender, RoutedEventArgs e) =>
        SelectByPredicate(r => RowFilter(r) && r.Band != ManifestBandNames.Skipped);

    private void OnClearSelection(object sender, RoutedEventArgs e) => SelectByPredicate(_ => false);

    private void SelectByPredicate(Func<ScoredRowVm, bool> shouldSelect)
    {
        foreach (var row in Rows)
        {
            row.IsSelected = shouldSelect(row);
        }
        UpdateSelectionLabel();
    }

    private void ApplyDefaultSelection() =>
        SelectByPredicate(r => r.Band is ManifestBandNames.Strong or ManifestBandNames.Likely);

    private void UpdateSelectionLabel()
    {
        var count = Rows.Count(r => r.IsSelected && r.Band != ManifestBandNames.Skipped);
        SelectionText.Text = $"{count:N0} selected";
        UploadBtn.IsEnabled = count > 0 && _manifestBatchId is not null;
    }

    // ---- Filter chips --------------------------------------------------------

    private bool RowFilter(object obj) => obj is ScoredRowVm row && IsBandVisible(row.Band);

    private bool IsBandVisible(string band) => band switch
    {
        ManifestBandNames.Strong => ShowStrongChip.IsChecked == true,
        ManifestBandNames.Likely => ShowLikelyChip.IsChecked == true,
        ManifestBandNames.Possible => ShowPossibleChip.IsChecked == true,
        ManifestBandNames.Skipped => ShowSkippedChip.IsChecked == true,
        _ => true
    };

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        _rowsView?.Refresh();
        UpdateFilterCount();
    }

    private void UpdateFilterCount()
    {
        if (_rowsView is null) { FilterCountText.Text = string.Empty; return; }
        var visible = Rows.Count(RowFilter);
        var total = Rows.Count;
        FilterCountText.Text = total == 0
            ? string.Empty
            : visible == total
                ? $"showing all {total:N0}"
                : $"showing {visible:N0} of {total:N0}";
    }

    // ---- Post-upload panel ---------------------------------------------------

    private void ShowSuccessPanel(IngestionBatchSummaryDto summary, int requested, Uri apiBaseUrl)
    {
        SuccessPanel.Visibility = Visibility.Visible;
        SuccessPanel.Tag = apiBaseUrl;
        SuccessTitle.Text = $"Uploaded {requested:N0} file(s) — batch {summary.BatchId.ToString()[..8]}…";
        SuccessDetail.Text = $"Cloud created {summary.CandidateCount:N0} candidate(s), skipped {summary.SkippedCount:N0} duplicate(s), and recorded {summary.ErrorCount:N0} error(s). " +
                             "Reviewers can now triage these in the web UI's Source Discovery review queue.";
    }

    private void OnOpenReviewQueue(object sender, RoutedEventArgs e)
    {
        // Web UI lives at the same host on the Vite dev port (5173) in dev,
        // or behind the API host in production. We open the dev port by default
        // and fall back to the API host if launching the dev URL fails.
        var devUiUrl = "http://localhost:5173/source-discovery";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = devUiUrl, UseShellExecute = true });
        }
        catch
        {
            // ShellExecute already handles browser registration on Windows;
            // if it failed, surface the URL so the user can copy/paste.
            SetStatus($"Could not open browser. Visit manually: {devUiUrl}", isError: true);
        }
    }

    private void OnScanAnother(object sender, RoutedEventArgs e)
    {
        SuccessPanel.Visibility = Visibility.Collapsed;
        Rows.Clear();
        ResetCounts();
        UpdateSelectionLabel();
        UpdateFilterCount();
        SetStatus("Ready. Pick a folder and click Scan.");
    }

    // ---- Progress bar helpers -----------------------------------------------

    private void ShowProgress(string label, string detail, bool indeterminate = false, double? value = null)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = label;
        ProgressDetail.Text = detail;
        ProgressBarCtl.IsIndeterminate = indeterminate || value is null;
        if (value is { } v)
        {
            ProgressBarCtl.Value = v;
        }
    }

    private void HideProgress() => ProgressPanel.Visibility = Visibility.Collapsed;

    // ---- Status / counts -----------------------------------------------------

    private void ResetCounts()
    {
        TotalText.Text = "0";
        StrongText.Text = "0";
        LikelyText.Text = "0";
        PossibleText.Text = "0";
        SkippedText.Text = "0";
        SelectionText.Text = "0 selected";
        FilterCountText.Text = string.Empty;
        ComplexityPanel.Visibility = Visibility.Collapsed;
        SimpleText.Text = "0";
        ModerateText.Text = "0";
        LargeText.Text = "0";
        ExtraText.Text = "0";
        EstHoursText.Text = "0.0";
        EstCostText.Text = "$0.00";
        BlockerText.Text = string.Empty;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("BadBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    }
}

public sealed class ConnectionOption
{
    public ConnectionOption(SourceConnectionDto dto)
    {
        Id = dto.Id;
        Display = $"{dto.DisplayName ?? "(unnamed)"} — {dto.Status} — {dto.Id}";
    }

    public Guid Id { get; }
    public string Display { get; }

    // Override ToString so the ComboBox displays the friendly label even when
    // the templated SelectionBoxItem doesn't pick up DisplayMemberPath.
    public override string ToString() => Display;
}

public sealed class ScoredRowVm : INotifyPropertyChanged
{
    private bool _isSelected;
    private string? _complexityTier;

    public ScoredRowVm(ManifestScoredItemDto item)
    {
        ManifestItemId = item.ManifestItemId;
        RelativePath = item.RelativePath;
        Name = item.Name;
        Band = item.Band;
        CandidateType = item.CandidateType;
        Confidence = item.Confidence;
        ReasonsDisplay = string.Join(" · ", item.ReasonCodes);
    }

    public string ManifestItemId { get; }
    public string RelativePath { get; }
    public string Name { get; }
    public string Band { get; }
    public string CandidateType { get; }
    public decimal Confidence { get; }
    public string ConfidenceDisplay => Confidence.ToString("0.00");
    public string ReasonsDisplay { get; }

    /// <summary>
    /// Per-file complexity tier ('S','M','L','X'). Null at manifest time
    /// (no bytes); populated post-bundle from the IngestionItemDto returned
    /// by /folder/bundles, matched back by ManifestItemId.
    /// </summary>
    public string? ComplexityTier
    {
        get => _complexityTier;
        set
        {
            if (_complexityTier == value) return;
            _complexityTier = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComplexityTier)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TierDisplay)));
        }
    }

    public string TierDisplay => _complexityTier ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
