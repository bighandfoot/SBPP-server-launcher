namespace server_launcher;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class LauncherService
{
    private const string BuildToolsUrl = "https://hub.spigotmc.org/jenkins/job/BuildTools/lastSuccessfulBuild/artifact/target/BuildTools.jar";
    private const int DefaultRamGb = 4;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private static readonly Regex ServerJarPattern = new("^(spigot|craftbukkit|paper|purpur)-(\\d+(?:\\.\\d+)+)(?:-(\\d+))?\\.jar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerLauncher", "settings.json");
    private readonly Dictionary<string, IReadOnlyList<LauncherVersion>> versionCache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record LauncherVersion(string Category, string Title, string Subtitle, string Version, string? ServerType, string? CopyCommand = null, bool IsBuildTools = false);
    internal sealed record DownloadTarget(string Url, string Filename, string? ServerType, string? Version, string? Note = null);
    internal sealed record InstalledServer(string Directory, string JarName, string Type, string Version, string? Build, string Category, string Title, long Size, DateTime LastModified, int RamGb);
    private sealed record Settings(string? DownloadRoot);

    public string? DownloadRoot { get; private set; }

    public static int TotalRamGb { get; } = DetectTotalRamGb();
    public static int MaxRamGb => Math.Max(1, TotalRamGb - 4);
    private static int DefaultRam => Math.Min(DefaultRamGb, MaxRamGb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static int DetectTotalRamGb()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        var bytes = GlobalMemoryStatusEx(ref status)
            ? status.TotalPhys
            : (ulong)Math.Max(0L, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        return Math.Max(1, (int)Math.Round(bytes / 1073741824.0));
    }

    public LauncherService()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                if (!string.IsNullOrWhiteSpace(settings?.DownloadRoot) && Directory.Exists(settings.DownloadRoot))
                    DownloadRoot = settings.DownloadRoot;
            }
        }
        catch
        {
            DownloadRoot = null;
        }
    }

    public async Task<IReadOnlyList<LauncherVersion>> LoadVersionsAsync(string category, CancellationToken cancellationToken = default)
    {
        if (versionCache.TryGetValue(category, out var cached)) return cached;

        IReadOnlyList<LauncherVersion> versions = category switch
        {
            "Spigot" => await LoadSpigotAsync(cancellationToken),
            "Bukkit" => await LoadBukkitAsync(cancellationToken),
            "Paper" => await LoadPaperAsync(cancellationToken),
            "Purpur" => await LoadPurpurAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        versionCache[category] = versions;
        return versions;
    }

    public async Task<DownloadTarget> ResolveDownloadAsync(LauncherVersion item, CancellationToken cancellationToken = default)
    {
        if (item.IsBuildTools)
            return new DownloadTarget(BuildToolsUrl, "BuildTools.jar", null, null);

        if (item.Category == "Spigot")
        {
            var index = await GetSpigotIndexAsync(cancellationToken);
            var info = index.TryGetValue(item.Version, out var value) ? value : default;
            if (TryGetString(info, "latest", "jarUrl", out var url))
                return new DownloadTarget(url, $"spigot-{item.Version}.jar", "spigot", item.Version);

            var result = await GetJsonAsync($"https://mcjars.app/api/v1/builds/SPIGOT/{item.Version}/latest", cancellationToken);
            if (TryGetString(result, "build", "jarUrl", out url))
                return new DownloadTarget(url, $"spigot-{item.Version}.jar", "spigot", item.Version);
            throw new InvalidOperationException($"No Spigot jar is available for {item.Version}.");
        }

        if (item.Category == "Paper")
        {
            using var response = await Http.GetAsync($"https://fill.papermc.io/v3/projects/paper/versions/{item.Version}/builds", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var builds = document.RootElement;
            if (builds.ValueKind != JsonValueKind.Array || builds.GetArrayLength() == 0)
                throw new InvalidOperationException($"No Paper builds are available for {item.Version}.");

            var ordered = builds.EnumerateArray().OrderByDescending(build => GetInt(build, "id")).ToArray();
            var selected = ordered.FirstOrDefault(build => GetString(build, "channel") == "STABLE");
            var stable = selected.ValueKind != JsonValueKind.Undefined;
            if (!stable) selected = ordered[0];
            if (!selected.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("server:default", out var download) || !TryGetString(download, "url", out var paperUrl))
                throw new InvalidOperationException($"Paper {item.Version} build #{GetInt(selected, "id")} has no server jar.");

            var buildNumber = GetInt(selected, "id");
            var filename = TryGetString(download, "name", out var name) ? name : $"paper-{item.Version}-{buildNumber}.jar";
            var note = stable ? null : $"No stable build for {item.Version} yet; downloading {GetString(selected, "channel").ToLowerInvariant()} build #{buildNumber}.";
            return new DownloadTarget(paperUrl, filename, "paper", item.Version, note);
        }

        if (item.Category == "Purpur")
            return new DownloadTarget($"https://api.purpurmc.org/v2/purpur/{item.Version}/latest/download", $"purpur-{item.Version}.jar", "purpur", item.Version);

        throw new InvalidOperationException("No download is available for this item.");
    }

    public async Task DownloadAndInstallAsync(DownloadTarget target, IProgress<(long Received, long? Total)>? progress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(DownloadRoot))
            throw new InvalidOperationException("Choose a downloads folder in Settings first.");

        var destinationDirectory = target.ServerType is null
            ? DownloadRoot
            : Path.Combine(DownloadRoot, ServerTitle(target.ServerType, target.Version!));
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, target.Filename);

        using var response = await Http.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                received += count;
                progress?.Report((received, total));
            }
        }

        if (target.ServerType is not null)
        {
            foreach (var file in Directory.EnumerateFiles(destinationDirectory))
            {
                var match = ServerJarPattern.Match(Path.GetFileName(file));
                if (match.Success && match.Groups[1].Value.Equals(target.ServerType, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(file).Equals(target.Filename, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }

            var ram = ReadRam(destinationDirectory) ?? DefaultRam;
            WriteRunBat(destinationDirectory, target.Filename, ram, ServerTitle(target.ServerType, target.Version!));
            File.WriteAllText(Path.Combine(destinationDirectory, "eula.txt"), "eula=true\r\n");
        }
    }

    public IReadOnlyList<InstalledServer> GetInstalledServers()
    {
        if (string.IsNullOrWhiteSpace(DownloadRoot) || !Directory.Exists(DownloadRoot)) return [];

        var servers = new List<InstalledServer>();
        foreach (var directory in Directory.EnumerateDirectories(DownloadRoot))
        {
            var jars = Directory.EnumerateFiles(directory).Select(path => (Path: path, Match: ServerJarPattern.Match(Path.GetFileName(path))))
                .Where(item => item.Match.Success)
                .OrderByDescending(item => File.GetLastWriteTimeUtc(item.Path))
                .ToArray();
            if (jars.Length == 0) continue;

            var selected = jars[0];
            var match = selected.Match;
            var type = match.Groups[1].Value.ToLowerInvariant();
            var version = match.Groups[2].Value;
            var category = type == "craftbukkit" ? "Bukkit" : char.ToUpperInvariant(type[0]) + type[1..];
            var info = new FileInfo(selected.Path);
            servers.Add(new InstalledServer(directory, info.Name, type, version, match.Groups[3].Success ? match.Groups[3].Value : null,
                category, ServerTitle(type, version), info.Length, info.LastWriteTime, ReadRam(directory) ?? DefaultRam));
        }

        return servers.OrderBy(server => server.Version, Comparer<string>.Create(CompareVersionsDescending)).ToArray();
    }

    public void SetDownloadRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        DownloadRoot = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(DownloadRoot)));
    }

    public void ResetDownloadRoot()
    {
        DownloadRoot = null;
        if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
    }

    public void SetServerRam(InstalledServer server, int ramGb)
    {
        WriteRunBat(server.Directory, server.JarName, ramGb, server.Title);
        File.WriteAllText(Path.Combine(server.Directory, "eula.txt"), "eula=true\r\n");
    }

    public static void PlayServer(InstalledServer server)
    {
        var runBat = Path.Combine(server.Directory, "run.bat");
        if (!File.Exists(runBat))
            throw new FileNotFoundException("This server is missing run.bat. Download the server again to create its launcher files.", runBat);

        Process.Start(new ProcessStartInfo { FileName = runBat, WorkingDirectory = server.Directory, UseShellExecute = true });
    }

    public void DeleteServer(InstalledServer server)
    {
        var directory = new DirectoryInfo(server.Directory);
        if (!directory.Exists) return;

        var root = string.IsNullOrWhiteSpace(DownloadRoot) ? null : Path.GetFullPath(DownloadRoot).TrimEnd('\\', '/');
        var parent = directory.Parent is null ? null : Path.GetFullPath(directory.Parent.FullName).TrimEnd('\\', '/');
        if (root is null || !string.Equals(root, parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("That folder isn't inside the downloads folder, so it wasn't deleted.");

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            if (file.IsReadOnly) file.IsReadOnly = false;
        directory.Delete(recursive: true);
    }


    public Dictionary<string, int> GetRunningServers(IEnumerable<InstalledServer> servers)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = servers.ToList();
        if (list.Count == 0) return result;

        foreach (var process in Process.GetProcessesByName("java").Concat(Process.GetProcessesByName("javaw")))
        {
            using (process)
            {
                var commandLine = TryGetCommandLine(process.Id);
                if (string.IsNullOrEmpty(commandLine)) continue;
                var match = list.FirstOrDefault(server => commandLine.Contains(Path.Combine(server.Directory, server.JarName), StringComparison.OrdinalIgnoreCase))
                    ?? list.FirstOrDefault(server => commandLine.Contains(server.JarName, StringComparison.OrdinalIgnoreCase));
                if (match is not null) result[match.Directory] = process.Id;
            }
        }
        return result;
    }

    public void PrepareRcon(InstalledServer server)
    {
        var path = Path.Combine(server.Directory, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var properties = ReadProperties(server.Directory);

        var password = properties.GetValueOrDefault("rcon.password");
        if (string.IsNullOrWhiteSpace(password)) password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var port = int.TryParse(properties.GetValueOrDefault("rcon.port"), out var existing) && IsPortFree(existing) ? existing : FindFreePort();

        SetProperty(lines, "enable-rcon", "true");
        SetProperty(lines, "rcon.port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetProperty(lines, "rcon.password", password);
        SetProperty(lines, "broadcast-rcon-to-ops", "false");
        File.WriteAllLines(path, lines);
    }

    public async Task<bool> RequestStopAsync(InstalledServer server)
    {
        var properties = ReadProperties(server.Directory);
        if (!string.Equals(properties.GetValueOrDefault("enable-rcon"), "true", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(properties.GetValueOrDefault("rcon.port"), out var port)) return false;
        var password = properties.GetValueOrDefault("rcon.password");
        if (string.IsNullOrEmpty(password)) return false;

        try
        {
            return await SendRconAsync(port, password, "stop");
        }
        catch
        {
            return false;
        }
    }

    public static void ForceStop(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task<bool> SendRconAsync(int port, string password, string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await using var stream = client.GetStream();

        await WriteRconPacketAsync(stream, 1, 3, password, timeout.Token);
        var (authId, _) = await ReadRconPacketAsync(stream, timeout.Token);
        if (authId == -1) return false;

        await WriteRconPacketAsync(stream, 2, 2, command, timeout.Token);
        try
        {
            await ReadRconPacketAsync(stream, timeout.Token);
        }
        catch
        {
        }
        return true;
    }

    private static async Task WriteRconPacketAsync(Stream stream, int id, int type, string body, CancellationToken token)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var packet = new byte[14 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0), 10 + payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        payload.CopyTo(packet, 12);
        await stream.WriteAsync(packet, token);
    }

    private static async Task<(int Id, int Type)> ReadRconPacketAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 10 || length > 1 << 20) throw new InvalidDataException("Bad RCON packet.");
        var rest = new byte[length];
        await stream.ReadExactlyAsync(rest, token);
        return (BinaryPrimitives.ReadInt32LittleEndian(rest), BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4)));
    }

    private static Dictionary<string, string> ReadProperties(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(directory, "server.properties");
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!') continue;
            var index = trimmed.IndexOf('=');
            if (index <= 0) continue;
            result[trimmed[..index].Trim()] = trimmed[(index + 1)..].Trim();
        }
        return result;
    }

    private static void SetProperty(List<string> lines, string key, string value)
    {
        var index = lines.FindIndex(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase) && trimmed[key.Length..].TrimStart().StartsWith('=');
        });
        var entry = $"{key}={value}";
        if (index >= 0) lines[index] = entry;
        else lines.Add(entry);
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreePort()
    {
        for (var port = 25575; port < 25700; port++)
            if (IsPortFree(port)) return port;
        return 25575;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    private static string? TryGetCommandLine(int processId)
    {
        const int ProcessQueryLimitedInformation = 0x1000;
        const int ProcessCommandLineInformation = 60;

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out _) != 0) return null;
                var byteLength = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, byteLength / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static void OpenServerFolder(InstalledServer server)
    {
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{server.Directory}\"", UseShellExecute = true });
    }

    private async Task<IReadOnlyList<LauncherVersion>> LoadSpigotAsync(CancellationToken cancellationToken)
    {
        var index = await GetSpigotIndexAsync(cancellationToken);
        return index.OrderBy(pair => pair.Key, Comparer<string>.Create(CompareVersionsDescending))
            .Select(pair => new LauncherVersion("Spigot", $"Spigot {pair.Key}", BuildSpigotSubtitle(pair.Key, pair.Value), pair.Key, "spigot"))
            .ToArray();
    }

    private async Task<IReadOnlyList<LauncherVersion>> LoadBukkitAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, JsonElement> index;
        try { index = await GetSpigotIndexAsync(cancellationToken); }
        catch when (!cancellationToken.IsCancellationRequested) { index = new Dictionary<string, JsonElement>(); }
        if (index.Count == 0) index["latest"] = default;

        return index.OrderBy(pair => pair.Key == "latest" ? "9999" : pair.Key, Comparer<string>.Create(CompareVersionsDescending))
            .Select(pair =>
            {
                var version = pair.Key;
                var command = $"java -jar BuildTools.jar --rev {version} --compile craftbukkit";
                var java = GetString(pair.Value, "java");
                return new LauncherVersion("Bukkit", version == "latest" ? "CraftBukkit (latest)" : $"CraftBukkit {version}",
                    java is null ? command : $"{command} • Java {java}+", version, null, command);
            }).ToArray();
    }

    private async Task<IReadOnlyList<LauncherVersion>> LoadPaperAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", cancellationToken);
        var versions = new List<string>();
        if (root.TryGetProperty("versions", out var allVersions) && allVersions.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in allVersions.EnumerateObject())
            {
                if (group.Value.ValueKind == JsonValueKind.Array)
                    versions.AddRange(group.Value.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
                else if (group.Value.ValueKind == JsonValueKind.String)
                    versions.Add(group.Value.GetString()!);
            }
        }

        return versions.Where(IsRelease).Distinct().OrderBy(version => version, Comparer<string>.Create(CompareVersionsDescending))
            .Select(version => new LauncherVersion("Paper", $"Paper {version}", $"Minecraft {version} • Latest stable build", version, "paper"))
            .ToArray();
    }

    private async Task<IReadOnlyList<LauncherVersion>> LoadPurpurAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync("https://api.purpurmc.org/v2/purpur", cancellationToken);
        var versions = root.TryGetProperty("versions", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Where(IsRelease)
            : [];

        return versions.OrderBy(version => version, Comparer<string>.Create(CompareVersionsDescending))
            .Select(version => new LauncherVersion("Purpur", $"Purpur {version}", $"Minecraft {version} • Latest build", version, "purpur"))
            .ToArray();
    }

    private async Task<Dictionary<string, JsonElement>> GetSpigotIndexAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync("https://mcjars.app/api/v2/builds/SPIGOT", cancellationToken);
        var result = new Dictionary<string, JsonElement>();
        if (!root.TryGetProperty("builds", out var builds) || builds.ValueKind != JsonValueKind.Object) return result;
        foreach (var entry in builds.EnumerateObject())
        {
            var type = GetString(entry.Value, "type");
            if (IsRelease(entry.Name) && (type is null || type == "RELEASE")) result[entry.Name] = entry.Value.Clone();
        }
        return result;
    }

    private static string BuildSpigotSubtitle(string version, JsonElement info)
    {
        var parts = new List<string> { $"Minecraft {version}" };
        if (info.TryGetProperty("latest", out var latest) && GetInt(latest, "buildNumber") is var build && build > 0) parts.Add($"Build #{build}");
        var java = GetString(info, "java");
        if (java is not null) parts.Add($"Java {java}+");
        return string.Join(" • ", parts);
    }

    private static async Task<JsonElement> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static bool TryGetString(JsonElement element, string property, out string value) => TryGetString(element, property, null, out value);

    private static bool TryGetString(JsonElement element, string parent, string? child, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(parent, out var current)) return false;
        if (child is not null && !current.TryGetProperty(child, out current)) return false;
        if (current.ValueKind != JsonValueKind.String) return false;
        value = current.GetString()!;
        return true;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool IsRelease(string version) => Regex.IsMatch(version, "^\\d+(\\.\\d+)+$");

    private static int CompareVersionsDescending(string left, string right)
    {
        var a = left.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        var b = right.Split('.').Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var comparison = (index < b.Length ? b[index] : 0).CompareTo(index < a.Length ? a[index] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static int? ReadRam(string directory)
    {
        var path = Path.Combine(directory, "run.bat");
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var match = Regex.Match(text, @"-Xmx(\d+)G", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var ram) ? ram : null;
    }

    private static void WriteRunBat(string directory, string jarName, int ramGb, string title)
    {
        var safeTitle = title.Replace("\r", string.Empty).Replace("\n", string.Empty);
        var java = ResolveJava();
        var content = string.Join("\r\n", [
            "@echo off",
            $"title {safeTitle}",
            "cd /d \"%~dp0\"",
            $"set \"JAVA={java}\"",
            "\"%JAVA%\" -version >nul 2>nul || (echo Java was not found. Install 64-bit Java 21 from https://adoptium.net and try again. & pause & exit /b 1)",
            "\"%JAVA%\" -version 2>&1 | findstr /c:\"64-Bit\" >nul || (echo The Java on this PC is 32-bit and cannot use more than about 1 GB of RAM. Install 64-bit Java 21 from https://adoptium.net and try again. & pause & exit /b 1)",
            ">eula.txt echo eula=true",
            $"\"%JAVA%\" -Xms{ramGb}G -Xmx{ramGb}G -jar \"%~dp0{jarName}\" nogui",
            "if errorlevel 1 pause",
            string.Empty
        ]);
        File.WriteAllText(Path.Combine(directory, "run.bat"), content);
    }

    private static string? resolvedJava;

    private static string ResolveJava()
    {
        if (resolvedJava is not null) return resolvedJava;

        var candidates = new List<string>();
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));

        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] vendors = ["Eclipse Adoptium", "Java", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru", "OpenJDK", "Eclipse Foundation"];
        foreach (var vendor in vendors)
        {
            var vendorDir = Path.Combine(programFiles, vendor);
            if (!Directory.Exists(vendorDir)) continue;
            try
            {
                foreach (var install in Directory.EnumerateDirectories(vendorDir))
                    candidates.Add(Path.Combine(install, "bin", "java.exe"));
            }
            catch
            {
            }
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "java.exe")); }
            catch { }
        }

        string? best = null;
        var bestMajor = -1;
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var probe = ProbeJava(path);
            if (probe is not { Bits: 64 } info || info.Major <= bestMajor) continue;
            best = path;
            bestMajor = info.Major;
        }

        if (best is not null) resolvedJava = best;
        return best ?? "java";
    }

    private static (int Bits, int Major)? ProbeJava(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "-XshowSettings:properties -version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            var text = error.Result + output.Result;
            var bitsMatch = Regex.Match(text, @"sun\.arch\.data\.model\s*=\s*(\d+)");
            var bits = bitsMatch.Success ? int.Parse(bitsMatch.Groups[1].Value) : text.Contains("64-Bit") ? 64 : 32;

            var versionMatch = Regex.Match(text, @"java\.specification\.version\s*=\s*([\d.]+)");
            var major = 0;
            if (versionMatch.Success)
            {
                var parts = versionMatch.Groups[1].Value.Split('.');
                int.TryParse(parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0], out major);
            }
            return (bits, major);
        }
        catch
        {
            return null;
        }
    }

    private static string ServerTitle(string type, string version)
    {
        var displayType = type.ToLowerInvariant() switch
        {
            "craftbukkit" => "CraftBukkit",
            "spigot" => "Spigot",
            "paper" => "Paper",
            "purpur" => "Purpur",
            _ => type
        };
        return $"{displayType} {version}";
    }
}