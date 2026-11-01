// SCL Terracotta on-demand provisioner.
// Downloads the official Windows package on first use; the launcher release does not bundle the EXE.
// Requires .NET 8 (System.Formats.Tar).
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCL.Core;

public sealed record TerracottaModule(
    string Version,
    string Architecture,
    string DirectoryPath,
    string ExecutablePath);

public sealed record TerracottaProvisioningProgress(string Message, int Percent);

public sealed class TerracottaProvisioningService
{
    private const string GiteeLatestReleaseApi = "https://gitee.com/api/v5/repos/burningtnt/Terracotta/releases/latest";
    private const string GitHubLatestReleaseApi = "https://api.github.com/repos/burningtnt/Terracotta/releases/latest";
    // Proxy first: it avoids requiring direct access to github.com:443 for release assets.
    private const string GitHubProxyAssetPrefix = "https://gh-proxy.org/https://github.com/burningtnt/Terracotta/releases/download/v";
    // Last-known stable fallback, used only when both release metadata APIs are unreachable.
    private const string FallbackVersion = "0.4.2";
    private const long MaximumMetadataBytes = 2 * 1024 * 1024;
    private const long MaximumArchiveBytes = 64 * 1024 * 1024;
    private const long MaximumExtractedFileBytes = 64 * 1024 * 1024;
    private const string RuntimeFileName = "VCRUNTIME140.DLL";
    private const string ExecutableFileName = "terracotta.exe";
    private const string ManifestFileName = ".scl-terracotta.json";

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly IReadOnlyDictionary<string, string> KnownArchiveDigests =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0.4.2/x86_64"] = "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0",
            ["0.4.2/arm64"] = "acfab0a87a02dedc6dab7c05303186c8907f56f815548b693fb3324358da7d14"
        };

    private readonly string moduleRoot;
    private readonly string architecture;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ReleaseInfo? cachedRelease;

    public TerracottaProvisioningService(string? installRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Terracotta provisioning in SCL currently supports Windows only.");

        architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("Terracotta supports Windows x64 and Arm64 only.")
        };

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = AppContext.BaseDirectory;
        moduleRoot = Path.GetFullPath(installRoot ?? Path.Combine(local, "SCL", "Tools", "Terracotta"));
    }

    public string InstallRoot => moduleRoot;

    public TerracottaModule? TryGetAvailable()
    {
        try
        {
            if (!Directory.Exists(moduleRoot)) return null;
            return Directory.EnumerateDirectories(moduleRoot)
                .Select(ValidateInstallation)
                .Where(x => x is not null)
                .OrderByDescending(x => ParseVersion(x!.Version))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task<TerracottaModule> EnsureAvailableAsync(
        IProgress<TerracottaProvisioningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(moduleRoot);
            TerracottaModule? installed = TryGetAvailable();
            ReleaseInfo release;
            try
            {
                release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (installed is not null)
            {
                progress?.Report(new TerracottaProvisioningProgress(
                    $"无法检查更新，使用已安装的 Terracotta {installed.Version}。", 100));
                return installed;
            }

            if (installed is not null && CompareVersions(installed.Version, release.Version) >= 0)
            {
                progress?.Report(new TerracottaProvisioningProgress(
                    $"Terracotta {installed.Version} 已就绪。", 100));
                return installed;
            }

            return await DownloadAndInstallAsync(release, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ReleaseInfo> GetLatestReleaseAsync(CancellationToken ct)
    {
        if (cachedRelease is not null) return cachedRelease;

        // Query both metadata sources concurrently. Asset downloads themselves use the proxy first.
        Task<ReleaseMetadata?> giteeTask = TryGetReleaseAsync(GiteeLatestReleaseApi, ct);
        Task<ReleaseMetadata?> githubTask = TryGetReleaseAsync(GitHubLatestReleaseApi, ct);
        await Task.WhenAll(giteeTask, githubTask).ConfigureAwait(false);
        ReleaseMetadata? gitee = await giteeTask.ConfigureAwait(false);
        ReleaseMetadata? github = await githubTask.ConfigureAwait(false);
        ReleaseMetadata? selected = gitee ?? github;

        // If metadata sites cannot be reached at all, still allow first-time install of the
        // version whose official proxy URL was supplied by the user. Future runs retry metadata.
        if (selected is null)
        {
            string fallbackAssetName = $"terracotta-{FallbackVersion}-windows-{architecture}-pkg.tar.gz";
            var fallbackSources = new List<Uri>();
            AddHttpsSource(fallbackSources, new Uri($"{GitHubProxyAssetPrefix}{FallbackVersion}/{fallbackAssetName}"));
            AddHttpsSource(fallbackSources, new Uri($"https://github.com/burningtnt/Terracotta/releases/download/v{FallbackVersion}/{fallbackAssetName}"));
            string? fallbackDigest = KnownArchiveDigests.TryGetValue($"{FallbackVersion}/{architecture}", out string? fallbackKnown)
                ? fallbackKnown
                : null;
            cachedRelease = new ReleaseInfo(FallbackVersion, architecture, fallbackAssetName, fallbackSources, fallbackDigest);
            return cachedRelease;
        }

        string assetName = $"terracotta-{selected.Version}-windows-{architecture}-pkg.tar.gz";
        ReleaseAsset? selectedAsset = selected.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.Ordinal));
        if (selectedAsset is null && github is not null)
        {
            selected = github;
            assetName = $"terracotta-{selected.Version}-windows-{architecture}-pkg.tar.gz";
            selectedAsset = selected.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.Ordinal));
        }
        if (selectedAsset is null)
            throw new InvalidDataException($"官方发行版中找不到 Windows {architecture} 安装包：{assetName}");

        var sources = new List<Uri>();
        // IMPORTANT: use the user's working proxy address before trying Gitee or direct GitHub.
        // The URL points to the .tar.gz package; it must be unpacked below before launching.
        AddHttpsSource(sources, new Uri($"{GitHubProxyAssetPrefix}{selected.Version}/{assetName}"));
        AddHttpsSource(sources, selectedAsset.DownloadUri);
        if (github is not null && github.Version == selected.Version)
        {
            ReleaseAsset? githubAsset = github.Assets.FirstOrDefault(a => a.Name.Equals(assetName, StringComparison.Ordinal));
            if (githubAsset is not null) AddHttpsSource(sources, githubAsset.DownloadUri);
        }
        AddHttpsSource(sources, new Uri($"https://github.com/burningtnt/Terracotta/releases/download/v{selected.Version}/{assetName}"));

        string? digest = null;
        if (github is not null && github.Version == selected.Version)
            digest = NormalizeSha256(github.Assets.FirstOrDefault(a => a.Name == assetName)?.Digest);
        if (digest is null && KnownArchiveDigests.TryGetValue($"{selected.Version}/{architecture}", out string? known))
            digest = known;

        cachedRelease = new ReleaseInfo(selected.Version, architecture, assetName, sources, digest);
        return cachedRelease;
    }

    private async Task<ReleaseMetadata?> TryGetReleaseAsync(string url, CancellationToken ct)
    {
        try
        {
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("SCL-Launcher/1.0");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, metadataTimeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumMetadataBytes)
                throw new InvalidDataException("Terracotta release metadata is too large.");
            await using Stream stream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, metadataTimeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > MaximumMetadataBytes) throw new InvalidDataException("Terracotta release metadata is too large.");
                memory.Write(buffer, 0, read);
            }
            using JsonDocument document = JsonDocument.Parse(memory.ToArray());
            JsonElement root = document.RootElement;
            string version = NormalizeVersion(root.GetProperty("tag_name").GetString());
            if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
                return null;
            if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                return null;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    string rawUrl = item.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                    if (name.Length == 0 || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? assetUri)) continue;
                    if (assetUri.Scheme != Uri.UriSchemeHttps) continue;
                    string? assetDigest = item.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    assets.Add(new ReleaseAsset(name, assetUri, assetDigest));
                }
            }
            return new ReleaseMetadata(version, assets);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or KeyNotFoundException or FormatException or OperationCanceledException)
        {
            // Per-request timeout on inaccessible metadata services is recoverable; explicit
            // user cancellation is handled by the catch above and must still propagate.
            return null;
        }
    }

    private async Task<TerracottaModule> DownloadAndInstallAsync(
        ReleaseInfo release,
        IProgress<TerracottaProvisioningProgress>? progress,
        CancellationToken ct)
    {
        string stagingRoot = Path.Combine(moduleRoot, $".install-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(moduleRoot, $".download-{Guid.NewGuid():N}.tar.gz");
        string target = Path.Combine(moduleRoot, release.Version);
        string backup = Path.Combine(moduleRoot, $".backup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            string actualDigest = await DownloadArchiveAsync(release, archivePath, progress, ct).ConfigureAwait(false);
            if (release.ExpectedSha256 is not null && !actualDigest.Equals(release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Terracotta 安装包 SHA-256 校验失败，下载文件可能损坏或被篡改。");

            progress?.Report(new TerracottaProvisioningProgress("正在解压并检查核心文件…", 92));
            Dictionary<string, FileRecord> files = await ExtractArchiveAsync(release, archivePath, stagingRoot, ct).ConfigureAwait(false);
            var manifest = new InstallationManifest(release.Version, release.Architecture, actualDigest, files);
            await File.WriteAllTextAsync(Path.Combine(stagingRoot, ManifestFileName), JsonSerializer.Serialize(manifest), ct).ConfigureAwait(false);

            if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(stagingRoot, target); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
                throw;
            }
            TryDeleteDirectory(backup);
            var installed = ValidateInstallation(target)
                ?? throw new InvalidDataException("安装完成后，Terracotta 文件校验未通过。");
            progress?.Report(new TerracottaProvisioningProgress($"Terracotta {installed.Version} 已安装。", 100));
            return installed;
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(backup);
        }
    }

    private async Task<string> DownloadArchiveAsync(
        ReleaseInfo release,
        string path,
        IProgress<TerracottaProvisioningProgress>? progress,
        CancellationToken ct)
    {
        Exception? last = null;
        foreach (Uri source in release.Sources)
        {
            try
            {
                TryDeleteFile(path);
                progress?.Report(new TerracottaProvisioningProgress($"正在下载 Terracotta 核心（{source.Host}）…", 0));
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                request.Headers.UserAgent.ParseAdd("SCL-Launcher/1.0");
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                Uri? final = response.RequestMessage?.RequestUri;
                if (final is null || final.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("下载地址不是 HTTPS。");
                if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
                    throw new InvalidDataException("Terracotta 安装包超过允许大小。");

                await using Stream input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                long total = 0;
                long? expectedLength = response.Content.Headers.ContentLength;

                // Close the output stream BEFORE opening the file for hashing. This avoids
                // the Windows sharing violation previously seen when FileShare.None was active.
                await using (var output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                        if (read == 0) break;
                        total += read;
                        if (total > MaximumArchiveBytes)
                            throw new InvalidDataException("Terracotta 安装包超过允许大小。");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        if (expectedLength is > 0)
                        {
                            int percent = Math.Clamp((int)(total * 88d / expectedLength.Value), 1, 88);
                            progress?.Report(new TerracottaProvisioningProgress("正在下载 Terracotta 核心…", percent));
                        }
                        else
                        {
                            progress?.Report(new TerracottaProvisioningProgress("正在下载 Terracotta 核心…", 30));
                        }
                    }
                    if (total == 0)
                        throw new InvalidDataException("下载的 Terracotta 安装包为空。");
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }

                string sha;
                await using (var file = File.OpenRead(path))
                {
                    sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
                }

                // If a proxy served an HTML error page instead of the archive, reject it here
                // and continue to the next configured source rather than aborting installation.
                if (release.ExpectedSha256 is not null
                    && !sha.Equals(release.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"下载源 {source.Host} 返回的文件 SHA-256 不匹配，继续尝试备用源。");
                }

                return sha;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                last = ex;
                TryDeleteFile(path);
            }
        }
        throw new HttpRequestException("Terracotta 核心下载失败：代理源和备用源均不可用。请检查代理网址或网络连接。", last);
    }

    private async Task<Dictionary<string, FileRecord>> ExtractArchiveAsync(
        ReleaseInfo release,
        string archivePath,
        string destinationDirectory,
        CancellationToken ct)
    {
        string expectedExe = $"terracotta-{release.Version}-windows-{release.Architecture}.exe";
        var files = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        await using var raw = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.EntryType is not TarEntryType.RegularFile and not TarEntryType.V7RegularFile)
                throw new InvalidDataException("Terracotta 安装包包含不支持的文件类型。");
            string normalizedPath = entry.Name.Replace('\\', '/');
            string[] pathParts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length == 0 || pathParts.Any(part => part is "." or ".."))
                throw new InvalidDataException("Terracotta 安装包包含不安全路径。");
            // Some tar.gz builds include a parent directory; extract only the two whitelisted leaf names.
            string name = pathParts[^1];
            string outputName = name.Equals(expectedExe, StringComparison.OrdinalIgnoreCase) ? ExecutableFileName
                : name.Equals(RuntimeFileName, StringComparison.OrdinalIgnoreCase) ? RuntimeFileName
                : throw new InvalidDataException($"安装包包含非预期文件：{name}");
            if (entry.Length <= 0 || entry.Length > MaximumExtractedFileBytes || entry.DataStream is null || files.ContainsKey(outputName))
                throw new InvalidDataException("Terracotta 安装包文件大小无效或包含重复文件。");

            string outputPath = Path.Combine(destinationDirectory, outputName);
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920];
                long copied = 0;
                while (true)
                {
                    int read = await entry.DataStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    copied += read;
                    if (copied > MaximumExtractedFileBytes) throw new InvalidDataException("解压文件超过允许大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            string fileHash;
            await using (var file = File.OpenRead(outputPath))
                fileHash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
            files.Add(outputName, new FileRecord(new FileInfo(outputPath).Length, fileHash));
        }
        if (!files.ContainsKey(ExecutableFileName) || !files.ContainsKey(RuntimeFileName) || files.Count != 2)
            throw new InvalidDataException("Terracotta 安装包缺少 EXE 或 VCRUNTIME140.DLL。");
        return files;
    }

    private TerracottaModule? ValidateInstallation(string versionDirectory)
    {
        try
        {
            string manifestPath = Path.Combine(versionDirectory, ManifestFileName);
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Architecture != architecture)
                return null;
            if (!Path.GetFileName(versionDirectory).Equals(manifest.Version, StringComparison.Ordinal)) return null;
            foreach (string name in new[] { ExecutableFileName, RuntimeFileName })
            {
                string file = Path.Combine(versionDirectory, name);
                if (!File.Exists(file)) return null;
                if (!manifest.Files.TryGetValue(name, out FileRecord? expected) || expected is null) return null;
                var info = new FileInfo(file);
                if (info.Length != expected.Size || !ComputeSha256(file).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return new TerracottaModule(manifest.Version, manifest.Architecture, versionDirectory, Path.Combine(versionDirectory, ExecutableFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCL-Launcher/1.0");
        return client;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string? tag)
    {
        string value = tag?.Trim() ?? "";
        if (value.StartsWith('v')) value = value[1..];
        if (value.Length == 0 || value.Length > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
            throw new InvalidDataException("Terracotta 版本号无效。");
        return value;
    }

    private static string? NormalizeSha256(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string value = input.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? input[7..] : input;
        return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    private static void AddHttpsSource(ICollection<Uri> sources, Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Terracotta 下载源必须使用 HTTPS。");
        if (!sources.Contains(uri)) sources.Add(uri);
    }

    private static Version ParseVersion(string value) => Version.TryParse(value.Split('-', 2)[0], out var parsed) ? parsed : new Version(0, 0);
    private static int CompareVersions(string a, string b) => ParseVersion(a).CompareTo(ParseVersion(b));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed record ReleaseInfo(string Version, string Architecture, string AssetName, IReadOnlyList<Uri> Sources, string? ExpectedSha256);
    private sealed record ReleaseMetadata(string Version, IReadOnlyList<ReleaseAsset> Assets);
    private sealed record ReleaseAsset(string Name, Uri DownloadUri, string? Digest);
    private sealed record FileRecord(long Size, string Sha256);
    private sealed record InstallationManifest(string Version, string Architecture, string ArchiveSha256, Dictionary<string, FileRecord> Files);
}
