using System.Text.Json;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using PortalAIHost.App.Services;
using PortalAIHost.App.ViewModels;

namespace PortalAIHost.App.Tests;

public class TryItStreamTests
{
    [Fact]
    public async Task Send_streams_into_the_conversation_and_shows_the_timing()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "hello" };
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Messages.Count);
        Assert.True(vm.Messages[0].IsUser);
        Assert.Equal("hello", vm.Messages[0].Text);
        Assert.True(vm.Messages[1].IsAssistant);
        Assert.Contains("You asked: hello", vm.Messages[1].Text);
        Assert.Equal("", vm.Prompt);
        Assert.True(vm.HasStats);
        Assert.StartsWith("Vm_TryStats:", vm.Stats);
        Assert.NotNull(vm.LastStats);
        Assert.False(vm.LastStats!.Cancelled);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Second_question_sends_the_earlier_turns_with_it()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "one" };
        await vm.SendCommand.ExecuteAsync(null);
        vm.Prompt = "two";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Messages.Count);
        Assert.Equal(["one", vm.Messages[1].Text, "two"], rig.Host.LastTryItTurns.Select(t => t.Text));
    }

    [Fact]
    public async Task Stop_ends_the_run_and_keeps_the_words_so_far()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "hello" };
        var run = vm.SendCommand.ExecuteAsync(null);
        var until = Environment.TickCount64 + 5000;
        while (vm.Reply.Length == 0 && Environment.TickCount64 < until) await Task.Delay(10);
        Assert.True(vm.StopCommand.CanExecute(null));
        await vm.StopCommand.ExecuteAsync(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsBusy);
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.Equal(2, vm.Messages.Count);
        Assert.NotEqual("", vm.Messages[1].Text);
        Assert.DoesNotContain("You asked: hello", vm.Messages[1].Text);
        // A stopped run can be followed by a new question.
        Assert.False(vm.SendCommand.CanExecute(null));
        vm.Prompt = "again";
        Assert.True(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Clear_forgets_the_conversation_and_the_numbers()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings) { Prompt = "hello" };
        await vm.SendCommand.ExecuteAsync(null);
        Assert.True(vm.ClearCommand.CanExecute(null));
        await vm.ClearCommand.ExecuteAsync(null);
        Assert.Empty(vm.Messages);
        Assert.False(vm.HasStats);
        Assert.Null(vm.LastStats);
        Assert.Equal("", vm.Reply);
        Assert.True(vm.ShowSuggestions);
        Assert.False(vm.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_suggestion_is_sent_as_the_question()
    {
        using var rig = new Rig(FakeScenario.Loaded);
        var vm = new TryItViewModel(rig.Shell, rig.Ui, rig.Strings);
        Assert.Equal(3, vm.Suggestions.Count);
        await vm.UseSuggestionCommand.ExecuteAsync("Vm_TrySuggest2");
        Assert.Equal("Vm_TrySuggest2", vm.Messages[0].Text);
        Assert.False(vm.ShowSuggestions);
    }

    [Fact]
    public async Task Pipe_stream_delivers_pieces_in_order_then_the_timing()
    {
        var channel = new ScriptedChannel();
        channel.Answers[ControlTypes.TryItStream] = payload =>
        {
            var run = payload!.Value.Deserialize(AiHostJsonContext.Default.TryItRequest)!;
            Assert.Equal("hi", run.Request.Messages[^1].Content!.AsText());
            channel.Push(ControlTypes.EventTryItChunk, JsonSerializer.SerializeToElement(
                new TryItChunkEvent { RunId = run.RunId!, Seq = 1, Reasoning = "hm" }, AiHostJsonContext.Default.TryItChunkEvent));
            // Another run's pieces are ignored.
            channel.Push(ControlTypes.EventTryItChunk, JsonSerializer.SerializeToElement(
                new TryItChunkEvent { RunId = "other", Seq = 1, Content = "NO" }, AiHostJsonContext.Default.TryItChunkEvent));
            channel.Push(ControlTypes.EventTryItChunk, JsonSerializer.SerializeToElement(
                new TryItChunkEvent { RunId = run.RunId!, Seq = 2, Content = "Hello" }, AiHostJsonContext.Default.TryItChunkEvent));
            return ScriptedChannel.Ok(new TryItDone
            {
                RunId = run.RunId!, Chunks = 2, FirstTokenMs = 120, TotalMs = 900, Tokens = 20, TokensPerSecond = 25.5,
            }, AiHostJsonContext.Default.TryItDone);
        };
        var factory = new ScriptedFactory();
        factory.Next.Enqueue(channel);
        await using var host = new PipeHostControl(factory);

        var pieces = new List<TryItPiece>();
        await foreach (var p in host.TryItAsync([new TryItTurn(true, "hi")]))
            pieces.Add(p);

        Assert.Equal(3, pieces.Count);
        Assert.Equal(new TryItPiece("hm", true), pieces[0]);
        Assert.Equal(new TryItPiece("Hello"), pieces[1]);
        Assert.Equal(new TryItStats(120, 900, 20, 25.5, false), pieces[2].Stats);
    }
}
