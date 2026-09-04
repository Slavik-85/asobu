using Asobu.Core;
using Asobu.Core.Skins;

namespace Asobu.Core.Tests;

/// <summary>
/// The skin library, and what it will take.
///
/// A skin is one of the few files a launcher hands to somebody else's server under their name, so
/// the size check is the whole of the gate: Mojang refuses anything that is not 64 wide, and
/// finding that out from a rejection after the upload is a worse way to learn it.
/// </summary>
public class SkinsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-skins-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SkinLibrary Library => new(new AsobuPaths(_root));

    /// <summary>
    /// A PNG as far as anything here looks. The size lives in the header, which is all the check
    /// reads — nothing decodes the image, so the pixels need not exist.
    /// </summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        signature.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);

        return bytes;
    }

    // ---- what counts as a skin ----

    [Fact]
    public void The_size_every_modern_skin_is()
    {
        SkinPng.Validate(Png(64, 64));
        Assert.Equal((64, 64), SkinPng.Size(Png(64, 64)));
    }

    /// <summary>The shape skins were before 1.8, which the game still accepts.</summary>
    [Fact]
    public void The_old_half_height_sheet_is_still_a_skin()
    {
        SkinPng.Validate(Png(64, 32));
    }

    [Fact]
    public void Anything_else_is_turned_away_with_its_own_size_in_the_message()
    {
        var refused = Assert.Throws<SkinException>(() => SkinPng.Validate(Png(128, 128)));

        Assert.Contains("128", refused.Message);
    }

    [Fact]
    public void Something_that_is_not_a_png_at_all_is_turned_away()
    {
        Assert.Throws<SkinException>(() => SkinPng.Validate("not a png, just words"u8.ToArray()));
    }

    [Fact]
    public void A_file_too_short_to_have_a_header_does_not_crash_the_reader()
    {
        Assert.Throws<SkinException>(() => SkinPng.Validate([0x89, 0x50]));
    }

    // ---- keeping them ----

    [Fact]
    public void A_saved_skin_comes_back_with_its_name_and_its_arms()
    {
        var library = Library;
        library.Save(Png(64, 64), "Pink hoodie", SkinModel.Slim);

        // A fresh library, so nothing is answered from memory: this is what a restart would read.
        var reloaded = Library.All().Single();

        Assert.Equal("Pink hoodie", reloaded.Name);
        Assert.Equal(SkinModel.Slim, reloaded.Model);
        Assert.True(File.Exists(reloaded.Path));
    }

    /// <summary>
    /// Two skins somebody named the same thing. The file on disk is named by us precisely so that
    /// saving the second cannot quietly overwrite the first.
    /// </summary>
    [Fact]
    public void Two_skins_of_one_name_are_two_skins()
    {
        var library = Library;
        library.Save(Png(64, 64), "Skin", SkinModel.Classic);
        library.Save(Png(64, 64), "Skin", SkinModel.Classic);

        Assert.Equal(2, library.All().Count);
    }

    [Fact]
    public void Removing_one_takes_its_file_with_it()
    {
        var library = Library;
        var saved = library.Save(Png(64, 64), "Gone", SkinModel.Classic);

        library.Remove(library.All().Single());

        Assert.Empty(library.All());
        Assert.False(File.Exists(saved.Path));
    }

    /// <summary>
    /// A folder of PNGs is a folder, and people tidy those. An index entry whose file has been
    /// deleted from underneath it is not an error worth showing anybody.
    /// </summary>
    [Fact]
    public void A_skin_deleted_from_the_folder_simply_stops_being_listed()
    {
        var library = Library;
        library.Save(Png(64, 64), "Kept", SkinModel.Classic);
        var doomed = library.Save(Png(64, 64), "Deleted by hand", SkinModel.Classic);

        File.Delete(doomed.Path);

        Assert.Equal("Kept", library.All().Single().Name);
    }

    [Fact]
    public void A_skin_that_is_the_wrong_size_is_never_written_at_all()
    {
        var library = Library;

        Assert.Throws<SkinException>(() => library.Save(Png(32, 32), "Too small", SkinModel.Classic));
        Assert.Empty(library.All());
    }

    [Fact]
    public void An_empty_library_is_an_empty_list_rather_than_a_failure()
    {
        Assert.Empty(Library.All());
    }
}
