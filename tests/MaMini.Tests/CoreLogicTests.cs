using System.Net;
using System.Text;
using MaMini.Core.Discovery;
using MaMini.Core.Geometry;
using MaMini.Core.Input;
using MaMini.Core.Settings;

namespace MaMini.Tests;

public sealed class SnapMathTests
{
    private static readonly IntRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Snaps_to_nearby_edges()
    {
        var snapped = SnapMath.Snap(new IntRect(1570, 960, 340, 72), WorkArea, 12);
        Assert.Equal(new IntRect(1580, 968, 340, 72), snapped);
    }

    [Fact]
    public void Leaves_distant_windows_alone()
    {
        var window = new IntRect(500, 500, 340, 72);
        Assert.Equal(window, SnapMath.Snap(window, WorkArea, 12));
    }

    [Fact]
    public void Clamps_into_the_work_area()
    {
        Assert.Equal(new IntRect(1580, 968, 340, 72), SnapMath.ClampInto(new IntRect(1800, 1000, 340, 72), WorkArea));
        Assert.Equal(new IntRect(0, 0, 340, 72), SnapMath.ClampInto(new IntRect(-50, -50, 340, 72), WorkArea));
    }

    [Fact]
    public void Off_screen_windows_return_to_the_default_corner()
    {
        var areas = new[] { WorkArea, new IntRect(1920, 0, 1920, 1040) };
        var lost = new IntRect(5000, 5000, 340, 72);

        Assert.Equal(new IntRect(1568, 956, 340, 72), SnapMath.EnsureVisible(lost, areas, WidgetCorner.BottomRight, 12));
    }

    [Fact]
    public void Partially_visible_windows_are_pulled_onto_their_monitor()
    {
        var areas = new[] { WorkArea, new IntRect(1920, 0, 1920, 1040) };
        var window = new IntRect(3700, 100, 340, 72);

        Assert.Equal(new IntRect(3500, 100, 340, 72), SnapMath.EnsureVisible(window, areas, WidgetCorner.BottomRight, 12));
    }

    [Theory]
    [InlineData(WidgetCorner.TopLeft, 12, 12)]
    [InlineData(WidgetCorner.TopRight, 1568, 12)]
    [InlineData(WidgetCorner.BottomLeft, 12, 956)]
    [InlineData(WidgetCorner.BottomRight, 1568, 956)]
    public void Default_positions_use_the_corner(WidgetCorner corner, int left, int top) =>
        Assert.Equal(new IntRect(left, top, 340, 72), SnapMath.DefaultPosition(340, 72, WorkArea, corner, 12));
}

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D)]
    [InlineData("shift + win + F5", HotkeyModifiers.Shift | HotkeyModifiers.Win, 0x74)]
    [InlineData("F9", HotkeyModifiers.None, 0x78)]
    [InlineData("MediaPlayPause", HotkeyModifiers.None, 0xB3)]
    [InlineData("Ctrl+Space", HotkeyModifiers.Control, 0x20)]
    public void Parses(string text, HotkeyModifiers modifiers, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, vk), hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Alt")]
    [InlineData("M")]
    [InlineData("Ctrl+M+N")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Formats_in_canonical_order()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50);
        Assert.Equal("Ctrl+Alt+Shift+Win+P", hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var roundTrip));
        Assert.Equal(hotkey, roundTrip);
    }
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mamini-tests-" + Guid.NewGuid().ToString("N"));

    private sealed class ReverseProtector : ITokenProtector
    {
        public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());

        public string? Unprotect(string protectedValue) => new(protectedValue.Reverse().ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_settings_and_protects_the_token()
    {
        var path = Path.Combine(_dir, "settings.json");
        var service = new SettingsService(path, new ReverseProtector());
        service.Load();
        service.Update(s =>
        {
            s.ServerUrl = "http://ma:8095";
            s.MediaKeys = MediaKeyMode.Hook;
            s.AutoPauseOnTeamsCall = false;
            s.AutoResumeAfterTeamsCall = true;
            s.Placement = new WindowPlacement { X = -100, Y = 20, Monitor = @"\\.\DISPLAY2" };
            service.SetToken(s, "my-token");
        });

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("my-token", json);
        Assert.Contains("\"Hook\"", json);

        var reloaded = new SettingsService(path, new ReverseProtector());
        var settings = reloaded.Load();
        Assert.Equal("http://ma:8095", settings.ServerUrl);
        Assert.Equal(MediaKeyMode.Hook, settings.MediaKeys);
        Assert.False(settings.AutoPauseOnTeamsCall);
        Assert.True(settings.AutoResumeAfterTeamsCall);
        Assert.Equal(-100, settings.Placement!.X);
        Assert.Equal("my-token", reloaded.GetToken());
    }

    [Fact]
    public void Corrupt_files_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsService(path, new ReverseProtector()).Load();

        Assert.Null(settings.ServerUrl);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Legacy_settings_enable_teams_call_pausing_by_default()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "serverUrl": "http://x" }""");

        var settings = new SettingsService(path, new ReverseProtector()).Load();

        Assert.True(settings.AutoPauseOnTeamsCall);
        Assert.False(settings.AutoResumeAfterTeamsCall);
    }

    [Fact]
    public void Out_of_range_values_are_normalized()
    {
        var settings = new AppSettings { Opacity = 5, VolumeStep = 0, MediaKeys = (MediaKeyMode)42 };
        settings.Normalize();

        Assert.Equal(1.0, settings.Opacity);
        Assert.Equal(1, settings.VolumeStep);
        Assert.Equal(MediaKeyMode.Smtc, settings.MediaKeys);
    }

    [Fact]
    public void Unknown_properties_survive_a_round_trip()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "serverUrl": "http://x", "futureSetting": 123 }""");

        var service = new SettingsService(path, new ReverseProtector());
        service.Load();
        service.Update(s => s.Locked = true);

        Assert.Contains("futureSetting", File.ReadAllText(path));
    }
}

