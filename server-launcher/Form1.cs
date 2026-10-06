using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace server_launcher;

public partial class Form1 : Form
{
    private const string FolderName = "Minecraft Server Jars";
    private static readonly string[] Categories = ["Spigot", "Bukkit", "Paper", "Purpur"];

    private readonly LauncherService launcher = new();
    private readonly Surface surface = new() { Dock = DockStyle.Fill };
    private readonly SidebarButton navVersions;
    private readonly SidebarButton navInstalled;
    private readonly SidebarButton navSettings;
    private readonly VStack versionsPage = new();
    private readonly VStack installedPage = new();
    private readonly VStack settingsPage = new();
    private readonly TabsEl versionTabs = new();
    private readonly TabsEl installedTabs = new();
    private readonly VStack versionList = new() { Gap = 16 };
    private readonly VStack installedList = new() { Gap = 16 };

    private IReadOnlyList<LauncherService.InstalledServer> installedServers = [];
    private string currentView = "versions";
    private string? installedCategory;
    private int renderToken;

    public Form1()
    {
        InitializeComponent();

        navVersions = new SidebarButton(Glyph.Versions, "Versions", () => ShowView("versions"));
        navInstalled = new SidebarButton(Glyph.Installed, "Installed", () => ShowView("installed"));
        navSettings = new SidebarButton(Glyph.Settings, "Settings", () => ShowView("settings"));
        surface.NavTop.Add(navVersions);
        surface.NavTop.Add(navInstalled);
        surface.NavBottom = navSettings;

        foreach (var category in Categories)
            versionTabs.Tabs.Add(new TabEl(category, () => SwitchCategory(category)));

        versionsPage.Items.Add(new TitleEl("Versions"));
        versionsPage.Items.Add(versionTabs);
        versionsPage.Items.Add(versionList);

        installedPage.Items.Add(new TitleEl("Installed"));
        installedPage.Items.Add(installedTabs);
        installedPage.Items.Add(installedList);

        Controls.Add(surface);
        Load += (_, _) =>
        {
            ShowView("versions");
            SwitchCategory("Spigot");
        };
        Shown += (_, _) => surface.Focus();
    }

