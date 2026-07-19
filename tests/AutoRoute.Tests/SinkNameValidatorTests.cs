using AutoRoute.Engine;

namespace AutoRoute.Tests;

public sealed class SinkNameValidatorTests
{
    [Theory]
    [InlineData("GameSink")]
    [InlineData("Music_Sink")]
    [InlineData("cap-2")]
    [InlineData("a.b.c")]
    public void Valid_names_pass(string name) => Assert.True(SinkNameValidator.IsValidName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quote\"x")]
    [InlineData("semi;colon")]
    [InlineData("arg=inject")] // would smuggle an extra module arg into the pactl argv
    public void Invalid_names_fail(string? name) => Assert.False(SinkNameValidator.IsValidName(name));

    [Theory]
    [InlineData("Game Sink")]
    [InlineData("Capture 2 (stereo)")]
    [InlineData("音楽")]
    public void Valid_descriptions_pass(string description) =>
        Assert.True(SinkNameValidator.IsValidDescription(description));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has'quote")]       // closes the sink_properties single-quote
    [InlineData("has\"quote")]      // closes the drop-in args double-quote
    [InlineData("back\\slash")]     // collides with SPA-JSON \\ escaping
    [InlineData("line\nbreak")]     // breaks the single-line args = "…" value → every sink fails at boot
    [InlineData("carriage\rreturn")]
    [InlineData("tab\there")]
    [InlineData("nul\0byte")]
    public void Invalid_descriptions_fail(string? description) =>
        Assert.False(SinkNameValidator.IsValidDescription(description));
}
