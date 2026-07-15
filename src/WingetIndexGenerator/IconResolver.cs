using System.Security.Cryptography;
using System.Text;
using SabreTools.Compression.MSZIP;
using WingetIndexGenerator.Dto;
using WingetIndexGenerator.Models;
using YamlDotNet.Serialization;

namespace WingetIndexGenerator;

internal sealed class IconResolver : IDisposable
{
    private const string CacheBaseUrl = "https://cdn.winget.microsoft.com/cache/";
    private const int CompressionHeaderLength = 28;
    private const int MaxConcurrency = 32;

    private readonly HttpClient _httpClient;
    private readonly IDeserializer _yamlDeserializer;
    private readonly SemaphoreSlim _semaphore = new(MaxConcurrency);

    private int _completed;
    private int _manifestIcons;
    private int _githubIcons;
    private int _faviconIcons;
    private int _noIcons;
    private int _failures;

    internal IconResolver()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(CacheBaseUrl),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("winget-pkgs-index/1.0");

        _yamlDeserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    internal async Task ResolveAllAsync(
        IEnumerable<(Package Package, PackageV2 Output)> packages,
        CancellationToken cancellationToken)
    {
        var items = packages.ToList();
        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine($"Resolving icons for {items.Count} packages with {MaxConcurrency} concurrent requests.");

        var tasks = items.Select(async item =>
        {
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                await ResolveAsync(item.Package, item.Output, cancellationToken);
            }
            finally
            {
                _semaphore.Release();
                var completed = Interlocked.Increment(ref _completed);
                if (completed % 250 == 0 || completed == items.Count)
                {
                    Console.WriteLine($"Resolved icons for {completed}/{items.Count} packages.");
                }
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine(
            $"Icon resolution complete: manifest={_manifestIcons}, github={_githubIcons}, " +
            $"favicon={_faviconIcons}, none={_noIcons}, failures={_failures}.");
    }

    private async Task ResolveAsync(Package package, PackageV2 output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.Id) || package.Hash is not { Length: > 0 })
        {
            output.IconSource = "none";
            Interlocked.Increment(ref _noIcons);
            return;
        }

        try
        {
            var mergedManifest = await DownloadMergedManifestAsync(package, cancellationToken);
            output.PackageUrl = NormalizeWebUrl(mergedManifest.PackageUrl);
            output.PublisherUrl = NormalizeWebUrl(mergedManifest.PublisherUrl);
            var result = SelectIcon(mergedManifest);

            output.IconUrl = result.Url;
            output.IconSource = result.Source;

            switch (result.Source)
            {
                case "manifest":
                    Interlocked.Increment(ref _manifestIcons);
                    break;
                case "github-avatar":
                    Interlocked.Increment(ref _githubIcons);
                    break;
                case "favicon":
                    Interlocked.Increment(ref _faviconIcons);
                    break;
                default:
                    Interlocked.Increment(ref _noIcons);
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            Interlocked.Increment(ref _failures);
        }
    }

    private async Task<MergedManifest> DownloadMergedManifestAsync(Package package, CancellationToken cancellationToken)
    {
        var packageHash = Convert.ToHexString(package.Hash!).ToLowerInvariant();
        var packageId = Uri.EscapeDataString(package.Id!);
        var versionDataPath = $"packages/{packageId}/{packageHash[..8]}/versionData.mszyml";

        var compressedVersionData = await _httpClient.GetByteArrayAsync(versionDataPath, cancellationToken);
        var versionDataYaml = DecompressVersionData(compressedVersionData);
        var versionData = _yamlDeserializer.Deserialize<PackageVersionDataManifest>(versionDataYaml);

        var latestVersion = versionData.Versions?
            .FirstOrDefault(version => string.Equals(version.Version, package.LatestVersion, StringComparison.OrdinalIgnoreCase))
            ?? versionData.Versions?.FirstOrDefault()
            ?? throw new InvalidDataException($"No version data found for {package.Id}.");

        if (string.IsNullOrWhiteSpace(latestVersion.RelativePath))
        {
            throw new InvalidDataException($"No manifest path found for {package.Id}.");
        }

        var manifestBytes = await _httpClient.GetByteArrayAsync(latestVersion.RelativePath, cancellationToken);
        if (!string.IsNullOrWhiteSpace(latestVersion.Sha256Hash))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!actualHash.Equals(latestVersion.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Manifest hash mismatch for {package.Id}.");
            }
        }

        return _yamlDeserializer.Deserialize<MergedManifest>(Encoding.UTF8.GetString(manifestBytes));
    }

