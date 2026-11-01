using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class MinecraftDownloader
    {
        private readonly string gamePath;
        private readonly HttpClient http;
        private readonly FileDownloader downloader;

        public MinecraftDownloader(string path)
        {
            gamePath = path;

            http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");

            downloader = new FileDownloader();
        }

        // =========================================================
        // 公共入口
        // =========================================================

        public async Task Install(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(version));
            }

            string versionDir = Path.Combine(
                gamePath,
                "versions",
                version);

            Directory.CreateDirectory(versionDir);

            WriteLog($"========== 开始安装 {version} ==========");

            // 1. 获取 version.json
            string json = await DownloadVersionJson(version);

            string jsonPath = Path.Combine(
                versionDir,
                version + ".json");

            await File.WriteAllTextAsync(
                jsonPath,
                json);

            using JsonDocument doc =
                JsonDocument.Parse(json);

            JsonElement root = doc.RootElement;

            // 2. Client JAR
            WriteLog("[1/4] 下载客户端 JAR...");
            await DownloadClientJar(version, root);

            // 3. Libraries + Natives
            WriteLog("[2/4] 下载 Libraries / Natives...");
            await DownloadLibrariesAndNatives(root, version);

            // 4. Assets
            WriteLog("[3/4] 下载 Assets...");
            await DownloadAssets(root);

            // 5. Logging
            WriteLog("[4/4] 下载 Logging...");
            await DownloadLogging(root);

            WriteLog($"========== {version} 安装完成 ==========");
        }

        // =========================================================
        // 获取 version.json
        // =========================================================

        private async Task<string> DownloadVersionJson(string version)
        {
            string manifestUrl =
                "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

            string manifest =
                await http.GetStringAsync(manifestUrl);

            using JsonDocument doc =
                JsonDocument.Parse(manifest);

            foreach (JsonElement item in
                doc.RootElement
                    .GetProperty("versions")
                    .EnumerateArray())
            {
                string id =
                    item.GetProperty("id").GetString()
                    ?? string.Empty;

                if (!string.Equals(
                    id,
                    version,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string url =
                    item.GetProperty("url").GetString()
                    ?? string.Empty;

                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new Exception(
                        $"版本 {version} 没有可用的 JSON 地址。");
                }

                return await http.GetStringAsync(url);
            }

            throw new Exception(
                $"Mojang 官方版本清单中不存在版本：{version}");
        }

        // =========================================================
        // 下载 Client JAR
        // =========================================================

        private async Task DownloadClientJar(
            string version,
            JsonElement root)
        {
            if (!root.TryGetProperty(
                "downloads",
                out JsonElement downloads))
            {
                throw new Exception(
                    $"版本 {version} 没有 downloads 数据。");
            }

            if (!downloads.TryGetProperty(
                "client",
                out JsonElement client))
            {
                throw new Exception(
                    $"版本 {version} 没有 client 下载信息。");
            }

            string url =
                client.GetProperty("url").GetString()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new Exception(
                    $"版本 {version} 的 client 下载地址为空。");
            }

            string savePath = Path.Combine(
                gamePath,
                "versions",
                version,
                version + ".jar");

            string? expectedSha1 = null;

            if (client.TryGetProperty(
                "sha1",
                out JsonElement sha1Element))
            {
                expectedSha1 = sha1Element.GetString();
            }

            if (File.Exists(savePath)
                && !string.IsNullOrWhiteSpace(expectedSha1))
            {
                string actual =
                    await ComputeSha1Async(savePath);

                if (string.Equals(
                    actual,
                    expectedSha1,
                    StringComparison.OrdinalIgnoreCase))
                {
                    WriteLog("客户端 JAR 校验通过，跳过下载。");
                    return;
                }

                WriteLog(
                    "客户端 JAR 校验失败，重新下载。");

                TryDelete(savePath);
            }
            else if (File.Exists(savePath))
            {
                WriteLog("客户端 JAR 已存在，跳过下载。");
                return;
            }

            await downloader.DownloadAsync(
                url,
                savePath);
        }

        // =========================================================
        // Libraries + Natives
        // =========================================================

        private async Task DownloadLibrariesAndNatives(
            JsonElement root,
            string version)
        {
            string librariesRoot =
                Path.Combine(
                    gamePath,
                    "libraries");

            Directory.CreateDirectory(librariesRoot);

            string nativesDir =
                Path.Combine(
                    gamePath,
                    "versions",
                    version,
                    version + "-natives");

            if (Directory.Exists(nativesDir))
            {
                TryDeleteDirectory(nativesDir);
            }

            Directory.CreateDirectory(nativesDir);

            if (!root.TryGetProperty(
                "libraries",
                out JsonElement libraries))
            {
                WriteLog("该版本没有 libraries。");
                return;
            }

            string currentOs = "windows";
            string currentArch = GetWindowsArchitecture();

            foreach (JsonElement lib in
                libraries.EnumerateArray())
            {
                if (!IsLibraryAllowed(
                    lib,
                    currentOs,
                    currentArch))
                {
                    continue;
                }

                await DownloadNormalArtifact(
                    lib,
                    librariesRoot);

                await DownloadAndExtractNatives(
                    lib,
                    librariesRoot,
                    nativesDir,
                    currentOs,
                    currentArch);
            }
        }

        // =========================================================
        // 普通 Library
        // =========================================================

        private async Task DownloadNormalArtifact(
            JsonElement lib,
            string librariesRoot)
        {
            if (!lib.TryGetProperty(
                "downloads",
                out JsonElement downloads))
            {
                // 老格式可能只有 name + url
                await DownloadLegacyMavenLibrary(
                    lib,
                    librariesRoot);

                return;
            }

            if (!downloads.TryGetProperty(
                "artifact",
                out JsonElement artifact))
            {
                // 没有普通 artifact，可能只有 natives
                return;
            }

            string relativePath =
                artifact.GetProperty("path")
                    .GetString()
                ?? string.Empty;

            string url =
                artifact.GetProperty("url")
                    .GetString()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(
                relativePath)
                ||
                string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            string savePath =
                Path.Combine(
                    librariesRoot,
                    relativePath);

            if (File.Exists(savePath))
            {
                return;
            }

            try
            {
                await downloader.DownloadAsync(
                    url,
                    savePath);
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"⚠️ Library 下载失败，已跳过：{relativePath}\n{ex.Message}");
            }
        }

        // =========================================================
        // 老版本 Maven Library
        // =========================================================

        private async Task DownloadLegacyMavenLibrary(
            JsonElement lib,
            string librariesRoot)
        {
            if (!lib.TryGetProperty(
                "name",
                out JsonElement nameElement))
            {
                return;
            }

            string name =
                nameElement.GetString()
                ?? string.Empty;

            string[] parts =
                name.Split(':');

            if (parts.Length < 3)
            {
                return;
            }

            string group =
                parts[0].Replace('.', '/');

            string artifact =
                parts[1];

            string version =
                parts[2];

            string relativePath =
                $"{group}/{artifact}/{version}/" +
                $"{artifact}-{version}.jar";

            string baseUrl =
                "https://libraries.minecraft.net/";

            if (lib.TryGetProperty(
                "url",
                out JsonElement urlElement))
            {
                string? custom =
                    urlElement.GetString();

                if (!string.IsNullOrWhiteSpace(custom))
                {
                    baseUrl = custom;

                    if (!baseUrl.EndsWith("/"))
                    {
                        baseUrl += "/";
                    }
                }
            }

            string url =
                baseUrl +
                relativePath;

            string savePath =
                Path.Combine(
                    librariesRoot,
                    relativePath);

            if (File.Exists(savePath))
            {
                return;
            }

            try
            {
                await downloader.DownloadAsync(
                    url,
                    savePath);
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"⚠️ 老版本 Library 下载失败，已跳过：{name}\n{ex.Message}");
            }
        }

        // =========================================================
        // Natives
        // =========================================================

        private async Task DownloadAndExtractNatives(
            JsonElement lib,
            string librariesRoot,
            string nativesDir,
            string currentOs,
            string currentArch)
        {
            if (!lib.TryGetProperty(
                "downloads",
                out JsonElement downloads))
            {
                return;
            }

            // 新格式：downloads.classifiers
            if (downloads.TryGetProperty(
                "classifiers",
                out JsonElement classifiers))
            {
                string? classifier =
                    GetNativeClassifier(
                        lib,
                        currentOs,
                        currentArch);

                if (!string.IsNullOrWhiteSpace(
                    classifier)
                    &&
                    classifiers.TryGetProperty(
                        classifier,
                        out JsonElement nativeArtifact))
                {
                    await DownloadOneNative(
                        nativeArtifact,
                        librariesRoot,
                        nativesDir);
                }
            }

            // 某些老版本直接把 natives 当 artifact
            if (lib.TryGetProperty(
                "name",
                out JsonElement nameElement))
            {
                string name =
                    nameElement.GetString()
                    ?? string.Empty;

                if (name.Contains(
                    "natives-windows",
                    StringComparison.OrdinalIgnoreCase)
                    &&
                    downloads.TryGetProperty(
                        "artifact",
                        out JsonElement artifact))
                {
                    await DownloadOneNative(
                        artifact,
                        librariesRoot,
                        nativesDir);
                }
            }
        }

        private async Task DownloadOneNative(
            JsonElement artifact,
            string librariesRoot,
            string nativesDir)
        {
            string relativePath =
                artifact.GetProperty("path")
                    .GetString()
                ?? string.Empty;

            string url =
                artifact.GetProperty("url")
                    .GetString()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(
                relativePath)
                ||
                string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            string jarPath =
                Path.Combine(
                    librariesRoot,
                    relativePath);

            try
            {
                if (!File.Exists(jarPath))
                {
                    await downloader.DownloadAsync(
                        url,
                        jarPath);
                }

                ExtractNativeJar(
                    jarPath,
                    nativesDir);
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"⚠️ Natives 下载/解压失败：{relativePath}\n{ex.Message}");
            }
        }

        private void ExtractNativeJar(
            string jarPath,
            string outputDir)
        {
            if (!File.Exists(jarPath))
            {
                return;
            }

            using FileStream stream =
                new FileStream(
                    jarPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            using System.IO.Compression.ZipArchive archive =
                new System.IO.Compression.ZipArchive(
                    stream,
                    System.IO.Compression.ZipArchiveMode.Read);

            foreach (System.IO.Compression.ZipArchiveEntry entry
                in archive.Entries)
            {
                string name =
                    entry.Name;

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!name.EndsWith(
                        ".dll",
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    !name.EndsWith(
                        ".so",
                        StringComparison.OrdinalIgnoreCase)
                    &&
                    !name.EndsWith(
                        ".dylib",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string target =
                    Path.Combine(
                        outputDir,
                        name);

                using Stream input =
                    entry.Open();

                using FileStream output =
                    new FileStream(
                        target,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);

                input.CopyTo(output);
            }
        }

        // =========================================================
        // Asset
        // =========================================================

        private async Task DownloadAssets(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                "assetIndex",
                out JsonElement assetIndex))
            {
                WriteLog(
                    "⚠️ 没有 assetIndex，跳过 Assets。");

                return;
            }

            string indexId =
                assetIndex.GetProperty("id")
                    .GetString()
                ?? string.Empty;

            string indexUrl =
                assetIndex.GetProperty("url")
                    .GetString()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(
                indexId)
                ||
                string.IsNullOrWhiteSpace(
                indexUrl))
            {
                return;
            }

            string assetsDir =
                Path.Combine(
                    gamePath,
                    "assets");

            string indexesDir =
                Path.Combine(
                    assetsDir,
                    "indexes");

            string objectsDir =
                Path.Combine(
                    assetsDir,
                    "objects");

            Directory.CreateDirectory(
                indexesDir);

            Directory.CreateDirectory(
                objectsDir);

            string indexPath =
                Path.Combine(
                    indexesDir,
                    indexId + ".json");

            if (!File.Exists(indexPath))
            {
                await downloader.DownloadAsync(
                    indexUrl,
                    indexPath);
            }

            string indexJson =
                await File.ReadAllTextAsync(
                    indexPath);

            using JsonDocument indexDoc =
                JsonDocument.Parse(indexJson);

            if (!indexDoc.RootElement.TryGetProperty(
                "objects",
                out JsonElement objects))
            {
                return;
            }

            int total =
                objects.EnumerateObject().Count();

            int current = 0;

            foreach (JsonProperty obj
                in objects.EnumerateObject())
            {
                current++;

                string hash =
                    obj.Value.GetProperty("hash")
                        .GetString()
                    ?? string.Empty;

                if (hash.Length < 2)
                {
                    continue;
                }

                string prefix =
                    hash.Substring(0, 2);

                string url =
                    $"https://resources.download.minecraft.net/{prefix}/{hash}";

                string objectPath =
                    Path.Combine(
                        objectsDir,
                        prefix,
                        hash);

                try
                {
                    if (!File.Exists(objectPath))
                    {
                        await downloader.DownloadAsync(
                            url,
                            objectPath);
                    }

                    // 给 legacy/旧版资源格式复制一份
                    string legacyPath =
                        Path.Combine(
                            assetsDir,
                            obj.Name.Replace(
                                '/',
                                Path.DirectorySeparatorChar));

                    string? legacyDir =
                        Path.GetDirectoryName(
                            legacyPath);

                    if (!string.IsNullOrWhiteSpace(
                        legacyDir))
                    {
                        Directory.CreateDirectory(
                            legacyDir);
                    }

                    if (!File.Exists(legacyPath))
                    {
                        File.Copy(
                            objectPath,
                            legacyPath);
                    }
                }
                catch (Exception ex)
                {
                    WriteLog(
                        $"⚠️ Asset 失败 {obj.Name}: {ex.Message}");
                }

                if (current % 50 == 0
                    || current == total)
                {
                    WriteLog(
                        $"Assets: {current}/{total}");
                }
            }
        }

        // =========================================================
        // Logging
        // =========================================================

        private async Task DownloadLogging(
            JsonElement root)
        {
            if (!root.TryGetProperty(
                "logging",
                out JsonElement logging))
            {
                return;
            }

            if (!logging.TryGetProperty(
                "client",
                out JsonElement client))
            {
                return;
            }

            if (!client.TryGetProperty(
                "file",
                out JsonElement file))
            {
                return;
            }

            string url =
                file.GetProperty("url")
                    .GetString()
                ?? string.Empty;

            string id =
                file.GetProperty("id")
                    .GetString()
                ?? "client-logging.xml";

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            string path =
                Path.Combine(
                    gamePath,
                    "assets",
                    "log_configs",
                    id);

            if (File.Exists(path))
            {
                return;
            }

            try
            {
                await downloader.DownloadAsync(
                    url,
                    path);
            }
            catch (Exception ex)
            {
                WriteLog(
                    $"⚠️ Logging 下载失败：{ex.Message}");
            }
        }

        // =========================================================
        // Library rules
        // =========================================================

        private bool IsLibraryAllowed(
            JsonElement lib,
            string currentOs,
            string currentArch)
        {
            if (!lib.TryGetProperty(
                "rules",
                out JsonElement rules))
            {
                return true;
            }

            bool allowed = false;

            foreach (JsonElement rule
                in rules.EnumerateArray())
            {
                string action =
                    rule.GetProperty("action")
                        .GetString()
                    ?? string.Empty;

                bool matches = true;

                if (rule.TryGetProperty(
                    "os",
                    out JsonElement os))
                {
                    if (os.TryGetProperty(
                        "name",
                        out JsonElement name))
                    {
                        string osName =
                            name.GetString()
                            ?? string.Empty;

                        if (!string.Equals(
                            osName,
                            currentOs,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            matches = false;
                        }
                    }

                    if (os.TryGetProperty(
                        "arch",
                        out JsonElement arch))
                    {
                        string ruleArch =
                            NormalizeArch(
                                arch.GetString());

                        if (!string.Equals(
                            ruleArch,
                            currentArch,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            matches = false;
                        }
                    }
                }

                if (matches)
                {
                    allowed =
                        string.Equals(
                            action,
                            "allow",
                            StringComparison.OrdinalIgnoreCase);
                }
            }

            return allowed;
        }

        // =========================================================
        // Native classifier
        // =========================================================

        private string? GetNativeClassifier(
            JsonElement lib,
            string currentOs,
            string currentArch)
        {
            if (!lib.TryGetProperty(
                "natives",
                out JsonElement natives))
            {
                return null;
            }

            if (!natives.TryGetProperty(
                currentOs,
                out JsonElement template))
            {
                return null;
            }

            string value =
                template.GetString()
                ?? string.Empty;

            if (value.Contains(
                "${arch}",
                StringComparison.OrdinalIgnoreCase))
            {
                string arch =
                    currentArch == "x64"
                        ? "64"
                        : "32";

                value =
                    value.Replace(
                        "${arch}",
                        arch,
                        StringComparison.OrdinalIgnoreCase);
            }

            return value;
        }

        // =========================================================
        // 系统架构
        // =========================================================

        private string GetWindowsArchitecture()
        {
            return Environment.Is64BitOperatingSystem
                ? "x64"
                : "x86";
        }

        private string NormalizeArch(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string valueLower =
                value.ToLowerInvariant();

            return valueLower switch
            {
                "amd64" => "x64",
                "x86_64" => "x64",
                "x86" => "x86",
                "ia32" => "x86",
                "arm64" => "arm64",
                "aarch64" => "arm64",
                _ => valueLower
            };
        }

        // =========================================================
        // SHA1
        // =========================================================

        private async Task<string> ComputeSha1Async(
            string filePath)
        {
            await using FileStream stream =
                new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            using SHA1 sha1 =
                SHA1.Create();

            byte[] hash =
                await sha1.ComputeHashAsync(
                    stream);

            return Convert.ToHexString(
                hash).ToLowerInvariant();
        }

        // =========================================================
        // 文件操作
        // =========================================================

        private void TryDelete(
            string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 忽略清理失败
            }
        }

        private void TryDeleteDirectory(
            string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(
                        path,
                        recursive: true);
                }
            }
            catch
            {
                // 忽略清理失败
            }
        }

        private void WriteLog(
            string message)
        {
            Debug.WriteLine(message);
        }
    }
}