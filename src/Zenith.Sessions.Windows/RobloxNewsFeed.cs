using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed class RobloxNewsFeed(HttpClient http) : IPublicRobloxNewsFeed
{
    public const int MaximumBytes = 256 * 1024;
    private static readonly Uri Endpoint = new("https://devforum.roblox.com/c/updates/announcements/36.rss");
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(100);
    public static HttpClient CreateAnonymousHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 16, MaxConnectionsPerServer = 1
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<PublicNewsResult> ReadAsync(CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ct.ThrowIfCancellationRequested();
        if (http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie")
            || http.DefaultRequestHeaders.Contains("Proxy-Authorization") || http.DefaultRequestHeaders.Contains("x-api-key"))
            throw new ArgumentException("News requires a dedicated anonymous client.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.UserAgent.ParseAdd("ZenithSessions/0.50");
            request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != Endpoint)
                return Failure(PublicNewsStatus.InvalidResponse);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure(PublicNewsStatus.RateLimited);
            if (response.StatusCode != HttpStatusCode.OK) return Failure(PublicNewsStatus.Unavailable);
            if (response.Content.Headers.ContentType?.MediaType is not ("application/rss+xml" or "application/atom+xml" or "application/xml" or "text/xml")
                || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > MaximumBytes)
                return Failure(PublicNewsStatus.InvalidResponse);
            using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] bytes = new byte[4096];
            int read;
            while ((read = await source.ReadAsync(bytes, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) return Failure(PublicNewsStatus.InvalidResponse);
                buffer.Write(bytes, 0, read);
            }
            buffer.Position = 0;
            using var reader = XmlReader.Create(buffer, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes,
                MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            deadline.Token.ThrowIfCancellationRequested();
            var items = Parse(document);
            deadline.Token.ThrowIfCancellationRequested();
            return new(PublicNewsStatus.Available, items, items.Count == 0 ? "The feed returned no readable announcements." : "DevForum announcements. Loaded on request; links open in your browser.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Failure(PublicNewsStatus.Unavailable); }
        catch (Exception ex) when (ex is XmlException or RegexMatchTimeoutException) { return Failure(PublicNewsStatus.InvalidResponse); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return Failure(PublicNewsStatus.Unavailable); }
    }

    private static IReadOnlyList<PublicNewsItem> Parse(XDocument document)
    {
        var root = document.Root ?? throw new XmlException("Missing feed root.");
        XNamespace atom = "http://www.w3.org/2005/Atom";
        bool rss = root.Name == "rss", isAtom = root.Name == atom + "feed";
        if (!rss && !isAtom) throw new XmlException("Unsupported feed.");
        var rows = rss ? root.Element("channel")?.Elements("item") ?? throw new XmlException("Missing channel.") : root.Elements(atom + "entry");
        var items = new List<PublicNewsItem>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.Take(100))
        {
            string? address = rss ? (string?)row.Element("link") : (string?)row.Elements(atom + "link")
                .FirstOrDefault(link => (string?)link.Attribute("rel") is null or "alternate")?.Attribute("href");
            if (address is not { Length: <= 2048 } || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
                || !PublicNewsLinks.IsAllowedArticle(uri) || !seen.Add(uri.AbsoluteUri)) continue;
            string title = PlainText((string?)row.Element(rss ? "title" : atom + "title"), 160);
            if (title.Length == 0) continue;
            var summary = rss ? row.Element("description") : row.Element(atom + "summary") ?? row.Element(atom + "content");
            string text = PlainText(summary?.HasElements == true ? string.Concat(summary.Nodes().Select(n => n.ToString())) : summary?.Value, 1200);
            string? date = (string?)row.Element(rss ? "pubDate" : atom + "published") ?? (string?)row.Element(atom + "updated");
            DateTimeOffset? published = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
            items.Add(new(title, text, uri, published));
            if (items.Count == 30) break;
        }
        return items.AsReadOnly();
    }

    private static string PlainText(string? text, int limit)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"<(script|style)\b[^>]*>.*?</\1\s*>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, RegexBudget);
        text = Regex.Replace(text, "<[^>]*>", " ", RegexOptions.CultureInvariant, RegexBudget);
        text = Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, RegexBudget).Trim();
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length <= limit ? text : text[..limit] + "…";
    }

    private static PublicNewsResult Failure(PublicNewsStatus status) => new(status, [], status switch
    {
        PublicNewsStatus.RateLimited => "The news service asked Zenith to wait. Refresh later; no automatic retry was made.",
        PublicNewsStatus.InvalidResponse => "The news service did not return a supported RSS or Atom feed. No page was scraped.",
        _ => "Announcements are unavailable. Refresh later or open the official website in your browser."
    });
}
