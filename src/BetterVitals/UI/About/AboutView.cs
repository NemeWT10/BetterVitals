using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BetterVitals.Core;
using BetterVitals.Localization;
using BetterVitals.Services;
using BetterVitals.UI.Controls;
using BetterVitals.UI.Panel;
using static BetterVitals.UI.Panel.SettingsUi;

namespace BetterVitals.UI.About;

/// <summary>
/// Settings ▸ About: mark, name, version with commit and build date, one-line pitch and links; "What's new" per version;
/// licenses (expanded on demand — no license text is read until asked for); where the data lives.
/// </summary>
internal sealed class AboutView : SectionView
{
    private readonly IPlatformService _platform;
    private readonly TextBlock _version;
    private readonly TextBlock _build;
    private readonly StackPanel _notes;

    public AboutView(PageContext? context, IPlatformService? platform = null) : base(context)
    {
        _platform = platform ?? AppServices.Platform;
        var root = new StackPanel();

        // ── hero ──
        var hero = Card();
        var heroRow = new DockPanel();
        var logoHost = new Grid { Width = 60, Height = 60, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 18, 0) };
        var glow = new System.Windows.Shapes.Ellipse { Width = 48, Height = 48, Opacity = 0.45, Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 20 } };
        glow.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Logo.Tile");
        logoHost.Children.Add(glow);
        var logo = new Image { Width = 56, Height = 56 };
        logo.SetResourceReference(Image.SourceProperty, "Logo.Image");
        Motion.SetHoverLift(logo, 3);
        logoHost.Children.Add(logo);
        heroRow.Children.Add(logoHost);

        var text = new StackPanel();
        var name = Text(null, 20, "Brush.Text", FontWeights.SemiBold);
        name.Text = AppEnv.ProductName;
        name.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Display");
        text.Children.Add(name);
        _version = Text(null, 13, "Brush.TextDim", FontWeights.SemiBold);
        _version.Margin = new Thickness(0, 2, 0, 0);
        _build = Text(null, 11.5, "Brush.TextFaint");
        _build.Margin = new Thickness(0, 1, 0, 0);
        text.Children.Add(_version);
        text.Children.Add(_build);
        var tagline = Text("about.tagline", 13, "Brush.Text");
        tagline.Margin = new Thickness(0, 12, 0, 0);
        text.Children.Add(tagline);
        var free = Text("about.free", 12);
        free.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(free);
        var links = new WrapPanel { Margin = new Thickness(-6, 10, 0, 0) };
        links.Children.Add(Link("about.github", Licenses.Repository));
        links.Children.Add(Link("about.releases", Licenses.Repository + "/releases"));
        links.Children.Add(Link("about.issues", Licenses.Repository + "/issues"));
        text.Children.Add(links);
        heroRow.Children.Add(text);
        hero.Child = heroRow;
        root.Children.Add(hero);

        // ── what's new ──
        root.Children.Add(RecordingSection.SubTitle("about.whatsNew"));
        var notesCard = Card();
        _notes = new StackPanel();
        notesCard.Child = _notes;
        root.Children.Add(notesCard);

        // ── licenses ──
        root.Children.Add(RecordingSection.SubTitle("about.licenses"));
        var licCard = Card(new Thickness(10, 12, 10, 10));
        var licPanel = new StackPanel();
        var lead = Text("about.licenses.lead", 12.5);
        lead.Margin = new Thickness(8, 0, 8, 8);
        licPanel.Children.Add(lead);
        foreach (var entry in Licenses.All) licPanel.Children.Add(LicenseRow(entry));
        licCard.Child = licPanel;
        root.Children.Add(licCard);

        // ── folders ──
        root.Children.Add(RecordingSection.SubTitle("about.folders"));
        root.Children.Add(Rows(
            Folder("about.folder.settings", AppEnv.RoamingDir, "Icon.Settings"),
            Folder("about.folder.data", AppEnv.LocalDir, "Icon.Disk")));

        Content = root;
        Initialize();
    }

    internal static (string Version, string? Commit, DateOnly? Built) ParseVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational, null, null);
        var version = informational[..plus];
        var meta = informational[(plus + 1)..].Split('.');
        var commit = meta.Length > 0 && meta[0].Length > 0 ? meta[0] : null;
        DateOnly? built = meta.Length > 1 && DateOnly.TryParseExact(meta[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (version, commit, built);
    }

    protected override IReadOnlyCollection<string> Watches => ["-"];

    protected override void Refresh()
    {
        var (version, commit, built) = ParseVersion(AppEnv.Version);
        _version.Text = Loc.T("about.version", version);
        _build.Text = commit is null ? "" : Loc.T("about.build", commit, built?.ToString("d", Loc.I.Culture) ?? "—");
        _build.Visibility = commit is null ? Visibility.Collapsed : Visibility.Visible;
        BuildNotes();
    }

    // ───────────── what's new ─────────────

    private void BuildNotes()
    {
        _notes.Children.Clear();
        var all = ReleaseNotes.All;
        if (all.Count == 0) return;
        _notes.Children.Add(Release(all[0], current: all[0].Version == AppEnv.ShortVersion));
        if (all.Count == 1) return;
        var older = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var r in all.Skip(1)) older.Children.Add(Release(r, current: false));
        var toggle = Disclosure(Text("about.older", 12.5, "Brush.TextDim", FontWeights.SemiBold));
        toggle.Margin = new Thickness(-10, 10, -10, 0);
        toggle.Checked += (_, _) => older.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) => older.Visibility = Visibility.Collapsed;
        _notes.Children.Add(toggle);
        _notes.Children.Add(older);
    }

    private static FrameworkElement Release(ReleaseNote note, bool current)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var head = new WrapPanel();
        var title = Text(null, 14, "Brush.Text", FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.Text = note.Date is { } d
            ? Loc.T("about.release.dated", note.Version, d.ToString("d", Loc.I.Culture))
            : Loc.T("about.release", note.Version);
        title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title);
        if (current)
        {
            var pill = new StatusPill { Margin = new Thickness(10, 0, 0, 0) };
            pill.Bind("about.current", PillKind.Accent);
            head.Children.Add(pill);
        }
        p.Children.Add(head);
        var summary = Text(null, 12.5);
        summary.Text = Loc.I.IsPolish ? note.SummaryPl : note.SummaryEn;
        summary.Margin = new Thickness(0, 4, 0, 8);
        p.Children.Add(summary);
        foreach (var c in note.Changes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var tag = new Border { Width = 82, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            tag.SetResourceReference(StyleProperty, "Tag");
            var tagText = new TextBlock { Text = Loc.T("about.kind." + c.Kind), HorizontalAlignment = HorizontalAlignment.Center };
            tagText.SetResourceReference(TextBlock.ForegroundProperty, c.Kind switch
            {
                ChangeKind.New => "Brush.AccentText",
                ChangeKind.Fixed => "Brush.Success",
                _ => "Brush.Info",
            });
            tag.Child = tagText;
            row.Children.Add(tag);
            var line = Text(null, 12.5, "Brush.Text");
            line.Text = Loc.I.IsPolish ? c.Pl : c.En;
            line.LineHeight = 18;
            row.Children.Add(line);
            p.Children.Add(row);
        }
        return p;
    }

    // ───────────── licenses ─────────────

    private FrameworkElement LicenseRow(LicenseEntry entry)
    {
        var host = new StackPanel();
        var head = new DockPanel();
        if (entry.License.Length > 0)
        {
            var license = new Border();
            license.SetResourceReference(StyleProperty, "Tag");
            license.Child = new TextBlock { Text = entry.License };
            DockPanel.SetDock(license, Dock.Right);
            head.Children.Add(license);
        }
        var title = Text(null, 13, "Brush.Text", FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        if (entry.Name == "THIRD-PARTY-NOTICES") title.SetBinding(TextBlock.TextProperty, Tr("about.licenses.notices"));
        else title.Text = entry.Name;
        var sub = Text(null, 12, "Brush.TextFaint");
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.VerticalAlignment = VerticalAlignment.Center;
        sub.Margin = new Thickness(8, 0, 8, 0);
        if (entry.License == "SIL OFL 1.1") sub.SetBinding(TextBlock.TextProperty, Tr("about.licenses.font"));
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, sub } });

        var toggle = Disclosure(head);
        System.Windows.Data.BindingOperations.SetBinding(toggle, System.Windows.Automation.AutomationProperties.NameProperty,
            new System.Windows.Data.Binding { Source = entry.Name });
        host.Children.Add(toggle);
        Border? body = null;
        toggle.Checked += (_, _) =>
        {
            // the text is read only now (resources / font files are not touched until asked for)
            body ??= LicenseBody(entry);
            if (!host.Children.Contains(body)) host.Children.Add(body);
            body.Visibility = Visibility.Visible;
            Motion.Appear(body, rise: 4, fromScale: 1, ms: 200);
        };
        toggle.Unchecked += (_, _) =>
        {
            if (body is not null) body.Visibility = Visibility.Collapsed;
        };
        return host;
    }

    private Border LicenseBody(LicenseEntry entry)
    {
        var text = entry.Text();
        var panel = new StackPanel();
        var tb = new TextBox
        {
            Text = text.Length > 0 ? text : Loc.T("about.licenses.missing"),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 11.5,
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
        };
        tb.SetResourceReference(TextBox.ForegroundProperty, "Brush.TextDim");
        tb.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        tb.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Text");
        panel.Children.Add(tb);
        if (entry.Url is { } url)
        {
            var open = Link("about.licenses.open", url);
            open.Margin = new Thickness(-6, 8, 0, 0);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(open);
        }
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(8, 2, 8, 8), Child = panel };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.BgAlt");
        return b;
    }

    // ───────────── pieces ─────────────

    private Button Link(string key, string url)
    {
        var b = new Button();
        b.SetResourceReference(StyleProperty, "FlatButton");
        var label = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.TextProperty, Tr(key));
        var icon = new Icon { Size = 13, Margin = new Thickness(5, 1, 0, 0) };
        icon.SetResourceReference(Icon.DataProperty, "Icon.External");
        b.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, icon } };
        b.Margin = new Thickness(0, 0, 6, 0);
        b.ToolTip = url;
        b.Click += (_, _) => _platform.OpenUrl(url);
        return b;
    }

    private SettingRow Folder(string key, string path, string icon)
    {
        var open = Button("SecondaryButton", "about.folder.open", "Icon.External");
        open.Click += (_, _) => _platform.OpenFolder(path);
        var row = Row(key, null, icon, open);
        row.Description = path;
        return row;
    }

    private static ToggleButton Disclosure(object content)
    {
        var t = new ToggleButton { Content = content };
        t.SetResourceReference(StyleProperty, "DisclosureRow");
        return t;
    }

    private static Border Card(Thickness? padding = null)
    {
        var b = new Border();
        b.SetResourceReference(StyleProperty, "Card");
        if (padding is { } p) b.Padding = p;
        return b;
    }
}
