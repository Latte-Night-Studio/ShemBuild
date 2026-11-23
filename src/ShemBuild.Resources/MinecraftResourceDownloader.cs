using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;

namespace ShemBuild.Resources;

/// <summary>Downloads and caches the official client archive that contains Minecraft assets.</summary>
public sealed class MinecraftResourceDownloader
{
    private const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const long MaxClientJarBytes = 64L * 1024 * 1024;
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public async Task<MinecraftReleaseCatalog> GetStableReleaseCatalogAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(Text("Перевіряю офіційний список стабільних версій Minecraft…", "Checking Mojang's stable Minecraft release list…"));
            using var manifest = await GetJsonAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);
            var latestRelease = ReadRequiredString(manifest.RootElement.GetProperty("latest"), "release");
            if (!IsSafeVersionId(latestRelease))
            {
                throw new ResourceDownloadException("Маніфест Mojang містить некоректний ідентифікатор останнього релізу.");
            }

            var releases = manifest.RootElement.GetProperty("versions").EnumerateArray()
                .Where(version => version.TryGetProperty("type", out var type) && type.GetString() == "release")
                .Select(version => version.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id) && IsSafeVersionId(id))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (!releases.Contains(latestRelease, StringComparer.Ordinal))
            {
                releases.Insert(0, latestRelease);
            }

            return new MinecraftReleaseCatalog(latestRelease, releases);
        }
        catch (ResourceDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var cachedCatalog = TryReadCachedReleaseCatalog();
            if (cachedCatalog is not null)
            {
                return cachedCatalog;
            }

            throw new ResourceDownloadException("Отримання списку версій скасовано або перевищено час очікування.");
        }
        catch (HttpRequestException exception)
        {
            var cachedCatalog = TryReadCachedReleaseCatalog();
            if (cachedCatalog is not null)
            {
                return cachedCatalog;
            }

            throw new ResourceDownloadException($"Не вдалося зв'язатися із серверами Mojang: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new ResourceDownloadException($"Сервер Mojang повернув некоректний список версій: {exception.Message}");
        }
        catch (KeyNotFoundException exception)
        {
            throw new ResourceDownloadException($"У маніфесті Mojang бракує потрібного поля: {exception.Message}");
        }
    }

    public async Task<AutoLoadedResourcePack> OpenForVersionAsync(
        string versionId,
        string selectionNote,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeVersionId(versionId))
        {
            throw new ResourceDownloadException("Вкажіть коректний ідентифікатор стабільної версії Minecraft, наприклад 1.20.4.");
        }

        try
        {
            progress?.Report(Text("Перевіряю офіційний список версій Minecraft…", "Checking Mojang's Minecraft version list…"));
            using var manifest = await GetJsonAsync(VersionManifestUrl, cancellationToken).ConfigureAwait(false);
            var latestRelease = ReadRequiredString(manifest.RootElement.GetProperty("latest"), "release");
            var versionEntry = FindVersionEntry(manifest.RootElement.GetProperty("versions"), versionId);
            if (versionEntry is null)
            {
                throw new ResourceDownloadException($"Стабільну версію {versionId} не знайдено в офіційному маніфесті Mojang.");
            }

            progress?.Report(string.Format(Text("Отримую метадані клієнта Minecraft {0}…", "Getting Minecraft {0} client metadata…"), versionId));
            using var versionMetadata = await GetJsonAsync(versionEntry.MetadataUrl, cancellationToken).ConfigureAwait(false);
            var client = ReadClientDownload(versionMetadata.RootElement);
            if (client.Size <= 0 || client.Size > MaxClientJarBytes || client.Sha1.Length != 40)
            {
                throw new ResourceDownloadException("Офіційні метадані містять некоректний розмір або контрольну суму клієнта.");
            }

            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShemBuild", "assets");
            Directory.CreateDirectory(cacheDirectory);
            await using var cacheLock = await AcquireCacheLockAsync(cacheDirectory, progress, cancellationToken).ConfigureAwait(false);
            if (!IsSafeVersionId(latestRelease))
            {
                throw new ResourceDownloadException("Маніфест Mojang містить некоректний ідентифікатор останнього релізу.");
            }
            await TrySaveLatestReleaseAsync(cacheDirectory, latestRelease, cancellationToken).ConfigureAwait(false);
            var cachePath = Path.Combine(cacheDirectory, $"minecraft-{versionId}-{client.Sha1}-client.jar");

            if (await MatchesExpectedFileAsync(cachePath, client.Size, client.Sha1, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(string.Format(Text("Ресурси Minecraft {0} вже є в кеші.", "Minecraft {0} resources are already cached."), versionId));
                return new AutoLoadedResourcePack(MinecraftResourcePack.Open(cachePath), versionId, true, selectionNote);
            }

            // Reuse the pre-hash cache name when valid, but never replace it: an older
            // ShemBuild process may still have that archive open for rendering.
            var legacyCachePath = Path.Combine(cacheDirectory, $"minecraft-{versionId}-client.jar");
            if (await MatchesExpectedFileAsync(legacyCachePath, client.Size, client.Sha1, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(string.Format(Text("Ресурси Minecraft {0} вже є в кеші.", "Minecraft {0} resources are already cached."), versionId));
                return new AutoLoadedResourcePack(MinecraftResourcePack.Open(legacyCachePath), versionId, true, selectionNote);
            }

            // A broken archive could be held open by another process. Give this verified
            // download a fresh immutable path instead of trying to replace the locked file.
            if (File.Exists(cachePath))
            {
                cachePath = Path.Combine(cacheDirectory, $"minecraft-{versionId}-{client.Sha1}-{Guid.NewGuid():N}-client.jar");
            }

            progress?.Report(string.Format(Text("Завантажую клієнтські ресурси Minecraft {0}…", "Downloading Minecraft {0} client resources…"), versionId));
            await DownloadClientAsync(client, cachePath, versionId, progress, cancellationToken).ConfigureAwait(false);
            return new AutoLoadedResourcePack(MinecraftResourcePack.Open(cachePath), versionId, false, selectionNote);
        }
        catch (ResourceDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                var cached = TryOpenCachedPack(versionId, selectionNote, progress);
                if (cached is not null)
                {
                    return cached;
                }
            }

            throw new ResourceDownloadException("Завантаження ресурсів скасовано або перевищено час очікування.");
        }
        catch (HttpRequestException exception)
        {
            var cached = TryOpenCachedPack(versionId, selectionNote, progress);
            if (cached is not null)
            {
                return cached;
            }

            throw new ResourceDownloadException($"Не вдалося зв'язатися з серверами Mojang: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ResourceDownloadException($"Немає доступу до локального кешу ресурсів: {exception.Message}");
        }
        catch (IOException exception)
        {
            throw new ResourceDownloadException($"Не вдалося зберегти або прочитати кеш ресурсів: {exception.Message}");
        }
        catch (JsonException exception)
        {
            throw new ResourceDownloadException($"Сервер Mojang повернув некоректні метадані: {exception.Message}");
        }
        catch (KeyNotFoundException exception)
        {
            throw new ResourceDownloadException($"У метаданих Mojang бракує потрібного поля: {exception.Message}");
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string address, CancellationToken cancellationToken)
    {
        if (!IsOfficialMojangUrl(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            throw new ResourceDownloadException("Метадані містять недовірене посилання замість офіційного сервера Mojang.");
        }

        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not Uri finalUri || !IsOfficialMojangUrl(finalUri.AbsoluteUri))
        {
            throw new ResourceDownloadException("Сервер Mojang перенаправив запит на неофіційну адресу.");
        }

        if (response.Content.Headers.ContentLength is > MaxJsonBytes)
        {
            throw new ResourceDownloadException("Метадані версій завеликі.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(stream, MaxJsonBytes, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new ResourceDownloadException("Відповідь сервера Mojang перевищила дозволений розмір.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task<FileStream> AcquireCacheLockAsync(
        string cacheDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(cacheDirectory, "resource-download.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var reported = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsSharingViolation(exception) && DateTime.UtcNow < deadline)
            {
                if (!reported)
                {
                    progress?.Report(Text("Інша копія ShemBuild оновлює кеш ресурсів; очікую на завершення…", "Another ShemBuild process is updating the resource cache; waiting…"));
                    reported = true;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new ResourceDownloadException($"Не вдалося отримати доступ до кешу ресурсів. Закрийте інші копії ShemBuild і спробуйте ще раз: {exception.Message}");
            }
        }
    }

    private static async Task TrySaveLatestReleaseAsync(string cacheDirectory, string latestRelease, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(cacheDirectory, "latest-release.txt");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, latestRelease, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (IOException)
        {
            // This hint is only for offline fallback; a locked sidecar must not stop loading assets.
        }
        catch (UnauthorizedAccessException)
        {
            // The verified client archive is the required cache item; this sidecar is optional.
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private static VersionEntry? FindVersionEntry(JsonElement versions, string versionId)
    {
        if (!IsSafeVersionId(versionId))
        {
            throw new ResourceDownloadException("Маніфест Mojang містить некоректний ідентифікатор версії.");
        }

        foreach (var version in versions.EnumerateArray())
        {
            if (version.TryGetProperty("id", out var id) && id.GetString() == versionId &&
                version.TryGetProperty("type", out var type) && type.GetString() == "release")
            {
                var url = ReadRequiredString(version, "url");
                if (!IsOfficialMojangUrl(url))
                {
                    throw new ResourceDownloadException("Маніфест містить адресу версії поза офіційними серверами Mojang.");
                }

                return new VersionEntry(versionId, url);
            }
        }

        return null;
    }

    private static ClientDownload ReadClientDownload(JsonElement metadata)
    {
        var client = metadata.GetProperty("downloads").GetProperty("client");
        var url = ReadRequiredString(client, "url");
        var sha1 = ReadRequiredString(client, "sha1").ToLowerInvariant();
        var size = client.GetProperty("size").GetInt64();
        if (!IsOfficialMojangUrl(url) || !sha1.All(Uri.IsHexDigit))
        {
            throw new ResourceDownloadException("Метадані клієнта містять недовірене посилання або некоректну контрольну суму.");
        }

        return new ClientDownload(url, sha1, size);
    }

    private static async Task<bool> MatchesExpectedFileAsync(string path, long expectedSize, string expectedSha1, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length != expectedSize)
        {
            return false;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException exception)
        {
            throw new ResourceDownloadException($"Не вдалося перевірити кешований JAR {path}: {exception.Message}");
        }
    }

    private static async Task DownloadClientAsync(
        ClientDownload client,
        string destination,
        string version,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await HttpClient.GetAsync(client.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength &&
                (contentLength > MaxClientJarBytes || contentLength != client.Size))
            {
                throw new ResourceDownloadException("Розмір завантаження не збігається з офіційними метаданими Mojang.");
            }

            long total = 0;
            string actualSha1;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await using (var destinationStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    var buffer = new byte[64 * 1024];
                    var lastReportedPercent = -1;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        total += read;
                        if (total > client.Size || total > MaxClientJarBytes)
                        {
                            throw new ResourceDownloadException("Завантажений файл перевищив розмір із метаданих Mojang.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        var percent = (int)(total * 100 / client.Size);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress?.Report(string.Format(Text("Завантажую ресурси Minecraft {0}… {1}%", "Downloading Minecraft {0} resources… {1}%"), version, percent));
                        }
                    }

                    await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    actualSha1 = Convert.ToHexString(hash.GetHashAndReset());
                }
            }

            if (total != client.Size || !actualSha1.Equals(client.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new ResourceDownloadException("Перевірка завантаження не пройшла: розмір або SHA-1 не збігається з Mojang.");
            }

            try
            {
                File.Move(temporaryPath, destination, overwrite: false);
            }
            catch (IOException exception)
            {
                if (File.Exists(destination) &&
                    await MatchesExpectedFileAsync(destination, client.Size, client.Sha1, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                throw new ResourceDownloadException($"Не вдалося записати JAR у кеш {destination}: {exception.Message}");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw new ResourceDownloadException($"У метаданих Mojang відсутнє поле {propertyName}.");
    }

    private static bool IsOfficialMojangUrl(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("mojang.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".mojang.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsSafeVersionId(string versionId) =>
        versionId.Length is > 0 and <= 64 && versionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static AutoLoadedResourcePack? TryOpenCachedPack(string version, string selectionNote, IProgress<string>? progress)
    {
        try
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShemBuild", "assets");

            if (!IsSafeVersionId(version)) return null;
            var legacyPath = Path.Combine(cacheDirectory, $"minecraft-{version}-client.jar");
            var candidates = new List<string>();
            if (File.Exists(legacyPath)) candidates.Add(legacyPath);
            if (Directory.Exists(cacheDirectory))
            {
                candidates.AddRange(Directory.EnumerateFiles(cacheDirectory, $"minecraft-{version}-*-client.jar")
                    .Where(path => !string.Equals(path, legacyPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }

            foreach (var archivePath in candidates)
            {
                try
                {
                    var pack = MinecraftResourcePack.Open(archivePath);
                    progress?.Report(string.Format(Text("Мережа недоступна; відкрив ресурси Minecraft {0} з локального кешу.", "Network unavailable; opened Minecraft {0} resources from the local cache."), version));
                    return new AutoLoadedResourcePack(pack, version, true,
                        selectionNote + " " + string.Format(Text("Інтернет недоступний; використовую кеш ресурсів Minecraft {0}.", "Internet unavailable; using cached Minecraft {0} resources."), version));
                }
                catch (IOException)
                {
                }
                catch (ResourcePackException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ResourcePackException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static MinecraftReleaseCatalog? TryReadCachedReleaseCatalog()
    {
        try
        {
            var cacheDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShemBuild", "assets");
            var path = Path.Combine(cacheDirectory, "latest-release.txt");
            if (!File.Exists(path))
            {
                return null;
            }

            var latest = File.ReadAllText(path).Trim();
            if (!IsSafeVersionId(latest))
            {
                return null;
            }

            var releases = new[] { latest, "1.20.4" }.Distinct(StringComparer.Ordinal).ToArray();
            return new MinecraftReleaseCatalog(latest, releases, IsCached: true);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ShemBuild/0.1");
        return client;
    }

    private static string Text(string ukrainian, string english) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? english
            : ukrainian;

    private sealed record VersionEntry(string Id, string MetadataUrl);
    private sealed record ClientDownload(string Url, string Sha1, long Size);
}

public sealed record AutoLoadedResourcePack(
    MinecraftResourcePack ResourcePack,
    string VersionId,
    bool WasCached,
    string SelectionNote);

public sealed record MinecraftReleaseCatalog(string LatestReleaseId, IReadOnlyList<string> ReleaseIds, bool IsCached = false);

public sealed class ResourceDownloadException(string message) : Exception(message)
{
}