    private static string DecompressVersionData(byte[] compressed)
    {
        if (compressed.Length <= CompressionHeaderLength)
        {
            throw new InvalidDataException("The compressed version data is too short.");
        }

        var expectedLength = BitConverter.ToInt64(compressed, 8);
        var payloadLength = BitConverter.ToInt32(compressed, 24);
        if (expectedLength <= 0 || expectedLength > int.MaxValue || payloadLength <= 0 || payloadLength > compressed.Length - CompressionHeaderLength)
        {
            throw new InvalidDataException("The compressed version data header is invalid.");
        }

        using var input = new MemoryStream(compressed, CompressionHeaderLength, payloadLength, writable: false);
        using var output = new MemoryStream((int)expectedLength);
        var decompressor = Decompressor.Create();
        if (!decompressor.CopyTo(input, output))
        {
            throw new InvalidDataException("The version data could not be decompressed.");
        }

        var outputBytes = output.ToArray();
        if (outputBytes.Length < expectedLength)
        {
            throw new InvalidDataException("The decompressed version data is incomplete.");
        }

        return Encoding.UTF8.GetString(outputBytes, 0, (int)expectedLength);
    }

    private static IconResult SelectIcon(MergedManifest manifest)
    {
        var manifestIcon = manifest.Icons?
            .Where(icon => Uri.TryCreate(icon.IconUrl, UriKind.Absolute, out _))
            .OrderBy(icon => GetThemePriority(icon.IconTheme))
            .ThenBy(icon => GetResolutionPriority(icon.IconResolution))
            .FirstOrDefault();

        if (manifestIcon is not null)
        {
            return new IconResult(manifestIcon.IconUrl, "manifest");
        }

        var installerUrls = manifest.Installers?
            .Select(installer => installer.InstallerUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .ToList() ?? [];

        foreach (var candidate in installerUrls.Prepend(manifest.PackageUrl).Prepend(manifest.PublisherUrl))
        {
            if (TryGetGitHubAvatar(candidate, out var githubAvatar))
            {
                return new IconResult(githubAvatar, "github-avatar");
            }
        }

        foreach (var candidate in new[] { manifest.PackageUrl, manifest.PublisherUrl }.Concat(installerUrls))
        {
            if (TryGetWebsiteFavicon(candidate, out var favicon))
            {
                return new IconResult(favicon, "favicon");
            }
        }

        return new IconResult(null, "none");
    }

    private static bool TryGetGitHubAvatar(string? value, out string? iconUrl)
    {
        iconUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        iconUrl = $"https://github.com/{Uri.EscapeDataString(segments[0])}.png?size=128";
        return true;
    }

    private static string? NormalizeWebUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static bool TryGetWebsiteFavicon(string? value, out string? iconUrl)
    {
        iconUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (IsGenericDownloadHost(host))
        {
            return false;
        }

        iconUrl = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/favicon.ico").Uri.AbsoluteUri;
        return true;
    }

    private static bool IsGenericDownloadHost(string host)
    {
        string[] genericSuffixes =
        [
            "githubusercontent.com",
            "amazonaws.com",
            "cloudfront.net",
            "azureedge.net",
            "windows.net",
            "storage.googleapis.com",
            "sourceforge.net",
        ];

        string[] genericHosts =
        [
            "aka.ms",
            "cdn.winget.microsoft.com",
            "download.microsoft.com",
            "go.microsoft.com",
        ];

        return genericHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
            genericSuffixes.Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetThemePriority(string? theme) => theme?.ToLowerInvariant() switch
    {
        "default" => 0,
        null or "" => 1,
        "light" => 2,
        "dark" => 3,
        _ => 4,
    };

    private static int GetResolutionPriority(string? resolution) => resolution?.ToLowerInvariant() switch
    {
        "64x64" => 0,
        "48x48" => 1,
        "72x72" => 2,
        "80x80" => 3,
        "96x96" => 4,
        "256x256" => 5,
        "custom" => 6,
        _ => 7,
    };

    public void Dispose()
    {
        _semaphore.Dispose();
        _httpClient.Dispose();
    }

    private sealed record IconResult(string? Url, string Source);

    private sealed class PackageVersionDataManifest
    {
        [YamlMember(Alias = "vD")]
        public List<PackageVersionData>? Versions { get; set; }
    }

    private sealed class PackageVersionData
    {
        [YamlMember(Alias = "v")]
        public string? Version { get; set; }

        [YamlMember(Alias = "rP")]
        public string? RelativePath { get; set; }

        [YamlMember(Alias = "s256H")]
        public string? Sha256Hash { get; set; }
    }

    private sealed class MergedManifest
    {
        public string? PackageUrl { get; set; }
        public string? PublisherUrl { get; set; }
        public List<ManifestIcon>? Icons { get; set; }
        public List<ManifestInstaller>? Installers { get; set; }
    }

    private sealed class ManifestIcon
    {
        public string? IconUrl { get; set; }
        public string? IconResolution { get; set; }
        public string? IconTheme { get; set; }
    }

    private sealed class ManifestInstaller
    {
        public string? InstallerUrl { get; set; }
    }
}
