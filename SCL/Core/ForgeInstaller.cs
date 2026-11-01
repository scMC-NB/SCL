using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SCL.Core
{
    public class ForgeInstaller
    {
        private readonly string gamePath;
        private readonly HttpClient http;
        private readonly MinecraftDownloader downloader;

        public ForgeInstaller(string gamePath)
        {
            this.gamePath = gamePath;

            http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");

            downloader =
                new MinecraftDownloader(gamePath);
        }

        public async Task<string> InstallAsync(
            string minecraftVersion)
        {
            if (string.IsNullOrWhiteSpace(minecraftVersion))
            {
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(minecraftVersion));
            }

            // =====================================================
            // 1. 确保 Vanilla 存在
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
            // 2. 获取 Forge 版本
            // =====================================================

            string forgeVersion =
                await GetLatestForgeVersionAsync(
                    minecraftVersion);

            if (string.IsNullOrWhiteSpace(forgeVersion))
            {
                throw new Exception(
                    $"没有找到 Minecraft {minecraftVersion} 对应的 Forge。");
            }

            // =====================================================
            // 3. Forge Installer
            // =====================================================

            string fullVersion =
                minecraftVersion +
                "-" +
                forgeVersion;

            string installerFile =
                $"forge-{fullVersion}-installer.jar";

            string installerUrl =
                "https://maven.minecraftforge.net/" +
                "net/minecraftforge/forge/" +
                fullVersion +
                "/" +
                installerFile;

            string installerPath =
                Path.Combine(
                    gamePath,
                    "forge-installer",
                    installerFile);

            string? installerDirectory =
                Path.GetDirectoryName(
                    installerPath);

            if (!string.IsNullOrWhiteSpace(
                installerDirectory))
            {
                Directory.CreateDirectory(
                    installerDirectory);
            }

            if (!File.Exists(installerPath))
            {
                await DownloadAsync(
                    installerUrl,
                    installerPath);
            }

            // =====================================================
            // 4. Java
            // =====================================================

            int requiredJava =
                JavaFinder.GetRequiredJavaVersion(
                    vanillaJson,
                    minecraftVersion);

            string? java =
                JavaFinder.FindJava(
                    requiredJava);

            if (string.IsNullOrWhiteSpace(java))
            {
                throw new Exception(
                    $"Forge 安装需要 Java {requiredJava}，" +
                    "但是没有找到对应 Java。");
            }

            // =====================================================
            // 5. 执行 Forge Installer
            // =====================================================

            string result =
                await RunInstallerAsync(
                    java,
                    installerPath);

            return
                $"Forge {fullVersion}\r\n" +
                result;
        }

        private async Task<string>
            GetLatestForgeVersionAsync(
                string minecraftVersion)
        {
            // =====================================================
            // Forge Maven metadata
            // =====================================================

            string metadataUrl =
                "https://maven.minecraftforge.net/" +
                "net/minecraftforge/forge/maven-metadata.xml";

            using HttpResponseMessage response =
                await http.GetAsync(metadataUrl);

            response.EnsureSuccessStatusCode();

            string xml =
                await response.Content.ReadAsStringAsync();

            XDocument document =
                XDocument.Parse(xml);

            XElement? versions =
                document.Root?
                    .Element("versioning")?
                    .Element("versions");

            if (versions == null)
            {
                throw new Exception(
                    "Forge Maven metadata 格式错误。");
            }

            string prefix =
                minecraftVersion + "-";

            List<string> candidates =
                new List<string>();

            foreach (
                XElement element
                in versions.Elements("version"))
            {
                string value =
                    element.Value.Trim();

                if (value.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(value.Substring(
                        prefix.Length));
                }
            }

            if (candidates.Count == 0)
            {
                throw new Exception(
                    $"Forge Maven 中没有找到 {minecraftVersion}。");
            }

            candidates.Sort(
                CompareForgeVersion);

            return candidates[^1];
        }

        private int CompareForgeVersion(
            string x,
            string y)
        {
            string[] xParts =
                x.Split(
                    '.',
                    '-');

            string[] yParts =
                y.Split(
                    '.',
                    '-');

            int count =
                Math.Max(
                    xParts.Length,
                    yParts.Length);

            for (int i = 0; i < count; i++)
            {
                int a =
                    i < xParts.Length
                        ? ParsePart(
                            xParts[i])
                        : 0;

                int b =
                    i < yParts.Length
                        ? ParsePart(
                            yParts[i])
                        : 0;

                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            return 0;
        }

        private int ParsePart(
            string value)
        {
            string digits = "";

            foreach (char c in value)
            {
                if (!char.IsDigit(c))
                {
                    break;
                }

                digits += c;
            }

            if (int.TryParse(
                digits,
                out int number))
            {
                return number;
            }

            return 0;
        }

        private async Task DownloadAsync(
            string url,
            string savePath)
        {
            string? directory =
                Path.GetDirectoryName(
                    savePath);

            if (!string.IsNullOrWhiteSpace(
                directory))
            {
                Directory.CreateDirectory(
                    directory);
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
                    savePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    true);

            await input.CopyToAsync(output);
        }

        private async Task<string>
            RunInstallerAsync(
                string java,
                string installerPath)
        {
            ProcessStartInfo psi =
                new ProcessStartInfo
                {
                    FileName = java,

                    Arguments =
                        "-jar " +
                        Quote(installerPath) +
                        " --installClient " +
                        Quote(gamePath),

                    WorkingDirectory =
                        gamePath,

                    UseShellExecute = false,

                    RedirectStandardOutput = true,

                    RedirectStandardError = true,

                    CreateNoWindow = true
                };

            using Process process =
                new Process();

            process.StartInfo =
                psi;

            if (!process.Start())
            {
                throw new Exception(
                    "无法启动 Forge Installer。");
            }

            Task<string> stdout =
                process.StandardOutput.ReadToEndAsync();

            Task<string> stderr =
                process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string output =
                await stdout;

            string error =
                await stderr;

            if (process.ExitCode != 0)
            {
                throw new Exception(
                    "Forge Installer 安装失败。\n\n" +
                    output +
                    "\n" +
                    error);
            }

            return output + "\n" + error;
        }

        private static string Quote(
            string value)
        {
            return "\"" +
                   value.Replace(
                       "\"",
                       "\\\"") +
                   "\"";
        }
    }
}