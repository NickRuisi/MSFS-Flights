using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MsfsTelemetry.Configuration;

/// <summary>
/// Keeps the web portal API key (from the profile page) in a file, encrypted with Windows DPAPI for
/// the current Windows user. Other users on the PC, and copies of the file on another PC, can't read it.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>Same as the web app's <c>ApiKeysRepository.KeyPrefix</c>.</summary>
    public const string KeyPrefix = "fsf_";

    /// <summary>The prefix plus 32 random bytes as unpadded base64url.</summary>
    public const int KeyLength = 47;

    /// <summary>Characters at the start of a key that are safe to show (the web page shows the same).</summary>
    public const int VisiblePrefixLength = 12;

    // Ties the encrypted blob to this app, so other DPAPI data of the same user can't be swapped in.
    private static readonly byte[] Entropy = "MsfsTelemetry.ApiKey.v1"u8.ToArray();

    private readonly ILogger<ApiKeyStore> _logger;
    private readonly object _gate = new();
    private string? _key;
    private bool _loaded;

    public ApiKeyStore(IOptions<StorageOptions> options, ILogger<ApiKeyStore> logger)
        : this(Environment.ExpandEnvironmentVariables(options.Value.ApiKeyPath), logger)
    {
    }

    public ApiKeyStore(string path, ILogger<ApiKeyStore> logger)
    {
        FilePath = Path.GetFullPath(path, AppContext.BaseDirectory);
        _logger = logger;
    }

    /// <summary>Where the encrypted key is saved.</summary>
    public string FilePath { get; }

    /// <summary>The saved key, or null if none is saved (or the file can't be decrypted).</summary>
    public string? Key
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _key = Load();
                    _loaded = true;
                }
                return _key;
            }
        }
    }

    /// <summary>Checks the format, encrypts the key and saves it, replacing any saved key.</summary>
    /// <exception cref="ArgumentException">The key doesn't look like an API key.</exception>
    public void Save(string key)
    {
        key = Normalize(key);
        if (!IsValid(key))
            throw new ArgumentException($"An API key starts with {KeyPrefix} and is {KeyLength} characters long.", nameof(key));

        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write then rename, so a crash mid-write can't leave a half-written key behind.
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, blob);
        File.Move(temp, FilePath, overwrite: true);

        lock (_gate)
        {
            _key = key;
            _loaded = true;
        }
        _logger.LogInformation("Saved API key {Key}", Mask(key));
    }

    /// <summary>Deletes the saved key. Returns false if there wasn't one.</summary>
    public bool Remove()
    {
        var existed = File.Exists(FilePath);
        File.Delete(FilePath);
        lock (_gate)
        {
            _key = null;
            _loaded = true;
        }
        if (existed)
            _logger.LogInformation("Removed the saved API key");
        return existed;
    }

    /// <summary>True for <c>fsf_</c> followed by 43 base64url characters.</summary>
    public static bool IsValid(string? key) =>
        key is { Length: KeyLength }
        && key.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && key.AsSpan(KeyPrefix.Length).IndexOfAnyExcept(Base64UrlChars) < 0;

    /// <summary>Removes whitespace that copy/paste tends to add (spaces, line breaks).</summary>
    public static string Normalize(string key) => string.Concat(key.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The key with everything after its first characters hidden, e.g. <c>fsf_OoaBnh_D…</c>.</summary>
    public static string Mask(string key) =>
        key.Length <= VisiblePrefixLength ? key : key[..VisiblePrefixLength] + "…";

    /// <summary>Characters allowed in a key after the prefix.</summary>
    public static bool IsKeyChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static readonly System.Buffers.SearchValues<char> Base64UrlChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private string? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        try
        {
            var key = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser));
            if (IsValid(key))
                return key;
            _logger.LogWarning("The saved API key in {Path} isn't valid. Press K to enter it again.", FilePath);
        }
        catch (CryptographicException)
        {
            // Saved by another Windows user, or copied from another PC.
            _logger.LogWarning("The saved API key in {Path} can't be read by this Windows user. Press K to enter it again.", FilePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Couldn't read the saved API key from {Path}", FilePath);
        }
        return null;
    }
}
