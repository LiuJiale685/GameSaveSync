// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameSaveSync.Data.Repositories;
using Serilog;

namespace GameSaveSync.Desktop.Services;

/// <summary>更新检查结果（Succeeded=false 表示网络失败/限流/数据异常，界面应静默降级）。</summary>
public sealed record UpdateCheckResult(bool Succeeded, Version? LatestVersion)
{
    /// <summary>是否存在比当前程序更新的版本。</summary>
    public bool HasUpdate =>
        Succeeded
        && LatestVersion is not null
        && UpdateService.Normalize(LatestVersion) > UpdateService.Normalize(UpdateService.CurrentVersion);
}

/// <summary>
/// “提醒式”更新检查：查询 GitHub Releases 最新 tag 并与程序集版本比对。
/// 只提示、不下载；结果缓存 24 小时，失败一律静默（不阻塞启动、不弹错误框）。
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "LiuJiale685";
    private const string Repository = "GameSaveSync";

    private static readonly Uri LatestReleaseApi =
        new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");

    /// <summary>发布页地址（人工下载入口）；分支或仓库改名时只需改常量。</summary>
    public static readonly Uri ReleasesPage =
        new($"https://github.com/{Owner}/{Repository}/releases/latest");

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    /// <summary>当前程序版本（与 Desktop.csproj 的 Version 一致）。</summary>
    public static Version CurrentVersion { get; } =
        typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);

    private readonly AppSettingsRepository _settings;
    private readonly HttpClient _http;

    /// <summary>最近一次检查结果（启动检查与设置页共享，避免重复请求）。</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    public UpdateService(AppSettingsRepository settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 要求 User-Agent；不带会被直接 403。
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GameSaveSync/{CurrentVersion.ToString(3)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 检查是否有新版本。<paramref name="force"/>=false 时 24 小时内复用缓存结果。
    /// 任何异常都被吞掉并返回 <c>Succeeded=false</c>。
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool force, CancellationToken ct = default)
    {
        try
        {
            if (!force)
            {
                var cached = await ReadCacheAsync(ct);
                if (cached is not null)
                    return LastResult = cached;
            }

            using var response = await _http.GetAsync(LatestReleaseApi, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Information("检查更新失败：HTTP {Status}", (int)response.StatusCode);
                return LastResult = new UpdateCheckResult(false, null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var version = ParseVersion(tag);
            if (version is null)
            {
                Log.Information("检查更新失败：无法解析 tag「{Tag}」。", tag);
                return LastResult = new UpdateCheckResult(false, null);
            }

            await _settings.SetAsync(
                AppSettingsRepository.UpdateCheckedAtKey, DateTimeOffset.UtcNow.ToString("O"), ct);
            await _settings.SetAsync(
                AppSettingsRepository.UpdateLatestVersionKey, version.ToString(3), ct);

            Log.Information("检查更新完成：当前 {Current}，最新 {Latest}。",
                CurrentVersion.ToString(3), version.ToString(3));
            return LastResult = new UpdateCheckResult(true, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 国内网络访问 GitHub 常超时：只记 Information，不打扰用户。
            Log.Information(ex, "检查更新失败（已忽略）。");
            return LastResult = new UpdateCheckResult(false, null);
        }
    }

    /// <summary>用系统默认浏览器打开 GitHub 发布页。</summary>
    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开更新下载页失败。");
        }
    }

    private async Task<UpdateCheckResult?> ReadCacheAsync(CancellationToken ct)
    {
        var checkedAtRaw = await _settings.GetAsync(AppSettingsRepository.UpdateCheckedAtKey, ct);
        var versionRaw = await _settings.GetAsync(AppSettingsRepository.UpdateLatestVersionKey, ct);
        if (!DateTimeOffset.TryParse(checkedAtRaw, out var checkedAt)
            || DateTimeOffset.UtcNow - checkedAt >= CacheDuration)
            return null;
        var version = ParseVersion(versionRaw);
        return version is null ? null : new UpdateCheckResult(true, version);
    }

    /// <summary>解析 tag（如 <c>v1.2.0</c> / <c>v1.2.0-beta</c>）为版本号。</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];
        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>补全未指定的版本段，避免 1.0.0 与 1.0.0.0 比较出“假更新”。</summary>
    internal static Version Normalize(Version version) => new(
        version.Major,
        version.Minor < 0 ? 0 : version.Minor,
        version.Build < 0 ? 0 : version.Build,
        version.Revision < 0 ? 0 : version.Revision);
}
