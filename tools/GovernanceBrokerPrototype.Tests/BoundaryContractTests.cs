using System.Text;
using System.Text.Json;
using Ashlar.Tools.GovernanceBrokerPrototype;
using Xunit;

namespace GovernanceBrokerPrototype.Tests;

// These tests use byte streams and metadata values only. They do not contact a
// Unix socket, inspect/change real permissions, or create additional identities.
public sealed class BrokerFramingTests
{
    [Fact]
    public async Task One_UTF8_frame_is_returned_without_its_delimiter()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"summary\":\"日本語 👩🏽‍💻 e\u0301\"}");
        using var stream = new MemoryStream(bytes.Concat(new byte[] { 10 }).ToArray());
        Assert.Equal(bytes, await Program.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("\n")]
    [InlineData("{}\ntrailing")]
    [InlineData("{}\n{}\n")]
    [InlineData("{}\n\n")]
    public async Task Empty_unterminated_or_coalesced_extra_frames_are_refused(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await Assert.ThrowsAsync<ArgumentException>(() => Program.ReadFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Exact_limit_is_accepted_and_one_extra_byte_is_refused()
    {
        var atLimit = Enumerable.Repeat((byte)'x', BrokerRequest.MaxBytes).Append((byte)10).ToArray();
        using var good = new MemoryStream(atLimit);
        Assert.Equal(BrokerRequest.MaxBytes,
            (await Program.ReadFrameAsync(good, CancellationToken.None)).Length);
        var overLimit = Enumerable.Repeat((byte)'x', BrokerRequest.MaxBytes + 1).Append((byte)10).ToArray();
        using var bad = new MemoryStream(overLimit);
        await Assert.ThrowsAsync<ArgumentException>(() => Program.ReadFrameAsync(bad, CancellationToken.None));
    }

    [Fact]
    public async Task Fragmented_multibyte_text_is_not_decoded_between_reads()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"summary\":\"👩🏽‍💻\"}\n");
        using var stream = new OneByteStream(bytes);
        Assert.Equal(bytes[..^1], await Program.ReadFrameAsync(stream, CancellationToken.None));
        // Later bytes are not part of the first frame. Main dispatches once and
        // closes this connection; this helper cannot predict future stream data.
        using var later = new OneByteStream(bytes.Concat(Encoding.UTF8.GetBytes("{}\n")).ToArray());
        Assert.Equal(bytes[..^1], await Program.ReadFrameAsync(later, CancellationToken.None));
        Assert.Equal((long)bytes.Length, later.Position);
        Assert.True(later.Position < later.Length);
    }

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

public sealed class BrokerConfigurationTests
{
    // Configuration helpers run in cross-platform readiness CI even though the
    // service itself is Linux-only. These fixture paths are strings, not created files.
    private static readonly string VolumeRoot = Path.GetPathRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("The test assembly must have an absolute path.");
    private static readonly string Valid = JsonSerializer.Serialize(new
    {
        project = "prototype-project",
        root = Path.Combine(VolumeRoot, "srv", "ashlar-prototype"),
        socketPath = Path.Combine(VolumeRoot, "run", "ashlar-prototype", "broker.sock"),
        workerUid = 12001,
    });

