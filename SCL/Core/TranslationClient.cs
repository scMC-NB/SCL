using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SCL.Core
{
    public class TranslationClient
    {
        private readonly HttpClient http;

        public TranslationClient()
        {
            http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SCL-Launcher/1.0");
        }

        /// <summary>
        /// 中文翻译成英文。
        /// 这是辅助搜索功能。
        /// 翻译失败时返回 null，不影响 Modrinth 搜索。
        /// </summary>
        public async Task<string?> TranslateChineseToEnglishAsync(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!ContainsChinese(text))
            {
                return text.Trim();
            }

            try
            {
                string url =
                    "https://api.mymemory.translated.net/get" +
                    "?q=" +
                    Uri.EscapeDataString(text) +
                    "&langpair=zh-CN%7Cen";

                using HttpResponseMessage response =
                    await http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json =
                    await response.Content.ReadAsStringAsync();

                using JsonDocument doc =
                    JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty(
                    "responseData",
                    out JsonElement responseData))
                {
                    return null;
                }

                if (!responseData.TryGetProperty(
                    "translatedText",
                    out JsonElement translated))
                {
                    return null;
                }

                string? result =
                    translated.GetString();

                if (string.IsNullOrWhiteSpace(result))
                {
                    return null;
                }

                return result.Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断是否包含中文字符。
        /// </summary>
        public static bool ContainsChinese(
            string text)
        {
            foreach (char c in text)
            {
                if (c >= 0x4E00 &&
                    c <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }
    }
}