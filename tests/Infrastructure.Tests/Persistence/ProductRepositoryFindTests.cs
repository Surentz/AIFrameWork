using AiFramework.Domain.Products;
using AiFramework.Infrastructure.Persistence;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Persistence;

[Collection(nameof(PostgresCollection))]
public sealed class ProductRepositoryFindTests(PostgresFixture fixture)
{
    // The catalogue is shared by every test in the collection, so each test invents names no other
    // test uses: a made-up word plus a per-test suffix of letters (digits would split the word).
    private static string Unique() => new([.. Guid.NewGuid().ToString("N").Where(char.IsLetter).Take(6)]);

    private async Task<Product> SeedAsync(string name, string sku, decimal price = 19.95m)
    {
        var product = Product.Create(Guid.NewGuid(), sku, name, null, price, DateTimeOffset.UtcNow);
        await using var context = fixture.CreateContext();
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product;
    }

    private async Task<IReadOnlyList<AiFramework.Application.Products.ProductMatch>> FindAsync(string text, params string[] skus)
    {
        await using var context = fixture.CreateContext();
        return await new ProductRepository(context).FindAsync(text, skus, 0.5, 5, CancellationToken.None);
    }

    [Fact]
    public async Task FindAsync_FindsANameInsideAQuestion()
    {
        var word = $"Quorblax{Unique()}";
        var product = await SeedAsync(word, $"Q-{Unique()}".ToUpperInvariant());

        var found = await FindAsync($"how much is the {word.ToLowerInvariant()}?");

        found.Should().ContainSingle(m => m.Id == product.Id).Which.Score.Should().BeApproximately(1, 0.001);
    }

    [Fact]
    public async Task FindAsync_FindsAMisspeltName()
    {
        var suffix = Unique();
        var product = await SeedAsync($"Zentrofax {suffix}", $"Z-{Unique()}".ToUpperInvariant());

        var found = await FindAsync($"price of the zentrofx {suffix}");

        found.Should().Contain(m => m.Id == product.Id);
    }

    [Fact]
    public async Task FindAsync_ScoresEveryNameContainedInTheQuestionAsExact()
    {
        // The composer relies on this: "Widget" and "Widget Pro" both come back as exact for a question
        // naming "Widget Pro", the shorter first, so it must pick by containment, not by order.
        var word = $"Widget{Unique()}";
        var plain = await SeedAsync(word, $"W-{Unique()}".ToUpperInvariant(), 9.95m);
        var pro = await SeedAsync($"{word} Pro", $"W-{Unique()}".ToUpperInvariant());

        var found = await FindAsync($"How much is the {word} Pro?");

        found.Where(m => m.Score >= 0.999).Select(m => m.Id).Should().Equal(plain.Id, pro.Id);
    }

    [Fact]
    public async Task FindAsync_FindsBySku()
    {
        var sku = $"SKU-{Unique()}".ToUpperInvariant();
        var product = await SeedAsync($"Plain {Unique()}", sku);

        var found = await FindAsync("what is this one", sku);

        found.Should().ContainSingle(m => m.Id == product.Id).Which.Score.Should().Be(1);
    }

    [Fact]
    public async Task FindAsync_FindsNothing_ForAnUnrelatedQuestion()
    {
        await SeedAsync($"Vextraplume {Unique()}", $"V-{Unique()}".ToUpperInvariant());

        var found = await FindAsync("write a poem about the rain");

        found.Should().NotContain(m => m.Name.StartsWith("Vextraplume", StringComparison.Ordinal));
    }
}
