using AppPlatform.Ids;

namespace AppPlatform.Ids.Tests;

public class PublicIdTests
{
    [Fact]
    public void A_new_id_is_prefixed_and_canonical()
    {
        var id = PublicId.New("emp");

        Assert.Equal("emp", id.Prefix);
        Assert.Equal(25, id.Body.Length);
        Assert.StartsWith("emp_", id.ToString(), StringComparison.Ordinal);
        Assert.Equal(id.ToString().ToLowerInvariant(), id.ToString());
    }

    [Fact]
    public void The_body_never_contains_an_ambiguous_character()
    {
        // The reason for Crockford over plain base32: an id read aloud to support, or
        // copied off a screen, must not contain characters people confuse.
        var bodies = string.Concat(Enumerable.Range(0, 500).Select(_ => PublicId.New("emp").Body));

        Assert.DoesNotContain('i', bodies);
        Assert.DoesNotContain('l', bodies);
        Assert.DoesNotContain('o', bodies);
        Assert.DoesNotContain('u', bodies);
    }

    [Fact]
    public void Ids_are_unique_across_many_generations()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => PublicId.New("emp").ToString()).ToHashSet();

        Assert.Equal(20_000, ids.Count);
    }

    [Fact]
    public void Ids_are_not_sequential()
    {
        // A sortable id publishes creation order and approximate creation time to anyone
        // holding two of them. Generating in order must not produce ordered output.
        var ids = Enumerable.Range(0, 200).Select(_ => PublicId.New("emp").ToString()).ToList();

        Assert.NotEqual(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void Every_position_uses_the_whole_alphabet()
    {
        // This caught a real flaw. At 26 characters the body holds 130 bits while the value
        // supplies 128, so the leading character could only ever be 0-7 — ids that still
        // looked random, were still unique, and quietly wasted part of the space at one
        // fixed position. Asserting per-position coverage is what makes that visible.
        var samples = Enumerable.Range(0, 3_000).Select(_ => PublicId.New("emp").Body).ToList();

        for (var position = 0; position < 25; position++)
        {
            var distinct = samples.Select(body => body[position]).ToHashSet();

            Assert.True(distinct.Count > 28,
                $"position {position} used only {distinct.Count} of 32 characters");
        }
    }

    [Fact]
    public void A_generated_id_round_trips()
    {
        var original = PublicId.New("tkt");

        Assert.True(PublicId.TryParse(original.ToString(), "tkt", out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void An_id_of_the_wrong_kind_is_refused()
    {
        // THE reason the prefix exists. Without this, a ticket id passed where an employee
        // id was meant simply finds nothing, the caller sees a 404, and the actual mistake
        // — two kinds of thing sharing one parameter — never surfaces.
        var ticket = PublicId.New("tkt");

        Assert.False(PublicId.TryParse(ticket.ToString(), "emp", out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("EMP_7H2K3M4N5P6Q7R8S9T0V1W2X3")]
    [InlineData("emp_7H2K3M4N5P6Q7R8S9T0V1W2X3")]
    public void Parsing_is_case_insensitive_but_canonicalises_to_lowercase(string input)
    {
        Assert.True(PublicId.TryParse(input, "emp", out var parsed));
        Assert.Equal(parsed!.Value.ToString(), parsed.Value.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Crockford_substitutions_are_folded_so_a_transcribed_id_still_works()
    {
        var original = PublicId.New("emp");

        // Someone reading an id back writes O for 0 and l for 1. Crockford defines these
        // as the same characters; honouring that turns an unexplainable 404 into a hit.
        var transcribed = original.ToString()
            .Replace('0', 'O')
            .Replace('1', 'l');

        Assert.True(PublicId.TryParse(transcribed, "emp", out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("emp")]
    [InlineData("_7h2k3m4n5p6q7r8s9t0v1w2x3")]
    [InlineData("emp_")]
    [InlineData("emp_tooshort")]
    [InlineData("emp_7h2k3m4n5p6q7r8s9t0v1w2x3y4z5")]
    [InlineData("emp_7h2k3m4n5p6q7r8s9t0v1w2x!")]
    public void Malformed_input_is_refused(string? input)
        => Assert.False(PublicId.TryParse(input, "emp", out _));

    [Fact]
    public void A_body_containing_u_is_refused_rather_than_folded()
    {
        // U is excluded from the alphabet and Crockford assigns it no substitution — it is
        // a malformed id, not a mistyped one, and guessing at an intent would be worse.
        var body = new string('u', 25);

        Assert.False(PublicId.TryParse($"emp_{body}", "emp", out _));
    }

    [Fact]
    public void TryParseAny_reports_the_kind_without_asserting_it()
    {
        var ticket = PublicId.New("tkt");

        Assert.True(PublicId.TryParseAny(ticket.ToString(), out var parsed));
        Assert.Equal("tkt", parsed!.Value.Prefix);
    }

    [Theory]
    [InlineData("e")]
    [InlineData("employeee")]
    [InlineData("Emp")]
    [InlineData("emp1")]
    [InlineData("emp_x")]
    [InlineData("")]
    public void An_invalid_prefix_is_rejected_at_generation(string prefix)
        => Assert.Throws<ArgumentException>(() => PublicId.New(prefix));
}
