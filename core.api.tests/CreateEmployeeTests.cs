using AppPlatform.Api;
using AppPlatform.Core.Data;
using AppPlatform.Core.Services;
using Moq;
using static AppPlatform.Core.Features.Employees.CreateEmployeeFeature;

namespace AppPlatform.Core.Tests;

public class CreateEmployeeTests
{
    private readonly Mock<IEmployeeService> _employees = new();
    private readonly RecordingOutbox _outbox = new();

    public CreateEmployeeTests() => _employees.Setup(s => s.DefaultCompanyIdAsync(default)).ReturnsAsync(1);

    private CreateEmployeeCommandHandler Handler() => new(_employees.Object, _outbox);

    [Fact]
    public async Task A_valid_employee_is_created_with_a_public_id()
    {
        Employee? added = null;
        _employees.Setup(s => s.Add(It.IsAny<Employee>())).Callback<Employee>(e => added = e);

        var result = await Handler().Handle(Fake.Caller(), new("Ada", "Lovelace", "Ada@Example.com"));

        Assert.True(result.Succeeded);
        Assert.NotNull(added);
        Assert.StartsWith("emp_", added.PublicId, StringComparison.Ordinal);
        // Lowercased on the way in, so a later lookup by address cannot miss on casing alone.
        Assert.Equal("ada@example.com", added.Email);
        _employees.Verify(s => s.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task The_creation_event_is_staged_in_the_same_save()
    {
        await Handler().Handle(Fake.Caller(), new("Ada", "Lovelace", null));

        var e = Assert.Single(_outbox.Events);
        Assert.Equal("employee.created", e.EventType);
        // The payload carries the PUBLIC id. An internal key here would be stored by a
        // subscriber and could never be re-keyed.
        Assert.StartsWith("emp_", e.AggregatePublicId, StringComparison.Ordinal);
        Assert.Equal(1, e.AggregateVersion);
    }

    [Theory]
    [InlineData(null, "Lovelace")]
    [InlineData("Ada", null)]
    [InlineData("  ", "Lovelace")]
    public async Task A_missing_name_is_rejected_before_anything_is_staged(string? first, string? last)
    {
        var result = await Handler().Handle(Fake.Caller(), new(first, last, null));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Empty(_outbox.Events);
        _employees.Verify(s => s.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task An_over_long_name_is_rejected_by_the_handler_not_the_database()
    {
        // One over-long value reaching the INSERT fails the whole statement with a message
        // naming a column, rather than the single field the person can actually fix.
        var result = await Handler().Handle(Fake.Caller(), new(new string('a', 101), "Lovelace", null));

        Assert.Equal(CommandOutcome.Invalid, result.Outcome);
        Assert.Contains("100 characters", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_email_is_a_conflict()
    {
        _employees.Setup(s => s.EmailInUseAsync("ada@example.com", default)).ReturnsAsync(true);

        var result = await Handler().Handle(Fake.Caller(), new("Ada", "Lovelace", "ada@example.com"));

        Assert.Equal(CommandOutcome.Conflict, result.Outcome);
        Assert.Equal(CoreProblems.EmailInUse, result.ProblemCode);
    }

    [Fact]
    public async Task An_employee_with_no_email_is_allowed()
    {
        // Not everyone on a roster has an address. They simply cannot be invited until they do.
        var result = await Handler().Handle(Fake.Caller(), new("Ada", "Lovelace", null));

        Assert.True(result.Succeeded);
    }
}
