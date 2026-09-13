using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.App.Services;
using Cclear.Core;
using Cclear.Core.Cleaner;
using Cclear.Core.Duplicates;
using Cclear.Core.Rules;

namespace Cclear.App.ViewModels;

/// <summary>重复文件与空目录页（默认只分析用户选定的目录）。</summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private readonly ICleaner _cleaner;
    private readonly ICleanDialogs _dialogs;

    public string Title => "重复与空目录";

    public DuplicatesViewModel() : this(new ShellCleaner(), new DialogService())
    {
    }

    public DuplicatesViewModel(ICleaner cleaner, ICleanDialogs dialogs)
    {
        _cleaner = cleaner;
        _dialogs = dialogs;
    }

    [ObservableProperty]
    private string _rootPath = @"C:\Users";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "选择要分析的目录。默认只保留每组最旧的一份。";

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public ObservableCollection<EmptyDirRow> EmptyDirs { get; } = new();

    public bool CanRun => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        FindDuplicatesCommand.NotifyCanExecuteChanged();
        FindEmptyDirsCommand.NotifyCanExecuteChanged();
        DeleteDuplicatesCommand.NotifyCanExecuteChanged();
        DeleteEmptyDirsCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelectedDuplicates => Groups.Any(g => g.Files.Any(f => f.IsChecked));
    public bool HasSelectedEmptyDirs => EmptyDirs.Any(d => d.IsChecked);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        Groups.Clear();
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            var report = await DuplicateFinder.FindAsync(
                new DuplicateOptions(RootPath, 1024 * 1024, SettingsStore.Instance.NormalizedExclusions()),
                progress, CancellationToken.None);
            foreach (var group in report.Groups.Take(200))
            {
                Groups.Add(new DuplicateGroupViewModel(group));
            }
            StatusText = report.Groups.Count == 0
                ? "没有发现重复文件。"
                : $"发现 {report.Groups.Count} 组重复文件，可回收 {ByteSizeFormatter.Format(report.WastedBytes)}"
                  + (report.Groups.Count > 200 ? $"（仅显示前 200 组）" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "分析已取消。";
        }
        catch (Exception ex)
        {
            StatusText = "分析失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task FindEmptyDirsAsync()
    {
        IsBusy = true;
        EmptyDirs.Clear();
        try
        {
            StatusText = "正在扫描空目录…";
            var empties = await Task.Run(() =>
                EmptyDirectoryFinder.Find(RootPath, SettingsStore.Instance.NormalizedExclusions(), CancellationToken.None));
            foreach (var dir in empties.Take(2000))
            {
                EmptyDirs.Add(new EmptyDirRow(dir));
            }
            StatusText = empties.Count == 0 ? "没有发现空目录。" : $"发现 {empties.Count} 个空目录链。";
        }
        catch (Exception ex)
        {
            StatusText = "分析失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void DeleteDuplicates()
    {
        ExecuteCategories("duplicates", Groups
            .SelectMany(g => g.Files.Where(f => f.IsChecked))
            .Select(f => new CleanItem(f.Path, f.SizeBytesValue, f.LastWriteTimeUtc))
            .ToList(), "删除重复文件（每组保留最旧一份）");
        Groups.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void DeleteEmptyDirs()
    {
        ExecuteCategories("空目录", EmptyDirs
            .Where(d => d.IsChecked)
            .Select(d => new CleanItem(d.Path, 0, Directory.GetLastWriteTimeUtc(d.Path)))
            .ToList(), "删除空目录（自底向上折叠后的目录链）");
        EmptyDirs.Clear();
    }

    private void ExecuteCategories(string ruleId, List<CleanItem> items, string displayName)
    {
        if (items.Count == 0)
        {
            StatusText = "没有勾选任何内容。";
            return;
        }
        bool permanent = SettingsStore.Instance.PermanentDelete;
        var category = new CleanCategory(ruleId, displayName, SafetyLevel.Caution,
            items.Sum(i => i.SizeBytes), items.Count, items, "用户手动勾选的清理项", null, false);
        IsBusy = true;
        try
        {
            if (!_dialogs.ConfirmClean([category], useRecycleBin: !permanent))
            {
                return;
            }
            var result = _dialogs.RunWithProgress((progress, ct) =>
                _cleaner.ExecuteAsync([category], new CleanOptions(UseRecycleBin: !permanent), progress, ct)
                    .GetAwaiter().GetResult());
            _dialogs.ShowResult(result, (_cleaner as ShellCleaner)?.LastAuditLogPath ?? "", category.EstimatedBytes);
            StatusText = $"清理完成：删除 {result.DeletedFiles:N0} 项，跳过 {result.SkippedFiles:N0} 项。";
        }
        catch (Exception ex)
        {
            StatusText = "清理失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>一组重复文件：默认保留最旧一份（不勾选），其余勾选待删。</summary>
public sealed partial class DuplicateGroupViewModel : ObservableObject
{
    public DuplicateGroupViewModel(DuplicateGroup group)
    {
        SizeText = ByteSizeFormatter.Format(group.SizeBytes);
        WastedText = ByteSizeFormatter.Format(group.WastedBytes);
        Files = new ObservableCollection<DuplicateFileRow>();
        for (int i = 0; i < group.Files.Count; i++)
        {
            var file = group.Files[i];
            var row = new DuplicateFileRow(file.Path, file.SizeBytes, file.LastWriteTimeUtc, i > 0);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DuplicateFileRow.IsChecked))
                {
                    OnPropertyChanged(nameof(SelectedCountText));
                }
            };
            Files.Add(row);
        }
    }

    public string SizeText { get; }
    public string WastedText { get; }
    public string Header => $"重复 x{Files.Count} · 每份 {SizeText} · 可回收 {WastedText}";
    public string SelectedCountText => $"已选 {Files.Count(f => f.IsChecked)}/{Files.Count}";

    public ObservableCollection<DuplicateFileRow> Files { get; }
}

public sealed partial class DuplicateFileRow : ObservableObject
{
    public DuplicateFileRow(string path, long sizeBytes, DateTime lastWrite, bool isChecked)
    {
        Path = path;
        SizeBytesValue = sizeBytes;
        LastWriteTimeUtc = lastWrite;
        SizeText = ByteSizeFormatter.Format(sizeBytes);
        LastWriteText = lastWrite.ToString("yyyy-MM-dd HH:mm");
        _isChecked = isChecked;
    }

    public string Path { get; }
    public long SizeBytesValue { get; }
    public DateTime LastWriteTimeUtc { get; }
    public string SizeText { get; }
    public string LastWriteText { get; }

    [ObservableProperty]
    private bool _isChecked;
}

public sealed partial class EmptyDirRow : ObservableObject
{
    public EmptyDirRow(string path)
    {
        Path = path;
    }

    public string Path { get; }

    [ObservableProperty]
    private bool _isChecked = true;
}
