using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;
using PlcComm.Slmp;

namespace PlcSim.Slmp.Tests;

/// <summary>
/// PlcComm.Slmp の通信 API 80 件を
/// すべて呼び、シミュレータの応答と読み戻しで確認する。
/// アドレスは実機 iQ-R の検査と同じ範囲を使う。
/// </summary>
public sealed class ApiCoverageTests
{
    private const SlmpPlcProfile P = SlmpPlcProfile.IqR;

    private static SlmpDeviceAddress Dev(string text) => SlmpAddress.Parse(text, P);

    private static SlmpQualifiedDeviceAddress Q(string text) => SlmpQualifiedDeviceParser.Parse(text, P);

    /// <summary>実機検査（U0\G・U2\G・J1\）と同じユニット・ネットワークを登録した設定。</summary>
    private static PlcSettings Units02() => new() { Units = new ushort[] { 0, 2 }, Networks = new byte[] { 1 } };

    // ---- SlmpClient: 状態・形名 -----------------------------------------
    [Fact]
    public async Task StatusApis()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        Assert.Equal("R08CPU", (await client.ReadTypeNameAsync()).Model);                                   // ReadTypeNameAsync
        Assert.Equal(SlmpCpuOperationStatus.Run, (await client.ReadCpuOperationStateAsync()).Status);       // ReadCpuOperationStateAsync
        Assert.Equal(262144u, (await client.ReadDeviceRangeCatalogAsync()).Entries.Single(e => e.Device == "D").PointCount); // ReadDeviceRangeCatalogAsync
        server.Plc.SetErrorCode(0x2400);
        Assert.Equal(0x2400, await client.ReadLatestSelfDiagnosisErrorCodeAsync());                         // ReadLatestSelfDiagnosisErrorCodeAsync
        await client.ClearErrorAsync();                                                                       // ClearErrorAsync
        Assert.Equal(0, server.Plc.ErrorCode);
        Assert.Equal("0123456789ABCDEF"u8.ToArray(), await client.SelfTestLoopbackAsync("0123456789ABCDEF"u8.ToArray())); // SelfTestLoopbackAsync
    }

    // ---- SlmpClient: 一括読み書き（通常・拡張） --------------------------
    [Fact]
    public async Task DirectApis()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        var words = Enumerable.Range(0, 10).Select(static i => (ushort)(1000 + i)).ToArray();
        await client.WriteWordsAsync(Dev("D1000"), words);                                                   // WriteWordsAsync
        Assert.Equal(words, await client.ReadWordsAsync(Dev("D1000"), 10));                                   // ReadWordsAsync

        var bits = new[] { true, false, true, true };
        await client.WriteBitsAsync(Dev("M1000"), bits);                                                     // WriteBitsAsync
        Assert.Equal(bits, await client.ReadBitsAsync(Dev("M1000"), 4));                                     // ReadBitsAsync

        await client.WriteDWordsAsync(Dev("D1020"), new uint[] { 0xDEADBEEF, 70000 });                      // WriteDWordsAsync
        Assert.Equal(new uint[] { 0xDEADBEEF, 70000 }, await client.ReadDWordsAsync(Dev("D1020"), 2));       // ReadDWordsAsync

        await client.WriteFloat32sAsync(Dev("D1030"), new[] { 3.25f, -1.5f });                               // WriteFloat32sAsync
        Assert.Equal(new[] { 3.25f, -1.5f }, await client.ReadFloat32sAsync(Dev("D1030"), 2));               // ReadFloat32sAsync

        await client.WriteWordsExtendedAsync(Q(@"U0\G0"), new ushort[] { 11, 12 });                          // WriteWordsExtendedAsync
        Assert.Equal(new ushort[] { 11, 12 }, await client.ReadWordsExtendedAsync(Q(@"U0\G0"), 2));          // ReadWordsExtendedAsync
        await client.WriteWordsExtendedAsync(Q(@"J1\W10"), new ushort[] { 0xABCD });
        Assert.Equal(new ushort[] { 0xABCD }, await client.ReadWordsExtendedAsync(Q(@"J1\W10"), 1));

        await client.WriteBitsExtendedAsync(Q(@"J1\X10"), new[] { true, true, false });                      // WriteBitsExtendedAsync
        Assert.Equal(new[] { true, true, false }, await client.ReadBitsExtendedAsync(Q(@"J1\X10"), 3));      // ReadBitsExtendedAsync
    }

    // ---- SlmpClient: ランダム（通常・拡張） -------------------------------
    [Fact]
    public async Task RandomApis()
    {
        await using var server = await TestServer.StartAsync(settings: Units02());
        await using var client = await server.ConnectAsync(P);

        await client.WriteRandomWordsAsync(new[] { (Dev("D1040"), (ushort)1) }, new[] { (Dev("D1042"), 0x00020003u) }); // WriteRandomWordsAsync
        await client.WriteRandomU16sAsync(new[] { (Dev("D1044"), (ushort)44), (Dev("W1F"), (ushort)31) });  // WriteRandomU16sAsync
        await client.WriteRandomU32sAsync(new[] { (Dev("D1046"), 460000u) });                                // WriteRandomU32sAsync
        await client.WriteRandomBitsAsync(new[] { (Dev("M1010"), true), (Dev("Y1F"), true) });             // WriteRandomBitsAsync

        var (w, d) = await client.ReadRandomAsync(new[] { Dev("D1040"), Dev("D1044") }, new[] { Dev("D1042"), Dev("D1046") }); // ReadRandomAsync
        Assert.Equal(new ushort[] { 1, 44 }, w);
        Assert.Equal(new uint[] { 0x00020003, 460000 }, d);
        Assert.Equal(new ushort[] { 31, 44 }, await client.ReadRandomWordsAsync(new[] { Dev("W1F"), Dev("D1044") })); // ReadRandomWordsAsync
        Assert.Equal(new uint[] { 460000 }, await client.ReadRandomDWordsAsync(new[] { Dev("D1046") }));     // ReadRandomDWordsAsync
        Assert.Equal(new[] { true, true }, new[] { (await client.ReadBitsAsync(Dev("M1010"), 1))[0], (await client.ReadBitsAsync(Dev("Y1F"), 1))[0] });

        await client.WriteRandomWordsExtendedAsync(new[] { (Q(@"U0\G10"), (ushort)10) }, new[] { (Q(@"U2\G20"), 200000u) }); // WriteRandomWordsExtendedAsync
        await client.WriteRandomU16sExtendedAsync(new[] { (Q(@"U0\G11"), (ushort)11) });                    // WriteRandomU16sExtendedAsync
        await client.WriteRandomU32sExtendedAsync(new[] { (Q(@"U2\G22"), 220000u) });                       // WriteRandomU32sExtendedAsync
        await client.WriteRandomBitsExtendedAsync(new[] { (Q(@"J1\X1E"), true) });                         // WriteRandomBitsExtendedAsync

        var (xw, xd) = await client.ReadRandomExtendedAsync(new[] { Q(@"U0\G10"), Q(@"U0\G11") }, new[] { Q(@"U2\G20") }); // ReadRandomExtendedAsync
        Assert.Equal(new ushort[] { 10, 11 }, xw);
        Assert.Equal(new uint[] { 200000 }, xd);
        Assert.Equal(new ushort[] { 11 }, await client.ReadRandomWordsExtendedAsync(new[] { Q(@"U0\G11") })); // ReadRandomWordsExtendedAsync
        Assert.Equal(new uint[] { 220000 }, await client.ReadRandomDWordsExtendedAsync(new[] { Q(@"U2\G22") })); // ReadRandomDWordsExtendedAsync
        Assert.Equal(new[] { true }, await client.ReadBitsExtendedAsync(Q(@"J1\X1E"), 1));
    }

    // ---- SlmpClient: ブロック・モニタ -------------------------------------
    [Fact]
    public async Task BlockAndMonitorApis()
    {
        await using var server = await TestServer.StartAsync(settings: Units02());
        await using var client = await server.ConnectAsync(P);

        await client.WriteBlockAsync(                                                                          // WriteBlockAsync
            new[] { new SlmpBlockWrite(Dev("D1050"), new ushort[] { 5, 6 }) },
            new[] { new SlmpBlockWrite(Dev("M1040"), new ushort[] { 0x0003 }) });
        await client.WriteWordBlocksAsync(new[] { new SlmpBlockWrite(Dev("D1060"), new ushort[] { 60, 61, 62 }) }); // WriteWordBlocksAsync
        await client.WriteBitBlocksAsync(new[] { new SlmpBlockWrite(Dev("M1056"), new ushort[] { 0x8000 }) });    // WriteBitBlocksAsync

        var block = await client.ReadBlockAsync(                                                               // ReadBlockAsync
            new[] { new SlmpBlockRead(Dev("D1050"), 2) },
            new[] { new SlmpBlockRead(Dev("M1040"), 1) });
        Assert.Equal(new ushort[] { 5, 6 }, block.WordValues);
        Assert.Equal(new ushort[] { 0x0003 }, block.BitWordValues);
        Assert.Equal(new ushort[] { 60, 61, 62 }, await client.ReadWordBlocksAsync(new[] { new SlmpBlockRead(Dev("D1060"), 3) })); // ReadWordBlocksAsync
        Assert.Equal(new ushort[] { 0x8000 }, await client.ReadBitBlocksAsync(new[] { new SlmpBlockRead(Dev("M1056"), 1) }));      // ReadBitBlocksAsync
        Assert.Equal(new[] { true }, await client.ReadBitsAsync(Dev("M1071"), 1));

        await client.RegisterMonitorDevicesAsync(new[] { Dev("D1050"), Dev("D1060") }, new[] { Dev("D1020") }); // RegisterMonitorDevicesAsync
        await client.WriteWordsAsync(Dev("D1020"), new ushort[] { 0x1111, 0x2222 });
        var monitor = await client.RunMonitorCycleAsync(2, 1);                                                 // RunMonitorCycleAsync
        Assert.Equal(new ushort[] { 5, 60 }, monitor.WordValues);
        Assert.Equal(new uint[] { 0x22221111 }, monitor.DwordValues);

        await client.WriteWordsExtendedAsync(Q(@"U0\G30"), new ushort[] { 30 });
        await client.RegisterMonitorDevicesExtendedAsync(new[] { Q(@"U0\G30") }, new[] { Q(@"U2\G40") }); // RegisterMonitorDevicesExtendedAsync（iQ-R 拡張形式）
        await client.WriteWordsExtendedAsync(Q(@"U2\G40"), new ushort[] { 0x0001, 0x0002 });
        var extMonitor = await client.RunMonitorCycleAsync(1, 1);
        Assert.Equal(new ushort[] { 30 }, extMonitor.WordValues);
        Assert.Equal(new uint[] { 0x00020001 }, extMonitor.DwordValues);
        await client.RegisterMonitorDevicesExtendedAsync(new[] { Q(@"J1\W1F") }, Array.Empty<SlmpQualifiedDeviceAddress>()); // リンクダイレクト（Q/L 形式）
        await client.WriteWordsExtendedAsync(Q(@"J1\W1F"), new ushort[] { 0x1F });
        Assert.Equal(new ushort[] { 0x1F }, (await client.RunMonitorCycleAsync(1, 0)).WordValues);
    }

    // ---- SlmpClient: ラベル -------------------------------------------------
    [Fact]
    public async Task LabelApis()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        await client.WriteRandomLabelsAsync(new[] { new SlmpLabelRandomWritePoint("SLMP_TEST.WordValue", new byte[] { 0xCD, 0xAB }) }); // WriteRandomLabelsAsync
        var random = await client.ReadRandomLabelsAsync(new[] { "%1.WordValue" }, new[] { "SLMP_TEST" });      // ReadRandomLabelsAsync
        Assert.Equal(new byte[] { 0xCD, 0xAB }, random[0].Data);
        Assert.Equal(2, random[0].DataTypeId);

        await client.WriteArrayLabelsAsync(new[] { new SlmpLabelArrayWritePoint("SLMP_TEST.WordArray[0]", 1, 4, new byte[] { 9, 0, 8, 0 }) }); // WriteArrayLabelsAsync
        var array = await client.ReadArrayLabelsAsync(new[] { new SlmpLabelArrayReadPoint("%1.WordArray[0]", 1, 4) }, new[] { "SLMP_TEST" }); // ReadArrayLabelsAsync
        Assert.Equal(new byte[] { 9, 0, 8, 0 }, array[0].Data);
    }

    // ---- SlmpClient: ロングタイマ ------------------------------------------
    [Fact]
    public async Task LongTimerApis()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        await client.WriteRandomU32sAsync(new[] { (Dev("LTN100"), 1000u), (Dev("LSTN100"), 2000u) });
        await client.WriteRandomBitsAsync(new[] { (Dev("LTC100"), true), (Dev("LTS101"), true), (Dev("LSTC100"), true), (Dev("LSTS101"), true) });

        var lt = await client.ReadLongTimerAsync(100, 2);                                                      // ReadLongTimerAsync
        Assert.Equal(1000u, lt[0].CurrentValue);
        var lst = await client.ReadLongRetentiveTimerAsync(100, 2);                                            // ReadLongRetentiveTimerAsync
        Assert.Equal(2000u, lst[0].CurrentValue);
        Assert.Equal(new[] { true, false }, await client.ReadLtcStatesAsync(100, 2));                         // ReadLtcStatesAsync
        Assert.Equal(new[] { false, true }, await client.ReadLtsStatesAsync(100, 2));                         // ReadLtsStatesAsync
        Assert.Equal(new[] { true, false }, await client.ReadLstcStatesAsync(100, 2));                        // ReadLstcStatesAsync
        Assert.Equal(new[] { false, true }, await client.ReadLstsStatesAsync(100, 2));                        // ReadLstsStatesAsync
    }

    // ---- SlmpClient: リモート操作・パスワード ------------------------------
    [Fact]
    public async Task RemoteApis()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        await client.RemotePauseAsync(SlmpRemoteMode.Normal);                                                 // RemotePauseAsync
        Assert.Equal(PlcRunState.Pause, server.Plc.State);
        await client.RemoteRunAsync(SlmpRemoteMode.Normal, SlmpRemoteClearMode.NoClear);                      // RemoteRunAsync
        Assert.Equal(PlcRunState.Run, server.Plc.State);
        await client.RemoteStopAsync();                                                                         // RemoteStopAsync
        Assert.Equal(PlcRunState.Stop, server.Plc.State);
        await client.RemoteLatchClearAsync();                                                                   // RemoteLatchClearAsync
        server.Plc.SetErrorCode(0x1234);
        await client.RemoteResetAsync();                                                                        // RemoteResetAsync（応答を待たずに切断する仕様）
        await WaitAsync(() => server.Plc.ErrorCode == 0);
        // 実機と同じく、再起動後は RUN/STOP スイッチ相当（起動時の状態、既定 RUN）に従う
        Assert.Equal(PlcRunState.Run, server.Plc.State);

        await client.OpenAsync();
        Assert.Equal(SlmpCpuOperationStatus.Run, (await client.ReadCpuOperationStateAsync()).Status);
    }

    [Fact]
    public async Task PasswordApis()
    {
        await using var server = await TestServer.StartAsync(settings: new PlcSettings { RemotePassword = "melsec" });
        await using var client = await server.ConnectAsync(P);

        var locked = await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("D1000"), 1));
        Assert.Equal(EndCodes.RemotePasswordLocked, locked.EndCode);
        await client.RemotePasswordUnlockAsync("melsec");                                                     // RemotePasswordUnlockAsync
        Assert.Equal(new ushort[] { 0 }, await client.ReadWordsAsync(Dev("D1000"), 1));
        await client.RemotePasswordLockAsync("melsec");                                                       // RemotePasswordLockAsync
        await Assert.ThrowsAsync<SlmpError>(() => client.ReadWordsAsync(Dev("D1000"), 1));
    }

    // ---- SlmpClientExtensions（文字列アドレス版） --------------------------
    [Fact]
    public async Task ExtensionApisWithText()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        await client.WriteTypedAsync("D1070", "S", (short)-5);                                                // WriteTypedAsync(string)
        Assert.Equal((short)-5, await client.ReadTypedAsync("D1070", "S"));                                   // ReadTypedAsync(string)
        await client.WriteTypedAsync("D1072", "F", 2.5f);
        Assert.Equal(2.5f, await client.ReadTypedAsync("D1072", "F"));
        await client.WriteTypedAsync("M1080", "BIT", true);
        Assert.Equal(true, await client.ReadTypedAsync("M1080", "BIT"));

        await client.WriteBitInWordAsync("D1074", 3, true);                                                  // WriteBitInWordAsync(string)
        Assert.Equal(new ushort[] { 0x0008 }, await client.ReadWordsAsync(Dev("D1074"), 1));

        await client.WriteBitsSingleRequestAsync("M1090", new[] { true, false, true });                      // WriteBitsSingleRequestAsync(string)
        Assert.Equal(new[] { true, false, true }, await client.ReadBitsSingleRequestAsync("M1090", 3));      // ReadBitsSingleRequestAsync(string)
        await client.WriteWordsSingleRequestAsync("D1080", new ushort[] { 80, 81 });                         // WriteWordsSingleRequestAsync(string)
        Assert.Equal(new ushort[] { 80, 81 }, await client.ReadWordsSingleRequestAsync("D1080", 2));         // ReadWordsSingleRequestAsync(string)
        await client.WriteDWordsSingleRequestAsync("D1084", new uint[] { 840000 });                          // WriteDWordsSingleRequestAsync(string)
        Assert.Equal(new uint[] { 840000 }, await client.ReadDWordsSingleRequestAsync("D1084", 1));          // ReadDWordsSingleRequestAsync(string)

        await client.WriteNamedAsync(new Dictionary<string, object> { ["D1090:U"] = (ushort)90, ["D1092:F"] = 9.5f }); // WriteNamedAsync
        await client.WriteNamedAsync(new Dictionary<string, object> { ["M1095:BIT"] = true });
        var named = await client.ReadNamedAsync(new[] { "D1090:U", "D1092:F", "D1074.3", "M1095:BIT" });       // ReadNamedAsync
        Assert.Equal((ushort)90, named["D1090:U"]);
        Assert.Equal(9.5f, named["D1092:F"]);
        Assert.Equal(true, named["D1074.3"]);
        Assert.Equal(true, named["M1095:BIT"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var polls = 0;
        await foreach (var snapshot in client.PollAsync(new[] { "D1090:U" }, TimeSpan.FromMilliseconds(20), cts.Token)) // PollAsync
        {
            Assert.Equal((ushort)90, snapshot["D1090:U"]);
            if (++polls == 3)
                break;
        }
    }

    // ---- SlmpClientExtensions（アドレス型版） ------------------------------
    [Fact]
    public async Task ExtensionApisWithAddress()
    {
        await using var server = await TestServer.StartAsync();
        await using var client = await server.ConnectAsync(P);

        await client.WriteTypedAsync(Dev("D1000"), "L", -70000);                                              // WriteTypedAsync(SlmpDeviceAddress)
        Assert.Equal(-70000L, Convert.ToInt64(await client.ReadTypedAsync(Dev("D1000"), "L")));                                 // ReadTypedAsync(SlmpDeviceAddress)
        await client.WriteBitInWordAsync(Dev("D1002"), 15, true);                                            // WriteBitInWordAsync(SlmpDeviceAddress)
        Assert.Equal(new ushort[] { 0x8000 }, await client.ReadWordsAsync(Dev("D1002"), 1));
        await client.WriteBitInWordAsync(Q(@"U0\G5"), 1, true);                                              // WriteBitInWordAsync(SlmpQualifiedDeviceAddress)
        Assert.Equal(new ushort[] { 0x0002 }, await client.ReadWordsExtendedAsync(Q(@"U0\G5"), 1));

        await client.WriteBitsSingleRequestAsync(Dev("M1000"), new[] { true, true });                       // WriteBitsSingleRequestAsync(SlmpDeviceAddress)
        Assert.Equal(new[] { true, true }, await client.ReadBitsSingleRequestAsync(Dev("M1000"), 2));       // ReadBitsSingleRequestAsync(SlmpDeviceAddress)
        await client.WriteWordsSingleRequestAsync(Dev("D1010"), new ushort[] { 1, 2, 3 });                  // WriteWordsSingleRequestAsync(SlmpDeviceAddress)
        Assert.Equal(new ushort[] { 1, 2, 3 }, await client.ReadWordsSingleRequestAsync(Dev("D1010"), 3));  // ReadWordsSingleRequestAsync(SlmpDeviceAddress)
        await client.WriteDWordsSingleRequestAsync(Dev("D1020"), new uint[] { 1, 0xFFFFFFFF });             // WriteDWordsSingleRequestAsync(SlmpDeviceAddress)
        Assert.Equal(new uint[] { 1, 0xFFFFFFFF }, await client.ReadDWordsSingleRequestAsync(Dev("D1020"), 2)); // ReadDWordsSingleRequestAsync(SlmpDeviceAddress)
    }

    // ---- 全プロファイルで共通 API ------------------------------------------
    public static TheoryData<string, SlmpPlcProfile> Profiles => new()
    {
        { CpuProfiles.IqR, SlmpPlcProfile.IqR },
        { CpuProfiles.IqRRj71En71, SlmpPlcProfile.IqRRj71En71 },
        { CpuProfiles.QnUDV, SlmpPlcProfile.QnUDV },
        { CpuProfiles.QnU, SlmpPlcProfile.QnU },
        { CpuProfiles.QCpuQj71E71100, SlmpPlcProfile.QCpuQj71E71100 },
        { CpuProfiles.QnUQj71E71100, SlmpPlcProfile.QnUQj71E71100 },
        { CpuProfiles.QnUDVQj71E71100, SlmpPlcProfile.QnUDVQj71E71100 },
        { CpuProfiles.LCpu, SlmpPlcProfile.LCpu },
        { CpuProfiles.LCpuLj71E71100, SlmpPlcProfile.LCpuLj71E71100 },
        { CpuProfiles.IqF, SlmpPlcProfile.IqF },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task CommonApisOnEveryProfile(string profileId, SlmpPlcProfile profile)
    {
        foreach (var transport in new[] { SlmpTransportMode.Tcp, SlmpTransportMode.Udp })
        {
            await using var server = await TestServer.StartAsync(profileId);
            await using var client = await server.ConnectAsync(profile, transport);
            SlmpDeviceAddress A(string text) => SlmpAddress.Parse(text, profile);

            await client.WriteWordsAsync(A("D100"), new ushort[] { 1, 2 });
            Assert.Equal(new ushort[] { 1, 2 }, await client.ReadWordsAsync(A("D100"), 2));
            await client.WriteBitsAsync(A("M100"), new[] { true, false });
            Assert.Equal(new[] { true, false }, await client.ReadBitsAsync(A("M100"), 2));
            await client.WriteRandomWordsAsync(new[] { (A("D110"), (ushort)7) }, new[] { (A("D112"), 70000u) });
            var (w, d) = await client.ReadRandomAsync(new[] { A("D110") }, new[] { A("D112") });
            Assert.Equal(new ushort[] { 7 }, w);
            Assert.Equal(new uint[] { 70000 }, d);
            await client.WriteTypedAsync("D120", "F", 1.25f);
            Assert.Equal(1.25f, await client.ReadTypedAsync("D120", "F"));
            Assert.Equal((ushort)7, (await client.ReadNamedAsync(new[] { "D110:U" }))["D110:U"]);
            Assert.Equal(SlmpCpuOperationStatus.Run, (await client.ReadCpuOperationStateAsync()).Status);
            var catalog = await client.ReadDeviceRangeCatalogAsync();
            Assert.Equal(server.Plc.Profile.PointsOf("D"), catalog.Entries.Single(e => e.Device == "D").PointCount);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