    [Fact]
    public void Valid_config_requires_distinct_nonroot_identities()
    {
        var config = BrokerConfiguration.Parse(Valid);
        config.ValidateIdentity(11001);
        Assert.Equal("prototype-project", config.Project);
        Assert.Equal(12001u, config.WorkerUid);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"project\":\"prototype-project\"}")]
    [InlineData("{\"project\":\"p\",\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":12001,\"unknown\":true}")]
    [InlineData("{\"project\":\"p\",\"project\":\"p\",\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":12001}")]
    [InlineData("{\"Project\":\"p\",\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":12001}")]
    [InlineData("{\"project\":42,\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":12001}")]
    [InlineData("{\"project\":\"p\",\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":\"12001\"}")]
    [InlineData("{\"project\":\"p\",\"root\":\"/srv/p\",\"socketPath\":\"/run/p.sock\",\"workerUid\":-1}")]
    public void Missing_duplicate_unknown_and_badly_typed_config_fields_are_refused(string json)
    {
        Assert.NotNull(Xunit.Record.Exception(() => BrokerConfiguration.Parse(json)));
    }

    [Theory]
    [InlineData(0u, 12001u)]
    [InlineData(11001u, 0u)]
    [InlineData(11001u, 11001u)]
    public void Root_or_shared_identity_is_refused(uint service, uint worker)
    {
        var config = BrokerConfiguration.Parse(Valid) with { WorkerUid = worker };
        Assert.Throws<InvalidOperationException>(() => config.ValidateIdentity(service));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/srv/../ashlar-prototype")]
    [InlineData("/srv/./ashlar-prototype")]
    public void Root_requires_a_normalized_absolute_nonroot_path(string path)
    {
        var config = BrokerConfiguration.Parse(Valid) with { Root = path };
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
    }

    [Theory]
    [InlineData("")]
    [InlineData("broker.sock")]
    [InlineData("/")]
    [InlineData("/run/../broker.sock")]
    public void Socket_requires_a_normalized_absolute_nonroot_path(string path)
    {
        var config = BrokerConfiguration.Parse(Valid) with { SocketPath = path };
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
    }

    [Fact]
    public void Socket_length_is_bounded()
    {
        var prefixBytes = Encoding.UTF8.GetByteCount(VolumeRoot);
        Assert.InRange(prefixBytes, 1, 97);
        var config = BrokerConfiguration.Parse(Valid) with
        {
            SocketPath = VolumeRoot + new string('x', 101 - prefixBytes),
        };
        Assert.Equal(101, Encoding.UTF8.GetByteCount(config.SocketPath));
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
        // Keep the exact UTF-8 boundary after either a Unix or Windows volume root.
        var remaining = 100 - prefixBytes;
        config = config with
        {
            SocketPath = VolumeRoot + new string('界', remaining / 3) + new string('x', remaining % 3),
        };
        Assert.Equal(100, Encoding.UTF8.GetByteCount(config.SocketPath));
        config.ValidateIdentity(11001);
        config = config with { SocketPath = config.SocketPath + "界" };
        Assert.Equal(103, Encoding.UTF8.GetByteCount(config.SocketPath));
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Project_must_be_nonempty(string project)
    {
        var config = BrokerConfiguration.Parse(Valid) with { Project = project };
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
    }

    [Fact]
    public void Project_length_is_bounded()
    {
        var config = BrokerConfiguration.Parse(Valid) with { Project = new string('x', 65) };
        Assert.Throws<ArgumentException>(() => config.ValidateIdentity(11001));
    }
}

public sealed class LinuxMetadataPolicyTests
{
    private const uint Service = 11001;
    private const ushort Directory = 0x4000;
    private const ushort Regular = 0x8000;

    [Theory]
    [InlineData(0u)]
    [InlineData(Service)]
    public void Trusted_nonwritable_directory_ancestors_are_accepted(uint owner)
    {
        LinuxBoundary.ValidateMetadata(new(owner, Directory | 0x1ed, 2), Service, requireDirectory: true); // 0755
    }

    [Fact]
    public void Trusted_regular_leaf_with_one_link_is_accepted()
    {
        LinuxBoundary.ValidateMetadata(new(0, Regular | 0x180, 1), Service); // 0600
        LinuxBoundary.ValidateMetadata(new(Service, Regular | 0x180, 1), Service);
    }

    [Fact]
    public void Third_party_owner_is_refused()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            LinuxBoundary.ValidateMetadata(new(12001, Directory | 0x1c0, 2), Service, requireDirectory: true));
    }

    [Theory]
    [InlineData(0x10)] // group write
    [InlineData(0x02)] // other write
    public void Group_or_other_write_on_an_ancestor_is_refused(int permission)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            LinuxBoundary.ValidateMetadata(new(0, (ushort)(Directory | 0x1c0 | permission), 2),
                Service, requireDirectory: true));
    }

    [Fact]
    public void Regular_file_cannot_be_an_ancestor()
    {
        Assert.Throws<IOException>(() =>
            LinuxBoundary.ValidateMetadata(new(0, Regular | 0x180, 1), Service, requireDirectory: true));
    }

    [Fact]
    public void Private_service_owned_directory_and_file_are_accepted()
    {
        LinuxBoundary.ValidateMetadata(new(Service, Directory | 0x1c0, 2), Service, privateTree: true); // 0700
        LinuxBoundary.ValidateMetadata(new(Service, Regular | 0x180, 1), Service, privateTree: true); // 0600
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(12001u)]
    public void Private_tree_requires_service_ownership(uint owner)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            LinuxBoundary.ValidateMetadata(new(owner, Directory | 0x1c0, 2), Service, privateTree: true));
    }

    [Theory]
    [InlineData(0x20)] // group read
    [InlineData(0x10)] // group write
    [InlineData(0x08)] // group execute
    [InlineData(0x04)] // other read
    [InlineData(0x02)] // other write
    [InlineData(0x01)] // other execute
    public void Private_tree_rejects_all_group_or_other_access(int permission)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            LinuxBoundary.ValidateMetadata(new(Service, (ushort)(Directory | 0x1c0 | permission), 2),
                Service, privateTree: true));
    }

    [Theory]
    [InlineData(0xa000)] // symlink
    [InlineData(0xc000)] // socket
    [InlineData(0x1000)] // fifo
    [InlineData(0x2000)] // character device
    [InlineData(0x6000)] // block device
    public void Nonregular_nondirectory_entries_are_refused(int type)
    {
        Assert.Throws<IOException>(() =>
            LinuxBoundary.ValidateMetadata(new(Service, (ushort)(type | 0x180), 1), Service));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(7u)]
    public void Files_must_have_exactly_one_hard_link(uint links)
    {
        Assert.Throws<IOException>(() =>
            LinuxBoundary.ValidateMetadata(new(Service, Regular | 0x180, links), Service, privateTree: true));
    }
}

public sealed class BrokerCapacityConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(129)]
    public void Invalid_capacity_is_refused_without_accessing_a_store(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Broker("prototype-project", 12001, null!, TimeProvider.System, maximumRecords: capacity));
    }
}
