using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Lantern.Core;

namespace Lantern.App;

public sealed class Preferences
{
    public bool AutoRefresh { get; set; } = true;
    public bool GridView { get; set; } = true;
    // Closing the window may hide to the tray instead of quitting.
    public bool CloseToTray { get; set; } = true;
    public string SelectedIdentity { get; set; } = "";
    public string Root { get; set; } = "";
    public string Extra { get; set; } = "";
    public List<MapEntry> Imported { get; set; } = new();
    public Dictionary<string, WorkshopItem> Metadata { get; set; } = new();
    public HostSettings Settings { get; set; } = new();
}

public partial class MainWindow : Window
{
    readonly string dataDir;
    readonly Workshop workshop = new();
    readonly CancellationTokenSource lifetime = new();
    Preferences prefs = new();
    Installation install = new("", Array.Empty<string>());
    List<MapEntry> maps = new();
    string steamRoot = "";
    string activeUser = "";
    bool enriching;
    RoomStatus? lastRoom;
    readonly HashSet<string> metadataAttempted = new();
    bool ready, busy;
    string? lastRules;
    TerminalWindow? terminal;
    // The dedicated server this launcher started, if any. Closing the launcher
    // stops it: a server we cannot administer is worse than no server.
    LocalServer? localServer;
    readonly bool smoke;
    readonly Stopwatch startup = Stopwatch.StartNew();
    readonly System.Windows.Threading.DispatcherTimer mapTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    string mapFingerprint = "", pendingFingerprint = "";
    DateTime pendingSince;
    bool checkingMaps;

