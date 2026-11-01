using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SCL.Core
{
    public class MinecraftLauncher
    {
        private readonly string gamePath;
        private readonly string javaPath;
        private string launchGamePath;

        public MinecraftLauncher(
            string gamePath,
            string javaPath)
        {
            this.gamePath = gamePath;
            this.javaPath = javaPath;
            this.launchGamePath = gamePath;
        }

        // =========================================================
        // 主入口
        // =========================================================

        public void Launch(
            string version,
            string username)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new Exception("Minecraft 版本为空。");

            if (string.IsNullOrWhiteSpace(javaPath))
                throw new Exception("Java 路径为空。");

            if (!File.Exists(javaPath))
            {
                throw new Exception(
                    "找不到 Java：\n" +
                    javaPath);
            }

            // ⭐ 实例隔离只改变 Minecraft 的 --gameDir。
            // 版本 JSON、libraries、assets、natives 仍使用原来的共享 .minecraft。
            launchGamePath =
                ResolveInstanceGamePath(version);

            List<VersionInfo> chain =
                BuildVersionChain(version);

            if (chain.Count == 0)
            {
                throw new Exception(
                    "无法建立版本继承链。");
            }

            string? clientJar =
                FindClientJar(chain);

            if (string.IsNullOrWhiteSpace(clientJar))
            {
                throw new Exception(
                    "找不到 Minecraft Client JAR：\n" +
                    version);
            }

            string mainClass =
                FindMainClass(chain);

            string assetsDir =
                Path.Combine(
                    gamePath,
                    "assets");

            Directory.CreateDirectory(
                assetsDir);

            string assetIndex =
                FindAssetIndex(chain);

            string nativesDir =
                Path.Combine(
                    gamePath,
                    "versions",
                    version,
                    version + "-natives");

            Directory.CreateDirectory(
                nativesDir);

            ClearDirectoryFiles(
                nativesDir);

            foreach (
                VersionInfo info
                in chain)
            {
                ExtractWindowsNatives(
                    info.Root,
                    nativesDir);
            }

            int javaMajor =
                GetJavaMajorVersion(
                    javaPath);

            bool isForge =
                IsForgeVersion(
                    version,
                    chain,
                    mainClass);

            bool isFabric =
                IsFabricVersion(
                    version,
                    chain,
                    mainClass);

            if (isForge)
            {
                LaunchForge(
                    version,
                    username,
                    javaMajor,
                    chain,
                    clientJar,
                    mainClass,
                    nativesDir,
                    assetsDir,
                    assetIndex);

                return;
            }

            LaunchVanillaOrFabric(
                version,
                username,
                javaMajor,
                chain,
                clientJar,
                mainClass,
                nativesDir,
                assetsDir,
                assetIndex,
                isFabric);
        }

        // =========================================================
        // Vanilla / Fabric
        // =========================================================

        private void LaunchVanillaOrFabric(
            string version,
            string username,
            int javaMajor,
            List<VersionInfo> chain,
            string clientJar,
            string mainClass,
            string nativesDir,
            string assetsDir,
            string assetIndex,
            bool isFabric)
        {
            // 1. 构建基础类路径
            string classPath = BuildClassPath(chain, clientJar);
            if (string.IsNullOrWhiteSpace(classPath))
            {
                throw new Exception("ClassPath 为空。");
            }

            // 2. 【关键修复】如果是 Fabric，手动将核心库插入类路径最前面
            if (isFabric)
            {
                string? fabricLoaderPath = null;
                string? mixinPath = null;

                foreach (VersionInfo info in chain)
                {
                    if (!info.Root.TryGetProperty("libraries", out JsonElement libraries)) continue;

                    foreach (JsonElement library in libraries.EnumerateArray())
                    {
                        if (!library.TryGetProperty("name", out JsonElement nameElement)) continue;
                        string name = nameElement.GetString() ?? "";

                        string? relativePath = GetLibraryPath(library);
                        if (string.IsNullOrWhiteSpace(relativePath)) continue;
                        string fullPath = Path.Combine(gamePath, "libraries", relativePath);

                        if (!File.Exists(fullPath)) continue;

                        if (name.StartsWith("net.fabricmc:fabric-loader:"))
                        {
                            fabricLoaderPath = fullPath;
                        }
                        else if (name.StartsWith("org.spongepowered:mixin:"))
                        {
                            mixinPath = fullPath;
                        }
                    }
                }

                List<string> cpList = new List<string> { classPath };
                if (!string.IsNullOrWhiteSpace(fabricLoaderPath)) cpList.Insert(0, fabricLoaderPath);
                if (!string.IsNullOrWhiteSpace(mixinPath)) cpList.Insert(0, mixinPath);

                classPath = string.Join(";", cpList);
            }

            // 3. 构建启动参数
            List<string> args = new List<string>();

            if (javaMajor >= 9)
            {
                args.Add("--enable-native-access=ALL-UNNAMED");
            }

            args.Add("-Djava.library.path=" + nativesDir);
            args.Add("-Djna.tmpdir=" + nativesDir);
            args.Add("-Dorg.lwjgl.system.SharedLibraryExtractPath=" + nativesDir);
            args.Add("-Dio.netty.native.workdir=" + nativesDir);

            args.Add("-Dminecraft.launcher.brand=SCL");
            args.Add("-Dminecraft.launcher.version=1.0.0");

            args.Add("-Xms512M");
            args.Add("-Xmx4G");

            args.Add("-cp");
            args.Add(classPath);
            args.Add(mainClass);

            AddCommonGameArguments(args, username, version, assetsDir, assetIndex, javaMajor);

            string command = BuildJavaCommand(javaPath, args);
            StartDebugCommand(version, javaMajor, isFabric ? "Fabric" : "Vanilla", mainClass, clientJar, nativesDir, assetIndex, command);
        }

        // =========================================================
        // 通用 Minecraft 参数
        // =========================================================

        private void AddCommonGameArguments(
            List<string> args,
            string username,
            string version,
            string assetsDir,
            string assetIndex,
            int javaMajor)
        {
            args.Add("--username");
            args.Add(username);

            args.Add("--version");
            args.Add(version);

            args.Add("--gameDir");
            args.Add(launchGamePath);   // ⭐ 用实例目录

            args.Add("--assetsDir");
            args.Add(assetsDir);

            if (!string.IsNullOrWhiteSpace(assetIndex))
            {
                args.Add("--assetIndex");
                args.Add(assetIndex);
            }

            args.Add("--uuid");
            args.Add("00000000-0000-0000-0000-000000000000");

            args.Add("--accessToken");
            args.Add("0");

            args.Add("--clientId");
            args.Add("0");

            args.Add("--xuid");
            args.Add("0");

            args.Add("--userType");
            args.Add("msa");

            args.Add("--versionType");
            args.Add("release");

            if (
                javaMajor >= 17 &&
                !IsLegacyVersion(version))
            {
                args.Add("--lang");
                args.Add("zh_cn");
            }
        }

        // =========================================================
        // Forge
        // =========================================================

        private void LaunchForge(
            string version,
            string username,
            int javaMajor,
            List<VersionInfo> chain,
            string clientJar,
            string mainClass,
            string nativesDir,
            string assetsDir,
            string assetIndex)
        {
            bool isForge1122 =
                version.StartsWith(
                    "1.12.2",
                    StringComparison.OrdinalIgnoreCase);

            if (!isForge1122 && javaMajor < 17)
            {
                throw new Exception(
                    "此 Forge 启动分支需要 Java 17 或更高版本。\n" +
                    "当前 Java：" + javaMajor);
            }

            VersionInfo forgeProfile = chain[0];

            List<string> legacyClassPath =
                BuildAllLibraryFiles(chain);

            if (File.Exists(clientJar) &&
                !legacyClassPath.Contains(
                    clientJar,
                    StringComparer.OrdinalIgnoreCase))
            {
                legacyClassPath.Add(clientJar);
            }

            if (legacyClassPath.Count == 0)
            {
                throw new Exception("Forge ClassPath 为空。");
            }

            List<string> args = new List<string>();

            args.Add("-Djava.library.path=" + nativesDir);
            args.Add("-Djna.tmpdir=" + nativesDir);
            args.Add("-Dorg.lwjgl.system.SharedLibraryExtractPath=" + nativesDir);
            args.Add("-Dio.netty.native.workdir=" + nativesDir);
            args.Add("-Dminecraft.launcher.brand=SCL");
            args.Add("-Dminecraft.launcher.version=1.0.0");

            args.Add("-Xms512M");
            args.Add("-Xmx4G");

            List<string> gameArgs = new List<string>();

            ReadForgeGameArguments(
                forgeProfile,
                username,
                version,
                assetsDir,
                assetIndex,
                gameArgs);

            if (isForge1122)
            {
                // 旧版 Forge：传统 ClassPath + LaunchWrapper。
                string classPath = string.Join(
                    Path.PathSeparator.ToString(),
                    legacyClassPath);

                args.Add("-cp");
                args.Add(classPath);

                // 1.12.2 Forge 通常使用 LaunchWrapper。
                args.Add("net.minecraft.launchwrapper.Launch");

                // ⭐⭐ 关键：强制用 launchGamePath，不用 gamePath
                ForceGameDir(gameArgs, launchGamePath);

                EnsureGamePair(gameArgs, "--version", version);
                EnsureGamePair(gameArgs, "--username", username);
                EnsureGamePair(gameArgs, "--assetsDir", assetsDir);

                if (!string.IsNullOrWhiteSpace(assetIndex))
                {
                    EnsureGamePair(
                        gameArgs,
                        "--assetIndex",
                        assetIndex);
                }
            }
            else
            {
                string legacyClassPathFile =
                    CreateLegacyClassPathFile(
                        version,
                        legacyClassPath);

                List<string> initialModules =
                    BuildInitialForgeModuleList(chain);

                if (initialModules.Count == 0)
                {
                    throw new Exception(
                        "Forge 初始 ModulePath 为空。\n" +
                        "版本：" + version +
                        "\n请检查 Forge 安装目录中的模块依赖。");
                }

                string modulePath = string.Join(";", initialModules);

                args.Add("-DlegacyClassPath.file=" + legacyClassPathFile);
                args.Add("-DlibraryDirectory=" +
                    Path.Combine(gamePath, "libraries"));

                string ignoreList =
                    BuildForgeIgnoreList(
                        Path.GetFileName(clientJar));

                args.Add("-DignoreList=" + ignoreList);

                string mergeModules = BuildMergeModules(chain);

                if (!string.IsNullOrWhiteSpace(mergeModules))
                {
                    args.Add("-DmergeModules=" + mergeModules);
                }

                args.Add("--module-path");
                args.Add(modulePath);
                args.Add("--add-modules=ALL-MODULE-PATH");

                args.Add(
                    "--add-opens=java.base/java.util.jar=cpw.mods.securejarhandler");
                args.Add(
                    "--add-opens=java.base/java.lang.invoke=cpw.mods.securejarhandler");
                args.Add(
                    "--add-exports=java.base/sun.security.util=cpw.mods.securejarhandler");
                args.Add(
                    "--add-exports=jdk.naming.dns/com.sun.jndi.dns=java.naming");

                args.Add("cpw.mods.bootstraplauncher.BootstrapLauncher");

                // ⭐⭐ 关键：强制用 launchGamePath
                ForceGameDir(gameArgs, launchGamePath);

                EnsureGamePair(gameArgs, "--launchTarget", "forgeclient");
                EnsureGamePair(
                    gameArgs,
                    "--fml.forgeVersion",
                    ExtractForgeVersion(forgeProfile.Id));
                EnsureGamePair(
                    gameArgs,
                    "--fml.mcVersion",
                    FindMinecraftVersion(forgeProfile));
                EnsureGamePair(
                    gameArgs,
                    "--fml.forgeGroup",
                    "net.minecraftforge");

                string mcpVersion = FindMcpVersion(forgeProfile);

                if (!string.IsNullOrWhiteSpace(mcpVersion))
                {
                    EnsureGamePair(gameArgs, "--fml.mcpVersion", mcpVersion);
                }
            }

            args.AddRange(gameArgs);

            string command = BuildJavaCommand(javaPath, args);

            StartDebugCommand(
                version,
                javaMajor,
                "Forge",
                mainClass,
                clientJar,
                nativesDir,
                assetIndex,
                command);
        }

        // =========================================================
        // ⭐ 强制修改 gameArgs 里的 --gameDir
        // =========================================================
        private void ForceGameDir(List<string> gameArgs, string newGameDir)
        {
            // 删除已存在的 --gameDir 和它的值
            for (int i = gameArgs.Count - 2; i >= 0; i--)
            {
                if (gameArgs[i] == "--gameDir")
                {
                    gameArgs.RemoveAt(i + 1);  // 删值
                    gameArgs.RemoveAt(i);      // 删 key
                }
            }

            // 重新加
            gameArgs.Add("--gameDir");
            gameArgs.Add(newGameDir);
        }

        // =========================================================
        // 初始 Forge ModulePath
        // =========================================================

        private List<string> BuildInitialForgeModuleList(
            List<VersionInfo> chain)
        {
            List<string> all =
                BuildAllLibraryFiles(
                    chain);

            List<string> result =
                new List<string>();

            AddFirstMatchingPath(
                result,
                all,
                "/cpw/mods/bootstraplauncher/");

            AddFirstMatchingPath(
                result,
                all,
                "/cpw/mods/securejarhandler/");

            AddFirstMatchingPath(
                result,
                all,
                "/org/ow2/asm/asm-commons/");

            AddFirstMatchingPath(
                result,
                all,
                "/org/ow2/asm/asm-util/");

            AddFirstMatchingPath(
                result,
                all,
                "/org/ow2/asm/asm-analysis/");

            AddFirstMatchingPath(
                result,
                all,
                "/org/ow2/asm/asm-tree/");

            AddFirstMatchingPath(
                result,
                all,
                "/org/ow2/asm/asm/");

            AddFirstMatchingPath(
                result,
                all,
                "/net/minecraftforge/jarjarfilesystem/");

            return result;
        }

        private void AddFirstMatchingPath(
            List<string> output,
            List<string> all,
            params string[] fragments)
        {
            foreach (
                string path
                in all)
            {
                string normalized =
                    path.Replace(
                        '\\',
                        '/');

                foreach (
                    string fragment
                    in fragments)
                {
                    if (
                        normalized.Contains(
                            fragment,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (!output.Contains(
                            path,
                            StringComparer.OrdinalIgnoreCase))
                        {
                            output.Add(path);
                        }

                        return;
                    }
                }
            }
        }

        // =========================================================
        // 所有 libraries
        // =========================================================

        private List<string> BuildAllLibraryFiles(
            List<VersionInfo> chain)
        {
            List<string> result =
                new List<string>();

            HashSet<string> added =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (
                VersionInfo info
                in chain)
            {
                if (!info.Root.TryGetProperty(
                    "libraries",
                    out JsonElement libraries))
                {
                    continue;
                }

                foreach (
                    JsonElement library
                    in libraries.EnumerateArray())
                {
                    if (!IsLibraryAllowed(
                        library))
                    {
                        continue;
                    }

                    string? relative =
                        GetLibraryPath(
                            library);

                    if (string.IsNullOrWhiteSpace(
                        relative))
                    {
                        continue;
                    }

                    string fullPath =
                        Path.Combine(
                            gamePath,
                            "libraries",
                            relative);

                    if (
                        File.Exists(fullPath) &&
                        added.Add(fullPath))
                    {
                        result.Add(fullPath);
                    }
                }
            }

            return result;
        }

        // =========================================================
        // IgnoreList
        // =========================================================

        private string BuildForgeIgnoreList(
            string clientJarName)
        {
            List<string> ignore =
                new List<string>
                {
                    "bootstraplauncher",
                    "securejarhandler",

                    "asm-commons",
                    "asm-util",
                    "asm-analysis",
                    "asm-tree",
                    "asm",

                    "JarJarFileSystems",

                    "client-extra",

                    "fmlcore",
                    "javafmllanguage",
                    "lowcodelanguage",
                    "mclanguage",

                    "forge-"
                };

            if (!string.IsNullOrWhiteSpace(
                clientJarName))
            {
                AddUnique(
                    ignore,
                    clientJarName);
            }

            return string.Join(
                ",",
                ignore);
        }

        private void AddUnique(
            List<string> list,
            string value)
        {
            if (!list.Contains(
                value,
                StringComparer.OrdinalIgnoreCase))
            {
                list.Add(
                    value);
            }
        }

        // =========================================================
        // mergeModules
        // =========================================================

        private string BuildMergeModules(
            List<VersionInfo> chain)
        {
            List<string> all =
                BuildAllLibraryFiles(
                    chain);

            List<string> jna =
                new List<string>();

            foreach (
                string file
                in all)
            {
                string normalized =
                    file.Replace(
                        '\\',
                        '/');

                if (
                    normalized.Contains(
                        "/net/java/dev/jna/jna/",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains(
                        "/net/java/dev/jna/jna-platform/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    jna.Add(
                        Path.GetFileName(file));
                }
            }

            if (jna.Count <= 1)
                return "";

            return string.Join(
                ",",
                jna);
        }

        // =========================================================
        // Legacy ClassPath 文件
        // =========================================================

        private string CreateLegacyClassPathFile(
            string version,
            List<string> classPath)
        {
            string safe =
                MakeSafeFileName(
                    version);

            string filePath =
                Path.Combine(
                    gamePath,
                    "SCL_Forge_LegacyClassPath_" +
                    safe +
                    ".txt");

            StringBuilder builder =
                new StringBuilder();

            foreach (
                string jar
                in classPath)
            {
                if (string.IsNullOrWhiteSpace(
                    jar))
                {
                    continue;
                }

                if (!File.Exists(
                    jar))
                {
                    continue;
                }

                builder.AppendLine(
                    jar);
            }

            File.WriteAllText(
                filePath,
                builder.ToString(),
                new UTF8Encoding(false));

            return filePath;
        }

        private string MakeSafeFileName(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return "minecraft";
            }

            foreach (
                char c
                in Path.GetInvalidFileNameChars())
            {
                value =
                    value.Replace(
                        c,
                        '_');
            }

            return value;
        }

        // =========================================================
        // 普通 ClassPath
        // =========================================================

        private string BuildClassPath(
            List<VersionInfo> chain,
            string clientJar)
        {
            List<string> all =
                BuildAllLibraryFiles(
                    chain);

            if (
                File.Exists(clientJar) &&
                !all.Contains(
                    clientJar,
                    StringComparer.OrdinalIgnoreCase))
            {
                all.Add(
                    clientJar);
            }

            return string.Join(
                ";",
                all);
        }

        // =========================================================
        // Library JSON → path
        // =========================================================

        private string? GetLibraryPath(
            JsonElement library)
        {
            if (
                library.TryGetProperty(
                    "downloads",
                    out JsonElement downloads) &&
                downloads.TryGetProperty(
                    "artifact",
                    out JsonElement artifact))
            {
                if (
                    artifact.TryGetProperty(
                        "path",
                        out JsonElement pathElement))
                {
                    string? path =
                        pathElement.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        path))
                    {
                        return path;
                    }
                }
            }

            if (
                library.TryGetProperty(
                    "name",
                    out JsonElement nameElement))
            {
                string? name =
                    nameElement.GetString();

                if (!string.IsNullOrWhiteSpace(
                    name))
                {
                    return MavenNameToPath(
                        name);
                }
            }

            return null;
        }

        private string? MavenNameToPath(
            string name)
        {
            string[] parts =
                name.Split(':');

            if (parts.Length < 3)
                return null;

            string group =
                parts[0].Replace(
                    '.',
                    '/');

            string artifact =
                parts[1];

            string version =
                parts[2];

            if (parts.Length >= 4)
            {
                string classifier =
                    parts[3];

                return
                    $"{group}/{artifact}/{version}/" +
                    $"{artifact}-{version}-{classifier}.jar";
            }

            return
                $"{group}/{artifact}/{version}/" +
                $"{artifact}-{version}.jar";
        }

        // =========================================================
        // Forge Game Arguments（已修复：兼容 minecraftArguments）
        // =========================================================

        private void ReadForgeGameArguments(
            VersionInfo forgeProfile,
            string username,
            string version,
            string assetsDir,
            string assetIndex,
            List<string> output)
        {
            JsonElement root =
                forgeProfile.Root;

            // ========== 1. 新版 arguments.game ==========
            if (
                root.TryGetProperty(
                    "arguments",
                    out JsonElement arguments) &&
                arguments.TryGetProperty(
                    "game",
                    out JsonElement game) &&
                game.ValueKind ==
                JsonValueKind.Array)
            {
                foreach (
                    JsonElement item
                    in game.EnumerateArray())
                {
                    foreach (
                        string raw
                        in ReadArgumentValues(item))
                    {
                        if (string.IsNullOrWhiteSpace(
                            raw))
                        {
                            continue;
                        }

                        string value =
                            ReplacePlaceholders(
                                raw,
                                username,
                                version,
                                assetsDir,
                                assetIndex);

                        if (value.Contains("${"))
                        {
                            continue;
                        }

                        AddRawIfMissing(
                            output,
                            value);
                    }
                }
            }

            // ========== 2. 老版 minecraftArguments（1.12.2 必需）==========
            bool hasTweak =
                output.Contains("--tweakClass");

            if (
                !hasTweak &&
                root.TryGetProperty(
                    "minecraftArguments",
                    out JsonElement mcArgsElement))
            {
                string? mcArgsRaw =
                    mcArgsElement.GetString();

                if (!string.IsNullOrWhiteSpace(
                    mcArgsRaw))
                {
                    string[] tokens =
                        mcArgsRaw.Split(
                            new[] { ' ', '\t', '\r', '\n' },
                            StringSplitOptions.RemoveEmptyEntries);

                    foreach (
                        string tok
                        in tokens)
                    {
                        string value =
                            ReplacePlaceholders(
                                tok,
                                username,
                                version,
                                assetsDir,
                                assetIndex);

                        if (value.Contains("${"))
                        {
                            continue;
                        }

                        AddRawIfMissing(
                            output,
                            value);
                    }
                }
            }

            // ========== 3. 补齐必须参数 ==========
            EnsureGamePair(
                output,
                "--username",
                username);

            EnsureGamePair(
                output,
                "--version",
                version);

            EnsureGamePair(
                output,
                "--gameDir",
                launchGamePath);   // ⭐ 用实例目录

            EnsureGamePair(
                output,
                "--assetsDir",
                assetsDir);

            if (!string.IsNullOrWhiteSpace(
                assetIndex))
            {
                EnsureGamePair(
                    output,
                    "--assetIndex",
                    assetIndex);
            }

            EnsureGamePair(
                output,
                "--uuid",
                "00000000-0000-0000-0000-000000000000");

            EnsureGamePair(
                output,
                "--accessToken",
                "0");

            EnsureGamePair(
                output,
                "--clientId",
                "0");

            EnsureGamePair(
                output,
                "--xuid",
                "0");

            EnsureGamePair(
                output,
                "--userType",
                "msa");

            EnsureGamePair(
                output,
                "--versionType",
                "release");
        }

        // ========== 占位符替换 ==========
        private string ReplacePlaceholders(
            string value,
            string username,
            string version,
            string assetsDir,
            string assetIndex)
        {
            return value
                .Replace("${auth_player_name}", username)
                .Replace("${version_name}", version)
                .Replace("${game_directory}", launchGamePath)   // ⭐ 用实例目录
                .Replace("${assets_root}", assetsDir)
                .Replace("${assets_index_name}", assetIndex)
                .Replace("${auth_uuid}", "00000000-0000-0000-0000-000000000000")
                .Replace("${auth_access_token}", "0")
                .Replace("${clientid}", "0")
                .Replace("${auth_xuid}", "0")
                .Replace("${user_type}", "msa")
                .Replace("${launcher_name}", "SCL")
                .Replace("${launcher_version}", "1.0.0");
        }

        // =========================================================
        // JSON arguments
        // =========================================================

        private IEnumerable<string>
            ReadArgumentValues(
            JsonElement item)
        {
            if (
                item.ValueKind ==
                JsonValueKind.String)
            {
                string? value =
                    item.GetString();

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    yield return value;
                }

                yield break;
            }

            if (
                item.ValueKind !=
                JsonValueKind.Object)
            {
                yield break;
            }

            if (
                item.TryGetProperty(
                    "rules",
                    out JsonElement rules) &&
                !RulesAllowWindows(
                    rules))
            {
                yield break;
            }

            if (!item.TryGetProperty(
                "value",
                out JsonElement valueElement))
            {
                yield break;
            }

            if (
                valueElement.ValueKind ==
                JsonValueKind.String)
            {
                string? value =
                    valueElement.GetString();

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    yield return value;
                }

                yield break;
            }

            if (
                valueElement.ValueKind ==
                JsonValueKind.Array)
            {
                foreach (
                    JsonElement child
                    in valueElement.EnumerateArray())
                {
                    if (
                        child.ValueKind !=
                        JsonValueKind.String)
                    {
                        continue;
                    }

                    string? value =
                        child.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        yield return value;
                    }
                }
            }
        }

        // =========================================================
        // Rules
        // =========================================================

        private bool RulesAllowWindows(
            JsonElement rules)
        {
            if (
                rules.ValueKind !=
                JsonValueKind.Array)
            {
                return true;
            }

            bool matched =
                false;

            bool allowed =
                false;

            foreach (
                JsonElement rule
                in rules.EnumerateArray())
            {
                if (
                    rule.ValueKind !=
                    JsonValueKind.Object)
                {
                    continue;
                }

                string action =
                    rule.TryGetProperty(
                        "action",
                        out JsonElement actionElement)
                    ? actionElement.GetString() ?? ""
                    : "";

                bool match =
                    true;

                if (
                    rule.TryGetProperty(
                        "os",
                        out JsonElement os) &&
                    os.TryGetProperty(
                        "name",
                        out JsonElement osName))
                {
                    string? name =
                        osName.GetString();

                    if (
                        !string.IsNullOrWhiteSpace(
                            name) &&
                        !string.Equals(
                            name,
                            "windows",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        match =
                            false;
                    }
                }

                if (!match)
                {
                    continue;
                }

                matched =
                    true;

                allowed =
                    string.Equals(
                        action,
                        "allow",
                        StringComparison.OrdinalIgnoreCase);
            }

            return !matched || allowed;
        }

        private bool IsLibraryAllowed(
            JsonElement library)
        {
            if (
                !library.TryGetProperty(
                    "rules",
                    out JsonElement rules))
            {
                return true;
            }

            return RulesAllowWindows(
                rules);
        }

        // =========================================================
        // Version Chain
        // =========================================================

        private List<VersionInfo>
            BuildVersionChain(
            string version)
        {
            List<VersionInfo> result =
                new List<VersionInfo>();

            HashSet<string> visited =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            string? current =
                version;

            while (
                !string.IsNullOrWhiteSpace(
                    current))
            {
                if (!visited.Add(
                    current))
                {
                    throw new Exception(
                        "发现循环版本继承：" +
                        current);
                }

                string jsonPath =
                    Path.Combine(
                        gamePath,
                        "versions",
                        current,
                        current + ".json");

                if (!File.Exists(
                    jsonPath))
                {
                    throw new Exception(
                        "找不到版本 JSON：\n" +
                        jsonPath);
                }

                string json =
                    File.ReadAllText(
                        jsonPath);

                JsonDocument document =
                    JsonDocument.Parse(
                        json);

                JsonElement root =
                    document.RootElement.Clone();

                string? parent =
                    null;

                if (
                    root.TryGetProperty(
                        "inheritsFrom",
                        out JsonElement inherits))
                {
                    parent =
                        inherits.GetString();
                }

                result.Add(
                    new VersionInfo(
                        current,
                        root,
                        document));

                current =
                    parent;
            }

            return result;
        }

        // =========================================================
        // Client
        // =========================================================

        private string? FindClientJar(
            List<VersionInfo> chain)
        {
            foreach (
                VersionInfo info
                in chain)
            {
                string path =
                    Path.Combine(
                        gamePath,
                        "versions",
                        info.Id,
                        info.Id + ".jar");

                if (File.Exists(
                    path))
                {
                    return path;
                }
            }

            return null;
        }

        // =========================================================
        // MainClass
        // =========================================================

        private string FindMainClass(
            List<VersionInfo> chain)
        {
            foreach (
                VersionInfo info
                in chain)
            {
                if (
                    info.Root.TryGetProperty(
                        "mainClass",
                        out JsonElement main))
                {
                    string? value =
                        main.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        value))
                    {
                        return value;
                    }
                }
            }

            return
                "net.minecraft.client.main.Main";
        }

        // =========================================================
        // Asset
        // =========================================================

        private string FindAssetIndex(
            List<VersionInfo> chain)
        {
            foreach (
                VersionInfo info
                in chain)
            {
                if (
                    !info.Root.TryGetProperty(
                        "assetIndex",
                        out JsonElement asset))
                {
                    continue;
                }

                if (
                    !asset.TryGetProperty(
                        "id",
                        out JsonElement id))
                {
                    continue;
                }

                string? value =
                    id.GetString();

                if (!string.IsNullOrWhiteSpace(
                    value))
                {
                    return value;
                }
            }

            return "legacy";
        }

        // =========================================================
        // Native
        // =========================================================

        private void ExtractWindowsNatives(
            JsonElement root,
            string nativesDir)
        {
            if (!root.TryGetProperty(
                "libraries",
                out JsonElement libraries))
            {
                return;
            }

            foreach (
                JsonElement library
                in libraries.EnumerateArray())
            {
                if (!IsLibraryAllowed(
                    library))
                {
                    continue;
                }

                if (!library.TryGetProperty(
                    "downloads",
                    out JsonElement downloads))
                {
                    continue;
                }

                if (!downloads.TryGetProperty(
                    "classifiers",
                    out JsonElement classifiers))
                {
                    continue;
                }

                string? classifier =
                    null;

                if (
                    library.TryGetProperty(
                        "natives",
                        out JsonElement natives) &&
                    natives.TryGetProperty(
                        "windows",
                        out JsonElement windows))
                {
                    string? template =
                        windows.GetString();

                    if (!string.IsNullOrWhiteSpace(
                        template))
                    {
                        classifier =
                            template.Replace(
                                "${arch}",
                                Environment.Is64BitOperatingSystem
                                    ? "64"
                                    : "32");
                    }
                }

                if (string.IsNullOrWhiteSpace(
                    classifier))
                {
                    foreach (
                        JsonProperty property
                        in classifiers.EnumerateObject())
                    {
                        if (
                            property.Name.Equals(
                                "natives-windows",
                                StringComparison.OrdinalIgnoreCase) ||
                            property.Name.Equals(
                                "natives-windows-64",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            classifier =
                                property.Name;

                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(
                    classifier))
                {
                    continue;
                }

                if (!classifiers.TryGetProperty(
                    classifier,
                    out JsonElement artifact))
                {
                    continue;
                }

                string? path =
                    artifact
                        .GetProperty(
                            "path")
                        .GetString();

                if (string.IsNullOrWhiteSpace(
                    path))
                {
                    continue;
                }

                string jar =
                    Path.Combine(
                        gamePath,
                        "libraries",
                        path);

                if (!File.Exists(
                    jar))
                {
                    continue;
                }

                try
                {
                    ExtractNativeJar(
                        jar,
                        nativesDir);
                }
                catch
                {
                }
            }
        }

        private void ExtractNativeJar(
            string jar,
            string outputDirectory)
        {
            using FileStream stream =
                new FileStream(
                    jar,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

            using ZipArchive archive =
                new ZipArchive(
                    stream,
                    ZipArchiveMode.Read);

            foreach (
                ZipArchiveEntry entry
                in archive.Entries)
            {
                string name =
                    entry.Name;

                if (string.IsNullOrWhiteSpace(
                    name))
                {
                    continue;
                }

                bool native =
                    name.EndsWith(
                        ".dll",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(
                        ".so",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(
                        ".dylib",
                        StringComparison.OrdinalIgnoreCase);

                if (!native)
                {
                    continue;
                }

                string destination =
                    Path.Combine(
                        outputDirectory,
                        name);

                using Stream input =
                    entry.Open();

                using FileStream output =
                    new FileStream(
                        destination,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);

                input.CopyTo(
                    output);
            }
        }

        // =========================================================
        // Loader
        // =========================================================

        private bool IsForgeVersion(
            string version,
            List<VersionInfo> chain,
            string mainClass)
        {
            if (
                version.Contains(
                    "forge",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (
                mainClass.Contains(
                    "bootstraplauncher",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (
                VersionInfo info
                in chain)
            {
                if (
                    info.Id.Contains(
                        "forge",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsFabricVersion(
            string version,
            List<VersionInfo> chain,
            string mainClass)
        {
            if (
                version.Contains(
                    "fabric",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (
                mainClass.Contains(
                    "fabric",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (
                VersionInfo info
                in chain)
            {
                if (
                    info.Id.Contains(
                        "fabric",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // =========================================================
        // Java
        // =========================================================

        private int GetJavaMajorVersion(
            string java)
        {
            try
            {
                ProcessStartInfo psi =
                    new ProcessStartInfo
                    {
                        FileName =
                            java,
                        Arguments =
                            "-version",
                        RedirectStandardOutput =
                            true,
                        RedirectStandardError =
                            true,
                        UseShellExecute =
                            false,
                        CreateNoWindow =
                            true
                    };

                using Process? process =
                    Process.Start(psi);

                if (process == null)
                    return 8;

                string text =
                    process.StandardError.ReadToEnd();

                process.WaitForExit();

                return ParseJavaMajorVersion(
                    text);
            }
            catch
            {
                return 8;
            }
        }

        private int ParseJavaMajorVersion(
            string text)
        {
            int first =
                text.IndexOf('"');

            if (first < 0)
                return 8;

            int second =
                text.IndexOf(
                    '"',
                    first + 1);

            if (second <= first)
                return 8;

            string value =
                text.Substring(
                    first + 1,
                    second -
                    first -
                    1);

            if (
                value.StartsWith(
                    "1.",
                    StringComparison.Ordinal))
            {
                string[] parts =
                    value.Split('.');

                if (
                    parts.Length >= 2 &&
                    int.TryParse(
                        parts[1],
                        out int oldMajor))
                {
                    return oldMajor;
                }
            }

            string number =
                "";

            foreach (
                char c
                in value)
            {
                if (!char.IsDigit(c))
                    break;

                number += c;
            }

            if (
                int.TryParse(
                    number,
                    out int major))
            {
                return major;
            }

            return 8;
        }

        // =========================================================
        // Legacy
        // =========================================================

        private bool IsLegacyVersion(
            string version)
        {
            if (
                !version.StartsWith(
                    "1.",
                    StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts =
                version.Split('.');

            if (parts.Length < 2)
                return true;

            if (!int.TryParse(
                parts[1],
                out int minor))
            {
                return true;
            }

            return minor < 18;
        }

        // =========================================================
        // Forge Version
        // =========================================================

        private string FindMinecraftVersion(
            VersionInfo forgeProfile)
        {
            if (
                forgeProfile.Root.TryGetProperty(
                    "inheritsFrom",
                    out JsonElement inherits))
            {
                string? parent =
                    inherits.GetString();

                if (!string.IsNullOrWhiteSpace(
                    parent))
                {
                    return parent;
                }
            }

            string id =
                forgeProfile.Id;

            int index =
                id.IndexOf(
                    "-forge-",
                    StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
            {
                return id.Substring(
                    0,
                    index);
            }

            return id;
        }

        private string ExtractForgeVersion(
            string forgeProfileId)
        {
            int index =
                forgeProfileId.IndexOf(
                    "-forge-",
                    StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
            {
                return forgeProfileId.Substring(
                    index + 7);
            }

            return "";
        }

        private string FindMcpVersion(
            VersionInfo forgeProfile)
        {
            if (
                forgeProfile.Root.TryGetProperty(
                    "mcpVersion",
                    out JsonElement mcp))
            {
                return mcp.GetString() ?? "";
            }

            return "";
        }

        // =========================================================
        // 参数
        // =========================================================

        private void AddRawIfMissing(
            List<string> args,
            string value)
        {
            if (!args.Contains(value))
            {
                args.Add(value);
            }
        }

        private void EnsureGamePair(
            List<string> args,
            string key,
            string value)
        {
            if (args.Contains(key))
                return;

            args.Add(key);
            args.Add(value);
        }

        // =========================================================
        // Java Command
        // =========================================================

        private string BuildJavaCommand(
            string java,
            List<string> args)
        {
            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                QuoteCommandLine(java));

            foreach (
                string arg
                in args)
            {
                builder.Append(' ');

                builder.Append(
                    QuoteCommandLine(arg));
            }

            return builder.ToString();
        }

        private string QuoteCommandLine(
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                value))
            {
                return "\"\"";
            }

            if (
                value.Length >= 2 &&
                value[0] == '"' &&
                value[^1] == '"')
            {
                return value;
            }

            if (
                value.Contains(' ') ||
                value.Contains('\t') ||
                value.Contains(';'))
            {
                return "\"" +
                    value.Replace(
                        "\"",
                        "\\\"") +
                    "\"";
            }

            return value;
        }

        // =========================================================
        // 实例隔离
        // =========================================================

        private string ResolveInstanceGamePath(
            string version)
        {
            string loader = DetectLoader(version);
            string loaderVersion =
                ExtractLoaderVersion(version, loader);

            return InstanceManager.GetInstancePath(
                version,
                loader,
                loaderVersion);
        }

        private string DetectLoader(string version)
        {
            if (version.Contains(
                "neoforge",
                StringComparison.OrdinalIgnoreCase))
            {
                return "NeoForge";
            }

            if (version.Contains(
                "forge",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Forge";
            }

            if (version.Contains(
                "fabric",
                StringComparison.OrdinalIgnoreCase))
            {
                return "Fabric";
            }

            return "Vanilla";
        }

        private string ExtractLoaderVersion(
            string version,
            string loader)
        {
            string marker = loader switch
            {
                "NeoForge" => "-neoforge-",
                "Forge" => "-forge-",
                "Fabric" => "-fabric-loader-",
                _ => ""
            };

            if (!string.IsNullOrEmpty(marker))
            {
                int index = version.IndexOf(
                    marker,
                    StringComparison.OrdinalIgnoreCase);

                if (index >= 0)
                {
                    string tail =
                        version.Substring(index + marker.Length);

                    if (loader == "Fabric")
                    {
                        int last = tail.LastIndexOf('-');
                        if (last > 0)
                            return tail.Substring(0, last);
                    }
                    else
                    {
                        return tail;
                    }
                }
            }

            return loader == "Vanilla"
                ? "release"
                : "unknown";
        }

        // =========================================================
        // CMD
        // =========================================================

        private void StartDebugCommand(
            string version,
            int javaMajor,
            string loader,
            string mainClass,
            string clientJar,
            string nativesDir,
            string assetIndex,
            string command)
        {
            string batPath =
                Path.Combine(
                    launchGamePath,
                    "SCL_Debug_Start.bat");

            StringBuilder bat =
                new StringBuilder();

            bat.AppendLine("@echo off");
            bat.AppendLine(
                "chcp 65001 >nul");

            bat.AppendLine(
                "title SCL Minecraft Debug Console");

            bat.AppendLine();

            bat.AppendLine(
                "echo ========================================");

            bat.AppendLine(
                "echo SCL Minecraft Debug Console");

            bat.AppendLine(
                "echo ========================================");

            bat.AppendLine();

            bat.AppendLine("echo Java:");
            bat.AppendLine("echo " + javaPath);
            bat.AppendLine();

            bat.AppendLine("echo Java Major:");
            bat.AppendLine("echo " + javaMajor);
            bat.AppendLine();

            bat.AppendLine("echo Minecraft:");
            bat.AppendLine("echo " + version);
            bat.AppendLine();

            bat.AppendLine("echo Loader:");
            bat.AppendLine("echo " + loader);
            bat.AppendLine();

            bat.AppendLine("echo MainClass:");
            bat.AppendLine("echo " + mainClass);
            bat.AppendLine();

            bat.AppendLine("echo Client JAR:");
            bat.AppendLine("echo " + clientJar);
            bat.AppendLine();

            bat.AppendLine("echo Native:");
            bat.AppendLine("echo " + nativesDir);
            bat.AppendLine();

            bat.AppendLine("echo AssetIndex:");
            bat.AppendLine("echo " + assetIndex);
            bat.AppendLine();

            bat.AppendLine("echo GameDir:");
            bat.AppendLine("echo " + launchGamePath);   // ⭐ 显示实例目录
            bat.AppendLine();

            bat.AppendLine(
                "echo Starting Minecraft...");

            bat.AppendLine();

            bat.AppendLine(
                command);

            bat.AppendLine();

            bat.AppendLine(
                "echo ========================================");

            bat.AppendLine(
                "echo Minecraft process exited.");

            bat.AppendLine(
                "echo ExitCode: %errorlevel%");

            bat.AppendLine(
                "echo ========================================");

            bat.AppendLine();

            bat.AppendLine(
                "pause");

            File.WriteAllText(
                batPath,
                bat.ToString(),
                new UTF8Encoding(false));

            ProcessStartInfo psi =
                new ProcessStartInfo
                {
                    FileName = "cmd.exe",

                    Arguments =
                        "/c call \"" +
                        batPath +
                        "\"",

                    WorkingDirectory =
                        launchGamePath,   // ⭐ 用实例目录

                    UseShellExecute = false,

                    CreateNoWindow = true,

                    WindowStyle =
                        ProcessWindowStyle.Hidden
                };

            Process? process =
                Process.Start(psi);

            if (process == null)
            {
                throw new Exception(
                    "无法启动 CMD 调试窗口。");
            }
        }

        // =========================================================
        // 清理 natives
        // =========================================================

        private void ClearDirectoryFiles(
            string directory)
        {
            if (!Directory.Exists(
                directory))
            {
                return;
            }

            foreach (
                string file
                in Directory.GetFiles(
                    directory))
            {
                try
                {
                    File.Delete(
                        file);
                }
                catch
                {
                }
            }
        }

        // =========================================================
        // VersionInfo
        // =========================================================

        private sealed class VersionInfo
        {
            public string Id { get; }

            public JsonElement Root { get; }

            public JsonDocument Document { get; }

            public VersionInfo(
                string id,
                JsonElement root,
                JsonDocument document)
            {
                Id = id;
                Root = root;
                Document = document;
            }
        }
    }
}