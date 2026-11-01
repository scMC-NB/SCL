using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class ModrinthClient
    {
        private readonly HttpClient http;

        private readonly TranslationClient translator;


        public ModrinthClient()
        {
            http =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromMinutes(10)
                };

            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");

            translator =
                new TranslationClient();
        }


        // =========================================================
        // 搜索模组
        // =========================================================

        public async Task<List<ModSearchResult>>
            SearchModsAsync(
                string query,
                string minecraftVersion,
                string loader)
        {
            if (string.IsNullOrWhiteSpace(
                minecraftVersion))
            {
                throw new ArgumentException(
                    "Minecraft 版本不能为空。",
                    nameof(minecraftVersion));
            }

            if (string.IsNullOrWhiteSpace(
                query))
            {
                throw new ArgumentException(
                    "搜索内容不能为空。",
                    nameof(query));
            }

            string normalizedLoader =
                NormalizeLoader(loader);

            // =====================================================
            // 构建搜索词
            // =====================================================

            List<string> searchQueries =
                await BuildSearchQueriesAsync(query);

            // 去重
            searchQueries =
                searchQueries
                    .Where(
                        x =>
                            !string.IsNullOrWhiteSpace(x))
                    .Select(
                        x => x.Trim())
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .ToList();

            // =====================================================
            // 依次搜索
            // =====================================================

            foreach (
                string searchQuery
                in searchQueries)
            {
                List<ModSearchResult> results =
                    await SearchInternalAsync(
                        searchQuery,
                        minecraftVersion,
                        normalizedLoader);

                if (results.Count > 0)
                {
                    return results;
                }
            }

            return new List<ModSearchResult>();
        }


        // =========================================================
        // 构建搜索词
        //
        // 例如：
        //
        // 苹果皮
        //
        // → AppleSkin
        // → Apple Skin
        // → API 翻译
        // → 苹果皮
        // =========================================================

        private async Task<List<string>>
            BuildSearchQueriesAsync(
                string query)
        {
            List<string> queries =
                new List<string>();

            string text =
                query.Trim();

            // 原始输入最后再搜索
            // 先不放进去


            // =====================================================
            // 1. 本地常用模组别名
            // =====================================================

            string[] aliases =
                GetKnownAliases(text);

            foreach (string alias in aliases)
            {
                queries.Add(alias);
            }


            // =====================================================
            // 2. 如果是中文，调用翻译 API
            // =====================================================

            if (TranslationClient.ContainsChinese(text))
            {
                string? translated =
                    await translator
                        .TranslateChineseToEnglishAsync(
                            text);

                if (!string.IsNullOrWhiteSpace(
                    translated))
                {
                    queries.Add(translated);
                }
            }


            // =====================================================
            // 3. 最后使用原始输入
            // =====================================================

            queries.Add(text);

            return queries;
        }


        // =========================================================
        // 常用中文模组名称
        //
        // 这个优先级高于机器翻译。
        // 因为：
        //
        // 苹果皮 → AppleSkin
        //
        // 机器翻译可能会得到 apple peel。
        // =========================================================

        private string[] GetKnownAliases(
            string query)
        {
            switch (query.Trim()
                .ToLowerInvariant())
            {
                case "苹果皮":
                    return new[]
                    {
                        "AppleSkin",
                        "Apple Skin"
                    };

                case "苹果":
                    return new[]
                    {
                        "AppleSkin"
                    };

                case "钠":
                    return new[]
                    {
                        "Sodium"
                    };

                case "钠优化":
                    return new[]
                    {
                        "Sodium"
                    };

                case "锂":
                    return new[]
                    {
                        "Lithium"
                    };

                case "锂优化":
                    return new[]
                    {
                        "Lithium"
                    };

                case "铁氧体":
                    return new[]
                    {
                        "FerriteCore"
                    };

                case "铁氧体核心":
                    return new[]
                    {
                        "FerriteCore"
                    };

                case "铟":
                    return new[]
                    {
                        "Indium"
                    };

                case "光影":
                    return new[]
                    {
                        "Iris",
                        "Iris Shaders"
                    };

                case "光影加载器":
                    return new[]
                    {
                        "Iris"
                    };

                case "jei":
                    return new[]
                    {
                        "Just Enough Items",
                        "JEI"
                    };

                case "rei":
                    return new[]
                    {
                        "Roughly Enough Items",
                        "REI"
                    };

                case "小地图":
                    return new[]
                    {
                        "Xaero's Minimap",
                        "Xaero Minimap"
                    };

                case "世界地图":
                    return new[]
                    {
                        "Xaero's World Map",
                        "Xaero World Map"
                    };

                case "实体渲染":
                    return new[]
                    {
                        "Entity Culling"
                    };

                case "实体裁剪":
                    return new[]
                    {
                        "Entity Culling"
                    };

                case "动态光源":
                    return new[]
                    {
                        "Dynamic Lights"
                    };

                case "fabric api":
                    return new[]
                    {
                        "Fabric API"
                    };

                case "fabric接口":
                    return new[]
                    {
                        "Fabric API"
                    };

                case "优化":
                    return new[]
                    {
                        "Sodium",
                        "Lithium",
                        "FerriteCore",
                        "ModernFix"
                    };

                case "内存优化":
                    return new[]
                    {
                        "FerriteCore",
                        "ModernFix"
                    };

                default:
                    return Array.Empty<string>();
            }
        }


        // =========================================================
        // 真正请求 Modrinth
        // =========================================================

        private async Task<List<ModSearchResult>>
            SearchInternalAsync(
                string query,
                string minecraftVersion,
                string loader)
        {
            List<string> facetGroups =
                new List<string>();


            // 只搜索 Mod
            facetGroups.Add(
                "[\"project_type:mod\"]");


            // 指定 Minecraft 版本
            facetGroups.Add(
                "[\"versions:" +
                EscapeFacetValue(
                    minecraftVersion) +
                "\"]");


            // 指定 Loader
            if (loader == "fabric" ||
                loader == "forge" ||
                loader == "neoforge")
            {
                facetGroups.Add(
                    "[\"categories:" +
                    loader +
                    "\"]");
            }


            string facets =
                "[" +
                string.Join(
                    ",",
                    facetGroups) +
                "]";


            string url =
                "https://api.modrinth.com/v2/search" +
                "?query=" +
                Uri.EscapeDataString(
                    query) +
                "&facets=" +
                Uri.EscapeDataString(
                    facets) +
                "&index=relevance" +
                "&limit=30";


            using HttpResponseMessage response =
                await http.GetAsync(url);


            response.EnsureSuccessStatusCode();


            string json =
                await response.Content
                    .ReadAsStringAsync();


            using JsonDocument doc =
                JsonDocument.Parse(json);


            List<ModSearchResult> results =
                new List<ModSearchResult>();


            if (!doc.RootElement.TryGetProperty(
                "hits",
                out JsonElement hits))
            {
                return results;
            }


            foreach (
                JsonElement hit
                in hits.EnumerateArray())
            {
                results.Add(
                    new ModSearchResult
                    {
                        ProjectId =
                            GetString(
                                hit,
                                "project_id"),

                        Name =
                            GetString(
                                hit,
                                "title"),

                        Description =
                            GetString(
                                hit,
                                "description"),

                        Author =
                            GetString(
                                hit,
                                "author"),

                        IconUrl =
                            GetString(
                                hit,
                                "icon_url"),

                        LatestVersion =
                            GetString(
                                hit,
                                "latest_version")
                    });
            }


            return results;
        }


        // =========================================================
        // 获取模组版本
        // =========================================================

        public async Task<List<ModrinthVersion>>
            GetVersionsAsync(
                string projectId,
                string minecraftVersion,
                string loader)
        {
            string normalizedLoader =
                NormalizeLoader(loader);


            string gameVersions =
                "[\"" +
                EscapeJson(
                    minecraftVersion) +
                "\"]";


            string loaders =
                "[\"" +
                EscapeJson(
                    normalizedLoader) +
                "\"]";


            string url =
                "https://api.modrinth.com/v2/project/" +
                Uri.EscapeDataString(
                    projectId) +
                "/version" +
                "?game_versions=" +
                Uri.EscapeDataString(
                    gameVersions) +
                "&loaders=" +
                Uri.EscapeDataString(
                    loaders) +
                "&include_changelog=false";


            using HttpResponseMessage response =
                await http.GetAsync(url);


            response.EnsureSuccessStatusCode();


            string json =
                await response.Content
                    .ReadAsStringAsync();


            using JsonDocument doc =
                JsonDocument.Parse(json);


            List<ModrinthVersion> versions =
                new List<ModrinthVersion>();


            if (doc.RootElement.ValueKind !=
                JsonValueKind.Array)
            {
                return versions;
            }


            foreach (
                JsonElement item
                in doc.RootElement
                    .EnumerateArray())
            {
                versions.Add(
                    ParseVersion(item));
            }


            return versions;
        }


        // =========================================================
        // 获取最佳版本
        // =========================================================

        public async Task<ModrinthVersion?>
            GetBestVersionAsync(
                string projectId,
                string minecraftVersion,
                string loader)
        {
            List<ModrinthVersion> versions =
                await GetVersionsAsync(
                    projectId,
                    minecraftVersion,
                    loader);


            // release 优先
            ModrinthVersion? release =
                versions.FirstOrDefault(
                    x =>
                        string.Equals(
                            x.VersionType,
                            "release",
                            StringComparison.OrdinalIgnoreCase)
                        &&
                        x.Files.Count > 0);


            if (release != null)
            {
                return release;
            }


            // 找一个能下载的版本
            return versions.FirstOrDefault(
                x =>
                    x.Files.Count > 0);
        }


        // =========================================================
        // 下载模组
        // =========================================================

        public async Task InstallModAsync(
            ModrinthVersion version,
            string modsDirectory)
        {
            if (version.Files.Count == 0)
            {
                throw new Exception(
                    "这个模组版本没有可下载文件。");
            }


            Directory.CreateDirectory(
                modsDirectory);


            ModrinthFile? file =
                version.Files.FirstOrDefault(
                    x =>
                        x.Primary
                        &&
                        x.Filename.EndsWith(
                            ".jar",
                            StringComparison.OrdinalIgnoreCase));


            file ??=
                version.Files.FirstOrDefault(
                    x =>
                        x.Filename.EndsWith(
                            ".jar",
                            StringComparison.OrdinalIgnoreCase));


            if (file == null)
            {
                throw new Exception(
                    "没有找到模组 JAR 文件。");
            }


            string savePath =
                Path.Combine(
                    modsDirectory,
                    file.Filename);


            await DownloadFileAsync(
                file.Url,
                savePath);
        }


        // =========================================================
        // Required 依赖
        // =========================================================

        public async Task
            InstallRequiredDependenciesAsync(
                ModrinthVersion version,
                string minecraftVersion,
                string loader,
                string modsDirectory)
        {
            foreach (
                ModrinthDependency dependency
                in version.Dependencies)
            {
                if (!string.Equals(
                    dependency.DependencyType,
                    "required",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }


                if (string.IsNullOrWhiteSpace(
                    dependency.ProjectId))
                {
                    continue;
                }


                ModrinthVersion? dependencyVersion =
                    await GetBestVersionAsync(
                        dependency.ProjectId,
                        minecraftVersion,
                        loader);


                if (dependencyVersion == null)
                {
                    continue;
                }


                await InstallModAsync(
                    dependencyVersion,
                    modsDirectory);
            }
        }


        // =========================================================
        // 文件下载
        // =========================================================

        private async Task DownloadFileAsync(
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
                await response.Content
                    .ReadAsStreamAsync();


            await using FileStream output =
                new FileStream(
                    savePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    true);


            await input.CopyToAsync(
                output);
        }


        // =========================================================
        // JSON 解析
        // =========================================================

        private ModrinthVersion ParseVersion(
            JsonElement item)
        {
            ModrinthVersion result =
                new ModrinthVersion
                {
                    Id =
                        GetString(
                            item,
                            "id"),

                    Name =
                        GetString(
                            item,
                            "name"),

                    VersionNumber =
                        GetString(
                            item,
                            "version_number"),

                    VersionType =
                        GetString(
                            item,
                            "version_type")
                };


            if (item.TryGetProperty(
                "loaders",
                out JsonElement loaders))
            {
                foreach (
                    JsonElement loader
                    in loaders.EnumerateArray())
                {
                    string? value =
                        loader.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        result.Loaders.Add(value);
                    }
                }
            }


            if (item.TryGetProperty(
                "game_versions",
                out JsonElement gameVersions))
            {
                foreach (
                    JsonElement gameVersion
                    in gameVersions.EnumerateArray())
                {
                    string? value =
                        gameVersion.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        result.GameVersions.Add(value);
                    }
                }
            }


            if (item.TryGetProperty(
                "files",
                out JsonElement files))
            {
                foreach (
                    JsonElement file
                    in files.EnumerateArray())
                {
                    result.Files.Add(
                        new ModrinthFile
                        {
                            Filename =
                                GetString(
                                    file,
                                    "filename"),

                            Url =
                                GetString(
                                    file,
                                    "url"),

                            Primary =
                                file.TryGetProperty(
                                    "primary",
                                    out JsonElement primary)
                                &&
                                primary.ValueKind ==
                                    JsonValueKind.True
                        });
                }
            }


            if (item.TryGetProperty(
                "dependencies",
                out JsonElement dependencies))
            {
                foreach (
                    JsonElement dependency
                    in dependencies.EnumerateArray())
                {
                    result.Dependencies.Add(
                        new ModrinthDependency
                        {
                            ProjectId =
                                dependency.TryGetProperty(
                                    "project_id",
                                    out JsonElement project)
                                    ? project.GetString()
                                    : null,

                            VersionId =
                                dependency.TryGetProperty(
                                    "version_id",
                                    out JsonElement version)
                                    ? version.GetString()
                                    : null,

                            DependencyType =
                                GetString(
                                    dependency,
                                    "dependency_type")
                        });
                }
            }


            return result;
        }


        // =========================================================
        // Loader
        // =========================================================

        private string NormalizeLoader(
            string loader)
        {
            if (loader.Contains(
                "Fabric",
                StringComparison.OrdinalIgnoreCase))
            {
                return "fabric";
            }


            if (loader.Contains(
                "NeoForge",
                StringComparison.OrdinalIgnoreCase))
            {
                return "neoforge";
            }


            if (loader.Contains(
                "Forge",
                StringComparison.OrdinalIgnoreCase))
            {
                return "forge";
            }


            return "minecraft";
        }


        // =========================================================
        // JSON / Facet
        // =========================================================

        private static string EscapeJson(
            string value)
        {
            return value
                .Replace(
                    "\\",
                    "\\\\")
                .Replace(
                    "\"",
                    "\\\"");
        }


        private static string EscapeFacetValue(
            string value)
        {
            return value
                .Replace(
                    "\\",
                    "\\\\")
                .Replace(
                    "\"",
                    "\\\"");
        }


        private static string GetString(
            JsonElement element,
            string property)
        {
            if (!element.TryGetProperty(
                property,
                out JsonElement value))
            {
                return string.Empty;
            }

            return value.GetString()
                   ?? string.Empty;
        }
    }


    // =============================================================
    // 搜索结果
    // =============================================================

    public class ModSearchResult
    {
        public string ProjectId { get; set; } = "";

        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        public string Author { get; set; } = "";

        public string IconUrl { get; set; } = "";

        public string LatestVersion { get; set; } = "";
    }


    // =============================================================
    // 模组版本
    // =============================================================

    public class ModrinthVersion
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public string VersionNumber { get; set; } = "";

        public string VersionType { get; set; } = "";

        public List<string> Loaders { get; set; } =
            new List<string>();

        public List<string> GameVersions { get; set; } =
            new List<string>();

        public List<ModrinthFile> Files { get; set; } =
            new List<ModrinthFile>();

        public List<ModrinthDependency> Dependencies { get; set; } =
            new List<ModrinthDependency>();
    }


    // =============================================================
    // 模组文件
    // =============================================================

    public class ModrinthFile
    {
        public string Filename { get; set; } = "";

        public string Url { get; set; } = "";

        public bool Primary { get; set; }
    }


    // =============================================================
    // 依赖
    // =============================================================

    public class ModrinthDependency
    {
        public string? ProjectId { get; set; }

        public string? VersionId { get; set; }

        public string DependencyType { get; set; } = "";
    }
}