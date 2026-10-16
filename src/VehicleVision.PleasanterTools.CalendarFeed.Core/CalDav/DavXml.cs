using System.Xml;
using System.Xml.Linq;

namespace VehicleVision.PleasanterTools.CalendarFeed.CalDav;

/// <summary>WebDAV / CalDAV の XML の名前空間と、要求本文の読み取り（Issue #85）。</summary>
/// <remarks>
/// 一次情報: RFC 4918（WebDAV）、RFC 4791（CalDAV）。<c>getctag</c> は Apple の拡張（calendarserver.org の名前空間）。
/// <b>要求の XML は信用しない。</b>DTD と外部実体は禁止し、大きさにも上限を置く（XXE・膨張の防止）。
/// </remarks>
public static class DavXml
{
    /// <summary>WebDAV（<c>DAV:</c>）。</summary>
    public static readonly XNamespace D = "DAV:";

    /// <summary>CalDAV。</summary>
    public static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";

    /// <summary>Apple の拡張（<c>getctag</c>）。</summary>
    public static readonly XNamespace CS = "http://calendarserver.org/ns/";

    /// <summary>要求本文の上限（バイト）。</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>要求本文を読む。空なら <c>null</c>。DTD を含む・壊れている・大きすぎるときは <see cref="DavXmlException"/>。</summary>
    public static XDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (body.Length > MaxBodyBytes)
        {
            throw new DavXmlException("The request body is too large.");
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBodyBytes,
            };
            using var reader = XmlReader.Create(new StringReader(body), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new DavXmlException("The request body is not well-formed XML: " + ex.Message, ex);
        }
    }
}

/// <summary>要求の XML が読めない（400 にする）。</summary>
public sealed class DavXmlException : Exception
{
    public DavXmlException(string message) : base(message)
    {
    }

    public DavXmlException(string message, Exception inner) : base(message, inner)
    {
    }
}
