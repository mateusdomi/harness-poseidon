using Harness.Modules.Projects.Application;

namespace Harness.UnitTests.Projects;

/// <summary>
/// CAT-07: o projetor puro de atividade ordena do mais recente para o mais antigo, humaniza cada
/// evento e pagina por cursor keyset de forma estável (sem sobreposição nem lacuna).
/// </summary>
public sealed class ProjectActivityProjectorTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private static ProjectActivityEvent Event(string id, int minute, string kind = "task_created",
        string entityKind = "task", string? title = null, string? state = null) =>
        new(id, kind, Origin.AddMinutes(minute), entityKind, id, Title: title, State: state);

    [Fact]
    public void OrdersMostRecentFirstWithStableTiebreak()
    {
        var events = new[]
        {
            Event("01ARZ3NDEKTSV4RRFFQ69G5FA1", 1),
            Event("01ARZ3NDEKTSV4RRFFQ69G5FA3", 3),
            Event("01ARZ3NDEKTSV4RRFFQ69G5FA2", 2),
            // Empate de instante: o id maior (Ordinal) vem primeiro, de forma determinística.
            Event("01ARZ3NDEKTSV4RRFFQ69G5FB0", 3),
        };

        var page = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", events, cursor: null, limit: 50);

        Assert.Null(page.NextCursor);
        Assert.Equal(4, page.Count);
        Assert.Equal(
            ["01ARZ3NDEKTSV4RRFFQ69G5FB0", "01ARZ3NDEKTSV4RRFFQ69G5FA3",
             "01ARZ3NDEKTSV4RRFFQ69G5FA2", "01ARZ3NDEKTSV4RRFFQ69G5FA1"],
            page.Items.Select(item => item.EntityId).ToArray());
    }

    [Fact]
    public void HumanizesEachEventKind()
    {
        var events = new[]
        {
            Event("01ARZ3NDEKTSV4RRFFQ69G5D01", 5, "demand_created", "demand", title: "Login"),
            Event("01ARZ3NDEKTSV4RRFFQ69G5T01", 4, "task_updated", "task", title: "API", state: "completed"),
            new ProjectActivityEvent("01ARZ3NDEKTSV4RRFFQ69G5A01", "attempt_finished",
                Origin.AddMinutes(3), "attempt", "01ARZ3NDEKTSV4RRFFQ69G5A01", Title: "API",
                State: "approved", AgentId: "agent-1", AttemptNumber: 2),
        };

        var page = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", events, cursor: null, limit: 50);

        Assert.Equal("Demand 'Login' was created.", page.Items[0].Summary);
        Assert.Equal("Task 'API' moved to completed.", page.Items[1].Summary);
        Assert.Equal("Attempt #2 on task 'API' ended as approved.", page.Items[2].Summary);
    }

    [Fact]
    public void PaginatesByCursorWithoutOverlapOrGap()
    {
        var events = Enumerable.Range(1, 5)
            .Select(index => Event($"01ARZ3NDEKTSV4RRFFQ69G5F{index:D2}", index))
            .ToArray();

        var first = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", events, cursor: null, limit: 2);
        Assert.Equal(2, first.Count);
        Assert.NotNull(first.NextCursor);
        // Mais recentes primeiro: minutos 5 e 4.
        Assert.Equal(
            ["01ARZ3NDEKTSV4RRFFQ69G5F05", "01ARZ3NDEKTSV4RRFFQ69G5F04"],
            first.Items.Select(item => item.EntityId).ToArray());

        var second = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", events, first.NextCursor, limit: 2);
        Assert.Equal(
            ["01ARZ3NDEKTSV4RRFFQ69G5F03", "01ARZ3NDEKTSV4RRFFQ69G5F02"],
            second.Items.Select(item => item.EntityId).ToArray());
        Assert.NotNull(second.NextCursor);

        var third = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", events, second.NextCursor, limit: 2);
        Assert.Equal(["01ARZ3NDEKTSV4RRFFQ69G5F01"], third.Items.Select(item => item.EntityId).ToArray());
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public void EmptyActivityYieldsAnEmptyPage()
    {
        var page = ProjectActivityProjector.Project("01ARZ3NDEKTSV4RRFFQ69G5PRJ", [], cursor: null, limit: 50);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void MalformedCursorIsReportedInvalidButNullIsValid()
    {
        Assert.True(ProjectActivityProjector.IsValidCursor(null));
        Assert.True(ProjectActivityProjector.IsValidCursor(string.Empty));
        Assert.False(ProjectActivityProjector.IsValidCursor("!!!not-base64!!!"));
    }
}
