using System.Security.Cryptography;
using System.Text;
using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Online;

namespace Asobu.Core.Tests;

/// <summary>
/// The chat encryption. Both halves matter: that two friends can read each other, and that
/// nobody else can — including the server, which is the only party guaranteed to see every byte.
/// </summary>
public class MessageCryptoTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-crypto-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private MessageCrypto Crypto(string who) =>
        new(new TokenVault(new AsobuPaths(Path.Combine(_root, who))));

    /// <summary>One person with their own vault, as two launchers on two machines would be.</summary>
    private (ECDiffieHellman Key, string Public) Person(string who)
    {
        var key = Crypto(who).MineFor("uuid-" + who);
        return (key, MessageCrypto.PublicKeyOf(key));
    }

    [Fact]
    public void TwoFriendsReadEachOther()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var box = MessageCrypto.Seal(alice.Key, bob.Public, "launching in five");

        Assert.Equal("launching in five", MessageCrypto.Open(bob.Key, alice.Public, box));
    }

    [Fact]
    public void AndInBothDirections()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var box = MessageCrypto.Seal(bob.Key, alice.Public, "on my way");

        Assert.Equal("on my way", MessageCrypto.Open(alice.Key, bob.Public, box));
    }

    [Fact]
    public void TheMessageIsNotInWhatGoesOverTheWire()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var box = MessageCrypto.Seal(alice.Key, bob.Public, "SECRETPHRASE");

        // What the server relays. If the words are in here the whole exercise is theatre.
        Assert.DoesNotContain("SECRETPHRASE", box, StringComparison.OrdinalIgnoreCase);

        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(box));
        Assert.DoesNotContain("SECRETPHRASE", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStrangerCannotOpenIt()
    {
        var alice = Person("alice");
        var bob = Person("bob");
        var eve = Person("eve");

        var box = MessageCrypto.Seal(alice.Key, bob.Public, "not for you");

        // Eve holds a real key pair and Alice's real public key. Neither helps.
        Assert.Null(MessageCrypto.Open(eve.Key, alice.Public, box));
    }

    [Fact]
    public void TheServerCannotOpenItEvenHoldingBothPublicKeys()
    {
        var alice = Person("alice");
        var bob = Person("bob");
        var server = Person("server");

        var box = MessageCrypto.Seal(alice.Key, bob.Public, "relay this");

        // Exactly what the server has: both published keys and the ciphertext. Its own private
        // key is the only one it can pair with them, and it is the wrong one.
        Assert.Null(MessageCrypto.Open(server.Key, alice.Public, box));
        Assert.Null(MessageCrypto.Open(server.Key, bob.Public, box));
    }

    [Fact]
    public void ATamperedMessageIsRefusedRatherThanMangled()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var raw = Convert.FromBase64String(MessageCrypto.Seal(alice.Key, bob.Public, "transfer 10 diamonds"));

        // One bit, in the ciphertext. AES-GCM authenticates, so this has to fail closed rather
        // than hand back plausible-looking nonsense.
        raw[^20] ^= 0x01;

        Assert.Null(MessageCrypto.Open(bob.Key, alice.Public, Convert.ToBase64String(raw)));
    }

    [Fact]
    public void RubbishDoesNotThrow()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        // Everything a broken or hostile server could send instead of a message.
        foreach (var box in new[] { "", "not base64 at all!!", "AAAA", Convert.ToBase64String(new byte[8]) })
            Assert.Null(MessageCrypto.Open(bob.Key, alice.Public, box));
    }

    [Fact]
    public void TheSameMessageTwiceLooksDifferent()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var once = MessageCrypto.Seal(alice.Key, bob.Public, "hi");
        var twice = MessageCrypto.Seal(alice.Key, bob.Public, "hi");

        // A fresh nonce each time. Identical ciphertext would tell anybody watching that the
        // same thing had been said, without their reading either.
        Assert.NotEqual(once, twice);
    }

    [Fact]
    public void TheKeyIsKeptRatherThanRemade()
    {
        // A second launch has to reach the same key, or every friend's copy of the public half
        // goes stale and the conversation stops.
        var first = MessageCrypto.PublicKeyOf(Crypto("alice").MineFor("uuid-alice"));
        var second = MessageCrypto.PublicKeyOf(Crypto("alice").MineFor("uuid-alice"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void EachAccountOnOneMachineHasItsOwn()
    {
        var crypto = Crypto("shared");

        Assert.NotEqual(
            MessageCrypto.PublicKeyOf(crypto.MineFor("uuid-one")),
            MessageCrypto.PublicKeyOf(crypto.MineFor("uuid-two")));
    }

    [Fact]
    public void BothSidesComputeTheSameFingerprint()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        // Neither side knows which of them "goes first", so the code must not depend on it —
        // two different codes would have them conclude they were being intercepted.
        Assert.Equal(
            MessageCrypto.Fingerprint(alice.Public, bob.Public),
            MessageCrypto.Fingerprint(bob.Public, alice.Public));
    }

    [Fact]
    public void AndADifferentPairGivesADifferentOne()
    {
        var alice = Person("alice");
        var bob = Person("bob");
        var eve = Person("eve");

        // The whole point: a server swapping Bob's key for its own changes what Alice reads out,
        // and it stops matching what Bob reads out.
        Assert.NotEqual(
            MessageCrypto.Fingerprint(alice.Public, bob.Public),
            MessageCrypto.Fingerprint(alice.Public, eve.Public));
    }

    [Fact]
    public void TheFingerprintIsReadableAloud()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        var code = MessageCrypto.Fingerprint(alice.Public, bob.Public);
        var groups = code.Split(' ');

        Assert.Equal(5, groups.Length);
        Assert.All(groups, g => Assert.Equal(5, g.Length));
        Assert.All(groups, g => Assert.True(g.All(char.IsAsciiDigit), $"'{g}' is not digits"));
    }

    [Fact]
    public void SurvivesEverythingSomebodyMightType()
    {
        var alice = Person("alice");
        var bob = Person("bob");

        foreach (var text in new[]
                 {
                     "x",
                     "привет, запускаю через 5 минут",
                     "絵文字 🌸🎮 and emoji",
                     new string('a', 2000),
                     "line one\nline two\twith a tab",
                 })
        {
            var box = MessageCrypto.Seal(alice.Key, bob.Public, text);
            Assert.Equal(text, MessageCrypto.Open(bob.Key, alice.Public, box));
        }
    }
}
