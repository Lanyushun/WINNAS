using Winnas.Console.Services;

namespace Winnas.Console.Infrastructure;

/// <summary>"--checkupdate": prints the GitHub Release comparison without a window.</summary>
public static class UpdateCli
{
    public static async Task<int> RunAsync()
    {
        System.Console.WriteLine("WINNAS 控制台 v" + HelpTopics.Version);
        System.Console.WriteLine("仓库       : " + UpdateChecker.RepoUrl);
        System.Console.WriteLine("联系方式   : " + UpdateChecker.ContactEmail);
        System.Console.WriteLine("检查 GitHub Releases ...");

        var info = await UpdateChecker.CheckAsync();
        System.Console.WriteLine("结果       : " + info.Message);
        if (!string.IsNullOrWhiteSpace(info.LatestVersion))
            System.Console.WriteLine("远端版本   : " + info.LatestVersion);
        if (!string.IsNullOrWhiteSpace(info.PublishedAt))
            System.Console.WriteLine("发布时间   : " + info.PublishedAt);
        if (!string.IsNullOrWhiteSpace(info.ReleaseUrl))
            System.Console.WriteLine("发布页     : " + info.ReleaseUrl);
        if (!string.IsNullOrWhiteSpace(info.DownloadUrl))
            System.Console.WriteLine("下载地址   : " + info.DownloadUrl);
        if (!string.IsNullOrWhiteSpace(info.Notes))
            System.Console.WriteLine("更新说明   :\n" + info.Notes);

        if (info.Kind is UpdateKind.Unreachable or UpdateKind.RateLimited or UpdateKind.BadResponse)
        {
            System.Console.WriteLine("提示       : 打不开 API 时可直接访问发布页：" + UpdateChecker.ReleasesUrl);
            return 2;
        }
        return info.HasNew ? 1 : 0;
    }
}
