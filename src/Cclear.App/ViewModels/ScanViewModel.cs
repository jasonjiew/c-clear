using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cclear.Core;
using Cclear.Core.Scanner;

namespace Cclear.App.ViewModels;

/// <summary>扫描页 VM：驱动扫描、进度展示与目录树。</summary>
public partial class ScanViewModel : ObservableObject
{
    private readonly IScanner _scanner;
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _cts;

    public ScanViewModel() : this(new ManagedTreeScanner())
    {
    }

    public ScanViewModel(IScanner scanner)
    {
        _scanner = scanner;
    }

    public string Title => "空间分析";

    [ObservableProperty]
    private string _rootPath = @"C:\";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusText = "选择要扫描的磁盘或目录，然后点击“开始扫描”。";

    [ObservableProperty]
    private string _elapsedText = "";

    [ObservableProperty]
    private int _deniedCount;

    [ObservableProperty]
    private long _filesScanned;

    [ObservableProperty]
    private long _bytesSeen;

    public System.Collections.ObjectModel.ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

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
            StatusText = $"正在扫描：{ByteSizeFormatter.Format(p.BytesSeen)} / {p.FilesScanned:N0} 个文件 / {TruncateDir(p.CurrentDir)}";
        });

        try
        {
            var result = await _scanner.ScanAsync(new ScanRequest(RootPath), progress, token);
            var root = new TreeNodeViewModel(result.Tree, result.Tree.RootIndex);
            RootNodes.Add(root);
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
