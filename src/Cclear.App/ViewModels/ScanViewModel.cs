using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.App.Controls;
using Cclear.Core;
using Cclear.Core.Analysis;
using Cclear.Core.Scanner;

namespace Cclear.App.ViewModels;

/// <summary>扫描页 VM：驱动扫描、进度展示、目录树与空间矩形图（V3 P2）。</summary>
public partial class ScanViewModel : ObservableObject
{
    private readonly IScanner _scanner;
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _cts;
    private FileTree? _lastTree;

    public ScanViewModel() : this(new ManagedTreeScanner())
    {
    }

    public ScanViewModel(IScanner scanner)
    {
        _scanner = scanner;
        // 默认扫描位置 = 上次选择的磁盘（设置持久化）
        var driveRoot = Cclear.Core.Win32.DriveCatalog.TryGetRoot(Services.SettingsStore.Instance.SelectedDrive)
                        ?? @"C:\";
        _rootPath = driveRoot;
        _selectedDriveIndex = Math.Max(0, Drives.ToList().FindIndex(d =>
            string.Equals(d.Root, driveRoot, StringComparison.OrdinalIgnoreCase)));
    }

    public string Title => "空间分析";

    [ObservableProperty]
    private string _rootPath = @"C:\";

    /// <summary>盘选择下拉（V3 多盘）：选中即把扫描位置切到该盘根。</summary>
    public IReadOnlyList<Cclear.Core.Win32.DriveOption> Drives { get; } =
        Cclear.Core.Win32.DriveCatalog.GetFixedDrives();

    [ObservableProperty]
    private int _selectedDriveIndex;

    partial void OnSelectedDriveIndexChanged(int value)
    {
        if (value >= 0 && value < Drives.Count)
        {
            RootPath = Drives[value].Root;
        }
    }

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "选择要扫描的磁盘或目录，然后点击“开始扫描”。";

    [ObservableProperty]
    private string _elapsedText = "";

    /// <summary>实时吞吐（MB/s 与 文件/s，U3 流式进度）。</summary>
    [ObservableProperty]
    private string _throughputText = "";

    [ObservableProperty]
    private int _deniedCount;

    [ObservableProperty]
    private long _filesScanned;

    [ObservableProperty]
    private long _bytesSeen;

    public System.Collections.ObjectModel.ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    // ---------- 空间矩形图（V3 P2 旗舰） ----------

    /// <summary>true=矩形树图（默认），false=目录树。</summary>
    [ObservableProperty]
    private bool _isTreemapMode = true;

    /// <summary>当前展示层的树图数据（Core 已做 0 尺寸过滤 + 400 块聚合护栏）。</summary>
    [ObservableProperty]
    private IReadOnlyList<TreemapItem>? _treemapItems;

    [ObservableProperty]
    private string _hoverInfoText = "";

    /// <summary>布局计算耗时（验收指标 <1s，展示给用户增强透明感）。</summary>
    [ObservableProperty]
    private string _treemapStatsText = "";

