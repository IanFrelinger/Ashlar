using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.IO;
using System.Text.Json;
using Ashlar.CLI;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Trait("Category", "CLI")]
public sealed class JsonOutputContractTests
{
    [Theory]
    [InlineData("probe --format-json", true)]
    [InlineData("--format-json probe", true)]
    [InlineData("probe --format-json=true", true)]
    [InlineData("probe --format-json:true", true)]
    [InlineData("probe --format-json false", false)]
    [InlineData("probe --format-json=false", false)]
    [InlineData("probe --format-json:false", false)]
    [InlineData("probe", false)]
    public async Task Parsed_boolean_controls_logging_before_handler(string args, bool expected)
    {
        var root = new RootCommand();
        root.AddGlobalOption(new Option<bool>("--format-json"));
        var probe = new Command("probe");
        bool? observed = null;
        probe.WithJsonOutput().SetHandler(() => { observed = Program.JsonOutputMode; });
        root.AddCommand(probe);
        CommandExecutionSupport.ApplyJsonOutputContract(root);

        (await root.InvokeAsync(args)).Should().Be(0);
        observed.Should().Be(expected);
    }

    [Fact]
    public async Task Response_file_controls_logging_before_handler()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "probe\n--format-json=true\n");
            await Parsed_boolean_controls_logging_before_handler($"@{path}", true);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("probe --format-json")]
    [InlineData("probe --format-json=true")]
    [InlineData("probe --format-json:true")]
    public async Task Unmarked_handler_refuses_without_running(string args)
    {
        var root = new RootCommand();
        root.AddGlobalOption(new Option<bool>("--format-json"));
        var probe = new Command("probe");
        var ran = false;
        probe.SetHandler(() => { ran = true; });
        root.AddCommand(probe);
        CommandExecutionSupport.ApplyJsonOutputContract(root);
        var console = new TestConsole();

        (await root.InvokeAsync(args, console)).Should().Be(1);
        ran.Should().BeFalse();
        console.Out.ToString().Should().BeEmpty();
        console.Error.ToString().Should().Contain("has no JSON rendering");
    }

    [Theory]
    [InlineData("mesh")]
    [InlineData("config set-mode step deterministic")]
    [InlineData("policy show")]
    [InlineData("ci runtime-gate")]
    [InlineData("dogfood all")]
    [InlineData("background-agent daemon")]
    public async Task Registered_prose_or_stream_commands_refuse_json(string command)
    {
        var console = new TestConsole();
        var root = Program.BuildRootCommand();
        (await root.InvokeAsync(command + " --format-json", console)).Should().Be(1);
        console.Out.ToString().Should().BeEmpty();
        console.Error.ToString().Should().Contain("has no JSON rendering");
    }

    [Fact]
    public async Task Json_on_parent_does_not_opt_in_child_and_help_still_works()
    {
        var root = new RootCommand();
        root.AddGlobalOption(new Option<bool>("--format-json"));
        var parent = new Command("parent");
        parent.WithJsonOutput().SetHandler(() => { });
        var child = new Command("child");
        child.SetHandler(() => throw new InvalidOperationException("must not run"));
        parent.AddCommand(child);
        root.AddCommand(parent);
        CommandExecutionSupport.ApplyJsonOutputContract(root);
        (await root.InvokeAsync("parent child --format-json", new TestConsole())).Should().Be(1);
        var help = new TestConsole();
        (await root.InvokeAsync("parent child --format-json --help", help)).Should().Be(0);
        help.Out.ToString().Should().Contain("unsupported");
    }

    [Theory]
    [InlineData("analyze")]
    [InlineData("config show")]
    [InlineData("pipeline run")]
    [InlineData("docker build")]
    [InlineData("docker run")]
    [InlineData("docker clean")]
    [InlineData("test portable")]
    [InlineData("test multi-env")]
    [InlineData("doctor")]
    [InlineData("certify brick")]
    [InlineData("trust allow")]
    [InlineData("trust deny")]
    [InlineData("trust dashboard")]
    [InlineData("runtime-studio status")]
    public void Existing_json_renderers_remain_opted_in(string path)
    {
        Command command = Program.BuildRootCommand();
        foreach (var name in path.Split(' ')) command = command.Subcommands.Single(c => c.Name == name);
        CommandExecutionSupport.SupportsJson(command).Should().BeTrue();
    }

    [Fact]
    public async Task Portable_list_emits_one_document_and_all_refuses_before_work()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var root = Program.BuildRootCommand();
            (await root.InvokeAsync("test portable --list --format-json")).Should().Be(0);
            using (var doc = JsonDocument.Parse(stdout.ToString()))
                doc.RootElement.GetProperty("host").GetString().Should().NotBeNullOrEmpty();
            stdout.GetStringBuilder().Clear();
            (await root.InvokeAsync("test multi-env --env unknown-json-contract-environment --format-json:true")).Should().Be(1);
            using (var doc = JsonDocument.Parse(stdout.ToString()))
                doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
            stdout.GetStringBuilder().Clear();
            (await root.InvokeAsync("test portable --scope all --format-json")).Should().Be(1);
            stdout.ToString().Should().BeEmpty();
            stderr.ToString().Should().Contain("no single JSON rendering");
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    [Fact]
    public async Task Mesh_without_operation_is_usage_error()
    {
        (await Program.BuildRootCommand().InvokeAsync("mesh")).Should().Be(1);
    }
}
