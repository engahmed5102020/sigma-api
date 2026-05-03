using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sigma.Api;

/// <summary>رفع ملفات إلى Dropbox باستخدام refresh token (إعدادات في appsettings → Dropbox).</summary>
internal static class DropboxStorage
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? s_accessToken;
    private static DateTimeOffset s_accessTokenExpiresUtc = DateTimeOffset.MinValue;

    public static async Task<(bool Ok, string Message)> UploadStationImageAsync(
        IConfiguration config,
        HttpClient http,
        int stationCode,
        bool isBefore,
        string originalFileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var appKey = config["Dropbox:AppKey"]?.Trim() ?? "";
        var appSecret = config["Dropbox:AppSecret"]?.Trim() ?? "";
        var refreshToken = config["Dropbox:RefreshToken"]?.Trim() ?? "";
        var root = (config["Dropbox:RootNamespacePath"] ?? config["Dropbox:RootPath"] ?? "/SigmaUploads").Trim();

        if (string.IsNullOrEmpty(appKey) ||
            string.IsNullOrEmpty(appSecret) ||
            string.IsNullOrEmpty(refreshToken))
        {
            return (false, "Dropbox غير مضبوط في الخادم (Dropbox:AppKey / AppSecret / RefreshToken).");
        }

        if (stationCode <= 0 || content.Length == 0)
        {
            return (false, "كود المحطة أو الملف غير صالح.");
        }

        var accessToken = await GetAccessTokenAsync(config, http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(accessToken))
        {
            return (false, "تعذر الحصول على رمز Dropbox.");
        }

        var safeName = SanitizeFileName(originalFileName);
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "image.jpg";
        }

        var folder = isBefore ? "Befor" : "After";
        var path = $"{root.TrimEnd('/')}/{stationCode}/{folder}/{safeName}";

        var apiArg = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["mode"] = "add",
                ["autorename"] = true,
                ["mute"] = true,
            });

        using var uploadReq = new HttpRequestMessage(
            HttpMethod.Post,
            "https://content.dropboxapi.com/2/files/upload");
        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        uploadReq.Headers.Add("Dropbox-API-Arg", apiArg);
        uploadReq.Content = new ByteArrayContent(content);
        uploadReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var uploadResp = await http.SendAsync(uploadReq, cancellationToken).ConfigureAwait(false);
        if (uploadResp.IsSuccessStatusCode)
        {
            return (true, path);
        }

        var errBody = await uploadResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (false, $"Dropbox {uploadResp.StatusCode}: {errBody}");
    }

    private static async Task<string?> GetAccessTokenAsync(
        IConfiguration config,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(s_accessToken) &&
                s_accessTokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return s_accessToken;
            }

            var appKey = config["Dropbox:AppKey"]?.Trim() ?? "";
            var appSecret = config["Dropbox:AppSecret"]?.Trim() ?? "";
            var refreshToken = config["Dropbox:RefreshToken"]?.Trim() ?? "";

            using var form = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", appKey),
                new KeyValuePair<string, string>("client_secret", appSecret),
            ]);

            using var resp = await http
                .PostAsync("https://api.dropboxapi.com/oauth2/token", form, cancellationToken)
                .ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                s_accessToken = null;
                s_accessTokenExpiresUtc = DateTimeOffset.MinValue;
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec)
                ? sec
                : 14400;

            s_accessToken = token;
            s_accessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120));
            return s_accessToken;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static string SanitizeFileName(string name)
    {
        var baseName = Path.GetFileName(name.Trim());
        if (string.IsNullOrEmpty(baseName))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(baseName.Length);
        foreach (var c in baseName)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }

    /// <summary>قائمة روابط مؤقتة لصور محطة في مجلد Befor أو After (Dropbox).</summary>
    public static async Task<(bool Ok, List<string> Urls, string Message)> ListStationImageTemporaryLinksAsync(
        IConfiguration config,
        HttpClient http,
        int stationCode,
        bool isBefore,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var appKey = config["Dropbox:AppKey"]?.Trim() ?? "";
        var appSecret = config["Dropbox:AppSecret"]?.Trim() ?? "";
        var refreshToken = config["Dropbox:RefreshToken"]?.Trim() ?? "";
        if (string.IsNullOrEmpty(appKey) ||
            string.IsNullOrEmpty(appSecret) ||
            string.IsNullOrEmpty(refreshToken))
        {
            return (false, [], "Dropbox غير مضبوط في الخادم.");
        }

        if (stationCode <= 0)
        {
            return (false, [], "كود المحطة غير صالح.");
        }

        maxCount = Math.Clamp(maxCount, 1, 99);

        var accessToken = await GetAccessTokenAsync(config, http, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(accessToken))
        {
            return (false, [], "تعذر الحصول على رمز Dropbox.");
        }

        var root = (config["Dropbox:RootNamespacePath"] ?? config["Dropbox:RootPath"] ?? "/SigmaUploads").Trim();
        var folder = isBefore ? "Befor" : "After";
        var folderPath = $"{root.TrimEnd('/')}/{stationCode}/{folder}";

        using var listReq = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.dropboxapi.com/2/files/list_folder");
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var listJson = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["path"] = folderPath,
                ["recursive"] = false,
                ["include_media_info"] = false,
            });
        listReq.Content = new StringContent(listJson, Encoding.UTF8, "application/json");

        using var listResp = await http.SendAsync(listReq, cancellationToken).ConfigureAwait(false);
        var listBody = await listResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!listResp.IsSuccessStatusCode)
        {
            // مجلد غير موجود أو فارغ — نعيد قائمة فارغة دون خطأ للواجهة.
            return (true, [], string.Empty);
        }

        using var listDoc = JsonDocument.Parse(listBody);
        if (!listDoc.RootElement.TryGetProperty("entries", out var entries))
        {
            return (true, [], string.Empty);
        }

        var filePaths = new List<string>();
        foreach (var e in entries.EnumerateArray())
        {
            if (!e.TryGetProperty(".tag", out var tag) || tag.GetString() != "file")
            {
                continue;
            }

            if (!e.TryGetProperty("path_display", out var pEl))
            {
                continue;
            }

            var p = pEl.GetString();
            if (string.IsNullOrEmpty(p))
            {
                continue;
            }

            var lower = p.ToLowerInvariant();
            if (lower.EndsWith(".jpg", StringComparison.Ordinal) ||
                lower.EndsWith(".jpeg", StringComparison.Ordinal) ||
                lower.EndsWith(".png", StringComparison.Ordinal) ||
                lower.EndsWith(".webp", StringComparison.Ordinal))
            {
                filePaths.Add(p);
            }
        }

        filePaths.Sort(StringComparer.OrdinalIgnoreCase);

        var urls = new List<string>();
        foreach (var path in filePaths.Take(maxCount))
        {
            using var linkReq = new HttpRequestMessage(
                HttpMethod.Post,
                "https://api.dropboxapi.com/2/files/get_temporary_link");
            linkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var linkJson = JsonSerializer.Serialize(new Dictionary<string, string?> { ["path"] = path });
            linkReq.Content = new StringContent(linkJson, Encoding.UTF8, "application/json");
            using var linkResp = await http.SendAsync(linkReq, cancellationToken).ConfigureAwait(false);
            var linkBody = await linkResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!linkResp.IsSuccessStatusCode)
            {
                continue;
            }

            using var linkDoc = JsonDocument.Parse(linkBody);
            if (linkDoc.RootElement.TryGetProperty("link", out var linkEl))
            {
                var url = linkEl.GetString();
                if (!string.IsNullOrEmpty(url))
                {
                    urls.Add(url);
                }
            }
        }

        return (true, urls, string.Empty);
    }
}
