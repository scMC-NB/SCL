using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SCL.Core
{
    public static class JavaFinder
    {
        // =========================================================
        // 根据 Minecraft version.json 获取所需 Java 主版本
        // =========================================================

        public static int GetRequiredJavaVersion(
            string versionJsonPath,
            string minecraftVersion)
        {
            // 第一优先级：
            // 从 Mojang 的 version.json 读取 javaVersion.majorVersion
            try
            {
                if (File.Exists(versionJsonPath))
                {
                    string json =
                        File.ReadAllText(versionJsonPath);

                    using JsonDocument doc =
                        JsonDocument.Parse(json);

                    JsonElement root =
                        doc.RootElement;

                    if (root.TryGetProperty(
                        "javaVersion",
                        out JsonElement javaVersion))
                    {
                        if (javaVersion.TryGetProperty(
                            "majorVersion",
                            out JsonElement majorVersion))
                        {
                            if (majorVersion.TryGetInt32(
                                out int required))
                            {
                                return required;
                            }
                        }
                    }
                }
            }
            catch
            {
                // JSON 中没有 Java 信息时进入备用规则
            }

            // 第二优先级：
            // 使用旧版 Minecraft 版本规则作为兜底
            return GetRequiredJavaVersionFallback(
                minecraftVersion);
        }


        // =========================================================
        // 旧版本备用规则
        // =========================================================

        private static int GetRequiredJavaVersionFallback(
            string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return 8;
            }

            string[] parts =
                version.Split('.');

            int minor = 0;
            int patch = 0;

            // 例如：
            // 1.20.1
            //   ↑
            // minor = 20

            if (parts.Length >= 2)
            {
                int.TryParse(
                    parts[1],
                    out minor);
            }

            // 例如：
            // 1.20.5
            //       ↑
            // patch = 5

            if (parts.Length >= 3)
            {
                string number = "";

                foreach (char c in parts[2])
                {
                    if (!char.IsDigit(c))
                    {
                        break;
                    }

                    number += c;
                }

                if (!string.IsNullOrWhiteSpace(number))
                {
                    int.TryParse(
                        number,
                        out patch);
                }
            }

            // 1.20.5 及以后
            if (minor == 20 && patch >= 5)
            {
                return 21;
            }

            // 1.21+
            if (minor >= 21)
            {
                return 21;
            }

            // 1.18 ~ 1.20.4
            if (minor >= 18 && minor <= 20)
            {
                return 17;
            }

            // 1.17
            if (minor == 17)
            {
                return 16;
            }

            // 老版本
            return 8;
        }


        // =========================================================
        // 根据 Minecraft JSON 自动找 Java
        // =========================================================

        public static string? FindJavaForMinecraft(
            string versionJsonPath,
            string minecraftVersion)
        {
            int requiredJava =
                GetRequiredJavaVersion(
                    versionJsonPath,
                    minecraftVersion);

            return FindJava(requiredJava);
        }


        // =========================================================
        // 查找指定 Java
        //
        // 优先：
        // 1. .minecraft/runtime
        // 2. 常见安装目录
        // 3. PATH
        // =========================================================

        public static string? FindJava(
            int majorVersion)
        {
            // =====================================================
            // 1. Minecraft runtime
            // =====================================================

            string minecraftDir =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    ".minecraft");

            string runtimeDir =
                Path.Combine(
                    minecraftDir,
                    "runtime");

            if (Directory.Exists(runtimeDir))
            {
                foreach (
                    string javaPath
                    in Directory.GetFiles(
                        runtimeDir,
                        "java.exe",
                        SearchOption.AllDirectories))
                {
                    if (IsCorrectJava(
                        javaPath,
                        majorVersion))
                    {
                        return javaPath;
                    }
                }
            }


            // =====================================================
            // 2. 常见 Java 安装目录
            // =====================================================

            string[] roots =
            {
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Temurin",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files (x86)\Java"
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (
                    string javaPath
                    in Directory.GetFiles(
                        root,
                        "java.exe",
                        SearchOption.AllDirectories))
                {
                    if (IsCorrectJava(
                        javaPath,
                        majorVersion))
                    {
                        return javaPath;
                    }
                }
            }


            // =====================================================
            // 3. PATH
            // =====================================================

            try
            {
                ProcessStartInfo psi =
                    new ProcessStartInfo
                    {
                        FileName = "where.exe",
                        Arguments = "java.exe",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                using Process? process =
                    Process.Start(psi);

                if (process != null)
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        string? line =
                            process.StandardOutput.ReadLine();

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        if (IsCorrectJava(
                            line,
                            majorVersion))
                        {
                            return line;
                        }
                    }
                }
            }
            catch
            {
                // PATH 查找失败就继续
            }


            return null;
        }


        // =========================================================
        // 检查 Java 实际版本
        // =========================================================

        private static bool IsCorrectJava(
            string javaPath,
            int expectedMajor)
        {
            try
            {
                if (!File.Exists(javaPath))
                {
                    return false;
                }

                ProcessStartInfo psi =
                    new ProcessStartInfo
                    {
                        FileName = javaPath,
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                using Process? process =
                    Process.Start(psi);

                if (process == null)
                {
                    return false;
                }

                string stdout =
                    process.StandardOutput.ReadToEnd();

                string stderr =
                    process.StandardError.ReadToEnd();

                process.WaitForExit();

                string text =
                    stdout +
                    Environment.NewLine +
                    stderr;

                int actualMajor =
                    ParseJavaMajorVersion(text);

                return actualMajor == expectedMajor;
            }
            catch
            {
                return false;
            }
        }


        // =========================================================
        // 解析 Java 主版本
        //
        // Java 8:
        // java version "1.8.0_..."
        //
        // Java 17:
        // java version "17.0.x"
        //
        // Java 21:
        // 21.0.x
        //
        // Java 25:
        // 25.0.x
        // =========================================================

        private static int ParseJavaMajorVersion(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return -1;
            }

            int firstQuote =
                text.IndexOf('"');

            if (firstQuote < 0)
            {
                return -1;
            }

            int secondQuote =
                text.IndexOf(
                    '"',
                    firstQuote + 1);

            if (secondQuote <= firstQuote)
            {
                return -1;
            }

            string version =
                text.Substring(
                    firstQuote + 1,
                    secondQuote -
                    firstQuote -
                    1);

            // Java 8
            // "1.8.0_..."
            if (version.StartsWith("1."))
            {
                string[] parts =
                    version.Split('.');

                if (parts.Length >= 2 &&
                    int.TryParse(
                        parts[1],
                        out int oldMajor))
                {
                    return oldMajor;
                }
            }

            // Java 9+
            string number = "";

            foreach (char c in version)
            {
                if (!char.IsDigit(c))
                {
                    break;
                }

                number += c;
            }

            if (int.TryParse(
                number,
                out int modernMajor))
            {
                return modernMajor;
            }

            return -1;
        }
    }
}