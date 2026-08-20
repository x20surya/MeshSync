using CoreLib.Transport;

namespace CoreLib.Tests;

/// <summary>
/// The boundary that browsing rests on.
///
/// <para>Everything else about the feature is a listing and a file transfer that already existed
/// and is already tested. What is new is that a paired device now names something for this one to
/// go and find, and a name supplied by someone else is the oldest source of this class of bug
/// there is. These tests are the reason to be comfortable shipping it.</para>
///
/// <para>They are written against a real directory rather than a mock on purpose: half of what
/// makes traversal hard is what the operating system does with a path once it has it, and a fake
/// filesystem would agree with whatever the code assumed.</para>
/// </summary>
public class SharedFoldersTests : IDisposable
{
    private readonly string _root;
    private readonly string _secret;
    private readonly SharedFolders _folders = new();
    private readonly string _id;

    public SharedFoldersTests()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "meshsync-tests", Guid.NewGuid().ToString("N"));

        _root = Path.Combine(baseDir, "shared");
        _secret = Path.Combine(baseDir, "private");

        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        Directory.CreateDirectory(_secret);

        File.WriteAllText(Path.Combine(_root, "notes.txt"), "in the shared folder");
        File.WriteAllText(Path.Combine(_root, "photos", "beach.jpg"), "pretend");
        File.WriteAllText(Path.Combine(_secret, "passwords.txt"), "not for the mesh");

        _id = _folders.Add(_root)!.Id;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ the ordinary cases

    [Fact]
    public void An_empty_path_is_the_shared_folder_itself()
    {
        Assert.Equal(SharedFolders.Refusal.None, _folders.TryResolve(_id, "", out string resolved));
        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void A_folder_lists_its_contents_with_directories_first()
    {
        Assert.Equal(SharedFolders.Refusal.None, _folders.TryList(_id, "", out var entries));

        Assert.Collection(entries,
            first => { Assert.Equal("photos", first.Name); Assert.True(first.IsDirectory); },
            second => { Assert.Equal("notes.txt", second.Name); Assert.False(second.IsDirectory); });
    }

    [Fact]
    public void A_subfolder_can_be_listed()
    {
        Assert.Equal(SharedFolders.Refusal.None, _folders.TryList(_id, "photos", out var entries));
        Assert.Equal("beach.jpg", Assert.Single(entries).Name);
    }

    [Fact]
    public void A_file_resolves_for_fetching()
    {
        Assert.Equal(SharedFolders.Refusal.None, _folders.TryResolveFile(_id, "photos/beach.jpg", out string path));
        Assert.True(File.Exists(path));
    }

    // ------------------------------------------------------------------ the refusals

    /// <summary>The oldest trick there is, and the one this whole class exists for.</summary>
    [Theory]
    [InlineData("../private/passwords.txt")]
    [InlineData("../../private/passwords.txt")]
    [InlineData("photos/../../private/passwords.txt")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("photos/..")]
    public void Climbing_out_with_dot_dot_is_refused(string relative)
    {
        Assert.Equal(SharedFolders.Refusal.OutsideTheFolder, _folders.TryResolve(_id, relative, out _));
    }

    /// <summary>A separator the host platform does not use must not smuggle a segment past.</summary>
    [Theory]
    [InlineData("..\\private\\passwords.txt")]
    [InlineData("photos\\..\\..\\private")]
    public void Climbing_out_with_the_other_separator_is_refused(string relative)
    {
        Assert.Equal(SharedFolders.Refusal.OutsideTheFolder, _folders.TryResolve(_id, relative, out _));
    }

    /// <summary>
    /// An absolute path is not a relative one, however it is spelled. The drive-relative form
    /// (<c>C:file</c>) is the one that gets missed: it has no separator after the colon and is
    /// resolved against the process's current directory on that drive, not against anything here.
    /// </summary>
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\config\\SAM")]
    [InlineData("C:file")]
    [InlineData("\\\\server\\share\\secrets")]
    [InlineData("//server/share/secrets")]
    public void An_absolute_or_rooted_path_is_refused(string relative)
    {
        Assert.Equal(SharedFolders.Refusal.OutsideTheFolder, _folders.TryResolve(_id, relative, out _));
    }

    [Fact]
    public void An_unknown_folder_id_is_refused_without_touching_the_disk()
    {
        Assert.Equal(SharedFolders.Refusal.NoSuchFolder, _folders.TryResolve("not-a-real-id", "", out _));
    }

    [Fact]
    public void A_path_that_does_not_exist_is_not_found_rather_than_forbidden()
    {
        Assert.Equal(SharedFolders.Refusal.NotFound, _folders.TryResolveFile(_id, "photos/missing.jpg", out _));
    }

    /// <summary>A directory is not a file, and fetching one must not half-work.</summary>
    [Fact]
    public void A_directory_does_not_resolve_as_a_file()
    {
        Assert.Equal(SharedFolders.Refusal.NotFound, _folders.TryResolveFile(_id, "photos", out _));
    }

    /// <summary>
    /// A sibling whose name merely begins with the shared folder's name is outside it. Without a
    /// trailing separator in the containment check, "shared-private" reads as inside "shared".
    /// </summary>
    [Fact]
    public void A_sibling_with_a_shared_prefix_is_outside()
    {
        string sibling = _root + "-private";
        Directory.CreateDirectory(sibling);

        try
        {
            Assert.Equal(SharedFolders.Refusal.OutsideTheFolder,
                         _folders.TryResolve(_id, "../shared-private", out _));
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------------ sharing itself

    [Fact]
    public void Nothing_is_shared_until_it_is_shared()
    {
        Assert.Empty(new SharedFolders().All());
    }

    [Fact]
    public void Sharing_the_same_folder_twice_is_one_entry_with_one_id()
    {
        var again = _folders.Add(_root);

        Assert.Equal(1, _folders.Count);
        Assert.Equal(_id, again!.Id);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_a_second_entry()
    {
        _folders.Add(_root + Path.DirectorySeparatorChar);

        Assert.Equal(1, _folders.Count);
    }

    [Fact]
    public void A_folder_that_does_not_exist_cannot_be_shared()
    {
        Assert.Null(_folders.Add(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void Unsharing_takes_the_access_with_it()
    {
        Assert.True(_folders.Remove(_id));
        Assert.Equal(SharedFolders.Refusal.NoSuchFolder, _folders.TryResolve(_id, "", out _));
    }

    /// <summary>The id says nothing about where the folder is, because peers can see it.</summary>
    [Fact]
    public void The_id_does_not_leak_the_path()
    {
        Assert.DoesNotContain("shared", _id, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, _id);
    }
}
