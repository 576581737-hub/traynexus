using System;
using System.IO;

namespace Traynexus
{
    /// <summary>
    /// 配置从旧目录 (%APPDATA%\MemTrayCN) 迁移到新目录 (%APPDATA%\Traynexus) 的结果。
    /// </summary>
    public class MigrationResult
    {
        public bool Success;
        public string Message;
        public int FilesCopied;
    }

    /// <summary>
    /// 配置目录迁移工具：从 MemTrayCN 旧目录迁移到 Traynexus 新目录。
    /// 仅在旧目录存在且新目录不存在时执行迁移；冲突时不自动覆盖，仅提示。
    /// </summary>
    public static class ConfigMigrator
    {
        public static string OldConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemTrayCN");
            }
        }

        public static string NewConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Traynexus");
            }
        }

        /// <summary>
        /// 是否需要迁移：旧目录存在 && 新目录不存在。
        /// </summary>
        public static bool NeedsMigration()
        {
            return Directory.Exists(OldConfigDir) && !Directory.Exists(NewConfigDir);
        }

        /// <summary>
        /// 是否检测到冲突：旧目录存在 && 新目录也存在。
        /// 此时不会自动迁移，由调用方决定如何提示用户。
        /// </summary>
        public static bool ConflictDetected()
        {
            return Directory.Exists(OldConfigDir) && Directory.Exists(NewConfigDir);
        }

        /// <summary>
        /// 执行迁移：把旧目录所有文件复制到新目录（不覆盖已存在文件）。
        /// 成功返回 FilesCopied 计数；失败返回 Success=false 及异常消息。
        /// </summary>
        public static MigrationResult Migrate()
        {
            try
            {
                Directory.CreateDirectory(NewConfigDir);
                int copied = 0;
                foreach (var oldFile in Directory.EnumerateFiles(OldConfigDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileName(oldFile);
                    string newPath = Path.Combine(NewConfigDir, fileName);
                    // 不覆盖：若目标已存在则跳过
                    if (!File.Exists(newPath))
                    {
                        File.Copy(oldFile, newPath, overwrite: false);
                        copied++;
                    }
                }
                return new MigrationResult
                {
                    Success = true,
                    FilesCopied = copied,
                    Message = "已从 MemTrayCN 迁移 " + copied + " 个文件"
                };
            }
            catch (Exception ex)
            {
                return new MigrationResult
                {
                    Success = false,
                    FilesCopied = 0,
                    Message = ex.Message
                };
            }
        }
    }
}
