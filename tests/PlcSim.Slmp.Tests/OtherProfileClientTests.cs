using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcComm.Slmp;

namespace PlcSim.Slmp.Tests;

/// <summary>Q / iQ-F プロファイルの統合テスト（3E・Q 互換サブコマンド）。</summary>
public sealed class OtherProfileClientTests
{
    [Fact]
    public async Task QnUdvBuiltInBehavesLikeMeasuredCpuPort()
    {
        const SlmpPlcProfile profile = SlmpPlcProfile.QnUDV;
        await using var server = await TestServer.StartAsync(CpuProfiles.QnUDV);
        await using var client = await server.ConnectAsync(profile);

        await client.WriteWordsAsync(SlmpAddress.Parse("D100", profile), new ushort[] { 1, 2 });
        Assert.Equal(new ushort[] { 1, 2 }, await client.ReadWordsAsync(SlmpAddress.Parse("D100", profile), 2));

        var typeName = await Assert.ThrowsAsync<SlmpError>(() => client.RawCommandAsync(SlmpCommand.ReadTypeName, 0, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(EndCodes.CommandSubcommandError, typeName.EndCode);

        var catalog = await client.ReadDeviceRangeCatalogAsync();
        Assert.Equal(32768u, catalog.Entries.Single(e => e.Device == "D").PointCount);
        Assert.Equal(131072u, catalog.Entries.Single(e => e.Device == "ZR").PointCount);
        Assert.Equal(0x2000u, catalog.Entries.Single(e => e.Device == "X").PointCount);
    }

    [Fact]
    public async Task QUnitSupportsTypeNameAndBlock()
    {
        const SlmpPlcProfile profile = SlmpPlcProfile.QnUDVQj71E71100;
        await using var server = await TestServer.StartAsync(CpuProfiles.QnUDVQj71E71100);
        await using var client = await server.ConnectAsync(profile);

        Assert.Equal("Q03UDVCPU", (await client.ReadTypeNameAsync()).Model);
        var d = SlmpAddress.Parse("D10", profile);
        await client.WriteBlockAsync(new[] { new SlmpBlockWrite(d, new ushort[] { 9 }) }, Array.Empty<SlmpBlockWrite>());
        Assert.Equal(new ushort[] { 9 }, await client.ReadWordBlocksAsync(new[] { new SlmpBlockRead(d, 1) }));
    }

    [Fact]
    public async Task IqFUsesOctalXyAndRejectsMonitor()
    {
        const SlmpPlcProfile profile = SlmpPlcProfile.IqF;
        await using var server = await TestServer.StartAsync(CpuProfiles.IqF);
        await using var client = await server.ConnectAsync(profile);

        await client.WriteBitsAsync(SlmpAddress.Parse("Y17", profile), new[] { true });
        lock (server.Plc.SyncRoot)
            Assert.True(server.Plc.Memory.ReadBits(DeviceCatalog.FindByName("Y")!, 15, 1)[0]);

        var monitor = await Assert.ThrowsAsync<SlmpError>(() => client.RawCommandAsync(SlmpCommand.Monitor, 0, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(EndCodes.CommandSubcommandError, monitor.EndCode);

        var catalog = await client.ReadDeviceRangeCatalogAsync();
        Assert.Equal(8000u, catalog.Entries.Single(e => e.Device == "D").PointCount);
        Assert.Equal(1024u, catalog.Entries.Single(e => e.Device == "X").PointCount);
    }
}
