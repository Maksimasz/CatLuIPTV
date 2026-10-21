using System.Text;
using IptvPlayer;

public static class BcuChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static Epg Parse(string xml)
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            return Epg.Parse(input);
        }
        Check(Epg.IsPlaceholderTitle(" Программа не найдена "), "Russian missing-programme placeholder is excluded");
        var channel = new Channel { Name = "Test HD US", Stream = new Uri("https://example.invalid/test") };
        var bcu = Parse("""
        <tv><channel id="same"><display-name>Test HD US</display-name></channel>
        <programme channel="same" start="20991020120000 +0000" stop="20991020130000 +0000"><title>BCU programme</title></programme></tv>
        """);
        var empty = Parse("<tv><channel id=\"same\"><display-name>Test HD US</display-name></channel></tv>");
        empty.AddBcu(bcu, new[] { channel });
        Check(empty.Match(channel) == "bcu:same" && empty.Upcoming("bcu:same").Single().Title == "BCU programme", "BCU fills empty schedule without ID collisions");
        var primary = Parse("""
        <tv><channel id="same"><display-name>Test HD US</display-name></channel>
        <programme channel="same" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Primary programme</title></programme></tv>
        """);
        primary.AddBcu(bcu, new[] { channel });
        Check(primary.Match(channel) == "same" && primary.Upcoming("same").Single().Title == "Primary programme", "BCU preserves working primary schedule");
        var regional = Parse("""
        <tv><channel id="same"><display-name>US - Test HD</display-name></channel>
        <programme channel="same" start="20991020120000 +0000" stop="20991020130000 +0000"><title>Regional programme</title></programme></tv>
        """);
        empty.AddRegional(regional, new[] { channel });
        Check(empty.Match(channel) == "regional:same", "Regional source retains priority over BCU");
        empty.AddRegional(new Epg(), new[] { channel });
        Check(empty.Match(channel) == "bcu:same", "BCU takes over when regional schedule is unavailable");
        var otherRegion = new Channel { Name = "Test HD IL", Stream = channel.Stream };
        empty.AddBcu(bcu, new[] { channel, otherRegion });
        Check(empty.Match(otherRegion) is null, "BCU does not guess a different country suffix");
        empty.AddBcu(new Epg(), new[] { channel });
        Check(empty.Match(channel) == "same" && !empty.Upcoming("same").Any(), "Obsolete BCU mappings are cleared");
        Console.WriteLine("BCU: exact matching, priority, fallback and ID isolation: OK");
    }
}
