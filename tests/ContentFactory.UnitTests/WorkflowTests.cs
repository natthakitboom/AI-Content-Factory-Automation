using ContentFactory.Contracts;
using ContentFactory.Core.Jobs;
using ContentFactory.Worker.Health;

namespace ContentFactory.UnitTests;

/// <summary>
/// T07 workflow and resource-lane checks. The stage graph is code-defined; an LLM never
/// gets to invent a stage or skip a paid-work guard. These tests use no database, provider
/// or FFmpeg.
/// </summary>
public sealed class WorkflowTests
{
    [Fact]
    public void TemplateModeSkipsCloudStages()
    {
        var template = WorkflowDefinition.StagesFor(VideoMode.TemplateOnly);
        var hybrid = WorkflowDefinition.StagesFor(VideoMode.HybridMotion);

        var cloudSubmitCountForTemplateMode = template.Count(
            stage => stage.Lane == JobLane.CloudSubmit);

        Assert.Equal(0, cloudSubmitCountForTemplateMode);
        Assert.DoesNotContain(template, stage => stage.RequiresCloud);
        Assert.Contains(hybrid, stage => stage.JobType == "submit_video");
        Assert.Contains(hybrid, stage => stage.JobType == "poll_video");
        Assert.Contains(hybrid, stage => stage.JobType == "download_video");

        Assert.Equal(
            "compile_timeline",
            WorkflowDefinition.NextJobType("synthesize_voice", VideoMode.TemplateOnly));
        Assert.Equal(
            "submit_video",
            WorkflowDefinition.NextJobType("synthesize_voice", VideoMode.HybridMotion));
        Assert.Equal(
            "compile_timeline",
            WorkflowDefinition.NextJobType("download_video", VideoMode.HybridMotion));
        Assert.Null(WorkflowDefinition.NextJobType("prepare_review", VideoMode.TemplateOnly));

        Assert.Equal(
            ["submit_video", "poll_video", "download_video"],
            WorkflowDefinition.NotRequiredStagesFor(VideoMode.TemplateOnly));
        Assert.Empty(WorkflowDefinition.NotRequiredStagesFor(VideoMode.HybridMotion));
    }

    [Fact]
    public void OrderedStagesMatchTheApprovedPipeline()
    {
        var jobTypes = WorkflowDefinition.OrderedStages
            .Select(stage => stage.JobType)
            .ToArray();

        Assert.Equal(
            [
                "validate_product",
                "plan_script",
                "prepare_visuals",
                "synthesize_voice",
                "submit_video",
                "poll_video",
                "download_video",
                "compile_timeline",
                "render_video",
                "technical_qa",
                "prepare_review",
                "approve",
                "export",
                "record_publication"
            ],
            jobTypes);
    }

    [Fact]
    public async Task HeavyLocalLaneIsSerialized()
    {
        var governor = new ResourceGovernor(new ResourceGovernorOptions
        {
            HeavyLocalConcurrency = 1,
            QuietMode = true
        });

        var current = 0;
        var maximumObservedHeavyLocalConcurrency = 0;

        async Task WorkAsync()
        {
            await using var lease = await governor.AcquireAsync(
                JobLane.HeavyLocal,
                CancellationToken.None);

            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref maximumObservedHeavyLocalConcurrency, now);
            await Task.Delay(40);
            Interlocked.Decrement(ref current);
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => WorkAsync()));

        Assert.Equal(1, maximumObservedHeavyLocalConcurrency);
    }

    [Fact]
    public async Task CloudWaitingDoesNotHoldHeavyLocalSlot()
    {
        var governor = new ResourceGovernor(new ResourceGovernorOptions
        {
            HeavyLocalConcurrency = 1,
            CloudSubmitConcurrency = 1,
            QuietMode = true
        });

        await using var cloud = await governor.AcquireAsync(
            JobLane.CloudSubmit,
            CancellationToken.None);

        var heavyTask = governor.AcquireAsync(JobLane.HeavyLocal, CancellationToken.None);
        var heavy = await heavyTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(heavy);
        await heavy.DisposeAsync();
    }

    [Fact]
    public void ResourceSensorsReportUnavailableInsteadOfGuessing()
    {
        var readings = ResourceGovernor.ReadReadings();

        Assert.Null(readings.MemoryAvailableMb);
        Assert.Null(readings.ThermalCelsius);
        Assert.Equal("sensor_not_supported", readings.UnavailableReason);
    }

    [Fact]
    public void QuietModeOptionsAreExplicit()
    {
        var governor = new ResourceGovernor(new ResourceGovernorOptions
        {
            HeavyLocalConcurrency = 1,
            QuietMode = true
        });

        Assert.True(governor.Options.QuietMode);
        Assert.Equal(1, governor.Options.HeavyLocalConcurrency);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
