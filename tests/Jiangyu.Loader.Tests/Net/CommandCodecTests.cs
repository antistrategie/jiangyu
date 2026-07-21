using Jiangyu.Shared.Net;
using Jiangyu.Shared.Net.Commands;
using Xunit;

namespace Jiangyu.Loader.Tests.Net;

public sealed class CommandCodecTests
{
    [Fact]
    public void SkillCommand_RoundTripsThroughAnEnvelopePayload()
    {
        var body = new SkillCommand { Actor = 42, Skill = "active.fire_voymastina_ak15", Usage = 2, Tile = new TileRef { X = 23, Z = 39 } };
        var command = new NetCommand { Seq = 0, Source = 1, Kind = CommandKinds.Skill, Payload = CommandCodec.Encode(body) };

        var decoded = CommandCodec.Decode<SkillCommand>(command.Payload);
        Assert.NotNull(decoded);
        Assert.Equal(42ul, decoded!.Actor);
        Assert.Equal("active.fire_voymastina_ak15", decoded.Skill);
        Assert.Equal(2, decoded.Usage);
        Assert.Equal(23, decoded.Tile!.X);
        Assert.Equal(39, decoded.Tile.Z);
    }

    [Fact]
    public void MoveCommand_RoundTrips()
    {
        var body = new MoveCommand { Actor = 7, Tile = new TileRef { X = 24, Z = 30 } };
        var decoded = CommandCodec.Decode<MoveCommand>(CommandCodec.Encode(body));

        Assert.NotNull(decoded);
        Assert.Equal(7ul, decoded!.Actor);
        Assert.Equal(24, decoded.Tile!.X);
        Assert.Equal(30, decoded.Tile.Z);
    }

    [Fact]
    public void EndTurnCommand_RoundTripsAsEmptyBody()
    {
        var decoded = CommandCodec.Decode<EndTurnCommand>(CommandCodec.Encode(new EndTurnCommand()));
        Assert.NotNull(decoded);
    }

    [Fact]
    public void Decode_ReturnsNullOnMissingOrMalformedPayload()
    {
        Assert.Null(CommandCodec.Decode<SkillCommand>(null));
        Assert.Null(CommandCodec.Decode<SkillCommand>(""));
        Assert.Null(CommandCodec.Decode<SkillCommand>("{ not json"));
    }

    [Fact]
    public void CommandOutcome_RoundTripsShippedDeltas()
    {
        var outcome = new CommandOutcome
        {
            Deltas =
            [
                new ActorDelta { Actor = 99, Field = "suppression", Value = 0.23681146 },
                new ActorDelta { Actor = 99, Field = "morale", Value = 84.99999 },
                new ActorDelta { Actor = 12, Field = "armour", Value = 187 },
            ],
        };
        var command = new NetCommand { Seq = 3, Source = 1, Kind = CommandKinds.Skill, Outcome = CommandCodec.Encode(outcome) };

        var decoded = CommandCodec.Decode<CommandOutcome>(command.Outcome);
        Assert.NotNull(decoded);
        Assert.Equal(3, decoded!.Deltas.Count);
        Assert.Equal("suppression", decoded.Deltas[0].Field);
        Assert.Equal(0.23681146, decoded.Deltas[0].Value, 8);
        Assert.Equal(187, decoded.Deltas[2].Value);
    }
}
