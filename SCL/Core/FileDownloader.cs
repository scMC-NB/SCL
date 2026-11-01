using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class FileDownloader
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");

            return client;
        }

        public async Task DownloadAsync(
            string url,
            string savePath,
            Action<long, long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException(
                    "下载地址为空。",
                    nameof(url));
            }

            if (string.IsNullOrWhiteSpace(savePath))
            {
                throw new ArgumentException(
                    "保存路径为空。",
                    nameof(savePath));
            }

            string? directory =
                Path.GetDirectoryName(savePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(savePath))
            {
                return;
            }

            string tempPath =
                savePath + ".download";

            Exception? lastException = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using HttpResponseMessage response =
                        await Http.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }

                    long total =
                        response.Content.Headers.ContentLength ?? -1;

                    await using Stream input =
                        await response.Content.ReadAsStreamAsync(
                            cancellationToken);

                    await using FileStream output =
                        new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            useAsync: true);

                    byte[] buffer = new byte[81920];
                    long current = 0;

                    while (true)
                    {
                        int read =
                            await input.ReadAsync(
                                buffer.AsMemory(
                                    0,
                                    buffer.Length),
                                cancellationToken);

                        if (read <= 0)
                        {
                            break;
                        }

                        await output.WriteAsync(
                            buffer.AsMemory(
                                0,
                                read),
                            cancellationToken);

                        current += read;

                        progress?.Invoke(
                            current,
                            total);
                    }

                    await output.FlushAsync(
                        cancellationToken);

                    output.Close();

                    if (File.Exists(savePath))
                    {
                        File.Delete(savePath);
                    }

                    File.Move(
                        tempPath,
                        savePath);

                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (File.Exists(tempPath))
                    {
                        try
                        {
                            File.Delete(tempPath);
                        }
                        catch
                        {
                            // 忽略临时文件清理失败
                        }
                    }

                    if (attempt < 3)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(attempt),
                            cancellationToken);
                    }
                }
            }

            throw new Exception(
                $"下载失败：\n{url}\n\n{lastException?.Message}",
                lastException);
        }
    }
}