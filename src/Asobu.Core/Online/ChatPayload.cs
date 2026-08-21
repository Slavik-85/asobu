using System.Text;

namespace Asobu.Core.Online;

/// <summary>What a message turns out to be once it is open.</summary>
public enum ChatKind : byte
{
    Text = 0,

    /// <summary>A JPEG, which is what the sender's launcher re-encodes every picture to.</summary>
    Image = 1,
}

/// <summary>
/// The inside of a message: one byte saying what it is, then the thing itself.
///
/// The kind lives inside the encryption rather than beside it on purpose. Put in the JSON the
/// server relays, it would tell the server that a picture had been sent — which is not the
/// contents, but it is more than the server needs and more than it has any business knowing.
/// Inside, all it sees is bytes.
///
/// One byte rather than a JSON envelope because it is read before anything is trusted: a
/// hostile or corrupted message must not be able to make the parser do anything interesting,
/// and reading one byte and a length cannot.
/// </summary>
public sealed class ChatPayload
{
    private ChatPayload(ChatKind kind, byte[] content)
    {
        Kind = kind;
        Content = content;
    }

    public ChatKind Kind { get; }

    public byte[] Content { get; }

    public static ChatPayload OfText(string text) =>
        new(ChatKind.Text, Encoding.UTF8.GetBytes(text));

    public static ChatPayload OfImage(byte[] jpeg) => new(ChatKind.Image, jpeg);

    /// <summary>The text, for a message that is one. Empty for anything else.</summary>
    public string AsText() => Kind == ChatKind.Text ? Encoding.UTF8.GetString(Content) : "";

    public byte[] ToBytes()
    {
        var bytes = new byte[Content.Length + 1];
        bytes[0] = (byte)Kind;
        Content.CopyTo(bytes, 1);

        return bytes;
    }

    /// <summary>
    /// Reads one back, or null when the bytes are not one.
    ///
    /// An unknown kind is null rather than a guess. A later version of Asobu sending something
    /// this one has no idea how to show should produce "can't read this" and not an attempt to
    /// render a sound file as a sentence.
    /// </summary>
    public static ChatPayload? FromBytes(byte[] bytes)
    {
        if (bytes.Length < 1) return null;

        var kind = (ChatKind)bytes[0];
        if (kind is not (ChatKind.Text or ChatKind.Image)) return null;

        return new ChatPayload(kind, bytes[1..]);
    }
}
