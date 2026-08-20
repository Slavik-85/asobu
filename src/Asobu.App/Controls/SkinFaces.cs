using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace Asobu.App.Controls;

/// <summary>
/// An account's head, as a ready-made avatar.
///
/// The rendering is done by a public avatar service rather than by cutting the face out of the
/// skin ourselves: one request per account, the hat layer already composited, and a sensible
/// default head for a UUID that has no profile — which is every offline account.
/// </summary>
public static class SkinFaces
{
    private const int Size = 128;

    /// <summary>
    /// Two services, because a single one being down should not blank every avatar in the
    /// launcher. Both take a dashless UUID and return a PNG with the hat layer already on.
    /// </summary>
    private static readonly string[] Sources =
    [
        "https://mc-heads.net/avatar/{0}/{1}",
        "https://minotar.net/helm/{0}/{1}.png",
    ];

    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Bitmap?> ForAsync(HttpClient http, string uuid)
    {
        var key = uuid.Replace("-", "");

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var face = await LoadAsync(http, key).ConfigureAwait(false);

        lock (Cache)
        {
            // Failures are cached too: if the network is off, retrying on every repaint just
            // spends the whole session timing out.
            Cache[key] = face;
        }

        return face;
    }

    private static async Task<Bitmap?> LoadAsync(HttpClient http, string uuid)
    {
        foreach (var source in Sources)
        {
            try
            {
                var url = string.Format(source, uuid, Size);
                var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);

                return new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                // A missing or undecodable avatar is a cosmetic gap, never a reason to fail
                // loading the accounts page. Fall through to the next source.
            }
        }

        return null;
    }
}
