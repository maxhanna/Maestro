using Weaver.Controllers;
using Xunit;

namespace Weaver.UnitTests;

/// <summary>
/// Locks the attach-files popup's fuzzy search (FileEditController.FileNameMatches,
/// wired into the /api/editor/list?search= handler). The OLD matcher was a strict
/// case-insensitive substring, so typing "movieservice" found nothing when the file
/// was named "movie.service.js" or "movie-service". The matcher now ALSO compares a
/// separator-stripped ("movieservice") form of both sides, so the user's plain-text
/// guess matches files/dirs whose real names carry '.', '-', '_' or spaces.
/// </summary>
public class FilePickerFuzzySearchTests
{
    [Theory]
    [InlineData("movie.service.js", "movieservice")]   // dots — the user's ask
    [InlineData("movie-service.js", "movieservice")]   // hyphens
    [InlineData("movie_service.js", "movieservice")]   // underscores
    [InlineData("movie service.js", "movieservice")]   // spaces
    [InlineData("MovieService.js", "movieservice")]    // case-insensitive + extension
    [InlineData("movieservice.js", "movieservice")]    // exact (no separators)
    [InlineData("Movie.Service", "movieservice")]      // dir-style, no extension
    [InlineData("my-movie-service.util.js", "movieservice")] // buried in a longer name
    public void Fuzzy_PunctuationIgnored_Match(string name, string term)
    {
        Assert.True(FileEditController.FileNameMatches(name, term),
            $"'{term}' should fuzzy-match '{name}'");
    }

    [Theory]
    [InlineData("service.js", "service.js")]           // strict substring still works
    [InlineData("MovieService.ts", "service")]         // plain substring, mixed case
    [InlineData("movieservice.js", "MOVIESERVICE")]    // case-insensitive fuzzy
    public void StrictSubstring_StillMatches(string name, string term)
    {
        Assert.True(FileEditController.FileNameMatches(name, term),
            $"'{term}' should match '{name}' via the strict substring rule");
    }

    [Theory]
    [InlineData("movie.service.js", "musicservice")]   // different word
    [InlineData("movie.service.js", "server")]         // letters must actually align
    [InlineData("README.md", "movieservice")]          // unrelated file
    [InlineData("movie.service.js", "-")]              // separator-only term = blank search
    public void NonMatches_StillRejected(string name, string term)
    {
        Assert.False(FileEditController.FileNameMatches(name, term),
            $"'{term}' must NOT match '{name}'");
    }

    [Fact]
    public void EmptyOrNull_EdgeCases()
    {
        // Empty term = "show everything" (the picker's no-search listing).
        Assert.True(FileEditController.FileNameMatches("anything.js", ""));
        // Empty name never matches a non-empty term.
        Assert.False(FileEditController.FileNameMatches("", "movieservice"));
        Assert.False(FileEditController.FileNameMatches(null!, "movieservice"));
    }
}
