using System.Text.Json;
using ReimaginedLauncher.Utilities;
using Xunit;

namespace ReimaginedLauncher.Tests;

public sealed class ServerSaveStatusTests
{
    private static readonly Guid LadderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");
    private static readonly ServerSaveMonitor Monitor = new("unused", "session", LadderId);

    private static ServerSaveStatus Status(string state = "ready") => new(
        "session", LadderId, state, "Server saves status.", "", "", Now, true,
        [new ServerSaveConfirmation("Hero.d2s", "Hero", 42, new string('a', 64), Now.AddMinutes(-2))]);

    private static string Json(ServerSaveStatus status) => JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void Status_is_scoped_to_the_launch_and_ladder()
    {
        Assert.NotNull(ServerSaveStatus.Parse(Json(Status()), Monitor));
        Assert.Null(ServerSaveStatus.Parse(Json(Status() with { SessionId = "previous" }), Monitor));
        Assert.Null(ServerSaveStatus.Parse(Json(Status() with { LadderId = Guid.NewGuid() }), Monitor));
    }

    [Fact]
    public void Malformed_or_incomplete_status_is_not_a_save_confirmation()
    {
        Assert.Null(ServerSaveStatus.Parse("{", Monitor));
        Assert.Null(ServerSaveStatus.Parse("{}", Monitor));
        Assert.Null(ServerSaveStatus.Parse(Json(Status() with { ConfirmedSaves = null! }), Monitor));
        Assert.Null(ServerSaveStatus.Parse(Json(Status() with { State = "unknown" }), Monitor));
        Assert.Null(ServerSaveStatus.Parse(Json(Status() with
        {
            ConfirmedSaves = [new ServerSaveConfirmation("Hero.d2s", "Hero", 0, "bad", default)]
        }), Monitor));
    }

    [Fact]
    public void Rejection_keeps_the_last_confirmed_version_and_time_visible()
    {
        var display = (Status("stopped") with
        {
            Code = "ladder_bundle_mismatch",
            Message = "Saving has stopped: your ladder revision is outdated. Relaunch through Reimagined."
        }).Describe(Now, false);
        Assert.True(display.Warning);
        Assert.Contains("outdated", display.Message);
        Assert.Contains("Hero", display.Confirmed);
        Assert.Contains("v42", display.Confirmed);
        Assert.Contains("ladder_bundle_mismatch", display.NotificationKey);
    }

    [Fact]
    public void Connectivity_does_not_claim_a_character_was_saved()
    {
        var display = (Status() with { ConfirmedSaves = [] }).Describe(Now, false);
        Assert.Contains("No character save was confirmed", display.Confirmed);
    }

    [Fact]
    public void Stale_status_is_visible_even_if_the_last_report_was_healthy()
    {
        var display = Status().Describe(Now.AddMinutes(1), false);
        Assert.True(display.Warning);
        Assert.Contains("not updated", display.Message);
    }

    [Theory]
    [InlineData("ready", true)]
    [InlineData("pending", false)]
    [InlineData("checking", false)]
    public void Missing_final_acknowledgement_warns_after_game_exit(string state, bool running)
    {
        var display = (Status(state) with { Running = running }).Describe(Now, true);
        Assert.True(display.Warning);
        Assert.Contains("without confirming its final save", display.Message);
    }

    [Fact]
    public void Recovery_notice_survives_a_healthy_new_session()
    {
        var display = (Status() with { RecoveryNotice = "Previous progress was retained for recovery." }).Describe(Now, false);
        Assert.True(display.Warning);
        Assert.Contains("Previous progress", display.Message);
    }

    [Fact]
    public async Task Atomic_file_replacement_is_read_without_locking_out_the_plugin()
    {
        var path = Path.Combine(Path.GetTempPath(), "server-save-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(path, Json(Status()));
            var result = await ServerSaveStatus.ReadAsync(Monitor with { StatusPath = path }, default);
            Assert.NotNull(result);
            await File.WriteAllTextAsync(path, Json(Status("stopped")));
            result = await ServerSaveStatus.ReadAsync(Monitor with { StatusPath = path }, default);
            Assert.Equal("stopped", result?.State);
        }
        finally { File.Delete(path); }
    }
}
