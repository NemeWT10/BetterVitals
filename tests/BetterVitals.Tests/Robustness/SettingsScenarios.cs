using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BetterVitals.Core.Catalog;
using BetterVitals.Core.Overlay;
using BetterVitals.Core.Sensors;
using BetterVitals.Core.Settings;
using BetterVitals.History;
using BetterVitals.Overlay;
using BetterVitals.Services;
using Xunit;
using Xunit.Abstractions;

namespace BetterVitals.Tests.Robustness;

/// <summary>PROJEKT §12: damaged settings (garbage, truncated, wrong types, a newer schema, hand-edited extremes), failing saves.</summary>
public sealed class SettingsScenarios(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Compare = new() { WriteIndented = false };

    private static AppSettings Rich() => new()
    {
        FirstRunDone = true,
        General = { Language = "pl", Theme = "neon", Animations = false, TemperatureUnit = TemperatureUnit.Fahrenheit },
        Recording = { IntervalSeconds = 5, RawRetentionDays = 14, SensorIds = { "cpu/x/0/temperature/a", "gpu/y/0/load/b" }, KeyPresetApplied = true },
        Overlay =
        {
            Enabled = true, RefreshMs = 500, Placement = { Anchor = OverlayAnchor.BottomRight, OffsetX = 12.5 },
            Cells = [new OverlayItem { SensorIds = ["cpu/x/0/temperature/a"], Label = "CPU" }],
        },
        Flyout = { PinnedSensorIds = ["gpu/y/0/load/b"] },
        Updates = { Mode = UpdateMode.Off },
        Driver = { Mode = ReadMode.Basic, PauseForAntiCheat = true },
    };

    /// <summary>The JSON the app itself writes for <see cref="Rich"/>.</summary>
    private static string RichJson(QaDir dir)
    {
        var path = dir.File_("rich-source.json");
        var store = SettingsStore.CreateDetached(path, Rich());
        store.Mutate("test", _ => { });
        store.Flush();
        return File.ReadAllText(path);
    }

    private static string Json(AppSettings s) => JsonSerializer.Serialize(s, Compare);

    private static void AssertDefaults(AppSettings s)
    {
        Assert.NotNull(s);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
        Assert.False(s.FirstRunDone);
        Assert.Equal("system", s.General.Theme);
        Assert.Equal(2, s.Recording.IntervalSeconds);
        Assert.Empty(s.Recording.SensorIds);
        Assert.True(s.Overlay.IsEmpty);
        Assert.Equal(UpdateMode.Ask, s.Updates.Mode);
        Assert.Equal(ReadMode.Full, s.Driver.Mode);
    }

    // ───────────────────────── damaged files ─────────────────────────

    public static TheoryData<string, byte[]> Unreadable()
    {
        var random = new byte[3000];
        new Random(4).NextBytes(random);
        var data = new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "whitespace", Encoding.UTF8.GetBytes("  \r\n\t  ") },
            { "text", Encoding.UTF8.GetBytes("this is not json at all") },
            { "random bytes", random },
            { "zeros", new byte[512] },
            { "json array", Encoding.UTF8.GetBytes("[1,2,3]") },
            { "json number", Encoding.UTF8.GetBytes("42") },
            { "json string", Encoding.UTF8.GetBytes("\"settings\"") },
            { "nested too deep", Encoding.UTF8.GetBytes(new string('[', 5000)) },
            { "unclosed object", Encoding.UTF8.GetBytes("{\"General\": {\"Theme\": \"neon\"") },
            { "section is a number", Encoding.UTF8.GetBytes("{\"General\": 5, \"Recording\": {\"IntervalSeconds\": 5}}") },
            { "section is a string", Encoding.UTF8.GetBytes("{\"Overlay\": \"on\"}") },
            { "number is a string", Encoding.UTF8.GetBytes("{\"Recording\": {\"IntervalSeconds\": \"fast\"}}") },
            { "list is an object", Encoding.UTF8.GetBytes("{\"Recording\": {\"SensorIds\": {\"a\": 1}}}") },
            { "items is a string", Encoding.UTF8.GetBytes("{\"Overlay\": {\"Items\": \"nope\"}}") },
            { "item is a number", Encoding.UTF8.GetBytes("{\"Overlay\": {\"Items\": [5]}}") },
            { "unknown enum name", Encoding.UTF8.GetBytes("{\"Overlay\": {\"Placement\": {\"Anchor\": \"Middle\"}}}") },
            { "bool is a number", Encoding.UTF8.GetBytes("{\"FirstRunDone\": 7}") },
            { "number out of range", Encoding.UTF8.GetBytes("{\"Recording\": {\"IntervalSeconds\": 99999999999999999999}}") },
            { "schema is a string", Encoding.UTF8.GetBytes("{\"SchemaVersion\": \"two\"}") },
            { "binary tail", Encoding.UTF8.GetBytes("{\"FirstRunDone\": true}").Concat(new byte[] { 0, 0, 1, 255, 254 }).ToArray() },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void An_unreadable_file_starts_with_defaults_and_is_kept_as_settings_bad_json(string name, byte[] content)
    {
        using var log = new LogCapture();
        using var dir = new QaDir("settings-bad");
        var path = dir.File_("settings.json");
        File.WriteAllBytes(path, content);

        var s = SettingsStore.Load(path);

        output.WriteLine(name);
        AssertDefaults(s);
        Assert.Equal(Json(new AppSettings()), Json(s));
        var bad = dir.File_("settings.bad.json");
        Assert.True(File.Exists(bad), "the damaged file is kept for the user / a bug report");
        Assert.Equal(content, File.ReadAllBytes(bad));
        Assert.True(log.MineWith("ERROR", "settings: unreadable") == 1);

        // the app goes on: it can change and save settings, the bad copy stays
        var store = SettingsStore.CreateDetached(path, s);
        store.Mutate("test", x => x.General.Theme = "graphite");
        store.Flush();
        Assert.Equal("graphite", SettingsStore.Load(path).General.Theme);
        Assert.Equal(content, File.ReadAllBytes(bad));
    }

    [Fact]
    public void A_file_cut_off_anywhere_never_throws_and_leaves_a_bad_copy()
    {
        using var dir = new QaDir("settings-cut");
        var json = RichJson(dir);
        var bytes = Encoding.UTF8.GetBytes(json);
        var path = dir.File_("settings.json");
        var cuts = 0;
        for (var n = 0; n < bytes.Length; n += 13)
        {
            File.WriteAllBytes(path, bytes.AsSpan(0, n).ToArray());
            if (File.Exists(dir.File_("settings.bad.json"))) File.Delete(dir.File_("settings.bad.json"));
            var s = SettingsStore.Load(path);
            AssertDefaults(s);
            Assert.True(File.Exists(dir.File_("settings.bad.json")), $"cut at {n}");
            cuts++;
        }
        output.WriteLine($"{cuts} cut positions of {bytes.Length} bytes");
        File.WriteAllBytes(path, bytes);
        Assert.Equal("neon", SettingsStore.Load(path).General.Theme); // the whole file is fine
    }

    [Fact]
    public void Sloppy_but_valid_json_is_read_comments_trailing_commas_unknown_keys_any_case()
    {
        using var dir = new QaDir("settings-sloppy");
        var path = dir.File_("settings.json");
        File.WriteAllText(path, """
            {
              // written by hand
              "general": { "THEME": "graphite", "Unknown": [1, 2, { "x": null }], },
              "Recording": { "IntervalSeconds": 10, "IntervalSeconds": 5 },
              "somethingNew": { "a": 1 },
            }
            """);
        var s = SettingsStore.Load(path);
        Assert.Equal("graphite", s.General.Theme);
        Assert.Equal(5, s.Recording.IntervalSeconds); // the last duplicate wins
        Assert.False(File.Exists(dir.File_("settings.bad.json")));

        File.WriteAllText(path, "null");
        AssertDefaults(SettingsStore.Load(path));
        File.WriteAllText(path, "{}");
        AssertDefaults(SettingsStore.Load(path));
        AssertDefaults(SettingsStore.Load(dir.File_("does-not-exist.json")));
    }

    [Fact]
    public void A_newer_schema_is_used_as_far_as_it_is_understood()
    {
        using var dir = new QaDir("settings-future");
        var node = JsonNode.Parse(RichJson(dir))!.AsObject();
        node["SchemaVersion"] = 99;
        node["BrandNewSection"] = new JsonObject { ["Deep"] = new JsonArray(1, 2, 3) };
        node["General"]!["HolographicTheme"] = "yes";
        node["Overlay"]!["Placement"]!["Warp"] = 9.5;
        node["Overlay"]!["Cells"]![0]!["Hologram"] = true;
        var path = dir.File_("settings.json");
        File.WriteAllText(path, node.ToJsonString());

        var s = SettingsStore.Load(path);

        Assert.Equal("neon", s.General.Theme);
        Assert.Equal(5, s.Recording.IntervalSeconds);
        Assert.Equal(OverlayAnchor.BottomRight, s.Overlay.Placement.Anchor);
        Assert.Equal("CPU", s.Overlay.Cells[0].Label);
        Assert.Equal(ReadMode.Basic, s.Driver.Mode);
        Assert.True(s.FirstRunDone);
        Assert.False(File.Exists(dir.File_("settings.bad.json")), "a newer file is not a damaged file");

        // saving it again keeps working and keeps what this version understands
        var store = SettingsStore.CreateDetached(path, s);
        store.Mutate("test", x => x.General.Animations = true);
        store.Flush();
        var again = SettingsStore.Load(path);
        Assert.Equal("neon", again.General.Theme);
        Assert.True(again.General.Animations);
        Assert.Equal(OverlayAnchor.BottomRight, again.Overlay.Placement.Anchor);
    }

    // ───────────────────────── hand-edited nulls and absurd numbers ─────────────────────────

    private const string Absurd = """
        {
          "Recording": {
            "Enabled": true, "SensorIds": [null, "", "cpu/amd-ryzen-7-9800x3d/0/temperature/core-tctl-tdie"], "RemovedSensorIds": [null, ""],
            "IntervalSeconds": 0, "RawRetentionDays": 2147483647, "MinuteRetentionDays": -5, "HourRetentionDays": 2147483647
          },
          "Flyout": { "PinnedSensorIds": [null, "", "no/such/sensor"] },
          "Overlay": {
            "Enabled": true, "RefreshMs": 5, "HideFromCapture": false,
            "Placement": { "OffsetX": 1e300, "OffsetY": -1e300, "Margin": -1e9, "Anchor": 42, "MonitorMode": "Specific", "MonitorKey": null },
            "Style": { "Padding": 1e9, "Spacing": 1e9, "CornerRadius": -5, "ScalePercent": 1e9, "FontFamily": "No Such Font", "ValueFontFamily": "", "Arrangement": 9, "LabelWeight": -1, "BackgroundColor": "red", "BackgroundOpacity": 7 },
            "Items": [
              null,
              { "SensorIds": [null, ""] },
              { "SensorIds": null },
              { "SensorIds": ["cpu/amd-ryzen-7-9800x3d/0/temperature/core-tctl-tdie"], "Thresholds": [null, { "Value": 1e30, "Color": "nonsense" }], "Decimals": 99, "AggregationSeconds": -4, "ValueSize": 1e9 }
            ]
          },
          "General": { "Language": "xx", "Theme": "does-not-exist" },
          "Updates": { "Mode": 99, "LastCheckUtc": "9999-12-31T23:59:59Z" },
          "Driver": { "Mode": 5 }
        }
        """;

    [Fact]
    public async Task Hand_edited_nulls_and_absurd_numbers_load_and_every_consumer_survives_them()
    {
        using var dir = new QaDir("settings-absurd");
        var path = dir.File_("settings.json");
        File.WriteAllText(path, Absurd);
        var s = SettingsStore.Load(path);
        Assert.False(File.Exists(dir.File_("settings.bad.json")), "valid JSON: nothing to keep apart");

        using var app = new SimApp("settings-absurd-app", sensors: _ => []);
        var pc = app.Pc;
        var cpuTemp = pc.Sensor("AMD Ryzen 7 9800X3D", SensorKind.Temperature, "Core (Tctl/Tdie)");

        // 1. history: recording + retention
        app.History.Configure(s.Recording);
        app.Run(TimeSpan.FromMinutes(3));
        app.History.Suspend();
        Assert.NotEmpty(Directory.GetFiles(app.HistoryDir, "raw-*.bvr"));
        Assert.Equal(0, app.History.RunRetentionNow()); // 2147483647 days of retention must not wrap around into "delete everything"
        Assert.NotEmpty(Directory.GetFiles(app.HistoryDir, "raw-*.bvr"));
        Assert.NotEmpty(await app.Series(cpuTemp.Id, pc.Clock.Utc.AddMinutes(-5), pc.Clock.Utc.AddMinutes(1)));

        // 2. flyout / recording policy
        var keys = SensorPresentation.KeySensors(pc.Engine, s.Flyout.PinnedSensorIds);
        Assert.NotEmpty(keys);
        var items = FriendlyCatalog.Resolve(pc.Engine.Hardware, pc.Engine.Sensors);
        _ = RecordingPolicy.MissingKeySensors(s.Recording, items, s.Recording.RemovedSensorIds);
        _ = RecordingPolicy.CountPresent(s.Recording, pc.Engine);

        // 3. the overlay: offscreen render and the live window
        var bmp = OverlayHost.RenderToBitmap(s.Overlay, OverlayTheme.Default, [], 1080);
        output.WriteLine($"offscreen render {bmp.Width}×{bmp.Height}");
        Assert.InRange(bmp.Width, 1, 4096);
        Assert.InRange(bmp.Height, 1, 4096);
        using var host = new OverlayHost(new OverlayHostOptions { NeverShow = true });
        host.Start();
        host.Apply(s.Overlay, OverlayTheme.Default, new OverlayContext { Hub = pc.Engine, Animations = false });
        var d = host.Diagnostics();
        output.WriteLine($"live window {d.SceneWidth}×{d.SceneHeight}, surface ready {d.SurfaceReady}");
        Assert.True(host.IsRunning);
        Assert.InRange(d.SceneWidth, 0, 4096);
        Assert.InRange(d.SceneHeight, 0, 4096);
        pc.Run(TimeSpan.FromSeconds(5));
        host.RefreshNow();
        Assert.True(host.IsRunning);
    }

    // ───────────────────────── saving ─────────────────────────

    [Fact]
    public void A_failing_save_is_survived_the_old_file_stays_and_the_save_is_retried()
    {
        using var log = new LogCapture();
        using var dir = new QaDir("settings-save");
        var path = dir.File_("settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new AppSettings { General = { Theme = "aurora" } }));
        var store = SettingsStore.CreateDetached(path, SettingsStore.Load(path));
        store.SaveRetryMs = 150;

        Directory.CreateDirectory(path + ".tmp"); // the temp file cannot be created (antivirus, permissions, a leftover)
        store.Mutate("test", s => s.General.Theme = "sunset");
        store.Flush();

        Assert.True(log.MineWith("ERROR", "settings: save failed") >= 1);
        Assert.Equal("aurora", SettingsStore.Load(path).General.Theme); // never a half-written file

        Directory.Delete(path + ".tmp"); // the obstacle goes away: the pending change reaches the disk by itself
        var saved = SpinWait.SpinUntil(() => SettingsStore.Load(path).General.Theme == "sunset", 5000);
        Assert.True(saved, "the failed save was retried");
    }

    [Fact]
    public void Numbers_that_cannot_be_written_as_json_do_not_stop_saving()
    {
        using var dir = new QaDir("settings-nan");
        var path = dir.File_("settings.json");
        var store = SettingsStore.CreateDetached(path, new AppSettings());
        store.Mutate("test", s =>
        {
            s.Overlay.Placement.OffsetX = double.NaN;
            s.Overlay.Placement.OffsetY = double.PositiveInfinity;
            s.General.Theme = "graphite";
        });

        store.Flush(); // must not throw (a timer thread would take the process down)

        var back = SettingsStore.Load(path);
        Assert.Equal("graphite", back.General.Theme);
        Assert.True(double.IsFinite(back.Overlay.Placement.OffsetX));
        Assert.True(double.IsFinite(back.Overlay.Placement.OffsetY));
    }

    [Fact]
    public void A_file_that_is_busy_for_a_moment_is_read_not_replaced_by_defaults()
    {
        using var dir = new QaDir("settings-locked");
        var path = dir.File_("settings.json");
        File.WriteAllText(path, RichJson(dir));
        using var gate = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); // a backup tool, an antivirus scan
            gate.Set();
            Thread.Sleep(350);
        });
        holder.Start();
        gate.Wait();

        var s = SettingsStore.Load(path);

        holder.Join();
        Assert.Equal("neon", s.General.Theme);
        Assert.True(s.FirstRunDone);
    }

    [Fact]
    public void A_corrupt_start_followed_by_a_normal_session_ends_with_a_valid_file()
    {
        using var dir = new QaDir("settings-session");
        var path = dir.File_("settings.json");
        File.WriteAllText(path, "{ \"General\": ");
        var store = SettingsStore.CreateDetached(path, SettingsStore.Load(path));
        for (var i = 0; i < 20; i++) store.Mutate("test", s => s.Recording.SensorIds.Add("a/b/c/d/" + i));
        store.Flush();
        var back = SettingsStore.Load(path);
        Assert.Equal(20, back.Recording.SensorIds.Count);
        Assert.Equal("{ \"General\": ", File.ReadAllText(dir.File_("settings.bad.json")));
    }
}