    public MainWindow()
    {
        smoke = Environment.GetCommandLineArgs().Contains("--smoke");
        dataDir = Environment.GetEnvironmentVariable("LANTERN_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS2Lantern");
        InitializeComponent();
        BuildTray();
        Loaded += async (_, _) => await Init();
        Closing += OnClosing;
        mapTimer.Tick += async (_, _) => await CheckMapChanges();
        roomTimer.Tick += async (_, _) => await MonitorRoom();
    }
    async Task Init()
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            var path = Path.Combine(dataDir, "preferences.json");
            if (File.Exists(path)) { try { prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new(); } catch (JsonException) { Status("偏好文件损坏，使用默认设置；原文件保留为 preferences.invalid.json。"); File.Copy(path, Path.Combine(dataDir, "preferences.invalid.json"), true); } }
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamRoot = key?.GetValue("SteamPath") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            using var active = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            activeUser = Convert.ToString(active?.GetValue("ActiveUser")) ?? "";
            install = Discovery.Detect(steamRoot, string.IsNullOrWhiteSpace(prefs.Root) ? null : prefs.Root);
            RootBox.Text = install.Root; ExtraBox.Text = prefs.Extra;
            ModeBox.ItemsSource = GameMode.All; ModeBox.SelectedIndex = Math.Clamp(prefs.Settings.ModeIndex, 0, GameMode.All.Length - 1);
            LoadSettings(prefs.Settings); TrayCheck.IsChecked = prefs.CloseToTray; ready = true; HostChanged(null, null!);
            SetView(prefs.GridView);
            // Restore a deliberate official-map selection (including crash recovery).
            // New users still default to local workshop maps.
            if (prefs.SelectedIdentity.StartsWith("official:", StringComparison.Ordinal)) FilterBox.SelectedIndex = 3;
            await Scan();
            AutoRefreshCheck.IsChecked = prefs.AutoRefresh;
            if (!smoke) { mapTimer.Start(); roomTimer.Start(); }
            if (smoke)
            {
                if (Environment.GetCommandLineArgs().Contains("--community-compat-smoke"))
                {
                    var root = RootBox.Text.Trim();
                    var clientBefore = GameProcesses.Find(root, false)?.ProcessId;
                    var signaturePath = Path.Combine(root, "game", "bin", "win64", "vpk.signatures");
                    var originalSignature = File.ReadAllBytes(signaturePath);
                    await RestartDedicatedServer();
                    var first = await Terminal().GetRoomStatus();
                    if (first.Addon != "3783280189") throw new Exception("Cache initial load failed");
                    foreach (var id in new[] { "3648210230", "3783280189" })
                    {
                        var map = maps.First(m => m.Id == id && m.Local);
                        await SwitchExistingRoom(map, Settings(), root);
                        var actual = await Terminal().GetRoomStatus();
                        if (!RoomTransitions.Matches(actual, map) || !RoomTransitions.SameEndpoint(first, actual)) throw new Exception("Community transition failed");
                    }
                    await RestartDedicatedServer();
                    var restarted = await Terminal().GetRoomStatus();
                    if (restarted.Addon != "3783280189" || clientBefore != GameProcesses.Find(root, false)?.ProcessId
                        || !originalSignature.SequenceEqual(File.ReadAllBytes(signaturePath))) throw new Exception("Restart or client isolation failed");
                    File.WriteAllText(Path.Combine(dataDir, "community-compat-smoke.txt"), $"PASS Cache cold start -> Redline -> Cache, same endpoint {first.Identity}; restart -> Cache {restarted.Identity}; client PID {clientBefore} unchanged; original vpk.signatures byte-identical; isolated server executable {DedicatedRuntime.Executable(root)}");
                }
                if (Environment.GetCommandLineArgs().Contains("--crash-recovery-smoke"))
                {
                    var clientBefore = GameProcesses.Find(RootBox.Text.Trim(), false)?.ProcessId;
                    var failedMap = Selected().MapName;
                    await RestartDedicatedServer();
                    var recovered = await Terminal().GetRoomStatus();
                    if (recovered.Map != "de_dust2" || !RunText.Text.Contains("原工坊地图未修复")) throw new Exception("Crash recovery was not verified");
                    var firstPid = GameProcesses.Find(RootBox.Text.Trim(), true)!.ProcessId;
                    await RestartDedicatedServer();
                    RoomStatus? restarted = null;
                    for (int attempt = 0; attempt < 60; attempt++)
                    {
                        try { restarted = await Terminal().GetRoomStatus(); if (restarted.Map == "de_dust2") break; } catch { }
                        await Task.Delay(500);
                    }
                    var secondPid = GameProcesses.Find(RootBox.Text.Trim(), true)!.ProcessId;
                    if (restarted?.Map != "de_dust2" || firstPid == secondPid || clientBefore != GameProcesses.Find(RootBox.Text.Trim(), false)?.ProcessId)
                        throw new Exception("Safe map restart/client preservation failed");
                    File.WriteAllText(Path.Combine(dataDir, "crash-recovery-smoke.txt"), $"PASS {failedMap} engine crash -> Dust II recovery -> normal restart {firstPid} -> {secondPid}; client {clientBefore} unchanged; actual map {restarted.Map}");
                }
                if (Environment.GetCommandLineArgs().Contains("--restart-smoke"))
                {
                    await RestartDedicatedServer();
                    var firstRestart = await Terminal().GetRoomStatus();
                    if (!RoomTransitions.Matches(firstRestart, Selected())) throw new Exception("Restart loaded wrong map");
                    var oldServer = GameProcesses.Find(RootBox.Text.Trim(), true)!;
                    var oldClient = GameProcesses.Find(RootBox.Text.Trim(), false);
                    await RestartDedicatedServer();
                    var restarted = await Terminal().GetRoomStatus();
                    var newServer = GameProcesses.Find(RootBox.Text.Trim(), true)!;
                    var newClient = GameProcesses.Find(RootBox.Text.Trim(), false);
                    if (!RoomTransitions.Matches(restarted, Selected()) || oldServer.ProcessId == newServer.ProcessId || oldClient?.ProcessId != newClient?.ProcessId) throw new Exception("Restart lifecycle validation failed");
                    File.WriteAllText(Path.Combine(dataDir, "restart-smoke.txt"), $"PASS absent server recovery and running server graceful restart; dedicated PID {oldServer.ProcessId} -> {newServer.ProcessId}; client PID {newClient?.ProcessId} retained; verified map {restarted.Map}; identity {restarted.Identity}");
                }
                if (Environment.GetCommandLineArgs().Contains("--same-map-smoke"))
                {
                    var speedCheck = Stopwatch.StartNew();
                    launching = true;
                    try { await SwitchExistingRoom(Selected(), Settings(), RootBox.Text.Trim()); }
                    finally { launching = false; }
                    File.WriteAllText(Path.Combine(dataDir, "same-map-smoke.txt"), $"PASS current selected map reload -> verified actual map and endpoint; total {speedCheck.ElapsedMilliseconds} ms including engine loading\n" + RunText.Text);
                }
                if (Environment.GetCommandLineArgs().Contains("--invite-smoke"))
                {
                    // Proves the link the launcher hands out actually pulls a client in,
                    // instead of only proving that a string was assembled.
                    if (HostBox.SelectedIndex == 0) HostBox.SelectedIndex = 2;
                    var inviteRoot = RootBox.Text.Trim();
                    if (await Task.Run(() => GameProcesses.Find(inviteRoot, true)) == null)
                    {
                        RunText.Text = "邀请链接核验：先启动独立服务器…";
                        await LaunchSelected();
                    }
                    await Terminal().ConnectGame();
                    var before = await Terminal().GetRoomStatus();
                    // Loopback still proves the mechanism end to end; it cannot prove
                    // that a friend's network can reach this machine.
                    AddressBox.Text = "127.0.0.1";
                    var link = await FreshLink(true);
                    if (!link.StartsWith("steam://connect/127.0.0.1:", StringComparison.Ordinal)) throw new Exception("Invite link built from an unexpected address: " + link);
                    OpenUri(link);
                    RoomStatus? after = null;
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(1500);
                        try { after = await Terminal().GetRoomStatus(); } catch { continue; }
                        if (after.Humans > before.Humans) break;
                    }
                    if (after == null || after.Humans <= before.Humans)
                        throw new Exception($"Link {link} did not pull a client into the server within 90s (humans {before.Humans} -> {after?.Humans}).");
                    var client = GameProcesses.Find(RootBox.Text.Trim(), false);
                    if (client == null) throw new Exception("Server accepted a player but no client process is running.");
                    File.WriteAllText(Path.Combine(dataDir, "invite-smoke.txt"),
                        $"PASS {link} pulled client PID {client.ProcessId} into the server; humans {before.Humans} -> {after.Humans}; map {after.Map}; port {after.Port}");
                }
                if (Environment.GetCommandLineArgs().Contains("--transition-smoke")) await TestRoomTransitions();
                if (Environment.GetCommandLineArgs().Contains("--official-layout"))
                {
                    FilterBox.SelectedIndex = 3;
                    SetView(!Environment.GetCommandLineArgs().Contains("--list-layout"));
                    foreach (var map in maps.Where(m => m.Official))
                    {
                        if (map.Title == map.MapName || !OfficialMaps.HasCover(map.MapName)) throw new Exception("Missing official metadata: " + map.MapName);
                        var image = new BitmapImage(new Uri(map.PreviewPath));
                        if (image.PixelWidth < 100) throw new Exception("Invalid official cover: " + map.MapName);
                    }
                    File.WriteAllText(Path.Combine(dataDir, "official-check.txt"), $"PASS {maps.Count(m => m.Official)} official names and embedded images decode; {(prefs.GridView ? "grid" : "list")} view");
                }
                if (Environment.GetCommandLineArgs().Contains("--selection-test")) await TestSelection();
                if (Environment.GetCommandLineArgs().Contains("--catalog-test"))
                {
                    var group = maps.Where(m => !m.Official && m.Local).GroupBy(m => m.Id).First(g => g.Count() > 1);
                    SearchBox.Text = group.Key;
                    Filter();
                    if (MapList.Items.Count != 1) throw new Exception("Workshop package did not collapse to one card");
                    var variant = group.Last(); VariantsBox.SelectedItem = variant;
                    if (Selected().Identity != variant.Identity) throw new Exception("Variant selection lost");
                    Filter();
                    if (Selected().Identity != variant.Identity) throw new Exception("Refresh lost variant");
                    var check = Hosting.Build(Selected(), Settings() with { Kind = HostKind.Listen }, "catalog_test");
                    if (!check.Boot.Contains($"map_workshop {variant.Id} {variant.MapName}")) throw new Exception("Launch does not use selected variant");
                    File.WriteAllText(Path.Combine(dataDir, "catalog-test.txt"), $"PASS package {group.Key}: {group.Count()} variants -> 1 card; variant selection, refresh, launch args verified");
                }
                if (Environment.GetCommandLineArgs().Contains("--terminal-layout"))
                {
                    var window = new TerminalWindow("", Settings().Kind != HostKind.Listen) { Owner = this };
                    window.Show(); window.UpdateLayout(); await window.CaptureCategories(dataDir);
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(Path.Combine(dataDir, "terminal-layout.png"))) png.Save(output);
                    window.Close();
                }
                if (Environment.GetCommandLineArgs().Contains("--map-management-test")) await TestMapManagement();
                if (Environment.GetCommandLineArgs().Contains("--tab-layout"))
                {
                    for (int index = 0; index < SettingsTabs.Items.Count; index++)
                    {
                        SettingsTabs.SelectedIndex = index;
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        UpdateLayout();
                        var frame = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                        frame.Render(this); var shot = new PngBitmapEncoder(); shot.Frames.Add(BitmapFrame.Create(frame));
                        using var file = File.Create(Path.Combine(dataDir, $"tab-{index}.png")); shot.Save(file);
                    }
                    File.WriteAllText(Path.Combine(dataDir, "tab-layout.txt"), "PASS rendered " + SettingsTabs.Items.Count + " tabs: " + string.Join(", ", SettingsTabs.Items.Cast<TabItem>().Select(t => t.Header)));
                }
                if (Environment.GetCommandLineArgs().Contains("--terminal-smoke"))
                {
                    var window = Terminal(); window.Show(); await window.VerifyAndCapture(Path.Combine(dataDir, "terminal-smoke.png")); window.Close();
                }
                if (Environment.GetCommandLineArgs().Contains("--room-smoke"))
                {
                    SettingsTabs.SelectedIndex = 1; await ReadRoom();
                    if (lastRoom == null) throw new Exception("Room read failed: " + RoomInfo.Text);
                    File.WriteAllText(Path.Combine(dataDir, "room-smoke.txt"), "PASS real status_json -> map and identity validation -> connection UI; remote reachability not tested\n");
                }
                var readyMs = startup.ElapsedMilliseconds;
                using var readyProcess = Process.GetCurrentProcess();
                var readyMemory = readyProcess.WorkingSet64 / 1048576d;
                if (Environment.GetCommandLineArgs().Contains("--smoke-covers"))
                {
                    var items = await workshop.Fetch(maps.Where(m => !m.Official).Take(6).Concat(maps.Where(m => ReferenceEquals(m, MapList.SelectedItem))).Where(m => !m.Official).Select(m => m.Id));
                    foreach (var item in items)
                    {
                        prefs.Metadata[item.Id] = item;
                        try { await workshop.CachePreview(item, Path.Combine(dataDir, "covers")); } catch (HttpRequestException) { }
                    }
                    foreach (var map in maps) ApplyMetadata(map);
                    Filter();
                }
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
                UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.Combine(dataDir, "ui-smoke.png"))) encoder.Save(output);
                using var process = Process.GetCurrentProcess();
                File.WriteAllText(Path.Combine(dataDir, "smoke.json"), JsonSerializer.Serialize(new { readyMs, readyWorkingSetMiB = readyMemory, totalSmokeMs = startup.ElapsedMilliseconds, renderWorkingSetMiB = process.WorkingSet64 / 1048576d, mapCount = maps.Count, workshopCount = maps.Count(m => !m.Official), root = install.Root, selected = MapList.SelectedItem }, new JsonSerializerOptions { WriteIndented = true }));
                Close();
            }
        }
        catch (Exception ex) { Error(ex); if (smoke) { File.WriteAllText(Path.Combine(dataDir, "smoke-error.txt"), ex.ToString()); Close(); } }
    }
    async Task TestSelection()
    {
        var first = maps.First(m => m.Local && !m.Official);
        MapList.SelectedItem = first;
        // Reproduce an online metadata refresh while the selected object is in WPF's selection table.
        first.Title += " [metadata refreshed]";
        first.Tags += " refreshed";
        Filter();
        var target = maps.FirstOrDefault(m => m.MapName == "crazy_chickens") ?? maps.Last(m => m.Local && !m.Official);
        MapList.ScrollIntoView(target); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var card = (ListBoxItem)MapList.ItemContainerGenerator.ContainerFromItem(target);
        card.IsSelected = true;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!ReferenceEquals(MapList.SelectedItem, target) || SelectedTitle.Text != target.Title || MapList.SelectedItems.Count != 1)
            throw new InvalidOperationException($"Selection mismatch: card={target.MapName}, selected={(MapList.SelectedItem as MapEntry)?.MapName}, selectedCount={MapList.SelectedItems.Count}, title={SelectedTitle.Text}");
        var plan = Plan();
        if (!plan.Boot.Contains($"map_workshop {target.Id} {target.MapName}")) throw new InvalidOperationException("Launch plan does not match selected card");
        foreach (var grid in new[] { false, true })
        {
            SetView(grid); UpdateLayout();
            if (!ReferenceEquals(MapList.SelectedItem, target) || MapList.SelectedItems.Count != 1) throw new InvalidOperationException("View switch lost selection");
        }
        File.WriteAllText(Path.Combine(dataDir, "selection-test.txt"), "PASS metadata refresh -> virtualized card selection -> title -> launch command\n" + plan.Boot);
    }
    async Task TestMapManagement()
    {
        var originalExtra = ExtraBox.Text;
        var fixtureRoot = Path.Combine(dataDir, "map-test"); Directory.CreateDirectory(fixtureRoot);
        ExtraBox.Text = fixtureRoot; AutoRefreshCheck.IsChecked = true; await Scan();
        var selection = (MapList.SelectedItem as MapEntry)?.Identity;
        var package = Path.Combine(fixtureRoot, "9999912345"); Directory.CreateDirectory(package);
        using (var tree = new MemoryStream())
        {
            using var t = new BinaryWriter(tree, System.Text.Encoding.UTF8, true);
            void Z(string value) { t.Write(System.Text.Encoding.UTF8.GetBytes(value)); t.Write((byte)0); }
            Z("vmap_c"); Z("maps"); Z("lantern_fixture"); t.Write(0u); t.Write((ushort)0); t.Write((ushort)0x7FFF); t.Write(0u); t.Write(0u); t.Write((ushort)0xFFFF); Z(""); Z(""); Z("");
            using var file = new BinaryWriter(File.Create(Path.Combine(package, "fixture.vpk")));
            file.Write(0x55AA1234u); file.Write(2u); file.Write((uint)tree.Length); file.Write(new byte[16]); file.Write(tree.ToArray());
        }
        await CheckMapChanges(); await CheckMapChanges();
        var fixture = maps.Single(m => m.Id == "9999912345");
        if ((MapList.SelectedItem as MapEntry)?.Identity != selection) throw new Exception("Automatic scan lost selection");
        var target = MapDeletion.Package(fixture, Discovery.WorkshopRoots(install, ExtraRoots()));
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        await CheckMapChanges(); await CheckMapChanges();
        if (Directory.Exists(target) || maps.Any(m => m.Id == "9999912345")) throw new Exception("Recycled map remained in library");
        ExtraBox.Text = originalExtra; await Scan();
        File.WriteAllText(Path.Combine(dataDir, "map-management-test.txt"), "PASS download fixture -> automatic detection -> selection retained -> recycle fixture files -> automatic removal\n");
    }
    void LoadSettings(HostSettings s)
    {
        HostBox.SelectedIndex = (int)s.Kind; BotsBox.Text = s.Bots.ToString(); DifficultyBox.SelectedIndex = s.Difficulty;
        QuotaBox.SelectedIndex = s.QuotaMode == "fill" ? 1 : 0; PlayersBox.Text = s.MaxPlayers.ToString(); RoundsBox.Text = s.MaxRounds.ToString();
        TimeBox.Text = s.RoundMinutes.ToString(); FreezeBox.Text = s.FreezeSeconds.ToString(); MoneyBox.Text = s.StartMoney.ToString();
        BalanceCheck.IsChecked = s.AutoBalance; FriendlyCheck.IsChecked = s.FriendlyFire; CheatsCheck.IsChecked = s.Cheats; RulesCheck.IsChecked = s.ApplyRules;
        NameBox.Text = s.Name; PortBox.Text = s.Port.ToString();
        ConsoleCheck.IsChecked = s.EnableConsole;
    }
    HostSettings Settings()
    {
        int Number(TextBox box, string name) => int.TryParse(box.Text, out var v) ? v : throw new ArgumentException(name + "需要填写整数。");
        return new() { Kind = (HostKind)HostBox.SelectedIndex, ModeIndex = ModeBox.SelectedIndex, Bots = Number(BotsBox, "人机数量"), Difficulty = DifficultyBox.SelectedIndex,
            QuotaMode = QuotaBox.SelectedIndex == 1 ? "fill" : "normal", MaxPlayers = Number(PlayersBox, "人数上限"), MaxRounds = Number(RoundsBox, "回合上限"),
            RoundMinutes = Number(TimeBox, "回合时间"), FreezeSeconds = Number(FreezeBox, "冻结时间"), StartMoney = Number(MoneyBox, "初始金钱"),
            AutoBalance = BalanceCheck.IsChecked == true, FriendlyFire = FriendlyCheck.IsChecked == true, Cheats = CheatsCheck.IsChecked == true, ApplyRules = RulesCheck.IsChecked == true,
            Name = NameBox.Text, Port = Number(PortBox, "端口"), Token = TokenBox.Password,
            EnableConsole = ConsoleCheck.IsChecked == true };
    }
    void Save()
    {
        prefs.SelectedIdentity = (MapList.SelectedItem as MapEntry)?.Identity ?? prefs.SelectedIdentity;
        prefs.Root = RootBox.Text.Trim(); prefs.Extra = ExtraBox.Text;
        try { prefs.Settings = Settings() with { Token = "", ConsolePassword = "" }; } catch (ArgumentException) { }
        var file = Path.Combine(dataDir, "preferences.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true })); File.Move(file + ".tmp", file, true);
    }
    void Status(string text) { StatusText.Text = text; }
    void Error(Exception ex) { Status("未完成：" + ex.Message); if (!smoke) MessageBox.Show(this, ex.Message, "CS2 Lantern", MessageBoxButton.OK, MessageBoxImage.Information); }
    void SetBusy(bool value) { busy = value; ScanButton.IsEnabled = MetadataButton.IsEnabled = ImportButton.IsEnabled = !value; }
    string[] ExtraRoots() => ExtraBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
    string[] SubscriptionFiles() => new[] { Subscriptions.CachePath(steamRoot, activeUser), Path.Combine(steamRoot, "logs", "workshop_log.txt") };
    async Task CheckMapChanges()
    {
        if (busy || checkingMaps || !ready || AutoRefreshCheck.IsChecked != true) return;
        checkingMaps = true;
        try
        {
            var detected = Discovery.Detect(steamRoot, RootBox.Text.Trim()); var extras = ExtraRoots();
            var fingerprint = await Task.Run(() => MapChanges.Fingerprint(detected, extras, SubscriptionFiles()), lifetime.Token);
            if (fingerprint == mapFingerprint) { pendingFingerprint = ""; pendingSince = default; return; }
            if (pendingSince == default) pendingSince = DateTime.UtcNow;
            if (fingerprint != pendingFingerprint && DateTime.UtcNow - pendingSince < TimeSpan.FromSeconds(12)) { pendingFingerprint = fingerprint; AutoState.Text = "下载变化 · 等待文件稳定"; return; }
            // Two matching observations avoid exposing a partially copied/downloaded package.
            if (busy || lifetime.IsCancellationRequested) return;
            await Scan(); pendingFingerprint = ""; pendingSince = default;
            AutoState.Text = "已自动更新 · " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AutoState.Text = "文件正在变化 · 稍后重试"; }
        finally { checkingMaps = false; }
    }
    void AutoRefreshChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        prefs.AutoRefresh = AutoRefreshCheck.IsChecked == true;
        AutoState.Text = prefs.AutoRefresh ? "监测 Steam 下载 · 约 8 秒更新" : "自动识别已暂停";
        Save();
    }
    async Task Scan()
    {
        if (busy) return;
        SetBusy(true); Status("扫描本地 VPK 索引…");
        try
        {
            var clock = Stopwatch.StartNew();
            install = Discovery.Detect(steamRoot, RootBox.Text.Trim());
            var extra = ExtraBox.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            var beforeScan = await Task.Run(() => MapChanges.Fingerprint(install, extra, SubscriptionFiles()), lifetime.Token);
            var result = await Task.Run(() => Discovery.Scan(install, extra), lifetime.Token);
            maps = result.Maps;
            foreach (var id in Subscriptions.Read(Subscriptions.CachePath(steamRoot, activeUser)))
                if (!maps.Any(m => m.Id == id)) maps.Add(new MapEntry { Id = id, Title = "工坊条目 " + id, PendingReason = "已订阅 · 等待 Steam 下载" });
            foreach (var imported in prefs.Imported) if (!maps.Any(m => m.Id == imported.Id)) maps.Add(imported);
            foreach (var map in maps) ApplyMetadata(map);
            maps.RemoveAll(m => !m.Local && prefs.Metadata.TryGetValue(m.Id, out var meta) && !meta.IsMap);
            foreach (var map in maps.Where(m => !m.Local))
            {
                var reason = Subscriptions.DownloadResult(steamRoot, map.Id);
                if (reason.Length > 0) map.PendingReason = reason;
            }
            Filter(); Save();
            UpdateDownloadNotice();
            Status($"识别 {maps.Count(m => !m.Official && m.Local)} 张本地工坊地图 · {clock.ElapsedMilliseconds} ms" + (result.Warnings.Count > 0 ? $" · {result.Warnings.Count} 条扫描提示，见数据目录 scan-warnings.txt" : " · 可搜索名称、ID、文件名"));
            File.WriteAllLines(Path.Combine(dataDir, "scan-warnings.txt"), result.Warnings);
            mapFingerprint = beforeScan;
        }
        finally { SetBusy(false); }
        if (!smoke) _ = EnrichMissing();
    }
    void UpdateDownloadNotice()
    {
        var packages = MapCatalog.Cards(maps);
        var pending = packages.Count(m => !m.Local);
        var cache = new FileInfo(Subscriptions.CachePath(steamRoot, activeUser));
        var drive = new DriveInfo(Path.GetPathRoot(install.Root) ?? "C:\\");
        var free = drive.IsReady ? drive.AvailableFreeSpace / 1048576d : -1;
        DownloadNotice.Text = $"可开工坊包 {packages.Count(m => m.Local && !m.Official)} · 待下载 / 检查 {pending}" +
            (free >= 0 && free < 2048 ? $"\n{drive.Name} 仅剩 {free:F0} MB，可能无法完成工坊下载。" : "") +
            (pending > 0 ? "\n已订阅 ≠ 已下载。请在 Steam 下载页查看进度；游戏运行时下载可能暂停。" : "");
        DownloadNotice.ToolTip = cache.Exists ? "Steam 本地订阅缓存更新：" + cache.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + "；未同步的订阅可粘贴链接添加。" : "Steam 尚未生成当前账号订阅缓存，可先粘贴链接添加。";
    }
    async Task EnrichMissing()
    {
        if (enriching || lifetime.IsCancellationRequested) return;
        var ids = maps.Where(m => !m.Official && !prefs.Metadata.ContainsKey(m.Id) && !metadataAttempted.Contains(m.Id)).Select(m => m.Id).Distinct().ToArray();
        if (ids.Length == 0) return;
        enriching = true; foreach (var id in ids) metadataAttempted.Add(id);
        try
        {
            foreach (var item in await workshop.Fetch(ids, lifetime.Token))
            {
                prefs.Metadata[item.Id] = item;
                try { await workshop.CachePreview(item, Path.Combine(dataDir, "covers"), lifetime.Token); } catch (HttpRequestException) { }
            }
            foreach (var map in maps) ApplyMetadata(map);
            maps.RemoveAll(m => !m.Local && prefs.Metadata.TryGetValue(m.Id, out var meta) && !meta.IsMap);
            Filter(); Save(); UpdateDownloadNotice();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { if (!lifetime.IsCancellationRequested) Status("地图已识别，名称补全暂不可用；可稍后点“补全名称”。"); }
        finally { enriching = false; }
    }
    async void ScanClick(object sender, RoutedEventArgs e) { try { await Scan(); } catch (Exception ex) { Error(ex); } }
    void FilterChanged(object? sender, RoutedEventArgs e) { if (ready) Filter(); }
    void Filter(string? preferredIdentity = null)
    {
        var selected = preferredIdentity ?? (MapList.SelectedItem as MapEntry)?.Identity ?? prefs.SelectedIdentity;
        var q = SearchBox.Text.Trim(); var filter = FilterBox.SelectedIndex;
        var visible = MapCatalog.Cards(maps.Where(m => (filter switch { 1 => m.Local && !m.Official, 2 => !m.Local, 3 => m.Official, _ => true }) &&
            (q.Length == 0 || (m.Title + " " + m.Id + " " + m.MapName + " " + m.Tags).Contains(q, StringComparison.OrdinalIgnoreCase))), selected);
        MapList.ItemsSource = visible; MapList.SelectedItem = visible.FirstOrDefault(m => m.Identity == selected) ?? visible.FirstOrDefault(m => !m.Official && m.Id == selected.Split(':')[0]) ?? visible.FirstOrDefault();
        if (MapList.SelectedItem != null) MapList.ScrollIntoView(MapList.SelectedItem);
        CountText.Text = $"{visible.Count} / {MapCatalog.Cards(maps).Count} 项"; EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    void ApplyMetadata(MapEntry map)
    {
        if (map.Official)
        {
            map.Title = OfficialMaps.Name(map.MapName);
            map.PreviewPath = OfficialMaps.HasCover(map.MapName) ? $"pack://application:,,,/Assets/Maps/{map.MapName}.png" : "";
            return;
        }
        if (prefs.Metadata.TryGetValue(map.Id, out var metadata)) { map.Title = metadata.Title; map.Tags = metadata.Tags; map.ValidatedRemote = metadata.IsMap; }
        if (!map.Local)
        {
            map.PendingReason = "未下载完整 · 在 Steam 检查";
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(install.Root) ?? "C:\\");
                if (metadata?.Bytes > 0 && drive.IsReady && drive.AvailableFreeSpace < metadata.Bytes)
                    map.PendingReason = $"空间不足 · 地图约 {metadata.Bytes / 1048576d:F0} MB";
            }
            catch (ArgumentException) { }
        }
        var image = Path.Combine(dataDir, "covers", map.Id + ".jpg"); map.PreviewPath = File.Exists(image) ? image : "";
    }
    void MapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LaunchButton == null) return;
        LaunchButton.IsEnabled = MapList.SelectedItem is MapEntry { Local: true };
        if (VariantsBox == null) return;
        updatingVariants = true;
        try
        {
            var selected = MapList.SelectedItem as MapEntry;
            var variants = selected == null ? new List<MapEntry>() : maps.Where(m => MapCatalog.PackageKey(m) == MapCatalog.PackageKey(selected)).OrderBy(m => m.MapName).ToList();
            VariantsBox.ItemsSource = variants; VariantsBox.SelectedItem = selected;
            VariantsPanel.Visibility = variants.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            VariantsLabel.Text = $"包内地图 / 版本（{variants.Count} 个，共用一个下载包）";
        }
        finally { updatingVariants = false; }
    }
    bool updatingVariants;
    void VariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVariants || VariantsBox.SelectedItem is not MapEntry selected) return;
        if (!(selected.Title + " " + selected.Id + " " + selected.MapName + " " + selected.Tags).Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            SearchBox.Clear();
        Filter(selected.Identity); Save();
    }
    async void MetadataClick(object sender, RoutedEventArgs e)
    {
        if (busy) return; SetBusy(true); Status("从 Steam 获取地图元数据与封面…");
        try
        {
            var items = await workshop.Fetch(maps.Where(m => !m.Official).Select(m => m.Id), lifetime.Token);
            foreach (var item in items) prefs.Metadata[item.Id] = item;
            int failed = 0;
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = lifetime.Token }, async (item, ct) =>
            { try { await workshop.CachePreview(item, Path.Combine(dataDir, "covers"), ct); } catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { Interlocked.Increment(ref failed); } });
            foreach (var map in maps) ApplyMetadata(map); Filter(); Save(); Status($"已补全 {items.Count} 个工坊条目" + (failed > 0 ? $"，{failed} 张封面暂不可用" : "，封面已缓存"));
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (busy) return; SetBusy(true);
        try
        {
            var ids = Workshop.ParseIds(WorkshopInput.Text); Status("验证工坊条目…");
            var items = await workshop.Fetch(ids, lifetime.Token);
            var accepted = items.Where(i => i.IsMap).ToArray();
            if (accepted.Length == 0) throw new ArgumentException("没有找到公开的 CS2 地图条目。");
            foreach (var item in accepted)
            {
                prefs.Metadata[item.Id] = item;
                try { await workshop.CachePreview(item, Path.Combine(dataDir, "covers"), lifetime.Token); } catch (HttpRequestException) { }
                if (!prefs.Imported.Any(m => m.Id == item.Id)) prefs.Imported.Add(new MapEntry { Id = item.Id, Title = item.Title, ValidatedRemote = true, PendingReason = "链接已添加 · 等待 Steam 下载" });
            }
            SearchBox.Clear(); FilterBox.SelectedIndex = 0; Save(); SetBusy(false); await Scan();
            MapList.SelectedItem = maps.FirstOrDefault(m => m.Id == accepted[0].Id);
            OpenUri("steam://url/CommunityFilePage/" + accepted[0].Id);
            Status($"已添加 {accepted.Length} 张地图；Steam 下载完成后会自动转为可开图。多个链接可一次粘贴。");
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    void HostChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HostHint == null) return;
        JoinMainButton.Visibility = HostBox.SelectedIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
        RestartServerButton.Visibility = JoinMainButton.Visibility;
        LaunchButton.Content = HostBox.SelectedIndex == 0 ? "启动所选地图  →" : "启动 / 切换服务器地图  →";
        // The console is what makes runtime management (map switching, bots,
        // room verification) possible, and only the launcher-owned dedicated
        // server can have one. It is mandatory there, so it is not offered as a
        // toggle that could only produce a broken server.
        ConsoleCheck.IsChecked = true;
        ConsoleCheck.IsEnabled = false;
        roomGeneration++; transitionUnverified = false; observedRoom = null; roomNotice = null;
        if (AddressBox != null) { lastRoom = null; AddressBox.Clear(); }
        HostHint.Text = HostBox.SelectedIndex switch
        {
            1 => "独立进程开服，启动器自己拉起服务器，因此换图、人数与人机管理都可用。关闭启动器会同时停服。",
            2 => "服务器独立运行；启动地图后，点击下方“本机加入此服务器”进入游戏。好友在“好友联机”页复制加入命令。",
            _ => "只启动一个游戏进程。加载地图后打开“好友联机”，读取实际房间并复制加入指令。此模式无法换图或管理人数：CS2 已移除终端端口，新通道只能读取启动器自己拉起的进程。"
        };
    }
    void ModeChanged(object sender, SelectionChangedEventArgs e) { if (ready && ModeBox.SelectedIndex == 0) RulesCheck.IsChecked = false; }
    MapEntry Selected() => MapList.SelectedItem as MapEntry ?? throw new ArgumentException("请先选择地图。");
    LaunchPlan Plan() => Hosting.Build(Selected(), Settings(), DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"));
    void PreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = Selected(); var plan = Plan(); var args = plan.Arguments.ToArray();
            var tokenIndex = Array.IndexOf(args, "+sv_setsteamaccount"); if (tokenIndex >= 0) args[tokenIndex + 1] = "<hidden>";
            var rules = plan.Rules;
            ShowText("启动配置预览 · " + selected.Title, $"地图：{selected.Title}\n内部名称：{selected.MapName}\n工坊 ID：{selected.Id}\n\n" + string.Join(" ", args) + "\n\n// " + plan.BootName + "\n" + plan.Boot + "\n// " + plan.RulesName + "\n" + rules);
        }
        catch (Exception ex) { Error(ex); }
    }
    void ShowText(string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(18) };
        new Window { Title = title, Content = box, Owner = this, Width = 720, Height = 600, Background = Background, WindowStartupLocation = WindowStartupLocation.CenterOwner }.Show();
    }
    bool launching;
    async void LaunchClick(object sender, RoutedEventArgs e) => await LaunchSelected();
    async Task LaunchSelected()
    {
        if (launching) { Status("正在处理上一次开图请求，请等待状态结果；不会重复启动游戏。"); return; }
        launching = true;
        serverExitedDuringLaunch = false;
        terminal?.ClearFailureOutput();
        var requestedTitle = (MapList.SelectedItem as MapEntry)?.Title ?? "所选地图";
        HostBox.IsEnabled = false;
        try
        {
            var s = Settings(); var plan = Plan(); var root = RootBox.Text.Trim(); var expected = Selected();
            if (s.Kind == HostKind.DedicatedP2P && !RelayPatch.Enabled(root)) throw new ArgumentException("请先在“服务器”页启用实验 P2P 配置。");
            var existing = await Task.Run(() => GameProcesses.Find(root, s.Kind != HostKind.Listen));
            if (existing != null && existing.RequestedMaxPlayers != s.MaxPlayers)
                throw new InvalidOperationException($"人数启动参数已改为 {s.MaxPlayers}，当前进程仍为 {existing.RequestedMaxPlayers?.ToString() ?? "未知"}。换图不会更新启动参数。本机模式请使用游戏终端的“启用终端并重新启动”入口；独立服务器请正常停服后再启动。参数也不保证突破地图／模式限制，开服后仍需核对人数。");
            var exec = Hosting.WriteConfigs(root, plan); lastRules = RoomTransitions.ConsoleCommands(plan.Rules);
            if (s.Kind == HostKind.Listen)
            {
                var hasGame = existing != null;
                if (hasGame)
                {
                    var console = Terminal();
                    console.Show(); console.Activate();
                    console.ReportRepair("本机边玩边开模式不再支持换图与终端管理。CS2 已移除终端端口，而新通道要求启动器自己拉起进程才能读到回显；Steam 启动的客户端做不到。请改用独立服务器模式，当前游戏未受影响。");
                    Status("本机模式无终端；换图与人数管理请改用独立服务器。"); return;
                }
                var steam = Path.Combine(steamRoot, "steam.exe"); if (!File.Exists(steam)) throw new FileNotFoundException("没有找到 Steam 客户端，请先安装或登录 Steam。");
                var start = new ProcessStartInfo(steam) { UseShellExecute = false, WorkingDirectory = steamRoot };
                start.ArgumentList.Add("-applaunch"); start.ArgumentList.Add("730"); foreach (var a in plan.Arguments) start.ArgumentList.Add(a);
                Process.Start(start)?.Dispose();
                RunText.Text = "已向 Steam 发送开图请求。首次请检查 Open Party；地图加载后若规则被覆盖，点“复制 / 应用规则”。";
            }
            else
            {
                if (existing != null)
                {
                    await SwitchExistingRoom(expected, s, root);
                    return;
                }
                if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(p => p.Port == s.Port))
                    throw new InvalidOperationException($"游戏 UDP 端口 {s.Port} 已被占用，未启动第二个服务器。请在“好友联机”页修改游戏 UDP 端口，或结束旧房间后再开服；这不是终端端口。");
                if (!s.EnableConsole) throw new InvalidOperationException("独立服务器需要启用私服终端模式，以使用隔离运行目录。");
                var serverExe = await Task.Run(() => DedicatedRuntime.Prepare(root));
                // The launcher must own the process: commands go out on a named
                // pipe, but the engine writes its output to stdout, which is only
                // redirectable for a process we start ourselves.
                localServer?.Dispose();
                localServer = LocalServer.Start(serverExe, plan, Path.GetDirectoryName(serverExe)!);
                var launchedServer = localServer.Process;
                RunText.Text = "独立服务器已启动。关闭启动器会同时停服，不会留在后台。";
                {
                    RunText.Text = expected.Official ? "正在启动并核验服务器：" + expected.Title
                        : "正在初始化独立服务器（临时加载 Dust II），等待 Steam 登录后切换到：" + expected.Title;
                    var target = Terminal();
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    bool requested = expected.Official; string lastError = "等待服务器启动";
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(250);
                        if (launchedServer.HasExited)
                        {
                            ReportServerExited();
                            throw new InvalidOperationException(ServerExitedMessage);
                        }
                        try
                        {
                            // A failed Connect disposes the connection; use the terminal's reconnect lifecycle.
                            var actual = await target.GetRoomStatus();
                            if (!requested && Hosting.ParseServerIdentity("steamid : " + actual.Identity) != null)
                            {
                                await target.SendCommand(RoomTransitions.ConsoleCommands(plan.Boot)); requested = true;
                                RunText.Text = "正在加载工坊地图：" + expected.Title;
                            }
                            else if (requested && RoomTransitions.Matches(actual, expected)
                                && Hosting.ParseServerIdentity("steamid : " + actual.Identity) != null)
                            {
                                Save(); DisplayRoom(actual);
                                RunText.Text = "独立服务器已就绪：" + expected.Title + "。点“本机加入此服务器”直接进入，或复制邀请链接发给好友。";
                                Status("已核实实际地图和工坊 ID。本机可直接点“本机加入此服务器”，好友用邀请链接进入。");
                                return;
                            }
                            lastError = requested ? "当前地图仍为 " + actual.Map : "等待 Steam 服务器身份";
                        }
                        catch (Exception ex) { lastError = ex.Message; }
                    }
                    throw new InvalidOperationException("所选地图尚未就绪：" + lastError + "。请查看游戏终端输出；不要将初始化地图当成开服成功。");
                }
            }
            Save(); Status("启动请求已发送；这不代表地图已就绪或好友已可连接。");
        }
        catch (Exception ex)
        {
            if (serverExitedDuringLaunch && HostBox.SelectedIndex != 0)
            {
                try { await RecoverOnSafeMap(requestedTitle); }
                catch (Exception recoveryError) { RunText.Text = "目标地图加载失败；安全恢复也未完成：" + recoveryError.Message; Error(recoveryError); }
            }
            else { RunText.Text = "本次开图未完成核验：" + ex.Message; Error(ex); }
        }
        finally { launching = false; HostBox.IsEnabled = true; }
    }
    async void ApplyRulesClick(object sender, RoutedEventArgs e)
    {
        try { var plan = Plan(); Hosting.WriteConfigs(RootBox.Text.Trim(), plan); lastRules = RoomTransitions.ConsoleCommands(plan.Rules);
            if (terminal?.IsConnected == true) { await terminal.SendCommand(lastRules); Status("规则已发送到当前游戏终端，请查看执行输出。"); }
            else { Clipboard.SetText(lastRules); Status("规则命令已复制，可在房主控制台粘贴执行。"); } }
        catch (Exception ex) { Error(ex); }
    }
    async void CopyJoin(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(await FreshJoin(false)); Status("已核验并复制当前房间加入命令。"); }
        catch (Exception ex) { Error(ex); }
    }
    void ParseIdentity(object sender, RoutedEventArgs e)
    {
        var id = Hosting.ParseServerIdentity(StatusInput.Text); if (id == null) { Status("没有找到 steamid : [A:…] 服务器身份，请粘贴完整 status 输出。"); return; }
        AddressBox.Text = id; Status("已提取当次服务器身份；可复制加入命令。");
    }
    void DownloadPage(object sender, RoutedEventArgs e) { try { OpenUri("steam://open/downloads"); } catch (Exception ex) { Error(ex); } }
    void ClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); FilterBox.SelectedIndex = 1; }
    void ShowConnection(object sender, RoutedEventArgs e) { SettingsTabs.SelectedIndex = 1; LoadAddresses(); }
    public sealed record NetworkAddress(string Address, string Label);
    static int NetworkPriority(NetworkAddress a) =>
        a.Label.Contains("etgame", StringComparison.OrdinalIgnoreCase) || a.Label.Contains("EasyTier", StringComparison.OrdinalIgnoreCase) ? 2 :
        a.Label.Contains("Radmin", StringComparison.OrdinalIgnoreCase) || a.Label.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    void LoadAddresses()
    {
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => new NetworkAddress(a.Address.ToString(), n.Name + " · " + a.Address)))
            .Where(a => !a.Address.StartsWith("127.") && !a.Address.StartsWith("169.254."))
            .OrderByDescending(NetworkPriority).ToList();
        NetworkAddresses.ItemsSource = addresses;
    }
    void UseNetworkAddress(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkAddresses.SelectedItem is not NetworkAddress address) return;
        AddressBox.Text = address.Address;
        RoomInfo.Text = "已选网络地址；好友须加入同一组网 / 局域网。普通家庭异地网络不能直接使用内网 IP。";
    }
    async void ReadRoomClick(object sender, RoutedEventArgs e) => await ReadRoom();
    async Task ReadRoom()
    {
        if (launching) { Status("正在换图，完成后会自动更新房间。"); return; }
        ReadRoomButton.IsEnabled = false; int generation = roomGeneration;
        try
        {
            var room = await Terminal().GetRoomStatus();
            if (generation != roomGeneration || launching) return;
            transitionUnverified = false; DisplayRoom(room); Status("实际房间已更新，分享前还会再次核验。");
        }
        catch (Exception ex) { InvalidateRoom("房间未就绪：" + ex.Message); }
        finally { ReadRoomButton.IsEnabled = true; }
    }

    async void CopyInvite(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = await FreshJoin(false);
            if (AddressBox.Text.Trim() == "127.0.0.1") throw new ArgumentException("127.0.0.1 不能分享给好友，请先读取房间或选择组网地址。");
            var link = await FreshLink();
            Clipboard.SetText("点这个链接直接进服，不用装启动器，也不用开控制台：\n" + link +
                "\n\n如果链接没反应，再在 CS2 控制台（~）粘贴：\n" + command +
                "\n\n若使用组网 IP，请先加入房主同一个 EasyTier / Radmin VPN / ZeroTier 网络。\n加入失败时请保留游戏控制台报错给房主。");
            Status("好友加入说明已复制，请发给好友。");
        }
        catch (Exception ex) { Error(ex); }
    }
    void ChooseExe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CS2 executable|cs2.exe", Title = "选择 game\\bin\\win64\\cs2.exe" };
        if (dialog.ShowDialog(this) != true) return;
        var root = Directory.GetParent(dialog.FileName)?.Parent?.Parent?.Parent?.FullName;
        if (root != null && File.Exists(Path.Combine(root, "game", "bin", "win64", "cs2.exe"))) RootBox.Text = root;
        else Status("该路径不符合 CS2 目录结构。");
    }
    void EnableRelay(object sender, RoutedEventArgs e) { try { EnsureStopped(); RelayPatch.Enable(RootBox.Text.Trim()); Status("P2P 配置已备份并启用。请选择独立 P2P 模式开服，双机连接待实测。"); } catch (Exception ex) { Error(ex); } }
    void RestoreRelay(object sender, RoutedEventArgs e) { try { EnsureStopped(); RelayPatch.Restore(RootBox.Text.Trim()); Status("已恢复启用前的原始配置。"); } catch (Exception ex) { Error(ex); } }
    static void EnsureStopped() { var processes = Process.GetProcessesByName("cs2"); var running = processes.Length > 0; foreach (var p in processes) p.Dispose(); if (running) throw new InvalidOperationException("请退出 CS2 和服务器后再调整 P2P 文件。"); }
    static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    void WorkshopBrowse(object sender, RoutedEventArgs e) { try { OpenUri("steam://openurl/https://steamcommunity.com/app/730/workshop/"); } catch (Exception ex) { Error(ex); } }
    async void DeleteMapClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var map = Selected();
            if (map.Official) throw new ArgumentException("游戏自带地图不能在这里删除。");
            if (!map.Local && Subscriptions.Read(Subscriptions.CachePath(steamRoot, activeUser)).Contains(map.Id))
                throw new ArgumentException("这是 Steam 订阅条目，当前没有可删除的完整本地地图。请在 Steam 工坊页取消订阅；订阅缓存更新后会自动移除。");
            var package = map.Local ? MapDeletion.Package(map, Discovery.WorkshopRoots(install, ExtraRoots())) : null;
            var siblings = maps.Where(m => m.Id == map.Id).Select(m => m.Title).Distinct();
            var message = package == null ? $"移除待下载条目：{map.Title}？" :
                $"将以下地图包移入回收站，同时从启动器移除：\n\n{string.Join("\n", siblings)}\n\n目录：{package}\n\n同一工坊包内的文件都会删除。Steam 订阅不会取消；保持订阅时 Steam 可能重新下载。可先在 Steam 工坊页取消订阅。";
            if (MessageBox.Show(this, message, "删除地图与本地文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            SetBusy(true);
            if (package != null)
            {
                MapDeletion.Validate(package, map.Id);
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(package, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            }
            prefs.Imported.RemoveAll(m => m.Id == map.Id); Save();
            SetBusy(false); await Scan();
            Status(package == null ? "已移除待下载条目。" : "本地包已移入回收站；Steam 仍订阅时保留待下载条目，其他库中的副本会保留。清空回收站才会释放磁盘空间。");
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }
    void OpenSelectedWorkshop(object sender, RoutedEventArgs e) { try { var map = Selected(); if (map.Official) { Status("这是游戏自带地图。"); return; } OpenUri("steam://url/CommunityFilePage/" + Workshop.ParseId(map.Id)); } catch (Exception ex) { Error(ex); } }
    void OpenData(object sender, RoutedEventArgs e) { try { OpenUri(dataDir); } catch (Exception ex) { Error(ex); } }
    TerminalWindow Terminal()
    {
        bool dedicated = HostBox.SelectedIndex != 0;
        if (terminal != null && terminal.Dedicated != dedicated) terminal.Close();
        if (terminal != null) return terminal;
        terminal = new TerminalWindow(RootBox.Text.Trim(), dedicated) { Owner = this };
        terminal.EnableTerminalRequested = EnableClientTerminal;
        terminal.Closed += (_, _) => terminal = null;
        return terminal;
    }
    void TerminalClick(object sender, RoutedEventArgs e) { var window = Terminal(); window.Show(); window.Activate(); }
    // The old repair path restarted the Steam-launched client with -netconport so
    // a terminal could attach. Both halves are gone: the port switch no longer
    // exists, and the replacement (pipe + stdout) needs the launcher to own the
    // process. Reimplementing this for Listen mode is not possible; the honest
    // answer is to send the user to the dedicated-server mode.
    Task EnableClientTerminal()
    {
        var message = "本机边玩边开模式已无法启用终端。\n\nCS2 在 1.41.7.8 移除了终端端口；现在的控制通道是命名管道，而命令回显只能从启动器自己拉起的进程 stdout 读到。通过 Steam 启动的客户端两项都不满足。\n\n请改用“独立服务器”模式：启动器会自己拉起服务器进程，换图、人数与人机管理都可用。当前游戏未受影响。";
        terminal?.ReportRepair(message.Replace("\n\n", " "));
        MessageBox.Show(this, message, "此模式不支持终端", MessageBoxButton.OK, MessageBoxImage.Information);
        return Task.CompletedTask;
    }
    void SetView(bool grid)
    {
        prefs.GridView = grid;
        MapList.ItemsPanel = (ItemsPanelTemplate)Resources[grid ? "GridMapPanel" : "ListMapPanel"];
        MapList.ItemTemplate = (DataTemplate)Resources[grid ? "GridMapTemplate" : "ListMapTemplate"];
        GridViewButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(grid ? "#725030" : "#253246"));
        ListViewButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(grid ? "#253246" : "#725030"));
    }
    void GridViewClick(object sender, RoutedEventArgs e) { SetView(true); Save(); }
    void ListViewClick(object sender, RoutedEventArgs e) { SetView(false); Save(); }
    void TrayCheckClick(object sender, RoutedEventArgs e) { prefs.CloseToTray = TrayCheck.IsChecked == true; Save(); }
}