public sealed class MdnsDiscoveryTests
{
    private sealed class PacketBuilder
    {
        private readonly List<byte> _bytes = [];
        private int _records;

        public PacketBuilder Record(string name, int type, byte[] data)
        {
            Name(name);
            U16(type);
            U16(1);
            _bytes.AddRange(new byte[] { 0, 0, 0, 120 });
            U16(data.Length);
            _bytes.AddRange(data);
            _records++;
            return this;
        }

        public byte[] Build()
        {
            var header = new byte[] { 0, 0, 0x84, 0, 0, 0, (byte)(_records >> 8), (byte)_records, 0, 0, 0, 0 };
            return header.Concat(_bytes).ToArray();
        }

        public static byte[] EncodeName(string name)
        {
            var list = new List<byte>();
            foreach (var label in name.Split('.'))
            {
                list.Add((byte)label.Length);
                list.AddRange(Encoding.UTF8.GetBytes(label));
            }

            list.Add(0);
            return list.ToArray();
        }

        public static byte[] Txt(params string[] entries) =>
            entries.SelectMany(e => new[] { (byte)Encoding.UTF8.GetByteCount(e) }.Concat(Encoding.UTF8.GetBytes(e))).ToArray();

        public static byte[] Srv(int port, string target) =>
            new byte[] { 0, 0, 0, 0, (byte)(port >> 8), (byte)port }.Concat(EncodeName(target)).ToArray();

        private void Name(string name) => _bytes.AddRange(EncodeName(name));

        private void U16(int value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }
    }

    private const string Instance = "abc123._mass._tcp.local";

    [Fact]
    public void Uses_base_url_from_txt_record()
    {
        var packet = new PacketBuilder()
            .Record(MdnsDiscovery.ServiceType, 12, PacketBuilder.EncodeName(Instance))
            .Record(Instance, 16, PacketBuilder.Txt("server_id=abc123", "base_url=http://192.168.1.50:8095", "server_version=2.7.1", "name=Living Room MA"))
            .Build();

        var server = Assert.Single(MdnsDiscovery.ParseResponse(packet, null));

        Assert.Equal("Living Room MA", server.Name);
        Assert.Equal(new Uri("http://192.168.1.50:8095"), server.BaseUrl);
        Assert.Equal("2.7.1", server.Version);
    }

    [Fact]
    public void Falls_back_to_srv_and_a_records()
    {
        var packet = new PacketBuilder()
            .Record(MdnsDiscovery.ServiceType, 12, PacketBuilder.EncodeName(Instance))
            .Record(Instance, 33, PacketBuilder.Srv(8095, "mass.local"))
            .Record("mass.local", 1, [10, 0, 0, 7])
            .Build();

        var server = Assert.Single(MdnsDiscovery.ParseResponse(packet, IPAddress.Parse("10.0.0.99")));

        Assert.Equal("abc123", server.Name);
        Assert.Equal(new Uri("http://10.0.0.7:8095"), server.BaseUrl);
    }

    [Fact]
    public void Handles_compressed_names()
    {
        // PTR record whose rdata points back at the question-less answer name (offset 12).
        var instanceLabel = new byte[] { 6 }.Concat(Encoding.ASCII.GetBytes("abc123")).Concat(new byte[] { 0xC0, 12 }).ToArray();
        var packet = new PacketBuilder()
            .Record(MdnsDiscovery.ServiceType, 12, instanceLabel)
            .Record(Instance, 16, PacketBuilder.Txt("base_url=http://h:1"))
            .Build();

        var server = Assert.Single(MdnsDiscovery.ParseResponse(packet, null));
        Assert.Equal(new Uri("http://h:1"), server.BaseUrl);
    }

    [Fact]
    public void Ignores_other_services()
    {
        var packet = new PacketBuilder()
            .Record("_http._tcp.local", 12, PacketBuilder.EncodeName("printer._http._tcp.local"))
            .Build();

        Assert.Empty(MdnsDiscovery.ParseResponse(packet, null));
    }

    [Fact]
    public void Builds_a_ptr_query_with_the_unicast_bit()
    {
        var query = MdnsDiscovery.BuildQuery(MdnsDiscovery.ServiceType);
        Assert.Equal(new byte[] { 0, 12, 0x80, 1 }, query[^4..]);
        Assert.Equal(1, query[5]);
    }
}