    // Dark title bar so the window chrome matches the page (ignored on older Windows).
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
            var caption = 0x000F0F0F;
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        }
        catch
        {
            // Not supported on this version of Windows.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // ---------- navigation ----------

    private void ShowView(string view)
    {
        currentView = view;
        navVersions.Active = view == "versions";
        navInstalled.Active = view == "installed";
        navSettings.Active = view == "settings";
        surface.CloseMenu();
        surface.Root.Content = view switch
        {
            "installed" => installedPage,
            "settings" => settingsPage,
            _ => versionsPage
        };

        if (view == "installed") RenderInstalled();
        else if (view == "settings") RenderSettings();
        surface.Relayout();
    }

    private void SwitchCategory(string category)
    {
        foreach (var tab in versionTabs.Tabs) tab.Active = tab.Text == category;
        _ = RenderVersionsAsync(category);
    }

    // ---------- versions ----------

    private async Task RenderVersionsAsync(string category)
    {
        var token = ++renderToken;
        versionList.Items.Clear();
        versionList.Items.Add(new LoadingEl("Fetching live version data..."));
        surface.Relayout();

        IReadOnlyList<LauncherService.LauncherVersion> items;
        try
        {
            items = await launcher.LoadVersionsAsync(category);
        }
        catch (Exception ex)
        {
            if (token != renderToken) return;
            versionList.Items.Clear();
            var retry = new ActionButton("Retry", Glyph.Download, secondary: true) { Backdrop = Theme.Page };
            retry.OnClick = () => _ = RenderVersionsAsync(category);
            versionList.Items.Add(new MessageEl($"Couldn't load {category} versions: {ex.Message}", Theme.Error, retry));
            surface.Relayout();
            return;
        }
        if (token != renderToken) return; // user switched tabs meanwhile

        versionList.Items.Clear();
        if (category == "Spigot")
        {
            versionList.Items.Add(new NoticeEl("SpigotMC only distributes BuildTools, so these are prebuilt jars mirrored by MCJars."));
        }
        else if (category == "Bukkit")
        {
            var buildTools = new LauncherService.LauncherVersion("Bukkit", "BuildTools", string.Empty, string.Empty, null, IsBuildTools: true);
            var getBuildTools = new ActionButton("Get BuildTools.jar", Glyph.Download) { Backdrop = Theme.NoticeBg };
            getBuildTools.OnClick = () => _ = RunDownloadAsync(getBuildTools, buildTools, "Get BuildTools.jar");
            versionList.Items.Add(new NoticeEl("CraftBukkit has no direct download. Get BuildTools, then run the command for your version in the same folder:", getBuildTools));
        }

        if (items.Count == 0)
        {
            versionList.Items.Add(new LoadingEl($"No {category} versions found."));
        }
        else
        {
            foreach (var item in items) versionList.Items.Add(CreateVersionBox(item));
        }

        surface.Relayout();
        surface.StartAppear();
    }

    private VersionBox CreateVersionBox(LauncherService.LauncherVersion item)
    {
        var box = new VersionBox(item.Title, item.Subtitle);
        if (!item.IsBuildTools && item.Category != "Bukkit")
        {
            var download = new ActionButton("Download", Glyph.Download);
            download.OnClick = () => _ = RunDownloadAsync(download, item, "Download");
            box.Actions.Add(download);
        }
        if (item.CopyCommand is { } command)
        {
            var copy = new ActionButton("Copy command", Glyph.Download, secondary: true);
            copy.OnClick = () => CopyCommand(command);
            box.Actions.Add(copy);
        }
        return box;
    }

    // ---------- downloading ----------

    private async Task RunDownloadAsync(ActionButton button, LauncherService.LauncherVersion item, string idleLabel)
    {
        if (button.Disabled) return;
        button.Disabled = true;
        button.Downloading = true;
        surface.Touch();
        var finished = false;

        try
        {
            // Folder first, like the page does.
            if (!HasDownloadRoot() && !await AskForFirstFolderAsync()) return;

            SetLabel(button, "Resolving…");
            var target = await launcher.ResolveDownloadAsync(item);
            surface.ShowToast(target.Note ?? $"Downloading {target.Filename}");
            SetLabel(button, "0%");

            var progress = new Progress<(long Received, long? Total)>(update =>
            {
                if (finished) return;
                SetLabel(button, update.Total is > 0
                    ? $"{Math.Min(100, update.Received * 100 / update.Total.Value)}%"
                    : $"{update.Received / 1048576d:F1} MB");
            });
            await launcher.DownloadAndInstallAsync(target, progress);

            if (target.ServerType is not null && target.Version is not null)
            {
                var title = ServerTitle(target.ServerType, target.Version);
                surface.ShowToast($"Installed {title} in {FolderLabel()}/{title}");
            }
            else
            {
                surface.ShowToast($"Saved {target.Filename} to {FolderLabel()}");
            }

            if (currentView == "installed") RenderInstalled();
            else if (currentView == "settings") RenderSettings();
        }
        catch (Exception ex)
        {
            surface.ShowToast(string.IsNullOrWhiteSpace(ex.Message) ? "Download failed" : ex.Message);
        }
        finally
        {
            finished = true;
            button.Disabled = false;
            button.Downloading = false;
            SetLabel(button, idleLabel);
            surface.Touch();
        }
    }

    private void SetLabel(ActionButton button, string text)
    {
        if (button.Label == text) return;
        var before = button.PreferredWidth(surface.Ui);
        button.Label = text;
        if (Math.Abs(button.PreferredWidth(surface.Ui) - before) > 0.1f) surface.Relayout();
        else surface.Invalidate();
    }

    private void CopyCommand(string command)
    {
        try
        {
            Clipboard.SetText(command);
            surface.ShowToast("Command copied");
        }
        catch (Exception ex)
        {
            surface.ShowToast($"Couldn't copy: {ex.Message}");
        }
    }

    // First-download folder prompt (the "Where should server jars go?" modal).
    private Task<bool> AskForFirstFolderAsync()
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish(bool value)
        {
            surface.CloseModal();
            result.TrySetResult(value);
        }

        var cancel = new ActionButton("Cancel", secondary: true);
        var choose = new ActionButton("Choose location");
        cancel.OnClick = () => Finish(false);
        choose.OnClick = () =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = $"Pick a location. A \"{FolderName}\" folder will be created inside it.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                InitialDirectory = DefaultDownloadsFolder()
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                Finish(false);
                return;
            }

            try
            {
                var selected = dialog.SelectedPath;
                // Don't nest if they picked an existing "Minecraft Server Jars" folder.
                var root = Path.GetFileName(selected.TrimEnd('\\', '/')).Equals(FolderName, StringComparison.OrdinalIgnoreCase)
                    ? selected
                    : Path.Combine(selected, FolderName);
                launcher.SetDownloadRoot(root);
                surface.ShowToast($"Created {FolderLabel()}");
                Finish(true);
            }
            catch (Exception ex)
            {
                surface.ShowToast($"Couldn't use that folder: {ex.Message}");
                Finish(false);
            }
        };

        surface.ShowModal(
            new ModalCard(460,
                new HeadingEl("Where should server jars go?"),
                new RichTextEl(new (string, bool)[]
                {
                    ("Pick a location and a ", false),
                    (FolderName, true),
                    (" folder will be created inside it. Your Downloads folder is a good choice. You can change this later in Settings.", false)
                }),
                new ButtonRow(cancel, choose)),
            closeOnBackdrop: false,
            onEscape: () => Finish(false));

        return result.Task;
    }

    // ---------- installed ----------

    private void RenderInstalled()
    {
        installedTabs.Tabs.Clear();
        installedList.Items.Clear();
        var root = launcher.DownloadRoot;

        if (string.IsNullOrWhiteSpace(root))
        {
            var browse = new ActionButton("Browse versions", Glyph.Download) { Backdrop = Theme.Page };
            browse.OnClick = () => ShowView("versions");
            installedList.Items.Add(new MessageEl("Nothing installed yet. Download a version and it will show up here.", Theme.Muted, browse));
            surface.Relayout();
            return;
        }

        if (!Directory.Exists(root))
        {
            launcher.ResetDownloadRoot();
            installedList.Items.Add(new MessageEl("Your downloads folder was moved or deleted. Download a version to pick a new one.", Theme.Muted));
            surface.Relayout();
            return;
        }

        try
        {
            installedServers = launcher.GetInstalledServers();
        }
        catch (Exception ex)
        {
            installedList.Items.Add(new MessageEl($"Couldn't read {FolderLabel()}: {ex.Message}", Theme.Muted));
            surface.Relayout();
            return;
        }

        // First visit: land on the first category that actually has something.
        installedCategory ??= Categories.FirstOrDefault(category => installedServers.Any(server => server.Category == category)) ?? "Spigot";
        BuildInstalledTabs();
        BuildInstalledList();
        surface.Relayout();
        surface.StartAppear();
    }

    private void BuildInstalledTabs()
    {
        installedTabs.Tabs.Clear();
        foreach (var category in Categories)
        {
            var count = installedServers.Count(server => server.Category == category);
            var tab = new TabEl(count > 0 ? $"{category} ({count})" : category, () =>
            {
                installedCategory = category;
                BuildInstalledTabs();
                BuildInstalledList();
                surface.Relayout();
                surface.StartAppear();
            })
            {
                Active = category == installedCategory
            };
            installedTabs.Tabs.Add(tab);
        }
    }

    private void BuildInstalledList()
    {
        installedList.Items.Clear();
        var category = installedCategory ?? "Spigot";
        var servers = installedServers.Where(server => server.Category == category).ToList();

        if (servers.Count == 0)
        {
            var browse = new ActionButton($"Browse {category}", Glyph.Download) { Backdrop = Theme.Page };
            browse.OnClick = () =>
            {
                ShowView("versions");
                SwitchCategory(category);
            };
            installedList.Items.Add(new MessageEl($"No {category} servers installed.", Theme.Muted, browse));
            return;
        }

        foreach (var server in servers)
        {
            var details = new List<string>();
            if (server.Build is not null) details.Add($"Build #{server.Build}");
            details.Add($"{server.RamGb} GB RAM");
            details.Add($"{(server.Size / 1048576d).ToString("F1", CultureInfo.InvariantCulture)} MB");
            details.Add($"Added {server.LastModified.ToShortDateString()}");

            var box = new VersionBox(server.Title, string.Join(" • ", details));

            var more = new KebabButton();
            more.IsExpanded = () => ReferenceEquals(surface.MenuOwner, more);
            more.OnClick = () =>
            {
                if (ReferenceEquals(surface.MenuOwner, more))
                {
                    surface.CloseMenu();
                    return;
                }
                surface.OpenMenu(more, new[]
                {
                    new MenuItemSpec("Open Folder", () => OpenServerFolder(server)),
                    new MenuItemSpec("More Options", () => OpenRamModal(server)),
                    new MenuItemSpec("Delete", () => ConfirmDelete(server), Danger: true)
                });
            };

            var play = new ActionButton("Play", Glyph.Play);
            play.OnClick = () => PlayServer(server);

            box.Actions.Add(more);
            box.Actions.Add(play);
            installedList.Items.Add(box);
        }
    }

    private void PlayServer(LauncherService.InstalledServer server)
    {
        try
        {
            // Refresh run.bat (keeps RAM) and pre-accept the EULA so the first launch goes straight through.
            launcher.SetServerRam(server, server.RamGb);
            LauncherService.PlayServer(server);
            surface.ShowToast($"Starting {server.Title}");
        }
        catch (Exception ex)
        {
            surface.ShowToast($"Couldn't start {server.Title}: {ex.Message}");
        }
    }

    private void OpenServerFolder(LauncherService.InstalledServer server)
    {
        try
        {
            LauncherService.OpenServerFolder(server);
        }
        catch (Exception ex)
        {
            surface.ShowToast($"Couldn't open the folder: {ex.Message}");
        }
    }

    // Small confirmation window (wider than tall) before deleting a server's folder.
    private void ConfirmDelete(LauncherService.InstalledServer server)
    {
        var cancel = new ActionButton("Cancel", secondary: true);
        var delete = new ActionButton("Delete") { Danger = true };
        cancel.OnClick = surface.CloseModal;
        delete.OnClick = () =>
        {
            try
            {
                launcher.DeleteServer(server);
                surface.CloseModal();
                surface.ShowToast($"Deleted {server.Title}");
                RenderInstalled();
            }
            catch (IOException)
            {
                surface.ShowToast($"Couldn't delete {server.Title}. Stop the server if it's running, then try again.");
            }
            catch (Exception ex)
            {
                surface.ShowToast($"Couldn't delete {server.Title}: {ex.Message}");
            }
        };

        surface.ShowModal(
            new ModalCard(440,
                new HeadingEl($"Delete {server.Title}?"),
                new RichTextEl(new (string, bool)[]
                {
                    ("This permanently deletes the ", false),
                    (Path.GetFileName(server.Directory), true),
                    (" folder, including its worlds and configs.", false)
                }),
                new ButtonRow(cancel, delete)),
            closeOnBackdrop: true,
            onEscape: surface.CloseModal);
    }

    // "More Options" window (RAM).
    private void OpenRamModal(LauncherService.InstalledServer server)
    {
        var slider = new SliderEl(1, Math.Max(32, server.RamGb), server.RamGb);
        slider.Changed = surface.Invalidate;

        var cancel = new ActionButton("Cancel", secondary: true);
        var save = new ActionButton("Save");
        cancel.OnClick = surface.CloseModal;
        save.OnClick = () =>
        {
            try
            {
                launcher.SetServerRam(server, slider.Value);
                surface.ShowToast($"{server.Title} will start with {slider.Value} GB");
                surface.CloseModal();
                RenderInstalled();
            }
            catch (Exception ex)
            {
                surface.ShowToast($"Couldn't update run.bat: {ex.Message}");
            }
        };

        surface.ShowModal(
            new ModalCard(380,
                new HeadingEl(server.Title),
                new RichTextEl("Server RAM"),
                new SliderRow(slider) { MarginTop = 18, MarginBottom = 8 },
                new RichTextEl("Saved to run.bat as -Xms and -Xmx. Leave at least 2–4 GB free for Windows.",
                    FontSpec.Body(13, FontStyle.Regular, 1.6f), Theme.Muted),
                new ButtonRow(cancel, save)),
            closeOnBackdrop: true,
            onEscape: surface.CloseModal,
            onKey: key =>
            {
                var step = key switch
                {
                    Keys.Left or Keys.Down => -1,
                    Keys.Right or Keys.Up => 1,
                    Keys.PageDown => -4,
                    Keys.PageUp => 4,
                    _ => 0
                };
                if (key == Keys.Home) slider.Set(slider.Min);
                else if (key == Keys.End) slider.Set(slider.Max);
                else if (step != 0) slider.Set(slider.Value + step);
            });
    }

    // ---------- settings ----------

    private void RenderSettings()
    {
        settingsPage.Items.Clear();
        settingsPage.Items.Add(new TitleEl("Settings"));

        var root = HasDownloadRoot() ? launcher.DownloadRoot : null;
        var reset = new ActionButton("Reset", secondary: true) { Disabled = root is null };
        reset.OnClick = ResetFolder;
        var change = new ActionButton("Change folder");
        change.OnClick = ChangeFolder;
        settingsPage.Items.Add(new SettingsSection(
            "Downloads folder",
            root ?? "Not set yet",
            root is null
                ? $"A \"{FolderName}\" folder is created on your first download."
                : "Each server gets its own folder in here, next to a run.bat that starts it.",
            reset, change));

        var setup = new ActionButton("Set up again");
        setup.OnClick = SetUpPlay;
        settingsPage.Items.Add(new SettingsSection(
            "Play button",
            null,
            "Play starts servers on your PC with their run.bat (Java must be installed). Run the setup again if Play stops working or you changed the downloads folder.",
            setup)
        {
            MarginTop = 16
        });

        surface.Relayout();
    }

    private void ChangeFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the downloads folder for server jars.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = HasDownloadRoot() ? launcher.DownloadRoot! : DefaultDownloadsFolder()
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            launcher.SetDownloadRoot(dialog.SelectedPath);
            surface.ShowToast($"Downloads will save to {FolderLabel()}");
        }
        catch (Exception ex)
        {
            surface.ShowToast($"Couldn't use that folder: {ex.Message}");
        }
        RenderSettings();
    }

    private void ResetFolder()
    {
        launcher.ResetDownloadRoot();
        surface.ShowToast("Reset. You will be asked again on the next download");
        RenderSettings();
    }

    // Rewrites run.bat + eula.txt for every installed server.
    private void SetUpPlay()
    {
        if (!HasDownloadRoot())
        {
            surface.ShowToast("Download a version first so there is a folder to set up");
            return;
        }

        try
        {
            foreach (var server in launcher.GetInstalledServers()) launcher.SetServerRam(server, server.RamGb);
            surface.ShowToast("Play is ready");
        }
        catch (Exception ex)
        {
            surface.ShowToast($"Couldn't set up Play: {ex.Message}");
        }
    }

    // ---------- helpers ----------

    private bool HasDownloadRoot() =>
        !string.IsNullOrWhiteSpace(launcher.DownloadRoot) && Directory.Exists(launcher.DownloadRoot);

    // Short label like the page shows ("Downloads/Minecraft Server Jars").
    private string FolderLabel()
    {
        var root = launcher.DownloadRoot;
        if (string.IsNullOrWhiteSpace(root)) return "your downloads folder";
        var trimmed = root.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return root;
        if (name.Equals(FolderName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed)?.TrimEnd('\\', '/') ?? string.Empty);
            if (!string.IsNullOrEmpty(parent)) return $"{parent}/{name}";
        }
        return name;
    }

    private static string DefaultDownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }

    private static string ServerTitle(string type, string version)
    {
        var name = type.ToLowerInvariant() switch
        {
            "craftbukkit" => "CraftBukkit",
            "spigot" => "Spigot",
            "paper" => "Paper",
            "purpur" => "Purpur",
            _ => type
        };
        return $"{name} {version}";
    }
}