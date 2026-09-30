using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Revive.App.Services;
using Revive.Core.IO;
using Revive.Core.Model;
using Revive.Core.Recovery;
using Revive.Core.Scanning;

namespace Revive.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const long MaxPreviewBytes = 60L * 1024 * 1024;

    private readonly List<FileItemViewModel> _all = [];
    private readonly ConcurrentQueue<FoundFile> _incoming = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Stopwatch _scanClock = new();

    private LocationViewModel? _selectedLocation;
    private bool _deepScan;
    private bool _showResults;
    private bool _isScanning;
    private string _scanTitle = "";
    private string _scanStage = "";
    private double _scanProgress;
    private string _scanProgressText = "";
    private string? _statusMessage;
    private CategoryViewModel _selectedCategory;
    private string _searchText = "";
    private bool _hideUnlikely;
    private FileItemViewModel? _selectedFile;
    private ImageSource? _previewImage;
    private string? _previewMessage;
    private bool _isPreviewLoading;
    private int _selectedCount;
    private long _selectedBytes;
    private bool _isRecovering;
    private string _recoveryText = "";
    private double _recoveryProgress;
    private string? _sortKey;
    private ListSortDirection _sortDirection;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _recoveryCts;
    private CancellationTokenSource? _previewCts;
    private IDiskSource? _source;
    private char? _sourceDrive;
    private int _scanId;

    public MainViewModel()
    {
        Categories =
        [
            new CategoryViewModel(null, "All files", ""),
            .. Enum.GetValues<FileCategory>().Select(c => new CategoryViewModel(c, FileTypes.PluralLabel(c), Formatting.Glyph(c))),
        ];
        _selectedCategory = Categories[0];

        SelectLocationCommand = new RelayCommand(p => SelectedLocation = p as LocationViewModel ?? SelectedLocation);
        RefreshDrivesCommand = new RelayCommand(RefreshLocations);
        OpenImageCommand = new RelayCommand(OpenImage);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        StartScanCommand = new RelayCommand(() => _ = StartScanAsync(), () => CanStartScan);
        StopScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        NewScanCommand = new RelayCommand(NewScan);
        RecoverCommand = new RelayCommand(() => _ = RecoverAsync(), () => SelectedCount > 0 && !IsRecovering);
        CancelRecoveryCommand = new RelayCommand(() => _recoveryCts?.Cancel());
        SelectAllShownCommand = new RelayCommand(() => SetSelection(VisibleFiles, true), () => VisibleFiles.Count > 0);
        ClearSelectionCommand = new RelayCommand(() => SetSelection(_all, false), () => SelectedCount > 0);

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => FlushIncoming(), Dispatcher.CurrentDispatcher);
        RefreshLocations();
    }

    // ---------- Home page ----------

    public ObservableCollection<LocationViewModel> Locations { get; } = [];

    public bool IsAdministrator => Elevation.IsAdministrator;

    public LocationViewModel? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (!Set(ref _selectedLocation, value))
                return;
            foreach (var location in Locations)
                location.IsSelected = location == value;
            OnPropertyChanged(nameof(ShowScanModes));
            OnPropertyChanged(nameof(ShowAdminHint));
        }
    }

    public bool ShowScanModes => SelectedLocation is not null && SelectedLocation.Kind != LocationKind.RecycleBin;

    public bool ShowAdminHint => SelectedLocation?.NeedsAdministrator == true && !IsAdministrator;

    public bool DeepScan
    {
        get => _deepScan;
        set
        {
            if (Set(ref _deepScan, value))
                OnPropertyChanged(nameof(QuickScan));
        }
    }

    public bool QuickScan
    {
        get => !_deepScan;
        set => DeepScan = !value;
    }

    public bool CanStartScan => SelectedLocation is not null && !IsScanning && !ShowAdminHint;

    public ICommand SelectLocationCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand OpenImageCommand { get; }
    public ICommand RestartAsAdminCommand { get; }
    public ICommand StartScanCommand { get; }

    // ---------- Results page ----------

    public bool ShowResults
    {
        get => _showResults;
        private set => Set(ref _showResults, value);
    }

    public string ScanTitle
    {
        get => _scanTitle;
        private set => Set(ref _scanTitle, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public string ScanStage
    {
        get => _scanStage;
        private set => Set(ref _scanStage, value);
    }

    public double ScanProgress
    {
        get => _scanProgress;
        private set => Set(ref _scanProgress, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => Set(ref _scanProgressText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public ObservableCollection<CategoryViewModel> Categories { get; }

    public CategoryViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (Set(ref _selectedCategory, value ?? Categories[0]))
                RebuildVisible();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
                RebuildVisible();
        }
    }

    public bool HideUnlikely
    {
        get => _hideUnlikely;
        set
        {
            if (Set(ref _hideUnlikely, value))
                RebuildVisible();
        }
    }

    public BulkObservableCollection<FileItemViewModel> VisibleFiles { get; } = [];

    public bool HasNoVisibleFiles => VisibleFiles.Count == 0;

    public string EmptyText => IsScanning
        ? "Searching… files will appear here as they're found."
        : _all.Count == 0
            ? (DeepScan || SelectedLocation?.Kind == LocationKind.RecycleBin
                ? "No deleted files were found here."
                : "No deleted files were found. Try a Deep scan: it searches every sector and finds files even after formatting.")
            : "No files match your filters.";

    public FileItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (Set(ref _selectedFile, value))
                _ = LoadPreviewAsync(value);
        }
    }

    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set => Set(ref _previewImage, value);
    }

    public string? PreviewMessage
    {
        get => _previewMessage;
        private set => Set(ref _previewMessage, value);
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => Set(ref _isPreviewLoading, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (Set(ref _selectedCount, value))
                OnPropertyChanged(nameof(SelectionText));
        }
    }

    public string SelectionText => SelectedCount == 0
        ? "Tick the files you want back"
        : $"{SelectedCount:N0} {(SelectedCount == 1 ? "file" : "files")} selected · {Formatting.Bytes(_selectedBytes)}";

    public ICommand StopScanCommand { get; }
    public ICommand NewScanCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand SelectAllShownCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    // ---------- Recovery overlay ----------

    public bool IsRecovering
    {
        get => _isRecovering;
        private set => Set(ref _isRecovering, value);
    }

    public string RecoveryText
    {
        get => _recoveryText;
        private set => Set(ref _recoveryText, value);
    }

    public double RecoveryProgress
    {
        get => _recoveryProgress;
        private set => Set(ref _recoveryProgress, value);
    }

    public ICommand CancelRecoveryCommand { get; }

    // ---------- Actions ----------

    private void RefreshLocations()
    {
        var previous = SelectedLocation;
        var images = Locations.Where(l => l.Kind == LocationKind.Image).ToList();
        Locations.Clear();
        Locations.Add(LocationViewModel.RecycleBin());
        foreach (var drive in DriveLister.List())
            Locations.Add(LocationViewModel.ForDrive(drive));
        foreach (var image in images)
            Locations.Add(image);

        SelectedLocation = Locations.FirstOrDefault(l => previous is not null && l.Title == previous.Title && l.Kind == previous.Kind)
            ?? Locations.FirstOrDefault(l => l.Drive?.IsRemovable == true)
            ?? Locations[0];
    }

    private void OpenImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a disk image to scan",
            Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.vhd;*.001)|*.img;*.dd;*.raw;*.bin;*.vhd;*.001|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            AddImage(dialog.FileName);
    }

    /// <summary>Adds a disk image to the list of places to scan and selects it.</summary>
    public void AddImage(string path)
    {
        var location = Locations.FirstOrDefault(l => string.Equals(l.ImagePath, path, StringComparison.OrdinalIgnoreCase))
            ?? LocationViewModel.ForImage(path);
        if (!Locations.Contains(location))
            Locations.Add(location);
        SelectedLocation = location;
    }

    private void RestartAsAdmin()
    {
        if (Elevation.TryRestartElevated())
            Application.Current.Shutdown();
    }

    private async Task StartScanAsync()
    {
        var location = SelectedLocation;
        if (location is null)
            return;

        ResetResults();
        int id = ++_scanId;
        // A cancelled scan can still be finishing on another thread; drop anything it reports.
        Action<FoundFile> found = file =>
        {
            if (Volatile.Read(ref _scanId) == id)
                _incoming.Enqueue(file);
        };
        bool deep = DeepScan && location.Kind != LocationKind.RecycleBin;
        ScanTitle = location.Kind == LocationKind.RecycleBin ? "Recycle Bin" : $"{location.Title} · {(deep ? "Deep scan" : "Quick scan")}";
        ShowResults = true;
        IsScanning = true;
        OnPropertyChanged(nameof(EmptyText));
        _scanCts = new CancellationTokenSource();
        _scanClock.Restart();
        _flushTimer.Start();

        var progress = new Progress<ScanStatus>(status =>
        {
            if (id == _scanId)
                UpdateScanStatus(status);
        });
        try
        {
            ScanSummary summary;
            if (location.Kind == LocationKind.RecycleBin)
                summary = await ScanSession.ScanRecycleBinAsync(found, progress, _scanCts.Token);
            else
            {
                _source = location.Kind == LocationKind.Drive
                    ? await Task.Run(() => RawVolumeSource.Open(location.Drive!.Letter))
                    : new ImageFileSource(location.ImagePath!);
                _sourceDrive = location.Drive?.Letter;
                summary = await ScanSession.ScanAsync(_source, deep ? ScanMode.Deep : ScanMode.Quick, found, progress, _scanCts.Token);
            }
            if (id != _scanId)
                return;
            FlushIncoming();
            StatusMessage = Describe(summary, deep);
        }
        catch (OperationCanceledException)
        {
            if (id != _scanId)
                return;
            FlushIncoming();
            StatusMessage = $"Scan stopped. Showing the {_all.Count:N0} files found before you stopped it.";
        }
        catch (UnauthorizedAccessException) when (id == _scanId)
        {
            StatusMessage = "Revive needs administrator permission to read this drive. Go back and choose \"Restart as administrator\".";
        }
        catch (Exception ex)
        {
            if (id != _scanId)
                return;
            FlushIncoming();
            StatusMessage = $"The scan couldn't finish: {ex.Message} Unplugging and reconnecting the drive sometimes helps.";
        }
        finally
        {
            if (id == _scanId)
                FinishScan();
        }
    }

    private void FinishScan()
    {
        _flushTimer.Stop();
        _scanClock.Stop();
        IsScanning = false;
        if (_sortKey is not null)
            RebuildVisible();
        OnPropertyChanged(nameof(EmptyText));
        CommandManager.InvalidateRequerySuggested();
    }

    private string Describe(ScanSummary summary, bool deep)
    {
        string found = _all.Count == 0 ? "Scan finished." : $"Scan finished: {_all.Count:N0} deleted {(_all.Count == 1 ? "file" : "files")} found.";
        var notes = new List<string> { found };
        if (summary.FellBackToDeep)
            notes.Add("The drive's file system couldn't be read (it may have been formatted or damaged), so a deep scan was run instead.");
        else if (!deep && _all.Count > 0 && SelectedLocation?.Kind != LocationKind.RecycleBin)
            notes.Add("Missing something? A Deep scan also finds files whose records are gone.");
        if (summary.Problem is not null)
            notes.Add(summary.Problem);
        if (summary.BadSectors > 0)
            notes.Add($"{summary.BadSectors:N0} damaged sectors couldn't be read. The drive may be failing, so copy what you need soon.");
        return string.Join(" ", notes);
    }

    private void UpdateScanStatus(ScanStatus status)
    {
        ScanStage = status.Stage;
        ScanProgress = status.Fraction * 100;
        var elapsed = _scanClock.Elapsed;
        string text = $"{status.Fraction:P0}";
        if (status.Fraction is > 0.02 and < 1 && elapsed.TotalSeconds > 5)
            text += $" · about {Formatting.Duration(elapsed / status.Fraction - elapsed)} left";
        ScanProgressText = text;
    }

    private void FlushIncoming()
    {
        if (_incoming.IsEmpty)
            return;
        var added = new List<FileItemViewModel>();
        while (_incoming.TryDequeue(out var file))
        {
            var item = new FileItemViewModel(file, OnItemSelectionChanged);
            _all.Add(item);
            added.Add(item);
        }

        Categories[0].Count = _all.Count;
        foreach (var group in added.GroupBy(a => a.Category))
            Categories.First(c => c.Category == group.Key).Count += group.Count();

        VisibleFiles.AddRange(added.Where(Matches));
        OnPropertyChanged(nameof(HasNoVisibleFiles));
        OnPropertyChanged(nameof(EmptyText));
    }

    private bool Matches(FileItemViewModel item) =>
        (SelectedCategory.Category is null || item.Category == SelectedCategory.Category)
        && (!HideUnlikely || item.Chance != RecoveryChance.Poor)
        && (SearchText.Length == 0
            || item.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Folder.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    public void Sort(string key, ListSortDirection direction)
    {
        _sortKey = key;
        _sortDirection = direction;
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        IEnumerable<FileItemViewModel> items = _all.Where(Matches);
        if (_sortKey is not null)
        {
            Func<FileItemViewModel, object?> key = _sortKey switch
            {
                nameof(FileItemViewModel.Size) => f => f.Size,
                nameof(FileItemViewModel.Date) => f => f.Date,
                nameof(FileItemViewModel.Folder) => f => f.Folder,
                nameof(FileItemViewModel.Chance) => f => f.Chance,
                _ => f => f.Name,
            };
            var comparer = Comparer<object?>.Create((a, b) => a is string sa && b is string sb
                ? StringComparer.CurrentCultureIgnoreCase.Compare(sa, sb)
                : Comparer<object?>.Default.Compare(a, b));
            items = _sortDirection == ListSortDirection.Ascending ? items.OrderBy(key, comparer) : items.OrderByDescending(key, comparer);
        }
        VisibleFiles.ReplaceAll(items);
        OnPropertyChanged(nameof(HasNoVisibleFiles));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void OnItemSelectionChanged(FileItemViewModel item, bool selected)
    {
        _selectedBytes += selected ? item.Size : -item.Size;
        SelectedCount += selected ? 1 : -1;
        OnPropertyChanged(nameof(SelectionText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static void SetSelection(IEnumerable<FileItemViewModel> items, bool selected)
    {
        foreach (var item in items.ToList())
            item.IsSelected = selected;
    }

    private void NewScan()
    {
        if (IsScanning)
        {
            _scanCts?.Cancel();
            _scanId++;
            FinishScan();
        }
        ShowResults = false;
        ResetResults();
        RefreshLocations();
    }

    private void ResetResults()
    {
        _all.Clear();
        _incoming.Clear();
        VisibleFiles.ReplaceAll([]);
        foreach (var category in Categories)
            category.Count = 0;
        SelectedCategory = Categories[0];
        SelectedFile = null;
        _selectedBytes = 0;
        SelectedCount = 0;
        StatusMessage = null;
        ScanStage = "";
        ScanProgress = 0;
        ScanProgressText = "";
        _source?.Dispose();
        _source = null;
        _sourceDrive = null;
        OnPropertyChanged(nameof(HasNoVisibleFiles));
    }

    private async Task LoadPreviewAsync(FileItemViewModel? item)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        PreviewImage = null;
        PreviewMessage = null;
        if (item is null)
            return;

        if (item.File.IsFolder)
        {
            PreviewMessage = "This is a whole folder from the Recycle Bin. Recovering it brings back everything inside.";
            return;
        }

        IsPreviewLoading = true;
        try
        {
            var (image, message) = await Task.Run(() => BuildPreview(item.File, ct), ct);
            if (ct.IsCancellationRequested)
                return;
            PreviewImage = image;
            PreviewMessage = message;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsPreviewLoading = false;
        }
    }

    private static (ImageSource? Image, string? Message) BuildPreview(FoundFile file, CancellationToken ct)
    {
        try
        {
            using var stream = file.Content.OpenRead();
            var head = new byte[(int)Math.Min(64 * 1024, file.Size)];
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (read > 0 && head.AsSpan(0, read).IndexOfAnyExcept((byte)0) < 0)
                return (null, "The start of this file is blank. Its space was probably wiped or reused, so it's unlikely to open.");

            if (file.Category != FileCategory.Photo)
                return (null, "No preview for this type. Recover it and open it with your usual app.");
            if (file.Size > MaxPreviewBytes)
                return (null, "This photo is too large to preview here. Recover it to view it.");

            ct.ThrowIfCancellationRequested();
            var buffer = new MemoryStream((int)file.Size);
            buffer.Write(head, 0, read);
            stream.CopyTo(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = 640;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze();
            return (image, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (null, file.Extension is "heic" or "avif" or "cr2" or "cr3" or "nef" or "arw" or "dng"
                ? "Windows can't preview this photo format here. Recover it and open it in the Photos app."
                : "This photo can't be previewed; it may be damaged. You can still try recovering it.");
        }
    }

    private async Task RecoverAsync()
    {
        var files = _all.Where(f => f.IsSelected).Select(f => f.File).ToList();
        if (files.Count == 0)
            return;

        var dialog = new OpenFolderDialog { Title = "Choose where to save the recovered files" };
        if (dialog.ShowDialog() != true)
            return;

        if (Recoverer.IsSameDrive(dialog.FolderName, _sourceDrive))
        {
            var answer = MessageBox.Show(
                $"You're about to save onto {_sourceDrive}:, the same drive you're recovering from.\n\n" +
                "Writing there can overwrite deleted files you haven't got back yet. It's much safer to choose " +
                "a folder on a different drive (another disk, or a USB stick).\n\nSave to this drive anyway?",
                "Choose a different drive", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        string destination = Path.Combine(dialog.FolderName, $"Recovered {DateTime.Now:yyyy-MM-dd HH.mm}");
        _recoveryCts = new CancellationTokenSource();
        IsRecovering = true;
        RecoveryProgress = 0;
        RecoveryText = "Starting…";
        var progress = new Progress<RecoveryProgress>(p =>
        {
            RecoveryProgress = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : 100.0 * p.FilesDone / Math.Max(1, p.FilesTotal);
            RecoveryText = p.CurrentFile.Length > 0 ? $"{Math.Min(p.FilesDone + 1, p.FilesTotal):N0} of {p.FilesTotal:N0} · {p.CurrentFile}" : "Finishing…";
        });

        try
        {
            var report = await Recoverer.RecoverAsync(files, destination, keepFolders: true, progress, _recoveryCts.Token);
            IsRecovering = false;
            string message = $"{report.Recovered:N0} {(report.Recovered == 1 ? "file was" : "files were")} saved to:\n{destination}";
            if (report.Failures.Count > 0)
                message += $"\n\n{report.Failures.Count:N0} couldn't be saved, for example {report.Failures[0].File.Name}: {report.Failures[0].Reason}";
            message += "\n\nOpen the folder now?";
            if (MessageBox.Show(message, "Recovery finished", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes
                && Directory.Exists(destination))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{destination}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            IsRecovering = false;
            MessageBox.Show($"Recovery was cancelled. Files saved so far are in:\n{destination}", "Recovery cancelled",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsRecovering = false;
            MessageBox.Show($"Couldn't save to that folder: {ex.Message}", "Recovery failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Shutdown()
    {
        _scanCts?.Cancel();
        _recoveryCts?.Cancel();
        _source?.Dispose();
    }
}
