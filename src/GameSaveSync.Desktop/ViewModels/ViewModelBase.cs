// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GameSaveSync.Desktop.Services;
using Serilog;

namespace GameSaveSync.Desktop.ViewModels;

/// <summary>
/// ViewModel 基类：统一的“忙碌 + 状态文本”执行包装。
/// 所有异步操作在后台线程（Task.Run）执行，异常统一捕获记日志并 MessageBox 显示。
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    protected IDialogService Dialog { get; }

    protected ViewModelBase(IDialogService dialog)
    {
        Dialog = dialog;
    }

    /// <summary>
    /// 后台线程执行 <paramref name="work"/>；期间显示忙碌状态，
    /// 异常被捕获（Serilog 记日志 + MessageBox 显示），返回是否成功。
    /// 注意：work 内不要直接设置可观察属性/集合，应在返回后于 UI 线程应用结果。
    /// </summary>
    protected async Task<bool> RunBusyAsync(Func<Task> work, string? busyText = null, string? doneText = null)
    {
        IsBusy = true;
        StatusText = busyText ?? "正在处理…";
        try
        {
            await Task.Run(work);
            StatusText = doneText ?? "完成";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "操作失败：{Message}", ex.Message);
            StatusText = $"失败：{ex.Message}";
            Dialog.Error(ex.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