    public System.Collections.ObjectModel.ObservableCollection<TreemapBreadcrumbItem> Breadcrumb { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<TreemapLegendItem> LegendItems { get; } = new();

    public bool HasTreemapData => TreemapItems is { Count: > 0 };

    partial void OnTreemapItemsChanged(IReadOnlyList<TreemapItem>? value) => OnPropertyChanged(nameof(HasTreemapData));

    /// <summary>扫描完成后重建矩形图（顶层开始）。</summary>
    private void ResetTreemap(FileTree tree)
    {
        _lastTree = tree;
        ShowTreemapNode(tree.RootIndex);
    }

    /// <summary>切换矩形图展示层并刷新面包屑与图例。</summary>
    public void ShowTreemapNode(int nodeIndex)
    {
        if (_lastTree is null)
        {
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        var items = TreemapLayout.SelectTopChildren(_lastTree, nodeIndex, maxBlocks: 400);
        stopwatch.Stop();
        TreemapItems = items;
        TreemapStatsText = $"布局计算 {stopwatch.Elapsed.TotalMilliseconds:F0} ms · {items.Count} 块";
        HoverInfoText = "";
        RebuildBreadcrumb(nodeIndex);
        RebuildLegend(items);
    }

    /// <summary>矩形图双击钻取（仅目录，聚合块不可钻）。</summary>
    [RelayCommand]
    private void DrillDown(TreemapItem? item)
    {
        if (item is { IsDirectory: true, IsAggregate: false })
        {
            ShowTreemapNode(item.NodeIndex);
        }
    }

    /// <summary>悬停信息（矩形图控件回调）。</summary>
    public void OnTreemapHover(TreemapItem? item)
    {
        HoverInfoText = item is null
            ? ""
            : $"{item.Path} · {ByteSizeFormatter.Format(item.SizeBytes)}"
              + (item.IsDirectory && !item.IsAggregate ? " · 双击进入" : "");
    }

    private void RebuildBreadcrumb(int nodeIndex)
    {
        Breadcrumb.Clear();
        if (_lastTree is null)
        {
            return;
        }
        var chain = new List<int>();
        var current = nodeIndex;
        while (current >= 0)
        {
            chain.Add(current);
            if (current == _lastTree.RootIndex)
            {
                break;
            }
            current = _lastTree.GetParentIndex(current);
        }
        chain.Reverse();
        foreach (var index in chain)
        {
            var node = _lastTree.GetNode(index);
            Breadcrumb.Add(new TreemapBreadcrumbItem(node.Name, index, index == nodeIndex));
        }
    }

    [RelayCommand]
    private void JumpTo(TreemapBreadcrumbItem? crumb)
    {
        if (crumb is not null)
        {
            ShowTreemapNode(crumb.NodeIndex);
        }
    }

    /// <summary>图例：当前层 Top 8 扩展名占用（颜色与矩形块一致）。</summary>
    private void RebuildLegend(IReadOnlyList<TreemapItem> items)
    {
        LegendItems.Clear();
        var byExtension = items
            .Where(i => !i.IsDirectory && !i.IsAggregate)
            .GroupBy(i => (System.IO.Path.GetExtension(i.Name) is { Length: > 0 } ext ? ext : "(无扩展名)").ToLowerInvariant())
            .Select(g => (Extension: g.Key, Bytes: g.Sum(x => x.SizeBytes)))
            .OrderByDescending(x => x.Bytes)
            .Take(8);
        foreach (var entry in byExtension)
        {
            var color = entry.Extension == "(无扩展名)"
                ? TreemapPalette.AggregateColor
                : TreemapPalette.ForExtension(entry.Extension);
            LegendItems.Add(new TreemapLegendItem(
                new SolidColorBrush(color), entry.Extension, ByteSizeFormatter.Format(entry.Bytes)));
        }
    }

    public bool CanScan => !IsScanning;
    public bool CanCancel => IsScanning;

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanCancel));
        StartScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task StartScanAsync()
    {
        RootNodes.Clear();
        DeniedCount = 0;
        ElapsedText = "";
        FilesScanned = 0;
        BytesSeen = 0;
        _stopwatch.Restart();
        IsScanning = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var progress = new Progress<ScanProgress>(p =>
        {
            FilesScanned = p.FilesScanned;
            BytesSeen = p.BytesSeen;
            var elapsed = _stopwatch.Elapsed.TotalSeconds;
            ThroughputText = elapsed > 0.5
                ? $"{ByteSizeFormatter.Format((long)(p.BytesSeen / elapsed))}/s · {p.FilesScanned / elapsed:N0} 文件/s"
                : "";
            StatusText = $"正在扫描：{ByteSizeFormatter.Format(p.BytesSeen)} / {p.FilesScanned:N0} 个文件 / {TruncateDir(p.CurrentDir)}";
        });

        try
        {
            var result = await _scanner.ScanAsync(new ScanRequest(RootPath, Services.SettingsStore.Instance.NormalizedExclusions()), progress, token);
            var root = new TreeNodeViewModel(result.Tree, result.Tree.RootIndex);
            RootNodes.Add(root);
            ResetTreemap(result.Tree);
            DeniedCount = result.AccessDeniedDirs.Count;
            StatusText = $"扫描完成：{result.Tree.Count:N0} 个条目"
                + (DeniedCount > 0 ? $"；{DeniedCount} 个目录无权限访问" : "");
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消。";
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
        }
        finally
        {
            _stopwatch.Stop();
            ElapsedText = $"用时 {_stopwatch.Elapsed:hh\\:mm\\:ss}";
            ThroughputText = "";
            IsScanning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelScan()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要扫描的目录",
        };
        if (dialog.ShowDialog() == true)
        {
            RootPath = dialog.FolderName;
        }
    }

    private static string TruncateDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return "";
        }
        return dir.Length <= 60 ? dir : "…" + dir[^59..];
    }
}

/// <summary>矩形图面包屑项。</summary>
public sealed record TreemapBreadcrumbItem(string Name, int NodeIndex, bool IsCurrent);

/// <summary>矩形图图例项（扩展名 → 颜色与占用）。</summary>
public sealed record TreemapLegendItem(Brush Brush, string Extension, string SizeText);
