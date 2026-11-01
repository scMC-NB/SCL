using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class ModManager
    {
        private readonly string gamePath;

        private readonly ModrinthClient modrinth;


        public ModManager(
            string gamePath)
        {
            this.gamePath =
                gamePath;

            modrinth =
                new ModrinthClient();
        }


        // =========================================================
        // 搜索
        // =========================================================

        public async Task<List<ModSearchResult>>
            SearchAsync(
                string query,
                string minecraftVersion,
                string loader)
        {
            return await modrinth.SearchModsAsync(
                query,
                minecraftVersion,
                loader);
        }


        // =========================================================
        // 安装
        // =========================================================

        public async Task InstallAsync(
            ModSearchResult mod,
            string minecraftVersion,
            string loader)
        {
            if (mod == null)
            {
                throw new ArgumentNullException(
                    nameof(mod));
            }

            if (string.IsNullOrWhiteSpace(
                minecraftVersion))
            {
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(minecraftVersion));
            }

            if (string.IsNullOrWhiteSpace(
                loader))
            {
                throw new ArgumentException(
                    "Loader 不能为空。",
                    nameof(loader));
            }

            ModrinthVersion? version =
                await modrinth.GetBestVersionAsync(
                    mod.ProjectId,
                    minecraftVersion,
                    loader);

            if (version == null)
            {
                throw new Exception(
                    "没有找到适合当前环境的模组版本。\n\n" +
                    $"模组：{mod.Name}\n" +
                    $"Minecraft：{minecraftVersion}\n" +
                    $"Loader：{loader}");
            }

            // =====================================================
            // 当前目录
            //
            // 先按照你现在启动器的结构：
            // .minecraft\mods
            //
            // 后续我们再做实例隔离。
            // =====================================================

            string modsDirectory =
                Path.Combine(
                    gamePath,
                    "mods");

            Directory.CreateDirectory(
                modsDirectory);


            // =====================================================
            // 安装主模组
            // =====================================================

            await modrinth.InstallModAsync(
                version,
                modsDirectory);


            // =====================================================
            // 安装 Required 依赖
            // =====================================================

            await modrinth
                .InstallRequiredDependenciesAsync(
                    version,
                    minecraftVersion,
                    loader,
                    modsDirectory);
        }
    }
}