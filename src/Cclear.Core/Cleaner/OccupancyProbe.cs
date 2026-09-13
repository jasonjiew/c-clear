using System;
using System.IO;

namespace Cclear.Core.Cleaner;

/// <summary>
/// 删除前占用预检：独占打开 + 就地重命名双重测试。任一失败即判定“被占用/不可动”，绝不强删。
/// </summary>
public static class OccupancyProbe
{
    /// <summary>true=可安全尝试删除；false=被占用或不可访问（应跳过）。</summary>
    public static bool CanDelete(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path))
        {
            return false; // 已不存在，无需删除
        }

        // 1) 独占打开测试（拒绝任何共享）：若其他进程持有句柄，这里直接失败
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1,
                FileOptions.None);
        }
        catch (IOException ex)
        {
            reason = "被占用：" + ex.GetType().Name;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "无访问权限";
            return false;
        }

        // 2) 就地重命名测试：被内存映射/执行的文件可打开但不可重命名
        string renamed = path + ".cclear-probe";
        try
        {
            File.Move(path, renamed);
            try
            {
                File.Move(renamed, path);
            }
            catch (Exception restoreEx)
            {
                // 极端情况：改名成功但改不回去——立即报警而不是继续删除
                throw new InvalidOperationException($"占用预检后无法恢复文件名：{renamed}", restoreEx);
            }
        }
        catch (IOException)
        {
            reason = "被占用（重命名测试失败）";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "无访问权限";
            return false;
        }
        return true;
    }
}
