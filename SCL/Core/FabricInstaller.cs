using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class FabricInstaller
    {
        private readonly string gamePath;
        private readonly HttpClient http;
        private readonly MinecraftDownloader downloader;

        public FabricInstaller(string gamePath)
        {
            this.gamePath = gamePath;

            http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");

            downloader =
                new MinecraftDownloader(gamePath);
        }

        /// <summary>
        /// 安装指定 Minecraft 版本的最新 Fabric Loader。
        /// </summary>
        public async Task<string> InstallAsync(
            string minecraftVersion)
        {
            if (string.IsNullOrWhiteSpace(
                minecraftVersion))
            {
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(minecraftVersion));
            }

            // =====================================================
            // 1. 获取 Fabric Loader 列表
            // =====================================================

            string loaderUrl =
                "https://meta.fabricmc.net/v2/versions/loader/" +
                Uri.EscapeDataString(minecraftVersion);

            string loaderJson =
                await http.GetStringAsync(loaderUrl);

            using JsonDocument loaderDoc =
                JsonDocument.Parse(loaderJson);

            if (loaderDoc.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                throw new Exception(
                    "Fabric 返回的数据格式错误。");
            }

            if (loaderDoc.RootElement.GetArrayLength() == 0)
            {
                throw new Exception(
                    $"Fabric 不支持 Minecraft {minecraftVersion}。");
            }

            // 第一项通常就是最新可用 Loader
            JsonElement first =
                loaderDoc.RootElement[0];

            string loaderVersion =
                first.GetProperty("loader")
                    .GetProperty("version")
                    .GetString()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(
                loaderVersion))
            {
                throw new Exception(
                    "无法获取 Fabric Loader 版本。");
            }

            // =====================================================
            // 2. 获取 Fabric profile JSON
            // =====================================================

            string profileUrl =
                "https://meta.fabricmc.net/v2/versions/loader/" +
                Uri.EscapeDataString(minecraftVersion) +
                "/" +
                Uri.EscapeDataString(loaderVersion) +
                "/profile/json";

            string profileJson =
                await http.GetStringAsync(profileUrl);

            using JsonDocument profileDoc =
                JsonDocument.Parse(profileJson);

            JsonElement profileRoot =
                profileDoc.RootElement;

            // =====================================================
            // 3. Fabric 版本 ID
            // =====================================================

            string versionId =
                $"fabric-loader-{loaderVersion}-{minecraftVersion}";

            string versionDir =
                Path.Combine(
                    gamePath,
                    "versions",
                    versionId);

            Directory.CreateDirectory(
                versionDir);

            string versionJsonPath =
                Path.Combine(
                    versionDir,
                    versionId + ".json");

            await File.WriteAllTextAsync(
                versionJsonPath,
                profileJson);

            // =====================================================
            // 4. 确保 Vanilla client.jar 存在
            // =====================================================

            string vanillaJson =
                Path.Combine(
                    gamePath,
                    "versions",
                    minecraftVersion,
                    minecraftVersion + ".json");

            string vanillaJar =
                Path.Combine(
                    gamePath,
                    "versions",
                    minecraftVersion,
                    minecraftVersion + ".jar");

            if (!File.Exists(vanillaJson) ||
                !File.Exists(vanillaJar))
            {
                await downloader.Install(
                    minecraftVersion);
            }

            // =====================================================
            // 5. 下载 Fabric libraries
            // =====================================================

            await DownloadFabricLibraries(
                profileRoot);

            return versionId;
        }

        private async Task DownloadFabricLibraries(
            JsonElement profileRoot)
        {
            if (!profileRoot.TryGetProperty(
                "libraries",
                out JsonElement libraries))
            {
                return;
            }

            string librariesRoot =
                Path.Combine(
                    gamePath,
                    "libraries");

            Directory.CreateDirectory(
                librariesRoot);

            foreach (
                JsonElement library
                in libraries.EnumerateArray())
            {
                // =================================================
                // 新格式
                // =================================================

                if (library.TryGetProperty(
                    "url",
                    out JsonElement urlElement) &&
                    library.TryGetProperty(
                        "name",
                        out JsonElement nameElement))
                {
                    string baseUrl =
                        urlElement.GetString()
                        ?? string.Empty;

                    string name =
                        nameElement.GetString()
                        ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(
                        name))
                    {
                        string[] parts =
                            name.Split(':');

                        if (parts.Length >= 3)
                        {
                            string group =
                                parts[0].Replace(
                                    '.',
                                    '/');

                            string artifact =
                                parts[1];

                            string version =
                                parts[2];

                            string relative =
                                $"{group}/{artifact}/{version}/" +
                                $"{artifact}-{version}.jar";

                            if (!baseUrl.EndsWith("/"))
                            {
                                baseUrl += "/";
                            }

                            string downloadUrl =
                                baseUrl +
                                relative;

                            string savePath =
                                Path.Combine(
                                    librariesRoot,
                                    relative);

                            if (!File.Exists(savePath))
                            {
                                await DownloadFileAsync(
                                    downloadUrl,
                                    savePath);
                            }
                        }
                    }
                }
            }
        }

        private async Task DownloadFileAsync(
            string url,
            string path)
        {
            string? dir =
                Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (File.Exists(path))
            {
                return;
            }

            using HttpResponseMessage response =
                await http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead);

            response.EnsureSuccessStatusCode();

            await using Stream input =
                await response.Content.ReadAsStreamAsync();

            await using FileStream output =
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);

            await input.CopyToAsync(output);
        }
    }
}