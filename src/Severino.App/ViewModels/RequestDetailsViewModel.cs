using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Severino.App.Resources;
using Severino.Proxy;

namespace Severino.App.ViewModels;

/// <summary>A label and its value on the General tab.</summary>
public sealed record DetailLine(string Label, string Value);

/// <summary>A header as shown: secret values masked until asked.</summary>
public sealed record HeaderLine(string Name, string Value, string Shown);

/// <summary>The side panel of the request log: one request, its headers and its bodies.</summary>
public sealed partial class RequestDetailsViewModel : ObservableObject
{
    /// <summary>Credentials: masked on screen. Copying as curl still carries them, as the request did.</summary>
    private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    };

    private const string Mask = "••••••••";

    public RequestDetailsViewModel(RequestItemViewModel item, string? routeName, bool bodiesOn)
    {
        Item = item;
        var entry = item.Entry;
        var details = entry.Details;

        Title = $"{item.Method} {entry.PathAndQuery}";
        Url = item.Url;
        IsHttp = details is not null;
        CanReplay = RequestExport.CanReplay(entry);

        var general = new List<DetailLine>
        {
            new(Strings.Requests_Time, entry.Time.ToString("G", CultureInfo.CurrentCulture)),
            new("Status", item.StatusText),
            new(Strings.Requests_Duration, item.DurationText),
        };
        if (routeName is not null)
            general.Add(new(Strings.Requests_Route, routeName));
        if (details is not null)
        {
            general.Add(new(Strings.Requests_Target, entry.FromProxy || details.Target is null ? Strings.Requests_AnsweredBySeverino : details.Target));
            general.Add(new(Strings.Requests_SentBytes, Size(details.RequestBytes)));
            general.Add(new(Strings.Requests_ReceivedBytes, Size(details.ResponseBytes)));
        }
        General = general;

        HasHidden = details is not null && details.RequestHeaders.Concat(details.ResponseHeaders).Any(h => Secret.Contains(h.Key));
        HeadersCutText = details?.HeadersCut == true ? Strings.Requests_HeadersCut : null;
        (RequestBodyText, RequestBodyNote) = Body(details, response: false, bodiesOn);
        (ResponseBodyText, ResponseBodyNote) = Body(details, response: true, bodiesOn);
        ShowHeaders();
    }

    public RequestItemViewModel Item { get; }

    public string Title { get; }

    public string Url { get; }

    /// <summary>False for a service route's TCP connection: no headers, no body.</summary>
    public bool IsHttp { get; }

    public bool CanReplay { get; }

    public IReadOnlyList<DetailLine> General { get; }

    [ObservableProperty]
    public partial IReadOnlyList<HeaderLine> RequestHeaders { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<HeaderLine> ResponseHeaders { get; private set; } = [];

    public bool HasHidden { get; }

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    partial void OnShowHiddenChanged(bool value) => ShowHeaders();

    public string? HeadersCutText { get; }

    public string? RequestBodyText { get; }

    public string? RequestBodyNote { get; }

    public string? ResponseBodyText { get; }

    public string? ResponseBodyNote { get; }

    /// <summary>"Mandada de novo: 200." or the error, under the buttons.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    private void ShowHeaders()
    {
        var details = Item.Entry.Details;
        RequestHeaders = Lines(details?.RequestHeaders);
        ResponseHeaders = Lines(details?.ResponseHeaders);
    }

    private IReadOnlyList<HeaderLine> Lines(IReadOnlyList<KeyValuePair<string, string>>? headers) =>
        [.. (headers ?? []).Select(h => new HeaderLine(h.Key, h.Value, !ShowHidden && Secret.Contains(h.Key) ? Mask : h.Value))];

    /// <summary>The text to show, or a note saying why there is none.</summary>
    private static (string? Text, string? Note) Body(RequestDetails? details, bool response, bool bodiesOn)
    {
        if (details is null)
            return (null, null);
        var bytes = response ? details.ResponseBytes : details.RequestBytes;
        var type = details.Header(response, "Content-Type");
        if (bytes == 0)
            return (null, Strings.Requests_BodyEmpty);
        var captured = response ? details.ResponseBody : details.RequestBody;
        if (captured is null)
        {
            if (!Proxy.CapturedBodies.IsText(type))
                return (null, string.Format(Strings.Requests_BodyNotText, type, Size(bytes)));
            return (null, bodiesOn ? Strings.Requests_BodyNotKept : Strings.Requests_BodyOff);
        }

        var encoding = details.Header(response, "Content-Encoding");
        if (Proxy.CapturedBodies.Decode(captured, encoding) is not { } decoded)
            return (null, string.Format(Strings.Requests_BodyUnreadable, encoding));
        var text = Encoding.UTF8.GetString(decoded);
        return (Pretty(text, type), captured.Cut ? string.Format(Strings.Requests_BodyCut, Size(bytes)) : null);
    }

    /// <summary>JSON indented; anything else, or JSON cut in half, as it came.</summary>
    private static string Pretty(string text, string? type)
    {
        var trimmed = text.TrimStart();
        var looksJson = type?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || trimmed.StartsWith('{') || trimmed.StartsWith('[');
        if (!looksJson)
            return text;
        try
        {
            using var document = JsonDocument.Parse(text);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
                document.WriteTo(writer);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Format(CultureInfo.CurrentCulture, Strings.Common_SizeBytes, bytes),
        < 1024 * 1024 => string.Format(CultureInfo.CurrentCulture, Strings.Common_SizeKb, bytes / 1024.0),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Common_SizeMb, bytes / (1024.0 * 1024)),
    };
}
