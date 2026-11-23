using System.Text.RegularExpressions;
using KanbanHub.Messaging;

namespace KanbanHub.Api.Tests.Deployment;

/// <summary>
/// The places where the infrastructure templates name something the code names too. A name that drifts fails
/// quietly in Azure (a trigger on a queue that is not there, a timer that was meant to be off and is not),
/// so the build checks them here.
/// </summary>
public sealed partial class DeploymentTemplateTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Every_timer_the_template_switches_off_is_a_timer_of_the_workers()
    {
        string[] switchedOff = [.. DisabledFunction().Matches(Read("infra/main.bicep")).Select(match => match.Groups[1].Value)];

        Assert.NotEmpty(switchedOff);
        Assert.Empty(switchedOff.Except(TimersOfTheWorkers()));
    }

    [Theory]
    [InlineData(ServiceBusGitHubInbox.Queue)]
    [InlineData(ScheduledRetry.OutboundQueue)]
    [InlineData(ServiceBusAiJobInbox.Queue)]
    [InlineData(ServiceBusEventPublisher.Topic)]
    public void The_template_creates_every_queue_and_topic_the_code_sends_to(string name) =>
        Assert.Contains($"name: '{name}'", Read("infra/modules/messaging.bicep"), StringComparison.Ordinal);

    [Fact]
    public void The_template_creates_every_subscription_the_workers_listen_on()
    {
        string template = Read("infra/modules/messaging.bicep");
        string[] subscriptions = [.. SubscriptionName().Matches(Read("src/KanbanHub.Workers/CardEventFunctions.cs")).Select(match => match.Groups[1].Value)];

        Assert.NotEmpty(subscriptions);
        Assert.All(subscriptions, name => Assert.Contains($"name: '{name}'", template, StringComparison.Ordinal));
    }

    private static IEnumerable<string> TimersOfTheWorkers() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", "KanbanHub.Workers"), "*.cs")
            .SelectMany(file => TimerFunction().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups[1].Value);

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "KanbanHub.slnx"))) folder = folder.Parent;

        return folder?.FullName ?? throw new InvalidOperationException("KanbanHub.slnx was not found above the test binaries.");
    }

    [GeneratedRegex(@"'AzureWebJobs\.(\w+)\.Disabled'")]
    private static partial Regex DisabledFunction();

    // A function whose first parameter is a timer trigger: [Function("Name")] ... ([TimerTrigger(
    [GeneratedRegex(@"\[Function\(""(\w+)""\)\][^(]*\(\s*\[TimerTrigger\(")]
    private static partial Regex TimerFunction();

    [GeneratedRegex(@"const string \w+Subscription = ""([\w-]+)""")]
    private static partial Regex SubscriptionName();
}
