using System;
using System.IO;

namespace SCL.Core
{
    public static class InstanceManager
    {
        // Minecraft 根目录：%AppData%\.minecraft
        public static string RootPath =
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
            ".minecraft");

        /// <summary>
        /// .minecraft/versions
        /// </summary>
        public static string VersionsPath
        {
            get
            {
                string path = Path.Combine(
                    RootPath,
                    "versions");

                Directory.CreateDirectory(path);

                return path;
            }
        }

        /// <summary>
        /// .minecraft/libraries
        /// </summary>
        public static string LibrariesPath
        {
            get
            {
                string path = Path.Combine(
                    RootPath,
                    "libraries");

                Directory.CreateDirectory(path);

                return path;
            }
        }

        /// <summary>
        /// .minecraft/assets
        /// </summary>
        public static string AssetsPath
        {
            get
            {
                string path = Path.Combine(
                    RootPath,
                    "assets");

                Directory.CreateDirectory(path);

                return path;
            }
        }

        /// <summary>
        /// 获取指定 Minecraft / Loader 的独立游戏目录。
        ///
        /// 目录结构：
        /// .minecraft\versions\1.20.1-Forge-47.4.23\
        ///     mods\
        ///     config\
        ///     saves\
        ///     logs\
        ///     ...
        ///
        /// 注意：这里不再创建额外的 .minecraft 子目录。
        /// libraries 和 assets 仍然位于 .minecraft 根目录并共享。
        /// </summary>
        public static string GetInstancePath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            if (string.IsNullOrWhiteSpace(minecraftVersion))
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(minecraftVersion));

            if (string.IsNullOrWhiteSpace(loader))
                loader = "Vanilla";

            if (string.IsNullOrWhiteSpace(loaderVersion))
                loaderVersion = "release";

            string baseVersion =
                GetMinecraftBaseVersion(minecraftVersion);

            string instanceName =
                loader.Equals(
                    "Vanilla",
                    StringComparison.OrdinalIgnoreCase)
                    ? $"{baseVersion}-Vanilla"
                    : $"{baseVersion}-{loader}-{loaderVersion}";

            instanceName =
                MakeSafeFileName(instanceName);

            // 目标就是：.minecraft\versions\实例名\
            string instancePath =
                Path.Combine(
                    VersionsPath,
                    instanceName);

            CreateInstanceFolders(instancePath);

            return instancePath;
        }

        /// <summary>
        /// 获取 mods 目录。
        /// </summary>
        public static string GetModsPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "mods");
        }

        /// <summary>
        /// 获取 config 目录。
        /// </summary>
        public static string GetConfigPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "config");
        }

        /// <summary>
        /// 获取 saves 目录。
        /// </summary>
        public static string GetSavesPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "saves");
        }

        /// <summary>
        /// 获取 resourcepacks 目录。
        /// </summary>
        public static string GetResourcePacksPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "resourcepacks");
        }

        /// <summary>
        /// 获取 shaderpacks 目录。
        /// </summary>
        public static string GetShaderPacksPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "shaderpacks");
        }

        /// <summary>
        /// 获取 screenshots 目录。
        /// </summary>
        public static string GetScreenshotsPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "screenshots");
        }

        /// <summary>
        /// 获取 logs 目录。
        /// </summary>
        public static string GetLogsPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "logs");
        }

        /// <summary>
        /// 获取 crash-reports 目录。
        /// </summary>
        public static string GetCrashReportsPath(
            string minecraftVersion,
            string loader,
            string loaderVersion)
        {
            return Path.Combine(
                GetInstancePath(
                    minecraftVersion,
                    loader,
                    loaderVersion),
                "crash-reports");
        }

        /// <summary>
        /// 创建实例目录。
        /// </summary>
        private static void CreateInstanceFolders(
            string instancePath)
        {
            Directory.CreateDirectory(instancePath);

            string[] folders =
            {
                "mods",
                "config",
                "saves",
                "resourcepacks",
                "shaderpacks",
                "screenshots",
                "logs",
                "crash-reports"
            };

            foreach (string folder in folders)
            {
                Directory.CreateDirectory(
                    Path.Combine(
                        instancePath,
                        folder));
            }
        }

        /// <summary>
        /// 从版本 ID 获取 Minecraft 基础版本。
        /// </summary>
        private static string GetMinecraftBaseVersion(
            string version)
        {
            string value =
                version.Trim();

            string[] markers =
            {
                "-neoforge-",
                "-forge-",
                "-fabric-",
                "-fabric-loader-"
            };

            foreach (string marker in markers)
            {
                int index =
                    value.IndexOf(
                        marker,
                        StringComparison.OrdinalIgnoreCase);

                if (index > 0)
                {
                    return value.Substring(
                        0,
                        index);
                }
            }

            // fabric-loader-0.16.9-1.20.1
            if (value.StartsWith(
                "fabric-loader-",
                StringComparison.OrdinalIgnoreCase))
            {
                string tail =
                    value.Substring(
                        "fabric-loader-".Length);

                int last =
                    tail.LastIndexOf('-');

                if (last > 0)
                {
                    string possibleMinecraft =
                        tail.Substring(
                            last + 1);

                    if (possibleMinecraft.StartsWith(
                        "1.",
                        StringComparison.Ordinal))
                    {
                        return possibleMinecraft;
                    }
                }
            }

            return value;
        }

        /// <summary>
        /// 将字符串转换为安全的文件夹名称。
        /// </summary>
        private static string MakeSafeFileName(
            string value)
        {
            foreach (char c in
                Path.GetInvalidFileNameChars())
            {
                value =
                    value.Replace(
                        c,
                        '_');
            }

            return value;
        }
    }
}