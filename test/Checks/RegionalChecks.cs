using System.Text;
using IptvPlayer;

public static class RegionalChecks
{
    public static void Run()
    {
        static Epg Parse(string xml)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return Epg.Parse(input);
        }
        static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        static Channel Channel(string name, string group = "") => new() { Name = name, Group = group, Stream = new Uri("https://example.invalid/test") };
        var guide = Parse("""
        <tv>
        <channel id="us"><display-name>US - Test HD</display-name></channel>
        <channel id="tr"><display-name>TR - Test HD</display-name></channel>
        <channel id="a"><display-name>US - Ambiguous HD</display-name></channel>
        <channel id="b"><display-name>US - Ambiguous SD</display-name></channel>
        <channel id="shift"><display-name>RU - Тест +2</display-name></channel>
        <programme channel="us" start="20991020120000 +0000" stop="20991020130000 +0000"><title>US schedule</title></programme>
        <programme channel="tr" start="20991020120000 +0000" stop="20991020130000 +0000"><title>TR schedule</title></programme>
        <programme channel="a" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Ambiguous A</title></programme>
        <programme channel="b" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Ambiguous B</title></programme>
        <programme channel="shift" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Shifted</title></programme>
        </tv>
        """);
        var placeholders = Parse("""
        <tv><channel id="empty"><display-name>US - Empty</display-name></channel>
        <programme channel="empty" start="20991020120000 +0000" stop="20991020130000 +0000"><title> No Data </title></programme></tv>
        """);
        Check(!placeholders.Upcoming("empty").Any(), "No Data placeholders are not programmes");
        var us = Channel("Test FHD US");
        var tr = Channel("Test HD", "türk");
        var unknown = Channel("Test HD");
        var ambiguous = Channel("Ambiguous FHD US");
        var shifted = Channel("Тест +2");
        var unshifted = Channel("Тест");
        var local = new[] { us, tr, unknown, ambiguous, shifted, unshifted };
        var combined = new Epg();
        combined.AddRegional(guide, local);
        Check(combined.Match(us) == "regional:us" && combined.Upcoming(combined.Match(us)!).Single().Title == "US schedule", "Country suffix and FHD normalization");
        Check(combined.Match(tr) == "regional:tr", "Country group isolates broadcasts");
        Check(combined.Match(unknown) is null, "Unknown country is not guessed for English names");
        Check(combined.Match(ambiguous) is null, "Ambiguous HD/SD candidates are skipped");
        Check(combined.Match(shifted) == "regional:shift" && combined.Match(unshifted) is null, "Time shift is preserved");
        var primary = Parse("""
        <tv><channel id="primary"><display-name>Test FHD US</display-name></channel>
        <programme channel="primary" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Primary schedule</title></programme></tv>
        """);
        primary.AddRegional(guide, local);
        Check(primary.Match(us) == "primary", "Working primary schedule has priority");
        var empty = Parse("<tv><channel id=\"empty\"><display-name>Test FHD US</display-name></channel></tv>");
        empty.AddRegional(guide, local);
        Check(empty.Match(us) == "regional:us", "Regional schedule fills an empty primary channel");
        combined.AddRegional(new Epg(), local);
        Check(combined.Match(us) is null, "Reattaching removes obsolete regional matches");
        Console.WriteLine("Regional EPG: country matching, ambiguity, time shift, priority and fallback: OK");
    }
}
