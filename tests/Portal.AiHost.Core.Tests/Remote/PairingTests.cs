using Portal.AiHost.Contracts;
using Portal.AiHost.Core.Remote;
using Portal.AiHost.Core.Tests.Support;

namespace Portal.AiHost.Core.Tests.Remote;

public sealed class PairingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly byte[] _fingerprint = new byte[32];
    private readonly DeviceStore _devices;
    private readonly PairingService _pairing;

    public PairingTests()
    {
        _fingerprint[0] = 7;
        _devices = new DeviceStore(_dir.Path, _time);
        _pairing = new PairingService(_devices, "Desk PC", _fingerprint, _time);
    }

    public void Dispose() => _dir.Dispose();

    private static string WrongCodeFor(string code) => code == "000000" ? "000001" : "000000";

    private static string ReasonOf(Action action) => Assert.Throws<PairingException>(action).Reason;

    [Fact]
    public void GoodCodePairsTheDeviceAndEachSideProvesTheCode()
    {
        var offer = _pairing.Begin();
        Assert.Matches(@"^\d{6}$", offer.Code);
        using var client = new PairingClient("Laptop");

        var id = client.Pair(_pairing, offer.Code);

        var stored = Assert.Single(_devices.List());
        Assert.Equal(id, stored.Id);
        Assert.Equal("Laptop", stored.Name);
        Assert.Equal(RemoteCodec.Encode(client.PublicKey), stored.PublicKey);
        Assert.Null(_pairing.Current);
    }

    [Fact]
    public void ThePairedDeviceSurvivesARestart()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        var id = client.Pair(_pairing, offer.Code);

        var reopened = new DeviceStore(_dir.Path, _time);
        Assert.Equal(id, Assert.Single(reopened.List()).Id);
    }

    [Fact]
    public void ACodeServesOneDeviceOnly()
    {
        var offer = _pairing.Begin();
        using var first = new PairingClient("One");
        first.Pair(_pairing, offer.Code);

        using var second = new PairingClient("Two");
        Assert.Equal(PairingReasons.NoOffer, ReasonOf(() => _pairing.Start(second.Start(offer.Code))));
        Assert.Single(_devices.List());
    }

    [Fact]
    public void WrongCodeIsRefusedAndTheHostRevealsNothingDerivedFromTheCode()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();

        Assert.Equal(PairingReasons.WrongCode, ReasonOf(() => _pairing.Start(client.Start(WrongCodeFor(offer.Code)))));
        Assert.Empty(_devices.List());
        Assert.NotNull(_pairing.Current);
    }

    [Fact]
    public void FifthWrongAttemptVoidsTheCodeEvenForTheRightCode()
    {
        var offer = _pairing.Begin();
        var wrong = WrongCodeFor(offer.Code);
        using var attacker = new PairingClient("Attacker");
        for (var i = 1; i < RemoteApi.MaxWrongAttempts; i++)
        {
            Assert.Equal(PairingReasons.WrongCode, ReasonOf(() => _pairing.Start(attacker.Start(wrong))));
        }

        Assert.Equal(PairingReasons.CodeVoided, ReasonOf(() => _pairing.Start(attacker.Start(wrong))));
        using var owner = new PairingClient("Owner");
        Assert.Equal(PairingReasons.CodeVoided, ReasonOf(() => _pairing.Start(owner.Start(offer.Code))));
        Assert.Null(_pairing.Current);
        Assert.Empty(_devices.List());
    }

    [Fact]
    public void ACodeExpiresAfterFiveMinutes()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        _time.Advance(TimeSpan.FromMinutes(RemoteApi.CodeLifetimeMinutes) - TimeSpan.FromSeconds(1));
        var start = _pairing.Start(client.Start(offer.Code));

        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(PairingReasons.Expired, ReasonOf(() => _pairing.Finish(client.Finish(offer.Code, start))));
        Assert.Empty(_devices.List());
        Assert.Equal(PairingReasons.NoOffer, ReasonOf(() => _pairing.Start(client.Start(offer.Code))));
    }

    [Fact]
    public void NoOpenCodeMeansNoPairing()
    {
        using var client = new PairingClient();
        Assert.Equal(PairingReasons.NoOffer, ReasonOf(() => _pairing.Start(client.Start("123456"))));

        var offer = _pairing.Begin();
        _pairing.Cancel();
        Assert.Equal(PairingReasons.NoOffer, ReasonOf(() => _pairing.Start(client.Start(offer.Code))));
    }

    [Fact]
    public void ANewCodeReplacesTheOldOne()
    {
        var first = _pairing.Begin();
        string second;
        do
        {
            second = _pairing.Begin().Code;
        }
        while (second == first.Code);

        using var client = new PairingClient();
        Assert.Equal(PairingReasons.WrongCode, ReasonOf(() => _pairing.Start(client.Start(first.Code))));
    }

    [Fact]
    public void FinishNeedsTheStartAndTheRightProof()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        var bogus = new PairFinishRequest { HostNonce = RemoteCodec.Encode(new byte[32]), Mac = RemoteCodec.Encode(new byte[32]) };
        Assert.Equal(PairingReasons.NoSession, ReasonOf(() => _pairing.Finish(bogus)));

        var start = _pairing.Start(client.Start(offer.Code));
        var tampered = client.Finish(WrongCodeFor(offer.Code), start);
        Assert.Equal(PairingReasons.WrongCode, ReasonOf(() => _pairing.Finish(tampered)));
        Assert.Empty(_devices.List());
    }

    [Fact]
    public void ClientRejectsAHostThatDoesNotKnowTheCode()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        var start = _pairing.Start(client.Start(offer.Code));

        Assert.True(client.VerifyHost(start, offer.Code));
        Assert.False(client.VerifyHost(start, WrongCodeFor(offer.Code)));
        Assert.False(client.VerifyHost(start, offer.Code, pinnedFingerprint: new byte[32]));
    }

    [Fact]
    public void MalformedStartIsRefusedWithoutCountingAsAWrongCode()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        var good = client.Start(offer.Code);
        foreach (var bad in new[]
        {
            new PairStartRequest { ClientNonce = "", DeviceName = good.DeviceName, DevicePublicKey = good.DevicePublicKey, Mac = good.Mac },
            new PairStartRequest { ClientNonce = good.ClientNonce, DeviceName = "", DevicePublicKey = good.DevicePublicKey, Mac = good.Mac },
            new PairStartRequest { ClientNonce = good.ClientNonce, DeviceName = new string('a', 65), DevicePublicKey = good.DevicePublicKey, Mac = good.Mac },
            new PairStartRequest { ClientNonce = good.ClientNonce, DeviceName = "a‮b", DevicePublicKey = good.DevicePublicKey, Mac = good.Mac },
            new PairStartRequest { ClientNonce = good.ClientNonce, DeviceName = good.DeviceName, DevicePublicKey = "AAAA", Mac = good.Mac },
            new PairStartRequest { ClientNonce = good.ClientNonce, DeviceName = good.DeviceName, DevicePublicKey = good.DevicePublicKey, Mac = "AAAA" },
        })
        {
            for (var i = 0; i < 2 * RemoteApi.MaxWrongAttempts; i++)
            {
                Assert.Equal(PairingReasons.Invalid, ReasonOf(() => _pairing.Start(bad)));
            }
        }

        Assert.NotNull(_pairing.Current);
        _pairing.Start(good);
    }

    [Fact]
    public void ADeviceKeyOnAnotherCurveIsRefused()
    {
        var offer = _pairing.Begin();
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        using var client = new PairingClient();
        var good = client.Start(offer.Code);
        var other = new PairStartRequest
        {
            ClientNonce = good.ClientNonce,
            DeviceName = good.DeviceName,
            DevicePublicKey = RemoteCodec.Encode(p384.ExportSubjectPublicKeyInfo()),
            Mac = good.Mac,
        };
        Assert.Equal(PairingReasons.Invalid, ReasonOf(() => _pairing.Start(other)));
    }

    [Fact]
    public void NoMoreThanTheDeviceLimitCanPair()
    {
        for (var i = 0; i < DeviceStore.MaxDevices; i++)
        {
            using var c = new PairingClient("Device " + i);
            c.Pair(_pairing, _pairing.Begin().Code);
        }

        var offer = _pairing.Begin();
        using var extra = new PairingClient("Extra");
        Assert.Equal(PairingReasons.TooManyDevices, ReasonOf(() => _pairing.Start(extra.Start(offer.Code))));
    }

    [Fact]
    public void CorruptDeviceFileIsKeptAsABackupAndTheListStartsEmpty()
    {
        File.WriteAllText(_dir.File(DeviceStore.FileName), "{ not json");
        var store = new DeviceStore(_dir.Path, _time);

        Assert.Empty(store.List());
        Assert.True(File.Exists(_dir.File("devices.corrupt.bak")));
        Assert.Equal("{ not json", File.ReadAllText(_dir.File("devices.corrupt.bak")));
    }

    [Fact]
    public void RemovedDeviceIsGoneFromTheFile()
    {
        var offer = _pairing.Begin();
        using var client = new PairingClient();
        var id = client.Pair(_pairing, offer.Code);

        Assert.True(_devices.Remove(id));
        Assert.False(_devices.Remove(id));
        Assert.Empty(new DeviceStore(_dir.Path, _time).List());
    }
}
