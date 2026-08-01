using System.Globalization;
using System.Xml.Linq;

namespace VideoHome.Services;

// Writes the MPD that points a browser at our own byte-range proxy.
//
// The shape is the on-demand profile: one Period, a video AdaptationSet holding the whole
// resolution ladder as switchable Representations, and an audio AdaptationSet with one. Each
// Representation carries a SegmentBase naming the byte ranges of its init segment and its
// sidx, which is what lets the player seek by asking for exactly the bytes it needs.
//
// Every number is formatted with the invariant culture on purpose. Under a German locale the
// default would write "PT635,000S", which no player parses.
public static class DashManifestBuilder
{
    private static readonly XNamespace Mpd = "urn:mpeg:dash:schema:mpd:2011";

    public static string Build(DashSet set)
    {
        var duration = Iso8601(set.Duration);

        var video = new XElement(Mpd + "AdaptationSet",
            new XAttribute("contentType", "video"),
            new XAttribute("mimeType", "video/mp4"),
            new XAttribute("segmentAlignment", "true"),
            new XAttribute("subsegmentAlignment", "true"),
            new XAttribute("startWithSAP", "1"),
            new XAttribute("subsegmentStartsWithSAP", "1"),
            new XAttribute("maxWidth", Num(set.Video.Max(r => r.Width))),
            new XAttribute("maxHeight", Num(set.Video.Max(r => r.Height))),
            new XAttribute("maxFrameRate", Num(set.Video.Max(r => r.Framerate))),
            // Highest first: the ladder reads top-down the way the qualities are usually
            // listed, and dash.js sorts internally regardless.
            set.Video.OrderByDescending(r => r.Height).Select(r => new XElement(Mpd + "Representation",
                new XAttribute("id", r.Id),
                new XAttribute("codecs", r.Codec),
                new XAttribute("bandwidth", Num(r.Bandwidth)),
                new XAttribute("width", Num(r.Width)),
                new XAttribute("height", Num(r.Height)),
                new XAttribute("frameRate", Num(r.Framerate)),
                new XAttribute("sar", "1:1"),
                SegmentBase(r))));

        var audio = new XElement(Mpd + "AdaptationSet",
            new XAttribute("contentType", "audio"),
            new XAttribute("mimeType", "audio/mp4"),
            new XAttribute("segmentAlignment", "true"),
            new XAttribute("startWithSAP", "1"),
            new XAttribute("lang", "und"),
            new XElement(Mpd + "Representation",
                new XAttribute("id", set.Audio.Id),
                new XAttribute("codecs", set.Audio.Codec),
                new XAttribute("bandwidth", Num(set.Audio.Bandwidth)),
                // A hint only - the authoritative rate is in the init segment's moov, and
                // YoutubeExplode does not surface it. Every AAC track YouTube serves for
                // these itags is 44.1 kHz.
                new XAttribute("audioSamplingRate", "44100"),
                new XElement(Mpd + "AudioChannelConfiguration",
                    new XAttribute("schemeIdUri", "urn:mpeg:dash:23003:3:audio_channel_configuration:2011"),
                    new XAttribute("value", "2")),
                SegmentBase(set.Audio)));

        var mpd = new XElement(Mpd + "MPD",
            new XAttribute("profiles", "urn:mpeg:dash:profile:isoff-on-demand:2011"),
            new XAttribute("type", "static"),
            new XAttribute("mediaPresentationDuration", duration),
            new XAttribute("minBufferTime", "PT1.5S"),
            new XElement(Mpd + "Period",
                new XAttribute("id", "0"),
                new XAttribute("duration", duration),
                video,
                audio));

        return new XDeclaration("1.0", "utf-8", null) + Environment.NewLine +
               new XDocument(mpd).ToString();
    }

    // BaseURL is relative to the manifest's own URL. The manifest is served from
    // /youtube/{id}/manifest.mpd, so a bare "v1080" resolves to /youtube/{id}/v1080 - which
    // means the same manifest works whatever host or scheme it is fetched over.
    private static IEnumerable<XElement> SegmentBase(DashRepresentation r) =>
    [
        new XElement(Mpd + "BaseURL", r.Id),
        new XElement(Mpd + "SegmentBase",
            new XAttribute("indexRange", r.Index.ToString()),
            new XAttribute("indexRangeExact", "true"),
            new XElement(Mpd + "Initialization",
                new XAttribute("range", r.Init.ToString()))),
    ];

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Iso8601(TimeSpan duration) =>
        "PT" + duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "S";
}
