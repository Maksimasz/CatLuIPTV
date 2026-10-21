using System.Text;
using IptvPlayer;

public static class ExtraChecks
{
    public static void Run()
    {
        static void Check(bool condition, string label) { if (!condition) throw new Exception(label); }
        static Epg Parse(string xml)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return Epg.Parse(input);
        }
        static Channel Channel(string name) => new() { Name = name, Stream = new Uri("https://example.invalid/extra") };
        var channel9 = Channel("Channel 9 FHD IL");
        var israel = Parse("""
        <tv><channel id="ישראלפלוס.il"/>
        <programme channel="ישראלפלוס.il" start="20991020120000 +0000" stop="20991020130000 +0000"><title>תוכנית</title></programme></tv>
        """);
        var iptvx = Parse("""
        <tv><channel id="nine"><display-name>9 канал [IL]</display-name></channel>
        <channel id="us"><display-name>Example [US]</display-name></channel>
        <programme channel="nine" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Русская передача</title></programme>
        <programme channel="us" start="20991020120000 +0000" stop="20991020130000 +0000"><title>English programme</title></programme></tv>
        """);
        var tvteam = Parse("""
        <tv><channel id="nine"><display-name>IL: Channel 9 HD</display-name></channel>
        <programme channel="nine" start="20991020120000 +0000" stop="20991020130000 +0000"><title>תוכנית אחרת</title></programme></tv>
        """);
        var us = Channel("Example HD US");
        var tr = Channel("Example HD TR");
        var local = new[] { channel9, us, tr };
        var combined = new Epg();
        combined.AddIsrael(israel, local);
        combined.AddRegional(tvteam, local, "tvteam");
        Check(combined.Match(channel9) == "israel:ישראלפלוס.il", "Hebrew TVTeam does not replace existing Israel schedule");
        combined.AddRegional(iptvx, local, "iptvx");
        Check(combined.Match(channel9) == "iptvx:nine" && combined.HasRussianSchedule("iptvx:nine"), "Russian Israel schedule takes priority");
        Check(combined.Upcoming("iptvx:nine").Single().Title == "Русская передача", "Russian programme displayed without changing primary data");
        Check(combined.Match(us) == "iptvx:us" && combined.Match(tr) is null, "Bracket country codes and country isolation");
        combined.AddRegional(new Epg(), local, "iptvx");
        Check(combined.Match(channel9) == "israel:ישראלפלוס.il", "Absent Russian schedule keeps Hebrew fallback");
        var expired = Parse("""
        <tv><channel id="nine"><display-name>9 канал [IL]</display-name></channel>
        <programme channel="nine" start="20001020120000 +0000" stop="20001020130000 +0000"><title>Старая передача</title></programme></tv>
        """);
        combined.AddRegional(expired, local, "iptvx");
        Check(combined.Match(channel9) == "israel:ישראלפלוס.il", "Expired Russian schedule does not displace Hebrew");
        var primary = Parse("""
        <tv><channel id="main"><display-name>Example HD US</display-name></channel>
        <programme channel="main" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Primary programme</title></programme></tv>
        """);
        primary.AddRegional(iptvx, local, "iptvx");
        Check(primary.Match(us) == "main", "Extra sources preserve working non-Israeli schedules");
        var variant = Parse("""
        <tv><channel id="he"><display-name>IL: HOT Cinema 1 HD</display-name></channel>
        <channel id="ru"><display-name>IL: HOT Cinema 1 HD (ru)</display-name></channel>
        <programme channel="he" start="20991020120000 +0000" stop="20991020130000 +0000"><title>סרט</title></programme>
        <programme channel="ru" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Русский фильм</title></programme></tv>
        """);
        var hot = Channel("HOT Cinema 1 HD IL");
        var variants = new Epg();
        variants.AddRegional(variant, new[] { hot }, "tvteam");
        Check(variants.Match(hot) == "tvteam:ru", "Explicit Russian variant is used only with actual Russian programmes");
        Console.WriteLine("Extra sources: colon/bracket countries, Russian priority, stale fallback and ru variants: OK");
    }
}
