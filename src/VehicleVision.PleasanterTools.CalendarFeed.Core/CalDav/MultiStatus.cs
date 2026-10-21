using System.Xml.Linq;

namespace VehicleVision.PleasanterTools.CalendarFeed.CalDav;

/// <summary>WebDAV の 207 Multi-Status の応答を組み立てる（RFC 4918 13 章）。</summary>
public sealed class MultiStatus
{
    private readonly XElement _root = new(DavXml.D + "multistatus");

    /// <summary>1 つの資源の応答を足す。<paramref name="found"/> は 200、<paramref name="notFound"/> は 404 のプロパティ。</summary>
    public MultiStatus Add(string href, IEnumerable<XElement> found, IEnumerable<XName>? notFound = null)
    {
        var response = new XElement(DavXml.D + "response", new XElement(DavXml.D + "href", href));
        var foundList = found.ToList();
        if (foundList.Count > 0)
        {
            response.Add(PropStat(foundList, "HTTP/1.1 200 OK"));
        }

        var missing = notFound?.Select(n => new XElement(n)).ToList() ?? [];
        if (missing.Count > 0)
        {
            response.Add(PropStat(missing, "HTTP/1.1 404 Not Found"));
        }

        if (foundList.Count == 0 && missing.Count == 0)
        {
            response.Add(PropStat([], "HTTP/1.1 200 OK"));
        }

        _root.Add(response);
        return this;
    }

    /// <summary>プロパティを持たない応答（<c>calendar-multiget</c> で見つからなかった href など）を足す。</summary>
    public MultiStatus AddStatus(string href, string status)
    {
        _root.Add(new XElement(
            DavXml.D + "response",
            new XElement(DavXml.D + "href", href),
            new XElement(DavXml.D + "status", status)));
        return this;
    }

    /// <summary>XML の文字列にする（UTF-8 の宣言付き）。</summary>
    public string ToXml()
    {
        _root.SetAttributeValue(XNamespace.Xmlns + "d", DavXml.D.NamespaceName);
        _root.SetAttributeValue(XNamespace.Xmlns + "c", DavXml.C.NamespaceName);
        _root.SetAttributeValue(XNamespace.Xmlns + "cs", DavXml.CS.NamespaceName);
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), _root);
        return doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement PropStat(IEnumerable<XElement> props, string status) =>
        new(DavXml.D + "propstat", new XElement(DavXml.D + "prop", props), new XElement(DavXml.D + "status", status));
}
