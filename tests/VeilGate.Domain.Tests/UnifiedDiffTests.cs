using FluentAssertions;
using VeilGate.Application.Policies;
using Xunit;

namespace VeilGate.Domain.Tests;

public class UnifiedDiffTests
{
    [Fact]
    public void Identical_texts_show_every_line_as_unchanged()
    {
        var diff = UnifiedDiff.Create("v1", "v2", "a\nb\n", "a\nb\n")!;

        diff.Should().Be("--- v1\n+++ v2\n@@ -1,2 +1,2 @@\n a\n b\n");
    }

    [Fact]
    public void A_changed_line_shows_as_removed_then_added()
    {
        var diff = UnifiedDiff.Create("v1", "v2", "a\nb\nc\n", "a\nX\nc\n")!;

        diff.Should().Contain("\n a\n").And.Contain("\n-b\n").And.Contain("\n+X\n").And.Contain("\n c\n");
    }

    [Fact]
    public void An_added_line_shows_only_as_added()
    {
        var diff = UnifiedDiff.Create("v1", "v2", "a\nc\n", "a\nb\nc\n")!;

        var body = diff.Split('\n').Skip(3);   // after the three header lines
        body.Should().Contain("+b");
        body.Should().NotContain(line => line.StartsWith('-'));
    }

    [Fact]
    public void Windows_line_endings_do_not_show_up_as_changes()
    {
        var diff = UnifiedDiff.Create("v1", "v2", "a\r\nb\r\n", "a\nb\n")!;

        diff.Should().NotContain("-a").And.NotContain("+a");
    }

    [Fact]
    public void Texts_with_too_many_lines_are_refused()
    {
        var big = string.Join('\n', Enumerable.Range(0, UnifiedDiff.MaxLines + 1));

        UnifiedDiff.Create("v1", "v2", big, "x").Should().BeNull();
    }
}
